using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that declare module-level variables and checks the output of the native executable.
public sealed class GlobalVariableIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public GlobalVariableIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void GlobalVariablesHoldTheirInitialValuesAndKeepChangesAcrossCalls()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 counter = 10;
            const i32 step = 5;
            f64 scale = 2.5;
            bool enabled = true;
            i32 zeroed;
            i64 wide = -3;

            void bump() {
                counter += step;
            }

            i32 main() {
                bump();
                bump();
                io.println(counter);
                io.println(zeroed);
                io.println(cast<i32>(scale * 2.0));
                if (enabled) {
                    io.println(1);
                }
                io.println(cast<i32>(wide));
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("20\n0\n5\n1\n-3\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void LocalVariableShadowsTheGlobalOnlyWithinItsScope()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 value = 7;

            i32 main() {
                io.println(value);
                i32 value = 3;
                io.println(value);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("7\n3\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void AddressOfAGlobalWritesThroughToTheGlobal()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 counter = 1;

            i32 main() {
                i32* pointer = &counter;
                *pointer = 9;
                io.println(counter);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("9\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void MethodOfAGenericClassReadsTheGlobalsOfItsModule()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 offset = 40;

            class Boxed<T> {
                T value;

                Boxed(T value) {
                    this.value = value;
                }

                i32 adjusted() {
                    return offset + 2;
                }
            }

            i32 main() {
                Boxed<i32> box = new Boxed<i32>(1);
                io.println(box.adjusted());
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("42\n", run.Output.Replace("\r\n", "\n"));
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
