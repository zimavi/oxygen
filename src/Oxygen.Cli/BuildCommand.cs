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
    public static readonly Option<bool> NoStd = new("--no-std")
    {
        Description = "Do not load the standard library. Nothing implicit is loaded from it, an import of a std module must be one of the " +
            "sources, and a null class reference stops the program with exit code 101, since there is no NullReferenceException to throw. " +
            "Use it to build the standard library itself, or a library that does not need it"
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
        Description = "Oxygen runtime to link: the static archive (liboxygenrt.a), or the shared runtime (oxygenrt.lib on Windows, " +
            "liboxygenrt.so elsewhere). --library --dynamic and --stdlib link the shared runtime, and it is their default"
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
            NoStd,
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
        if (!TryResolveStandardLibraries(result, target, input.Sources, formatter, useColor, out var standardLibraries))
            return ExitCodes.ToolchainFailure;
        // The runtime is checked before the sources are analyzed, when the build links it. A shared library, and a program that uses the
        // standard library's shared object, need the shared runtime: a static archive there would give the process two runtimes.
        if (!(library && !dynamic))
        {
            var shared = dynamic || standardLibraries.Count > 0;
            var runtimePath = RuntimePathOf(result, target, shared);
            var runtimeKind = RuntimeKindOf(runtimePath, target);
            if (runtimeKind is null)
            {
                Console.Error.WriteLine($"oxygenc: --runtime must be a static archive (.a) or the shared runtime ({SharedRuntimeFileName(target)}), " +
                    $"not '{runtimePath}'.");
                return ExitCodes.SourceError;
            }
            if (shared && runtimeKind == NativeDependencyKind.Static)
            {
                var flag = dynamic ? "--dynamic" : "--stdlib";
                Console.Error.WriteLine($"oxygenc: {flag} links the shared runtime, so --runtime must name it ({SharedRuntimeFileName(target)}), " +
                    $"not the static archive '{runtimePath}'.");
                return ExitCodes.SourceError;
            }
        }
        if (input.Diagnostics.HasErrors)
        {
            Display(input.Diagnostics.Diagnostics, input.Sources, formatter, useColor);
            return ExitCodes.SourceError;
        }

        // The native libraries of --stdlib are read before the sources are resolved, since the modules they provide are not loaded from source.
        if (!TryLoadNativeModules(standardLibraries, target, input.Sources, formatter, useColor, out var loadedModules))
            return ExitCodes.SourceError;
        var externalModules = loadedModules.Select(loaded => loaded.Module).ToList();
        var providedModules = externalModules.Select(module => module.ModuleName).ToHashSet(StringComparer.Ordinal);
        var resolved = ResolveModules(input.Sources, !result.GetValue(NoStd), providedModules);
        if (resolved.Diagnostics.Count > 0)
        {
            Display(resolved.Diagnostics,
                resolved.Sources.Concat(input.Sources).GroupBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()).ToList(),
                formatter, useColor);
            return ExitCodes.SourceError;
        }
        var sources = resolved.Sources;

        // A library module that the program does not import is not declared to the analyzer, and its warnings are not shown: the program
        // cannot use its functions, and its classes are not loaded for them. The generic declarations of the imported modules are analyzed
        // with the program, so it can instantiate them.
        var importedModules = ImportedModuleNames(loadedModules.SelectMany(loaded => loaded.GenericUnits), resolved.Units);
        var importedLoaded = loadedModules.Where(loaded => importedModules.Contains(loaded.Module.ModuleName)).ToList();
        DisplayNativeWarnings(importedLoaded, input.Sources, formatter, useColor);
        var importedExternalModules = importedLoaded.Select(loaded => loaded.Module).ToList();
        var libraryGenerics = importedLoaded.SelectMany(loaded => loaded.GenericUnits).ToList();
        var semanticResult = new SemanticAnalyzer(Log.Logger.ForContext("SourceContext", "Semantic"))
            {
                DefaultMemoryMode = result.GetValue(ManualMemory) ? MemoryMode.Manual : MemoryMode.Automatic
            }
            .AnalyzeWithDiagnostics(
                resolved.Units.Concat(libraryGenerics.Select(generic => generic.Unit)).ToList(),
                sources.Select(source => (string?)source.Name).Concat(libraryGenerics.Select(generic => (string?)generic.SourceName)).ToList(),
                importedExternalModules,
                requireMain: !library);
        if (semanticResult.Diagnostics.Count > 0)
        {
            Display(semanticResult.Diagnostics, sources, formatter, useColor);
            if (semanticResult.HasBlockingDiagnostics(result.GetValue(WarningsAsErrors)))
                return ExitCodes.SourceError;
        }

        if (library)
            return BuildLibraryModules(result, standardLibraries, input.Sources, resolved.Units, sources, semanticResult.Model!,
                target, formatter, useColor);

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

            return BuildExecutable(result, standardLibraries, generated, target, sources, formatter, useColor);
        }
    }

    // The shared runtime's file: the import library on Windows, and the shared object elsewhere.
    internal static string SharedRuntimeFileName(OxygenTarget target) =>
        target.Triple.Contains("-windows-", StringComparison.Ordinal) ? "oxygenrt.lib" : "liboxygenrt.so";

    // The toolchain installation that this compiler runs from, or null when it runs from a build folder.
    private static readonly InstalledToolchain? Installed = InstalledToolchain.Find(AppContext.BaseDirectory);

    // A standard library to read and link: the file with its metadata, and the file the linker takes.
    private sealed record StandardLibraryFile(FileInfo Metadata, string LinkPath);

    // The runtime of the installation, or else the one that runtime/build.ps1 or build.sh builds for the target, under OXYGEN_PATH (the
    // repository root): the static archive, or the shared runtime.
    private static string DefaultRuntimePath(OxygenTarget target, bool shared) =>
        Installed?.RuntimePath(target, shared) ?? Path.Combine(Environment.GetEnvironmentVariable("OXYGEN_PATH") ?? ".", "runtime", "build", target.Triple,
            shared ? SharedRuntimeFileName(target) : "liboxygenrt.a");

    // The runtime that the build links: --runtime, or the default for the target.
    private static string RuntimePathOf(System.CommandLine.ParseResult result, OxygenTarget target, bool shared) =>
        result.GetValue(Runtime)?.FullName ?? DefaultRuntimePath(target, shared);

    // How a runtime file is linked for the target, or null when it is not a runtime. The static archive is .a on every target. The shared
    // runtime is linked through its import library on Windows and through the shared object elsewhere.
    internal static NativeDependencyKind? RuntimeKindOf(string path, OxygenTarget target)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".a", StringComparison.OrdinalIgnoreCase))
            return NativeDependencyKind.Static;
        return extension.Equals(Path.GetExtension(SharedRuntimeFileName(target)), StringComparison.OrdinalIgnoreCase)
            ? NativeDependencyKind.Dynamic
            : null;
    }

    // Writes the module as a static library, with its metadata in the object file. The library is not linked, so the runtime is not needed.
    // Generates each module that the given sources declare, and builds them into one library. A module that the sources import is not
    // built: it is declared, and the program that uses the library provides it.
    private static int BuildLibraryModules(
        System.CommandLine.ParseResult result,
        IReadOnlyList<StandardLibraryFile> standardLibraries,
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
            return BuildLibrary(result, standardLibraries, modules, target, sources, formatter, useColor);
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
        IReadOnlyList<StandardLibraryFile> standardLibraries,
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
            var runtimePath = RuntimePathOf(result, target, shared: true);
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
            dependencies.Add(new NativeDependency(runtimePath, NativeDependencyKind.Dynamic));
            // A shared library that uses a standard library's classes or functions links that library too.
            if (!TryAddStandardLibraryDependencies(standardLibraries, sources, formatter, useColor, dependencies))
                return ExitCodes.ToolchainFailure;
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

    // Adds the standard libraries to the link. A library whose file for the linker is missing is an error.
    private static bool TryAddStandardLibraryDependencies(
        IReadOnlyList<StandardLibraryFile> standardLibraries,
        IReadOnlyList<SourceText> sources,
        DiagnosticFormatter formatter,
        bool useColor,
        List<NativeDependency> dependencies)
    {
        foreach (var standardLibrary in standardLibraries)
        {
            if (!File.Exists(standardLibrary.LinkPath))
            {
                var libraryDiagnostics = new DiagnosticBag();
                libraryDiagnostics.Add(
                    DiagnosticPhase.Toolchain,
                    DiagnosticSeverity.Error,
                    $"No library to link for '{standardLibrary.Metadata.FullName}': '{standardLibrary.LinkPath}' was not found. Build the " +
                    "library with --dynamic, which writes the import library beside a DLL on Windows.",
                    new SourceLocation(1, 1));
                Display(libraryDiagnostics.Diagnostics, sources, formatter, useColor);
                return false;
            }
            dependencies.Add(new NativeDependency(standardLibrary.LinkPath, NativeDependencyKind.Dynamic));
        }
        return true;
    }

    // The standard libraries of the build: the installed standard library unless --no-std is given, and the ones --stdlib names. A
    // library is read for its metadata from one file, and linked through another on Windows (the DLL and its import library). The
    // installed library is not optional: when the installation lacks it, the build stops and names the missing file.
    private static bool TryResolveStandardLibraries(
        System.CommandLine.ParseResult result,
        OxygenTarget target,
        IReadOnlyList<SourceText> sources,
        DiagnosticFormatter formatter,
        bool useColor,
        out List<StandardLibraryFile> standardLibraries)
    {
        standardLibraries = [];
        if (Installed is not null && !result.GetValue(NoStd))
        {
            var metadata = Installed.StandardLibraryPath(target);
            var link = Installed.StandardLibraryLinkPath(target);
            var missing = new[] { metadata, link }.FirstOrDefault(path => !File.Exists(path));
            if (missing is not null)
            {
                var diagnostics = new DiagnosticBag();
                diagnostics.Add(
                    DiagnosticPhase.Toolchain,
                    DiagnosticSeverity.Error,
                    $"The installed standard library for target '{target.Triple}' was not found at '{missing}'. Reinstall Oxygen, " +
                    "or pass --no-std to build without the standard library.",
                    new SourceLocation(1, 1));
                Display(diagnostics.Diagnostics, sources, formatter, useColor);
                return false;
            }
            standardLibraries.Add(new StandardLibraryFile(new FileInfo(metadata), link));
        }
        foreach (var file in result.GetValue(StandardLibrary) ?? [])
        {
            // A DLL on Windows is linked through the import library beside it.
            var link = target.Triple.Contains("-windows-", StringComparison.Ordinal) &&
                file.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                    ? Path.ChangeExtension(file.FullName, ".lib")
                    : file.FullName;
            standardLibraries.Add(new StandardLibraryFile(file, link));
        }
        return true;
    }

    // Links the generated module into the native executable with the runtime archive and the given dependencies.
    private static int BuildExecutable(
        System.CommandLine.ParseResult result,
        IReadOnlyList<StandardLibraryFile> standardLibraries,
        GeneratedLlvmModule generated,
        OxygenTarget target,
        IReadOnlyList<SourceText> sources,
        DiagnosticFormatter formatter,
        bool useColor)
    {
        // A program that uses the standard library's shared object links the shared runtime. Run has checked that --runtime is a runtime
        // that can be linked this way.
        var shared = standardLibraries.Count > 0;
        var runtimePath = RuntimePathOf(result, target, shared);
        if (!File.Exists(runtimePath))
        {
            var runtimeDiagnostics = new DiagnosticBag();
            runtimeDiagnostics.Add(
                DiagnosticPhase.Toolchain,
                DiagnosticSeverity.Error,
                $"No {(shared ? "shared runtime" : "runtime archive")} for target '{target.Triple}' at '{runtimePath}'. Build it with " +
                "runtime/build.ps1 or runtime/build.sh, or pass --runtime with a runtime built for this target.",
                new SourceLocation(1, 1));
            Display(runtimeDiagnostics.Diagnostics, sources, formatter, useColor);
            return ExitCodes.ToolchainFailure;
        }

        var runtimeKind = RuntimeKindOf(runtimePath, target)
            ?? throw new InvalidOperationException($"The runtime '{runtimePath}' was not checked before linking.");
        var dependencies = new List<NativeDependency> { new(runtimePath, runtimeKind) };
        if (!TryAddStandardLibraryDependencies(standardLibraries, sources, formatter, useColor, dependencies))
            return ExitCodes.ToolchainFailure;

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
        ResolveModules(IReadOnlyList<SourceText> rootSources, bool loadStandardLibrary, IReadOnlySet<string> providedModules)
    {
        var tokenizer = new Tokenizer(Log.Logger.ForContext("SourceContext", "Lexer"));
        var resolved = new SourceModuleResolver(tokenizer, Log.Logger.ForContext("SourceContext", "Resolver"))
            .Resolve(rootSources, loadStandardLibrary, providedModules);
        return (resolved.Sources, resolved.Units, resolved.Diagnostics);
    }

    // The library modules that the program imports: each module the program imports, and each module that one of those imports in turn,
    // through its generic units. A module that nothing imports is not used, so an unused library cannot stop the build.
    private static HashSet<string> ImportedModuleNames(
        IEnumerable<LoadedGenericUnit> generics,
        IReadOnlyList<CompilationUnit> programUnits)
    {
        var all = generics.ToList();
        var pending = new Queue<string>(programUnits.SelectMany(unit => unit.Imports).Select(import => import.Name));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var module = pending.Dequeue();
            if (!visited.Add(module))
                continue;
            foreach (var generic in all.Where(generic => (generic.Unit.ModuleName ?? "program") == module))
            {
                foreach (var import in generic.Unit.Imports)
                    pending.Enqueue(import.Name);
            }
        }
        return visited;
    }

    private static void DisplayNativeWarnings(
        IReadOnlyList<LoadedNativeModule> loadedModules,
        IReadOnlyList<SourceText> sources,
        DiagnosticFormatter formatter,
        bool useColor)
    {
        foreach (var loaded in loadedModules.Where(loaded => loaded.Warnings.Count > 0))
        {
            var warningBag = new DiagnosticBag();
            foreach (var warning in loaded.Warnings)
                warningBag.Add(DiagnosticPhase.Metadata, DiagnosticSeverity.Warning, warning, new SourceLocation(1, 1),
                    sourceName: loaded.Module.ModuleName);
            Display(warningBag.Diagnostics, sources, formatter, useColor);
        }
    }

    // Reads the native libraries of --stdlib. Each library gives its modules, whose functions are then declared for the build without
    // their source. Warnings are displayed, and an unusable library is an error, which gives false.
    private static bool TryLoadNativeModules(
        IReadOnlyList<StandardLibraryFile> standardLibraries,
        OxygenTarget target,
        IReadOnlyList<SourceText> sources,
        DiagnosticFormatter formatter,
        bool useColor,
        out List<LoadedNativeModule> loadedModules)
    {
        loadedModules = [];
        foreach (var modulePath in standardLibraries.Select(library => library.Metadata))
        {
            try
            {
                foreach (var loaded in NativeModuleLoader.Load(modulePath, target))
                {
                    loadedModules.Add(loaded);
                    foreach (var note in loaded.Notes)
                        Log.Information("{Note}", note);
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
                return false;
            }
        }
        return true;
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
