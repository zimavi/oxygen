using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs whose array literals take their element type from the declared array type, and checks the output of
// the native executable. Each element is converted to that type, so an i32 literal can fill an i64 or f64 array.
public sealed class ArrayLiteralIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public ArrayLiteralIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void ElementsConvertToTheDeclaredElementType()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 main() {
                i64[2] wide = { 7, 8 };
                f64[2] half = { 1, 2 };
                io.print(cast<i32>(wide[0] + wide[1]));
                io.print(cast<i32>(half[1] * 10.0));
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("1520", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void ArrayLiteralReturnedFromAFunctionTakesTheReturnElementType()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i64[2] pair() {
                return { 3, 4 };
            }

            i32 main() {
                i64[2] values = pair();
                io.print(cast<i32>(values[0] * values[1]));
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("12", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void ArrayIndexOutOfRangeKeepsTheArrayMessage()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 main() {
                i32[3] values = { 1, 2, 3 };
                i32 index = 9;
                io.println(values[index]);
                return 0;
            }
            """);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("Oxygen array index 9 is out of bounds for length 3", run.Error + run.Output);
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
