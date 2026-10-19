using System.CommandLine;
using System.Text;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Parsing;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;
using Oxygen.Llvm;
using Oxygen.Cli;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: "[{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{ExceptionMessage}")
    .CreateLogger();

Log.Logger.ForContext("SourceContext", "Compiler");

var rootCommand = new RootCommand("Oxygen CLI");

var sourcesArgument = new Argument<FileInfo[]>("sources")
{
    Description = "The source files to process",
    Arity = ArgumentArity.OneOrMore
};
var outputOption = new Option<FileInfo?>("--output", "-o")
{
    Description = "Path of the native executable to produce",
    DefaultValueFactory = _ => new FileInfo("program")
};
var emitIrOption = new Option<FileInfo?>("--emit-ir")
{
    Description = "Write textual LLVM IR to this path instead of building a native executable"
};
var emitMetadataOption = new Option<FileInfo?>("--emit-metadata")
{
    Description = "Write versioned native-module metadata (.oxmeta) to this path"
};
var clangOption = new Option<string>("--clang", "-c")
{
    Description = "LLVM compiler driver used for object generation and linking",
    DefaultValueFactory = _ => "clang"
};
var debugOption = new Option<bool>("--debug")
{
    Description = "Generate debug symbols and use the debug runtime"
};
var manualMemoryOption = new Option<bool>("--manual-memory", "-mm")
{
    Description = "Manual memory mode for every function (C++ style): lifetimes are tracked and leaks are reported, and objects are freed only by delete"
};
var warningsAsErrorsOption = new Option<bool>("--warnings-as-errors")
{
    Description = "Treat semantic warnings as errors"
};
var optimizationOption = new Option<string>("--optimization", "-O")
{
    Description = "Clang optimization level, for example O0 or O2",
    DefaultValueFactory = _ => "2"
};
var jobsOption = new Option<int>("--jobs", "-j")
{
    Description = "Number of LLVM backend workers; values greater than one enable ThinLTO parallelism",
    DefaultValueFactory = _ => 1
};
var runtimeOption = new Option<FileInfo?>("--runtime", "-r")
{
    Description = "Static Oxygen runtime library to link"
};
var standardLibraryOption = new Option<FileInfo[]>("--stdlib")
{
    Description = "Optional dynamic standard-library dependency",
    Arity = ArgumentArity.ZeroOrMore
};
var buildCommand = new Command("build", "Build the project")
{
    sourcesArgument,
    outputOption,
    emitIrOption,
    emitMetadataOption,
    clangOption,
    debugOption,
    warningsAsErrorsOption,
    manualMemoryOption,
    optimizationOption,
    jobsOption,
    runtimeOption,
    standardLibraryOption
};
// Exit codes: 0 on success, 1 when the program could not be compiled (errors in the source, unreadable
// inputs, or a failing native toolchain), 2 when the compiler itself failed on valid source.
int RunBuild(System.CommandLine.ParseResult result)
{
    FileInfo[]? paths = result.GetValue(sourcesArgument);
    var inputDiagnostics = new DiagnosticBag();
    var rootSources = new List<SourceText>();
    foreach (var file in paths!)
    {
        try
        {
            rootSources.Add(new SourceText(file.FullName, File.ReadAllText(file.FullName)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            inputDiagnostics.Add(
                DiagnosticPhase.Toolchain,
                DiagnosticSeverity.Error,
                $"Unable to read source file '{file.FullName}': {exception.Message}",
                new SourceLocation(1, 1),
                sourceName: file.FullName);
        }
    }

    var formatter = new DiagnosticFormatter();
    var useColor = !Console.IsErrorRedirected;
    if (inputDiagnostics.HasErrors)
    {
        DisplayDiagnostics(inputDiagnostics.Diagnostics, rootSources, formatter, useColor);
        return ExitCodes.CompilationFailed;
    }
    var tokenizer = new Tokenizer(Log.Logger.ForContext("SourceContext", "Lexer"));
    var resolved = new SourceModuleResolver(tokenizer, Log.Logger.ForContext("SourceContext", "Resolver"))
        .Resolve(rootSources);
    var sources = resolved.Sources;
    var compilationUnits = resolved.Units;
    if (resolved.Diagnostics.Count > 0)
    {
        DisplayDiagnostics(
            resolved.Diagnostics,
            sources.Concat(rootSources).GroupBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToList(),
            formatter,
            useColor);
        return ExitCodes.CompilationFailed;
    }

    var externalModules = new List<ExternalModuleDeclaration>();
    foreach (var modulePath in result.GetValue(standardLibraryOption) ?? [])
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
            DisplayDiagnostics(diagnostics.Diagnostics, sources, formatter, useColor);
            return ExitCodes.CompilationFailed;
        }
    }
    var semanticResult = new SemanticAnalyzer(Log.Logger.ForContext("SourceContext", "Semantic"))
        {
            DefaultMemoryMode = result.GetValue(manualMemoryOption) ? MemoryMode.Manual : MemoryMode.Automatic
        }
        .AnalyzeWithDiagnostics(
            compilationUnits,
            sources.Select(source => (string?)source.Name).ToList(),
            externalModules);
    var warningsAsErrors = result.GetValue(warningsAsErrorsOption);
    if (semanticResult.Diagnostics.Count > 0)
    {
        DisplayDiagnostics(semanticResult.Diagnostics, sources, formatter, useColor);
        if (semanticResult.HasBlockingDiagnostics(warningsAsErrors))
        {
            return ExitCodes.CompilationFailed;
        }
    }

    GeneratedLlvmModule generated;
    try
    {
        generated = new LlvmModuleGenerator().Generate("program", semanticResult.Model!);
    }
    catch (Exception exception) when (exception is PlatformNotSupportedException or InvalidOperationException)
    {
        var diagnostics = new DiagnosticBag();
        diagnostics.Add(
            DiagnosticPhase.Backend,
            DiagnosticSeverity.Error,
            exception.Message,
            new SourceLocation(1, 1));
        DisplayDiagnostics(diagnostics.Diagnostics, sources, formatter, useColor);
        // The source already passed semantic analysis, so a backend failure is a compiler limitation or bug.
        return ExitCodes.InternalError;
    }

    using (generated)
    {
        var irOutput = result.GetValue(emitIrOption);
        var metadataOutput = result.GetValue(emitMetadataOption);
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

        var runtimePath = result.GetValue(runtimeOption)?.FullName
            ?? Path.Combine(Environment.GetEnvironmentVariable("OXYGEN_PATH") ?? "./",
                "liboxygenrt.a");
        var dependencies = new List<NativeDependency>
    {
        new(runtimePath, NativeDependencyKind.Static)
    };
        dependencies.AddRange((result.GetValue(standardLibraryOption) ?? [])
            .Select(path => new NativeDependency(path.FullName, NativeDependencyKind.Dynamic)));

        try
        {
            generated.BuildNative(new NativeBuildOptions
            {
                CompilerPath = result.GetValue(clangOption) ?? "clang",
                OptimizationLevel = NormalizeOptimizationLevel(result.GetValue(optimizationOption) ?? "O2"),
                Jobs = result.GetValue(jobsOption),
                GenerateDebugSymbols = result.GetValue(debugOption),
                OutputPath = result.GetValue(outputOption)?.FullName ?? "program",
                MetadataPath = metadataOutput?.FullName
                    ?? (result.GetValue(outputOption)?.FullName ?? "program") + ".oxmeta",
                Dependencies = dependencies
            });
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
            DisplayDiagnostics(diagnostics.Diagnostics, sources, formatter, useColor);
            return ExitCodes.CompilationFailed;
        }
    }

    return ExitCodes.Success;
}

buildCommand.SetAction(result =>
{
    try
    {
        return RunBuild(result);
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

rootCommand.Add(buildCommand);

return rootCommand.Parse(args).Invoke();

static void DisplayDiagnostics(
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

static string NormalizeOptimizationLevel(string value) =>
    value.StartsWith('O') ? value : "O" + value;