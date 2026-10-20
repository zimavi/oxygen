using Xunit;

namespace Oxygen.Cli.Tests;

// Compiles programs whose parameters have default values, runs them, and checks the values they produce when the calls
// omit some arguments and pass others.
public sealed class DefaultParameterIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-defaults", Guid.NewGuid().ToString("N"));

    public DefaultParameterIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [SkippableFact]
    public void OmittedArgumentsTakeTheDefaultsOfFunctionsConstructorsAndMethods()
    {
        NativeProgram.RequireNativeToolchain();
        var run = NativeProgram.BuildAndRun(_directory,
            """
            import std.io;

            class Box {
                i32 value;

                Box(i32 value = 7) {
                    this.value = value;
                }

                i32 get(i32 extra = 100) {
                    return value + extra;
                }
            }

            i32 scale(i32 value, i32 factor = 2) {
                return value * factor;
            }

            i64 widen(i32 a, i64 b = -5) {
                return cast<i64>(a) + b;
            }

            i32 pair(i32 a, i32 b) {
                return a - b;
            }

            i32 pair(i32 a) {
                return a;
            }

            i32 main() {
                io.println(scale(5));
                io.println(scale(5, 3));
                io.println(widen(1));
                io.println(widen(1, 10));
                io.println(new Box().get());
                io.println(new Box(1).get());
                io.println(Box().get(1));
                io.println(pair(9));
                io.println(pair(9, 4));
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("10\n15\n-4\n11\n107\n101\n8\n9\n5\n", run.Output.Replace("\r\n", "\n"));
    }
}
