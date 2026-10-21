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
        Description = "Path of the native executable to produce",
        DefaultValueFactory = _ => new FileInfo("program")
    };
    public static readonly Option<FileInfo?> EmitIr = new("--emit-ir")
    {
        Description = "Write textual LLVM IR to this path instead of building a native executable"
    };
    public static readonly Option<FileInfo?> EmitMetadata = new("--emit-metadata")
    {
        Description = "Write versioned native-module metadata (.oxmeta) to this path"
    };
    public static readonly Option<string> Clang = new("--clang", "-c")
    {
        Description = "LLVM compiler driver used for object generation and linking",
        DefaultValueFactory = _ => "clang"
    };
    public static readonly Option<bool> Debug = new("--debug")
    {
        Description = "Generate debug symbols and use the debug runtime"
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
        Description = "Clang optimization level, for example O0 or O2",
        DefaultValueFactory = _ => "2"
    };
    public static readonly Option<int> Jobs = new("--jobs", "-j")
    {
        Description = "Number of LLVM backend workers; values greater than one enable ThinLTO parallelism",
        DefaultValueFactory = _ => 1
    };
    public static readonly Option<FileInfo?> Runtime = new("--runtime", "-r")
    {
        Description = "Static Oxygen runtime library to link"
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
            EmitMetadata,
            Clang,
            Debug,
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
                var module = NativeModuleLoader.Load(modulePath);
                if (module is not null)
                    externalModules.Add(module);
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
                externalModules);
        if (semanticResult.Diagnostics.Count > 0)
        {
            Display(semanticResult.Diagnostics, sources, formatter, useColor);
            if (semanticResult.HasBlockingDiagnostics(result.GetValue(WarningsAsErrors)))
                return ExitCodes.SourceError;
        }

        GeneratedLlvmModule generated;
        try
        {
            generated = new LlvmModuleGenerator(target).Generate("program", semanticResult.Model!);
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
            var metadataOutput = result.GetValue(EmitMetadata);
            if (metadataOutput is not null)
                generated.WriteMetadata(metadataOutput.FullName);
            if (irOutput is not null)
            {
                var irOutputPath = Path.GetFullPath(irOutput.FullName);
                var irOutputDirectory = Path.GetDirectoryName(irOutputPath);
                if (!string.IsNullOrEmpty(irOutputDirectory))
                    Directory.CreateDirectory(irOutputDirectory);

                File.WriteAllText(irOutputPath, generated.ToString());
                return ExitCodes.Success;
            }

            return BuildExecutable(result, generated, target, metadataOutput, sources, formatter, useColor);
        }
    }

    // Links the generated module into the native executable with the runtime archive and the given dependencies.
    private static int BuildExecutable(
        System.CommandLine.ParseResult result,
        GeneratedLlvmModule generated,
        OxygenTarget target,
        FileInfo? metadataOutput,
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
                OptimizationLevel = NormalizeOptimizationLevel(result.GetValue(Optimization) ?? "O2"),
                Target = target.Triple,
                TemporaryDirectory = temporaryDirectory,
                KeepTemporaryFiles = keepTemporaryFiles,
                Jobs = result.GetValue(Jobs),
                GenerateDebugSymbols = result.GetValue(Debug),
                OutputPath = result.GetValue(Output)?.FullName ?? "program",
                MetadataPath = metadataOutput?.FullName
                    ?? (result.GetValue(Output)?.FullName ?? "program") + ".oxmeta",
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

    private static string NormalizeOptimizationLevel(string value) =>
        value.StartsWith('O') ? value : "O" + value;
}
