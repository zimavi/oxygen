using System.CommandLine;
using System.Text;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Parsing;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;
using Oxygen.Llvm;
using Serilog;
using CommandParseResult = System.CommandLine.ParseResult;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
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
var clangOption = new Option<string>("--clang", "-c")
{
    Description = "LLVM compiler driver used for object generation and linking",
    DefaultValueFactory = _ => "clang"
};
var debugOption = new Option<bool>("--debug")
{
    Description = "Generate debug symbols and use the debug runtime"
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
    clangOption,
    debugOption,
    warningsAsErrorsOption,
    optimizationOption,
    runtimeOption,
    standardLibraryOption
};
buildCommand.SetAction(result =>
{
    FileInfo[]? paths = result.GetValue(sourcesArgument);
    var sources = paths!.Select(file => new SourceText(file.FullName, File.ReadAllText(file.FullName))).ToList();
    var formatter = new DiagnosticFormatter();
    var useColor = !Console.IsErrorRedirected;
    var tokenizer = new Tokenizer(Log.Logger.ForContext("SourceContext", "Lexer"));
    List<CompilationUnit> compilationUnits = [];

    foreach (var source in sources)
    {
        var tokenization = tokenizer.TokenizeWithDiagnostics(source.Text, source.Name);
        if (tokenization.Diagnostics.Count > 0)
        {
            DisplayDiagnostics(tokenization.Diagnostics, [source], formatter, useColor);
            return;
        }

        var parser = new Parser(tokenization.Tokens, Log.Logger.ForContext("SourceContext", "Parser"));
        var parsed = parser.ParseWithDiagnostics(source.Name);
        compilationUnits.Add(parsed.Unit);
        if (parsed.Diagnostics.Count > 0)
        {
            DisplayDiagnostics(parsed.Diagnostics, [source], formatter, useColor);
            return;
        }
    }

    var semanticResult = new SemanticAnalyzer(Log.Logger.ForContext("SourceContext", "Semantic"))
        .AnalyzeWithDiagnostics(compilationUnits, sources.Select(source => (string?)source.Name).ToList());
    var warningsAsErrors = result.GetValue(warningsAsErrorsOption);
    if (semanticResult.Diagnostics.Count > 0)
    {
        DisplayDiagnostics(semanticResult.Diagnostics, sources, formatter, useColor);
        if (semanticResult.HasBlockingDiagnostics(warningsAsErrors))
        {
            return;
        }
    }

    using var generated = new LlvmModuleGenerator().Generate("program", semanticResult.Model!);
    var irOutput = result.GetValue(emitIrOption);
    if (irOutput is not null)
    {
        var irOutputPath = Path.GetFullPath(irOutput.FullName);
        var irOutputDirectory = Path.GetDirectoryName(irOutputPath);
        if (!string.IsNullOrEmpty(irOutputDirectory))
            Directory.CreateDirectory(irOutputDirectory);

        File.WriteAllText(irOutputPath, generated.ToString());
        return;
    }

    var runtimePath = result.GetValue(runtimeOption)?.FullName
        ?? Path.Combine(Environment.GetEnvironmentVariable("OXYGEN_PATH") ?? "./",
            result.GetValue(debugOption)
                ? OperatingSystem.IsWindows() ? "oxygen_runtime_debug.lib" : "liboxygen_runtime_debug.a"
                : OperatingSystem.IsWindows() ? "oxygen_runtime.lib" : "liboxygen_runtime.a");
    var dependencies = new List<NativeDependency>
    {
        new(runtimePath, NativeDependencyKind.Static)
    };
    dependencies.AddRange((result.GetValue(standardLibraryOption) ?? [])
        .Select(path => new NativeDependency(path.FullName, NativeDependencyKind.Dynamic)));

    generated.BuildNative(new NativeBuildOptions
    {
        CompilerPath = result.GetValue(clangOption) ?? "clang",
        OptimizationLevel = "O" + (result.GetValue(optimizationOption) ?? "O2"),
        GenerateDebugSymbols = result.GetValue(debugOption),
        OutputPath = result.GetValue(outputOption)?.FullName ?? "program",
        Dependencies = dependencies
    });
});

rootCommand.Add(buildCommand);

rootCommand.Parse(args).Invoke();

static void DisplayDiagnostics(
    IReadOnlyList<Diagnostic> diagnostics,
    IReadOnlyList<SourceText> sources,
    DiagnosticFormatter formatter,
    bool useColor)
{
    foreach (var diagnostic in diagnostics)
    {
        var source = sources.FirstOrDefault(candidate => candidate.Name == diagnostic.SourceName) ?? sources[0];
        Console.Error.WriteLine(formatter.Format(diagnostic, source, useColor));
    }
}