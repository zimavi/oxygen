using Xunit;

namespace Oxygen.Cli.Tests;

// @noreturn in a native program (docs/exceptions.md).
public sealed class NoReturnIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public NoReturnIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    // A @noreturn function that stops the program never returns, so the program ends with its exit status and the output before it.
    [SkippableFact]
    public void ANoreturnFunctionThatStopsTheProgramEndsItWithItsExitStatus()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            @extern("oxygen_runtime_abort")
            @noreturn
            extern void stop();

            @noreturn
            i32 fail(i32 code) {
                stop();
            }

            i32 main() {
                io.println_char('i');
                return fail(3);
            }
            """);

        Assert.Equal(101, run.ExitCode);
        Assert.Equal("i\n", run.Output.Replace("\r\n", "\n"));
    }

    // std.process.abort is @noreturn, so a @noreturn function may end with it: the program ends with exit code 101 after the output before it.
    [SkippableFact]
    public void TheNoreturnAbortOfStdProcessEndsTheProgram()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.process;

            @noreturn
            i32 fail() {
                io.println_char('i');
                abort();
            }

            i32 main() {
                return fail();
            }
            """);

        Assert.Equal(101, run.ExitCode);
        Assert.Equal("i\n", run.Output.Replace("\r\n", "\n"));
    }

    // A @noreturn function may throw, since the throw leaves the function: its caller catches the exception.
    [SkippableFact]
    public void ANoreturnFunctionMayThrowAndItsCallerCatchesIt()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            @noreturn
            i32 raise() {
                throw new RuntimeException("boom");
            }

            i32 main() {
                try {
                    return raise();
                } catch (RuntimeException error) {
                    return 2;
                }
            }
            """);

        Assert.Equal(2, run.ExitCode);
    }

    // The flag is rejected with a message, and nothing is built.
    [SkippableFact]
    public void TheNoExceptionsFlagIsRejectedAsWorkInProgress()
    {
        NativeProgram.RequireNativeToolchain();
        var sourcePath = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(sourcePath, "i32 main() { return 0; }");

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", sourcePath, "--no-exceptions", "-r", NativeProgram.RuntimeLibrary, "-o", Path.Combine(_directory, "main.exe")]);

        Assert.NotEqual(0, build.ExitCode);
        Assert.Contains("--no-exceptions is not supported yet", build.Error);
        Assert.False(File.Exists(Path.Combine(_directory, "main.exe")));
    }

    // Builds the source as main.oxy, then runs the executable.
    private (int ExitCode, string Output, string Error) BuildAndRun(string source)
    {
        var sourcePath = Path.Combine(_directory, "main.oxy");
        var executable = Path.Combine(_directory, "main.exe");
        File.WriteAllText(sourcePath, source);

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", sourcePath, "-o", executable, "-r", NativeProgram.RuntimeLibrary]);
        Assert.True(build.ExitCode == 0, $"Build failed with exit code {build.ExitCode}:\n{build.Error}");

        return NativeProgram.RunProcess(executable, []);
    }
}
