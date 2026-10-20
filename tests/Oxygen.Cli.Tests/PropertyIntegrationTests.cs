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

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
