using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that read the length and the bytes of strings and arrays, and checks the output of the native executable.
public sealed class StringIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public StringIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void StringAndArrayLengthsAndStringBytesAreRead()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 length(i32 x) {
                return x + 1;
            }

            i32 main() {
                string a = "hello";
                i32[3] items = { 1, 2, 3 };
                io.println(cast<i32>(a.length));
                io.println(cast<i32>(items.length));
                io.println(cast<i32>(a[1]));
                io.println(length(4));
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("5\n3\n101\n5\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void StringIndexOutOfRangeStopsTheProgram()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 main() {
                string a = "hey";
                io.println(cast<i32>(a[9]));
                return 0;
            }
            """);

        Assert.Equal(101, run.ExitCode);
        Assert.Contains("Unhandled IndexOutOfRangeException: String index 9 is outside the bounds of the string of length 3.", run.Error + run.Output);
    }

    [SkippableFact]
    public void StringRoundTripsThroughTheCStringConversion()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.memory;
            import std.strings;

            i32 main() {
                i8* copy = to_c_string("round trip");
                io.println(from_c_string(copy));
                memory.free(copy);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("round trip\n", run.Output.Replace("\r\n", "\n"));
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
