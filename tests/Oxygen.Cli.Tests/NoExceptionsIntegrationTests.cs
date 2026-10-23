using Xunit;

namespace Oxygen.Cli.Tests;

// A program built with --no-exceptions, run natively (docs/exceptions.md): a throw ends it, a catch clause never runs, and finally runs
// when the body returns. A throw from a library built with exceptions is in LibraryDependencyTests.
public sealed class NoExceptionsIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public NoExceptionsIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    // The throw ends the program with exit code 101, after the output before it. The report names the exception, and says that no stack
    // trace was kept, since a program without exceptions has no trace stack.
    [SkippableFact]
    public void AThrowEndsTheProgram()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.exception;

            i32 main() {
                io.println_char('i');
                throw new Exception("boom");
            }
            """);

        Assert.Equal(101, run.ExitCode);
        Assert.Equal("i\n", run.Output.Replace("\r\n", "\n"));
        Assert.Contains("boom", run.Error);
        Assert.Contains("(no stack trace: the program was built with --no-exceptions)", run.Error);
    }

    // The catch clause is not emitted, so the throw is not caught: the program ends with exit code 101 and the catch's value is never returned.
    [SkippableFact]
    public void ACatchClauseNeverRuns()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            i32 main() {
                try {
                    throw new Exception("boom");
                } catch (Exception error) {
                    return 9;
                }
            }
            """);

        Assert.Equal(101, run.ExitCode);
    }

    // A finally clause runs when its body returns, because the body is an ordinary block without exceptions.
    [SkippableFact]
    public void AFinallyClauseRunsWhenItsBodyReturns()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 main() {
                try {
                    return 4;
                } finally {
                    io.println_char('f');
                }
            }
            """);

        Assert.Equal(4, run.ExitCode);
        Assert.Equal("f\n", run.Output.Replace("\r\n", "\n"));
    }

    // Builds the source as main.oxy with --no-exceptions, then runs the executable.
    private (int ExitCode, string Output, string Error) BuildAndRun(string source)
    {
        var sourcePath = Path.Combine(_directory, "main.oxy");
        var executable = Path.Combine(_directory, "main.exe");
        File.WriteAllText(sourcePath, source);

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", sourcePath, "--no-exceptions", "-o", executable, "-r", NativeProgram.RuntimeLibrary]);
        Assert.True(build.ExitCode == 0, $"Build failed with exit code {build.ExitCode}:\n{build.Error}");

        return NativeProgram.RunProcess(executable, []);
    }
}
