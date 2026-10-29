using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that use property accessors and checks the output of the native executable.
public sealed class PropertyIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public PropertyIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void BodiedAccessorsReadFieldsAndSetterRunsOnAssignment()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class Sink {
                private i32 stored = 7;
                public i32 Total { get { return stored; } set { stored = stored + value; } }
            }

            i32 main() {
                Sink s = new Sink();
                io.println(s.Total);
                s.Total = 4;
                io.println(s.Total);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("7\n11\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void InitOnlyPropertyIsSetInItsConstructor()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class Config {
                public i32 Level { get; init; }
                Config() {
                    Level = 3;
                }
            }

            i32 main() {
                Config c = new Config();
                io.println(c.Level);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("3\n", run.Output.Replace("\r\n", "\n"));
    }

    // A value class passes `this` by value, so its accessors get the receiver as a copy: the getter reads the stored value through
    // `this`, and a write through `this` compiles and runs. Whether a write reaches the caller is open (see the value class rules).
    [SkippableFact]
    public void BodiedPropertyOfAValueClassIsReadThroughThis()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            @value class Box {
                i32 v;
                public Box(i32 x) { v = x; }
                public i32 value {
                    get { return v; }
                    set { v = value; }
                }
                public i32 read() { return this.value; }
                public void write() { this.value = 9; }
            }

            i32 main() {
                Box b = Box(3);
                b.write();
                io.println(b.read());
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("3\n", run.Output.Replace("\r\n", "\n"));
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
