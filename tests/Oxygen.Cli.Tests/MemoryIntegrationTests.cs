using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that allocate memory and checks how the native executable ends.
public sealed class MemoryIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public MemoryIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void AllocationThatOverflowsTheByteCountStopsTheProgram()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.memory;

            i32 main() {
                i64* values = memory.alloc<i64>(9223372036854775807);
                return 0;
            }
            """);

        Assert.Equal(101, run.ExitCode);
        Assert.Contains("is too large", run.Error);
    }

    [SkippableFact]
    public void AllocationAndReallocationOfSmallArraysSucceed()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.memory;

            i32 main() {
                i64* values = memory.alloc<i64>(2);
                values[0] = 4;
                values = memory.realloc<i64>(values, 4);
                values[3] = 5;
                io.println(values[0] + values[3]);
                memory.free(values);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("9\n", run.Output.Replace("\r\n", "\n"));
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
