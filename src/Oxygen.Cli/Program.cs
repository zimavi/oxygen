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
var buildCommand = new Command("build", "Build the project")
{
    sourcesArgument
};
buildCommand.SetAction((CommandParseResult result) =>
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
    if (semanticResult.Diagnostics.Count > 0)
    {
        DisplayDiagnostics(semanticResult.Diagnostics, sources, formatter, useColor);
        return;
    }

    using var generated = new LlvmModuleGenerator().Generate("program", semanticResult.Model!);
    
    File.WriteAllText("output.ll", generated.ToString());
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