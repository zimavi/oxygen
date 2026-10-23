using Xunit;

namespace Oxygen.Cli.Tests;

// A @nothrow constructor in a native program (docs/exceptions.md).
public sealed class NoThrowConstructorIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public NoThrowConstructorIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    // A @nothrow function creates an object with a @nothrow constructor, and the constructor's effect is what the function returns.
    [SkippableFact]
    public void ANothrowFunctionCreatesAnObjectWithANothrowConstructor()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            class Box {
                i32 value;

                @nothrow
                public Box(i32 start) {
                    value = start;
                }
            }

            @nothrow
            i32 make(i32 start) {
                Box box = new Box(start);
                return box.value;
            }

            i32 main() {
                return make(7);
            }
            """);

        Assert.Equal(7, run.ExitCode);
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
