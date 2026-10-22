using Xunit;

namespace Oxygen.Cli.Tests;

// The behaviour of @nothrow in a native program (docs/exceptions.md). The --no-exceptions flag is rejected until its feature is finished.
public sealed class NoThrowIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public NoThrowIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    // A runtime failure in a @nothrow function stops the program, so the catch in its caller does not see it.
    [SkippableFact]
    public void ARuntimeFailureInANothrowFunctionIsNotCaughtByItsCaller()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            @nothrow
            i32 pick(i32 index) {
                i32[3] values = {1, 2, 3};
                return values[index];
            }

            i32 main() {
                try {
                    return pick(7);
                } catch (IndexOutOfRangeException error) {
                    return 9;
                }
            }
            """);

        Assert.Equal(101, run.ExitCode);
        Assert.Contains("array index 7 is out of bounds for length 3", run.Error.Replace("\r\n", "\n"));
    }

    // The same failure outside a @nothrow function is an exception, and the catch handles it.
    [SkippableFact]
    public void ARuntimeFailureOutsideANothrowFunctionIsCaught()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            i32 pick(i32 index) {
                i32[3] values = {1, 2, 3};
                return values[index];
            }

            i32 main() {
                try {
                    return pick(7);
                } catch (IndexOutOfRangeException error) {
                    return 9;
                }
            }
            """);

        Assert.Equal(9, run.ExitCode);
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
