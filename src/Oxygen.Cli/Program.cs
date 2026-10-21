using System.CommandLine;
using Oxygen.Cli;
using Serilog;
using Serilog.Events;

var rootCommand = new RootCommand("Oxygen CLI");
rootCommand.Add(BuildCommand.Create());

var parsed = rootCommand.Parse(args);
// A command line with errors is reported by Invoke, which does not run the command. Its option values cannot be read.
if (parsed.Errors.Count == 0)
{
    ConfigureLogging(parsed.GetValue(BuildCommand.LogLevel)
        ?? (parsed.GetValue(BuildCommand.Verbose) ? LogEventLevel.Information : LogEventLevel.Warning));
}
return parsed.Invoke();

// Compiler messages go to the console at the chosen level. The default shows warnings and errors only.
static void ConfigureLogging(LogEventLevel minimumLevel) =>
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Is(minimumLevel)
        .WriteTo.Console(outputTemplate: "[{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{ExceptionMessage}")
        .CreateLogger();
