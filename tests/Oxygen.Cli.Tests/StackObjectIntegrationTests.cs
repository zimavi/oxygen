using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that build objects on the stack and that call functions whose names begin with a capital letter.
public sealed class StackObjectIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public StackObjectIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void StackObjectAndCapitalizedFunctionCallWork()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class Box {
                i32 v;
                Box(i32 x) {
                    v = x;
                }
                i32 get() {
                    return v;
                }
            }

            i32 Make(i32 x) {
                return x + 1;
            }

            i32 main() {
                Box b = Box(7);
                io.println(b.get());
                io.println(Make(3));
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("7\n4\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void StackObjectPassedToAParameterThatOnlyReadsItIsUsedDuringTheCall()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            class Box {
                i32 v = 5;
            }

            i32 read(Box b) {
                return b.v;
            }

            i32 main() {
                return read(Box()) + read(Box());
            }
            """);

        Assert.Equal(10, run.ExitCode);
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
