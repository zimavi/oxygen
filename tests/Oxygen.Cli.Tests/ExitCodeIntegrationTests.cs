using Xunit;

namespace Oxygen.Cli.Tests;

// Builds small programs to native executables and checks the process exit code, the simplest end-to-end behaviour.
public sealed class ExitCodeIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public ExitCodeIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void MainReturnValueBecomesTheExitCode()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun("i32 main() { return 42; }");

        Assert.Equal(42, run.ExitCode);
    }

    [Fact]
    public void LoopAndArithmeticProduceTheExpectedExitCode()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            i32 main() {
                i32 total = 0;
                for (i32 index = 1; index <= 10; index = index + 1) {
                    total = total + index;
                }
                return total;
            }
            """);

        Assert.Equal(55, run.ExitCode);
    }

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
