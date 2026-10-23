using Xunit;

namespace Oxygen.Cli.Tests;

// @throws in a native program (docs/exceptions.md): a function declared to throw throws to its caller, which catches the exception.
public sealed class ThrowsIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public ThrowsIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void ACallerCatchesTheExceptionOfAThrowsFunction()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            @throws
            i32 risky() {
                throw new Exception("boom");
            }

            i32 main() {
                try {
                    return risky();
                } catch (Exception error) {
                    return 7;
                }
            }
            """);

        Assert.Equal(7, run.ExitCode);
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
