using System.CommandLine;
using Oxygen.Compiler.Lexer;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
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
buildCommand.SetAction((ParseResult result) =>
{
    List<string> sources = [];

    FileInfo[]? paths = result.GetValue(sourcesArgument);

    foreach (var file in paths!)
    {
        using var stream = file.OpenRead();
        using TextReader reader = new StreamReader(stream);
        sources.Add(reader.ReadToEnd());
    }
    
    Tokenizer tokenizer = new Tokenizer(Log.Logger.ForContext("SourceContext", "Lexer"));

    List<IReadOnlyList<Token>> sourceTokens = [];

    foreach (var source in sources)
    {
        sourceTokens.Add(tokenizer.Tokenize(source));
    }
});

rootCommand.Add(buildCommand);

rootCommand.Parse(args).Invoke();