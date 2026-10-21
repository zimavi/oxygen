using Xunit;

namespace Oxygen.Cli.Tests;

// Runs the compiler's command line: the version, the logging options, and the option that keeps intermediate files.
public sealed class CommandLineTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public CommandLineTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void VersionPrintsTheCompilerVersion()
    {
        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable, ["--version"]);

        Assert.Equal(0, run.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(run.Output));
    }

    [Fact]
    public void LogLevelIsValidatedAgainstTheLevelNames()
    {
        var source = WriteSource("i32 main() { return 0; }");

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "--emit-ir", Path.Combine(_directory, "program.ll"), "--log-level", "Loud"]);

        Assert.Equal(ExitCodes.SourceError, run.ExitCode);
        Assert.Contains("Loud", run.Output + run.Error);
    }

    [Fact]
    public void VerboseIsAcceptedForABuild()
    {
        var source = WriteSource("i32 main() { return 0; }");

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "--emit-ir", Path.Combine(_directory, "program.ll"), "--verbose"]);

        Assert.Equal(0, run.ExitCode);
    }

    [SkippableFact]
    public void KeepTempsKeepsTheIntermediateFilesAndNamesTheirDirectory()
    {
        NativeProgram.RequireNativeToolchain();
        var source = WriteSource("i32 main() { return 0; }");

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "-o", Path.Combine(_directory, "program.exe"), "-r", NativeProgram.RuntimeLibrary, "--keep-temps"]);

        Assert.Equal(0, run.ExitCode);
        var line = run.Error.Split('\n').Single(text => text.Contains("keeping the intermediate files in"));
        var directory = line.Split('\'')[1];
        try
        {
            Assert.True(File.Exists(Path.Combine(directory, "program.ll")));
            Assert.True(File.Exists(Path.Combine(directory, "program.o")));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private string WriteSource(string text)
    {
        var path = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(path, text);
        return path;
    }
}
