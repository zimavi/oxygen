using System.CommandLine;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;
using Oxygen.Llvm;
using Serilog;
using Serilog.Events;

namespace Oxygen.Cli;

// The build command. It reads the sources, resolves and analyzes them, and then writes IR, metadata or a native executable. The
// steps run in this order, and a step that fails ends the build with the exit code of its failure: ExitCodes.SourceError for invalid
// input, ExitCodes.ToolchainFailure for the native toolchain, and ExitCodes.InternalError for a compiler bug.
internal static class BuildCommand
{
    public static readonly Argument<FileInfo[]> Sources = new("sources")
    {
        Description = "The source files to process",
        Arity = ArgumentArity.OneOrMore
    };
    public static readonly Option<FileInfo?> Output = new("--output", "-o")
    {
        Description = "Path of the native executable to produce, or of the library with --library. Default: program, or the module name for a library " +
            "(the first module when the library has several)"
    };
    public static readonly Option<bool> Library = new("--library")
    {
        Description = "Build a static library instead of an executable. A library has no main function, and it is not linked, " +
            "so --runtime and --stdlib do not apply"
    };
    public static readonly Option<bool> Dynamic = new("--dynamic")
    {
        Description = "With --library, build a shared library instead of a static one: a .dll with an import library on Windows, " +
            "and a .so elsewhere. It links the shared runtime, which --runtime names (oxygenrt.lib or liboxygenrt.so)"
    };
    public static readonly Option<FileInfo?> EmitIr = new("--emit-ir")
    {
        Description = "Write textual LLVM IR to this path instead of building a native executable"
    };
    public static readonly Option<string> Clang = new("--clang", "-c")
    {
        Description = "LLVM compiler driver used for object generation and linking",
        DefaultValueFactory = _ => "clang"
    };
    public static readonly Option<bool> Unoptimized = new("--unoptimized", "-u")
    {
        Description = "Build without optimization, the same as --optimization O0. Cannot be combined with --optimization"
    };
    public static readonly Option<bool> ManualMemory = new("--manual-memory", "-mm")
    {
        Description = "Manual memory mode for every function (C++ style): lifetimes are tracked and leaks are reported, and objects are freed only by delete"
    };
    public static readonly Option<bool> WarningsAsErrors = new("--warnings-as-errors")
    {
        Description = "Treat semantic warnings as errors"
    };
    public static readonly Option<string> Optimization = new("--optimization", "-O")
    {
        Description = "Clang optimization level, for example O0 or O2. Default: O2"
    };
    public static readonly Option<int> Jobs = new("--jobs", "-j")
    {
        Description = "Number of LLVM backend workers; values greater than one enable ThinLTO parallelism",
        DefaultValueFactory = _ => 1
    };
    public static readonly Option<FileInfo?> Runtime = new("--runtime", "-r")
    {
        Description = "Oxygen runtime library to link: the static archive for an executable, and the shared runtime (oxygenrt.lib " +
            "or liboxygenrt.so) for --library --dynamic"
    };
    public static readonly Option<FileInfo[]> StandardLibrary = new("--stdlib")
    {
        Description = "Optional dynamic standard-library dependency",
        Arity = ArgumentArity.ZeroOrMore
    };
    public static readonly Option<TargetTriple?> Target = new("--target")
    {
        Description = "Target, written with underscores for the triple: x86_64_pc_windows_msvc is x86_64-pc-windows-msvc. " +
            "Defaults to the host platform. The runtime library passed with --runtime must be built for the same target"
    };
    public static readonly Option<LogEventLevel?> LogLevel = new("--log-level")
    {
        Description = "Lowest level of compiler messages to show: Verbose, Debug, Information, Warning, Error or Fatal. Default: Warning"
    };
    public static readonly Option<bool> Verbose = new("--verbose", "-v")
    {
        Description = "Show informational compiler messages. Same as --log-level Information"
    };
    public static readonly Option<bool> KeepTemps = new("--keep-temps")
    {
        Description = "Keep the intermediate IR and object files, and print the directory that holds them"
    };

    public static Command Create()
    {
        var command = new Command("build", "Build the project")
        {
            Sources,
            Output,
            EmitIr,
            Library,
            Dynamic,
            Clang,
            Unoptimized,
            WarningsAsErrors,
            ManualMemory,
            Optimization,
            Jobs,
            Runtime,
            StandardLibrary,
            Target,
            LogLevel,
            Verbose,
            KeepTemps
        };
        command.Validators.Add(result =>
        {
            if (result.GetResult(Unoptimized) is not null && result.GetResult(Optimization) is not null)
                result.AddError("--unoptimized and --optimization cannot be used together. Use one of them.");
        });
        command.SetAction(result =>
        {
            try
            {
                return Run(result);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Ordinary invalid programs are reported as diagnostics; reaching this point means the
                // compiler violated one of its own invariants.
                Console.Error.WriteLine("oxygenc: internal compiler error. This is a bug in the compiler, not in your program.");
                Console.Error.WriteLine(exception);
                return ExitCodes.InternalError;
            }
        });
        return command;
    }

    private static int Run(System.CommandLine.ParseResult result)
    {
        var formatter = new DiagnosticFormatter();
        var useColor = !Console.IsErrorRedirected;
        var library = result.GetValue(Library);
        var dynamic = result.GetValue(Dynamic);
        // A static library is not linked, so the options for linking do not apply to it. A shared library links the runtime, and it
        // does not link standard-library dependencies yet.
        if (dynamic && !library)
        {
            Console.Error.WriteLine("oxygenc: --dynamic builds a shared library, so it needs --library.");
            return ExitCodes.SourceError;
        }
        if (library && !dynamic && result.GetValue(Runtime) is not null)
        {
            Console.Error.WriteLine("oxygenc: --library does not link, so --runtime cannot be used with it. Add --dynamic for a shared library.");
            return ExitCodes.SourceError;
        }
        if (library && (result.GetValue(StandardLibrary) ?? []).Length > 0)
        {
            Console.Error.WriteLine("oxygenc: --stdlib cannot be used with --library yet.");
            return ExitCodes.SourceError;
        }
        if (library && result.GetValue(EmitIr) is not null)
        {
            Console.Error.WriteLine("oxygenc: --emit-ir writes one module's IR, so it cannot be used with --library.");
            return ExitCodes.SourceError;
        }

        var input = ReadSources(result.GetValue(Sources)!);
        // An unsupported target is reported before the sources are analyzed, since no code can be generated for it.
        if (!TryResolveTarget(result, out var target, out var targetDiagnostics))
        {
            Display(targetDiagnostics, input.Sources, formatter, useColor);
            return ExitCodes.ToolchainFailure;
        }
        if (input.Diagnostics.HasErrors)
        {
            Display(input.Diagnostics.Diagnostics, input.Sources, formatter, useColor);
            return ExitCodes.SourceError;
        }

        var resolved = ResolveModules(input.Sources);
        if (resolved.Diagnostics.Count > 0)
        {
            Display(resolved.Diagnostics,
                resolved.Sources.Concat(input.Sources).GroupBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()).ToList(),
                formatter, useColor);
            return ExitCodes.SourceError;
        }
        var sources = resolved.Sources;

        var externalModules = new List<ExternalModuleDeclaration>();
        foreach (var modulePath in result.GetValue(StandardLibrary) ?? [])
        {
            try
            {
                var loaded = NativeModuleLoader.Load(modulePath, target);
                if (loaded is null)
                    continue;
                externalModules.Add(loaded.Module);
                if (loaded.Warnings.Count > 0)
                {
                    var warningBag = new DiagnosticBag();
                    foreach (var warning in loaded.Warnings)
                        warningBag.Add(DiagnosticPhase.Metadata, DiagnosticSeverity.Warning, warning, new SourceLocation(1, 1),
                            sourceName: modulePath.FullName);
                    Display(warningBag.Diagnostics, sources, formatter, useColor);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                var diagnostics = new DiagnosticBag();
                diagnostics.Add(
                    DiagnosticPhase.Metadata,
                    DiagnosticSeverity.Error,
                    exception.Message,
                    new SourceLocation(1, 1),
                    sourceName: modulePath.FullName);
                Display(diagnostics.Diagnostics, sources, formatter, useColor);
                return ExitCodes.SourceError;
            }
        }

        var semanticResult = new SemanticAnalyzer(Log.Logger.ForContext("SourceContext", "Semantic"))
            {
                DefaultMemoryMode = result.GetValue(ManualMemory) ? MemoryMode.Manual : MemoryMode.Automatic
            }
            .AnalyzeWithDiagnostics(
                resolved.Units,
                sources.Select(source => (string?)source.Name).ToList(),
                externalModules,
                requireMain: !library);
        if (semanticResult.Diagnostics.Count > 0)
        {
            Display(semanticResult.Diagnostics, sources, formatter, useColor);
            if (semanticResult.HasBlockingDiagnostics(result.GetValue(WarningsAsErrors)))
                return ExitCodes.SourceError;
        }

        if (library)
            return BuildLibraryModules(result, input.Sources, resolved.Units, sources, semanticResult.Model!, target,
                formatter, useColor);

        GeneratedLlvmModule generated;
        try
        {
            // The program's module is the one its first source file declares, which its metadata names.
            var programModule = resolved.Units.FirstOrDefault()?.ModuleName ?? "program";
            generated = new LlvmModuleGenerator(target).Generate(programModule, semanticResult.Model!,
                GenericsOf(resolved.Units, resolved.Sources, programModule), library);
        }
        catch (LlvmVerificationException exception)
        {
            // The IR was generated from a program that passed semantic analysis, so invalid IR is a compiler bug.
            Console.Error.WriteLine("oxygenc: internal compiler error. This is a bug in the compiler, not in your program.");
            Console.Error.WriteLine(exception.Message);
            return ExitCodes.InternalError;
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or InvalidOperationException)
        {
            var diagnostics = new DiagnosticBag();
            diagnostics.Add(
                DiagnosticPhase.Backend,
                DiagnosticSeverity.Error,
                exception.Message,
                new SourceLocation(1, 1));
            Display(diagnostics.Diagnostics, sources, formatter, useColor);
            // The source already passed semantic analysis, so a backend failure is a compiler limitation or bug.
            return ExitCodes.InternalError;
        }

        using (generated)
        {
            var irOutput = result.GetValue(EmitIr);
            if (irOutput is not null)
            {
                var irOutputPath = Path.GetFullPath(irOutput.FullName);
                var irOutputDirectory = Path.GetDirectoryName(irOutputPath);
                if (!string.IsNullOrEmpty(irOutputDirectory))
                    Directory.CreateDirectory(irOutputDirectory);

                File.WriteAllText(irOutputPath, generated.ToString());
                return ExitCodes.Success;
            }

            return BuildExecutable(result, generated, target, sources, formatter, useColor);
        }
    }

    // The shared runtime that runtime/build.ps1 or build.sh builds for the target, under OXYGEN_PATH (the repository root). It is the
    // import library on Windows and the shared object elsewhere.
    private static string DefaultSharedRuntimePath(OxygenTarget target) =>
        Path.Combine(Environment.GetEnvironmentVariable("OXYGEN_PATH") ?? ".", "runtime", "build", target.Triple,
            target.Triple.Contains("-windows-", StringComparison.Ordinal) ? "oxygenrt.lib" : "liboxygenrt.so");

    // Writes the module as a static library, with its metadata in the object file. The library is not linked, so the runtime is not needed.
    // Generates each module that the given sources declare, and builds them into one library. A module that the sources import is not
    // built: it is declared, and the program that uses the library provides it.
    private static int BuildLibraryModules(
        System.CommandLine.ParseResult result,
        IReadOnlyList<SourceText> roots,
        IReadOnlyList<CompilationUnit> units,
        IReadOnlyList<SourceText> sources,
        SemanticModel model,
        OxygenTarget target,
        DiagnosticFormatter formatter,
        bool useColor)
    {
        var moduleNames = units
            .Select((unit, index) => (Unit: unit, Source: sources[index]))
            .Where(pair => roots.Any(root => root.Name == pair.Source.Name))
            .Select(pair => pair.Unit.ModuleName ?? "program")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var modules = new List<GeneratedLlvmModule>();
        try
        {
            foreach (var moduleName in moduleNames)
                modules.Add(new LlvmModuleGenerator(target).Generate(moduleName, model,
                    GenericsOf(units, sources, moduleName), library: true));
            return BuildLibrary(result, modules, target, sources, formatter, useColor);
        }
        catch (LlvmVerificationException exception)
        {
            // The IR was generated from a program that passed semantic analysis, so invalid IR is a compiler bug.
            Console.Error.WriteLine("oxygenc: internal compiler error. This is a bug in the compiler, not in your program.");
            Console.Error.WriteLine(exception.Message);
            return ExitCodes.InternalError;
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or InvalidOperationException)
        {
            var diagnostics = new DiagnosticBag();
            diagnostics.Add(
                DiagnosticPhase.Backend,
                DiagnosticSeverity.Error,
                exception.Message,
                new SourceLocation(1, 1));
            Display(diagnostics.Diagnostics, sources, formatter, useColor);
            return ExitCodes.InternalError;
        }
        finally
        {
            foreach (var module in modules)
                module.Dispose();
        }
    }

    private static int BuildLibrary(
        System.CommandLine.ParseResult result,
        IReadOnlyList<GeneratedLlvmModule> modules,
        OxygenTarget target,
        IReadOnlyList<SourceText> sources,
        DiagnosticFormatter formatter,
        bool useColor)
    {
        var shared = result.GetValue(Dynamic);
        // A shared library links the shared runtime. The default is the one that runtime/build.ps1 or build.sh builds for the target.
        var dependencies = new List<NativeDependency>();
        if (shared)
        {
            var runtimePath = result.GetValue(Runtime)?.FullName ?? DefaultSharedRuntimePath(target);
            if (!File.Exists(runtimePath))
            {
                var runtimeDiagnostics = new DiagnosticBag();
                runtimeDiagnostics.Add(
                    DiagnosticPhase.Toolchain,
                    DiagnosticSeverity.Error,
                    $"No shared runtime for target '{target.Triple}' at '{runtimePath}'. Build it with runtime/build.ps1 or " +
                    "runtime/build.sh, or pass --runtime with the shared runtime built for this target.",
                    new SourceLocation(1, 1));
                Display(runtimeDiagnostics.Diagnostics, sources, formatter, useColor);
                return ExitCodes.ToolchainFailure;
            }
            dependencies.Add(new NativeDependency(runtimePath, NativeDependencyKind.Static));
        }

        var keepTemporaryFiles = result.GetValue(KeepTemps);
        var temporaryDirectory = keepTemporaryFiles
            ? Path.Combine(Path.GetTempPath(), $"oxygen-{Guid.NewGuid():N}")
            : null;
        if (temporaryDirectory is not null)
            Console.Error.WriteLine($"oxygenc: keeping the intermediate files in '{temporaryDirectory}'.");

        using var interrupt = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArguments) =>
        {
            eventArguments.Cancel = true;
            interrupt.Cancel();
        };
        try
        {
            // A library is named after its module unless -o gives a path.
            new NativeBuildPipeline(new ProcessNativeToolRunner(cancellation: interrupt.Token)).BuildLibrary(modules, new NativeBuildOptions
            {
                CompilerPath = result.GetValue(Clang) ?? "clang",
                OptimizationLevel = OptimizationLevelOf(result),
                Target = target.Triple,
                TemporaryDirectory = temporaryDirectory,
                KeepTemporaryFiles = keepTemporaryFiles,
                OutputPath = result.GetValue(Output)?.FullName ?? modules[0].Metadata.ModuleName,
                Shared = shared,
                Dependencies = dependencies
            });
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("oxygenc: the build was cancelled.");
            return ExitCodes.Interrupted;
        }
        catch (Exception exception) when (exception is NativeToolException or InvalidOperationException
            or ArgumentException or IOException)
        {
            var diagnostics = new DiagnosticBag();
            diagnostics.Add(
                DiagnosticPhase.Toolchain,
                DiagnosticSeverity.Error,
                exception.Message,
                new SourceLocation(1, 1));
            Display(diagnostics.Diagnostics, sources, formatter, useColor);
            return ExitCodes.ToolchainFailure;
        }
        return ExitCodes.Success;
    }

    // Links the generated module into the native executable with the runtime archive and the given dependencies.
    private static int BuildExecutable(
        System.CommandLine.ParseResult result,
        GeneratedLlvmModule generated,
        OxygenTarget target,
        IReadOnlyList<SourceText> sources,
        DiagnosticFormatter formatter,
        bool useColor)
    {
        // The default runtime is the archive that runtime/build.ps1 or build.sh builds for the target, under OXYGEN_PATH
        // (the repository root, which holds runtime/ and stdlib/).
        var runtimePath = result.GetValue(Runtime)?.FullName
            ?? Path.Combine(Environment.GetEnvironmentVariable("OXYGEN_PATH") ?? ".", "runtime", "build", target.Triple,
                "liboxygenrt.a");
        if (!File.Exists(runtimePath))
        {
            var runtimeDiagnostics = new DiagnosticBag();
            runtimeDiagnostics.Add(
                DiagnosticPhase.Toolchain,
                DiagnosticSeverity.Error,
                $"No runtime archive for target '{target.Triple}' at '{runtimePath}'. Build it with runtime/build.ps1 or " +
                "runtime/build.sh, or pass --runtime with an archive built for this target.",
                new SourceLocation(1, 1));
            Display(runtimeDiagnostics.Diagnostics, sources, formatter, useColor);
            return ExitCodes.ToolchainFailure;
        }

        var dependencies = new List<NativeDependency> { new(runtimePath, NativeDependencyKind.Static) };
        dependencies.AddRange((result.GetValue(StandardLibrary) ?? [])
            .Select(path => new NativeDependency(path.FullName, NativeDependencyKind.Dynamic)));

        var keepTemporaryFiles = result.GetValue(KeepTemps);
        var temporaryDirectory = keepTemporaryFiles
            ? Path.Combine(Path.GetTempPath(), $"oxygen-{Guid.NewGuid():N}")
            : null;
        if (temporaryDirectory is not null)
            Console.Error.WriteLine($"oxygenc: keeping the intermediate files in '{temporaryDirectory}'.");

        using var interrupt = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArguments) =>
        {
            // Ctrl+C stops the native tool and the build, rather than ending the process in the middle of the link.
            eventArguments.Cancel = true;
            interrupt.Cancel();
        };
        try
        {
            generated.BuildNative(new NativeBuildOptions
            {
                CompilerPath = result.GetValue(Clang) ?? "clang",
                OptimizationLevel = OptimizationLevelOf(result),
                Target = target.Triple,
                TemporaryDirectory = temporaryDirectory,
                KeepTemporaryFiles = keepTemporaryFiles,
                Jobs = result.GetValue(Jobs),
                OutputPath = result.GetValue(Output)?.FullName ?? "program",
                Dependencies = dependencies
            }, new ProcessNativeToolRunner(cancellation: interrupt.Token));
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("oxygenc: the build was cancelled.");
            return ExitCodes.Interrupted;
        }
        catch (Exception exception) when (exception is NativeToolException or InvalidOperationException
            or ArgumentException or IOException)
        {
            var diagnostics = new DiagnosticBag();
            diagnostics.Add(
                DiagnosticPhase.Toolchain,
                DiagnosticSeverity.Error,
                exception.Message,
                new SourceLocation(1, 1));
            Display(diagnostics.Diagnostics, sources, formatter, useColor);
            return ExitCodes.ToolchainFailure;
        }

        return ExitCodes.Success;
    }

    // The source files named on the command line. A file that cannot be read is a diagnostic, not a crash.
    private static (List<SourceText> Sources, DiagnosticBag Diagnostics) ReadSources(FileInfo[] paths)
    {
        var diagnostics = new DiagnosticBag();
        var sources = new List<SourceText>();
        foreach (var file in paths)
        {
            try
            {
                sources.Add(new SourceText(file.FullName, File.ReadAllText(file.FullName)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(
                    DiagnosticPhase.Toolchain,
                    DiagnosticSeverity.Error,
                    $"Unable to read source file '{file.FullName}': {exception.Message}",
                    new SourceLocation(1, 1),
                    sourceName: file.FullName);
            }
        }
        return (sources, diagnostics);
    }

    private static bool TryResolveTarget(System.CommandLine.ParseResult result, out OxygenTarget target, out IReadOnlyList<Diagnostic> diagnostics)
    {
        try
        {
            var triple = result.GetValue(Target);
            target = triple is null ? OxygenTarget.Host : triple.Value.ToTarget();
            diagnostics = [];
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or PlatformNotSupportedException)
        {
            var bag = new DiagnosticBag();
            bag.Add(DiagnosticPhase.Toolchain, DiagnosticSeverity.Error, exception.Message, new SourceLocation(1, 1));
            target = OxygenTarget.WindowsMsvcX64;
            diagnostics = bag.Diagnostics;
            return false;
        }
    }

    private static (IReadOnlyList<SourceText> Sources, IReadOnlyList<CompilationUnit> Units, IReadOnlyList<Diagnostic> Diagnostics)
        ResolveModules(IReadOnlyList<SourceText> rootSources)
    {
        var tokenizer = new Tokenizer(Log.Logger.ForContext("SourceContext", "Lexer"));
        var resolved = new SourceModuleResolver(tokenizer, Log.Logger.ForContext("SourceContext", "Resolver"))
            .Resolve(rootSources);
        return (resolved.Sources, resolved.Units, resolved.Diagnostics);
    }

    private static void Display(
        IReadOnlyList<Diagnostic> diagnostics,
        IReadOnlyList<SourceText> sources,
        DiagnosticFormatter formatter,
        bool useColor)
    {
        foreach (var diagnostic in diagnostics)
        {
            var source = sources.FirstOrDefault(candidate => candidate.Name == diagnostic.SourceName);
            Console.Error.WriteLine(formatter.Format(diagnostic, source, useColor));
        }
    }

    // --unoptimized means O0. Otherwise --optimization applies, and O2 is the default. The two are rejected together when parsed.
    internal static string OptimizationLevelOf(System.CommandLine.ParseResult result) =>
        result.GetValue(Unoptimized) ? "O0" : NormalizeOptimizationLevel(result.GetValue(Optimization) ?? "O2");

    // The generic declarations of the module being built, with one entry per source file that declares it. A file without a
    // module header is in the module "program", as the analyzer treats it. Only the file name is recorded, so the metadata
    // does not depend on where the sources are.
    internal static IReadOnlyList<NativeGenericMetadata> GenericsOf(
        IReadOnlyList<CompilationUnit> units,
        IReadOnlyList<SourceText> sources,
        string moduleName)
    {
        var entries = new List<NativeGenericMetadata>();
        for (var index = 0; index < units.Count; index++)
        {
            var unit = units[index] with { ModuleName = units[index].ModuleName ?? "program" };
            if (unit.ModuleName != moduleName || !GenericSyntax.HasDeclarations(unit))
                continue;
            entries.Add(NativeGenericMetadata.Create(unit, Path.GetFileName(sources[index].Name)));
        }
        return entries;
    }

    private static string NormalizeOptimizationLevel(string value) =>
        value.StartsWith('O') ? value : "O" + value;
}
