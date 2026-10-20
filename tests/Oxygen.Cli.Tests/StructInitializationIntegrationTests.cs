using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that initialize structs field by field and with `{0}`, runs them, and checks the values that they return.
public sealed class StructInitializationIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public StructInitializationIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void ZeroLiteralAndFieldWritesInitializeEachField()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            struct Point {
                i32 x;
                i32 y;
            }

            i32 main() {
                Point p = {0};
                p.x = 7;
                Point q;
                q.y = 3;
                q.x = 4;
                return p.x + p.y + q.x + q.y;
            }
            """);

        Assert.Equal(14, run.ExitCode);
    }

    [SkippableFact]
    public void ZeroLiteralReassignsANestedStructAfterItsFieldsWerePartlyWritten()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            struct Point {
                i32 x;
                i32 y;
            }

            struct Line {
                Point a;
                Point b;
            }

            i32 main() {
                Line l = {0};
                l.a.x = 5;
                l.b = {0};
                l.b.y = 2;
                return l.a.x * 10 + l.b.y + l.a.y;
            }
            """);

        Assert.Equal(52, run.ExitCode);
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
