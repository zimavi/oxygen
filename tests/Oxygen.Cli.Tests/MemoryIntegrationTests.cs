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

    [SkippableFact]
    public void CallocZeroesMemoryAndTheByteOperationsCopyMoveAndFill()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.memory;

            i32 main() {
                i32* zeros = memory.calloc<i32>(3);
                io.println(zeros[0] + zeros[1] + zeros[2]);
                i32* first = memory.alloc<i32>(3);
                first[0] = 4;
                first[1] = 5;
                first[2] = 6;
                i32* second = memory.calloc<i32>(3);
                memory.memcpy(cast<void*>(second), cast<void*>(first), 12);
                io.println(second[2]);
                memory.memmove(cast<void*>(first + 1), cast<void*>(first), 8);
                io.println(first[2]);
                memory.memset(cast<void*>(second), 0, 12);
                io.println(second[0]);
                memory.free(first);
                memory.free(second);
                memory.free(zeros);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        // zeroed sum, the copied last element, the overlapping move's last element, and the cleared first element
        Assert.Equal("0\n6\n5\n0\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void AllocOfZeroElementsReturnsAPointerThatIsFreedNormally()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.memory;

            i32 main() {
                i32* empty = memory.alloc<i32>(0);
                if (empty == null) {
                    io.println(1);
                } else {
                    io.println(2);
                }
                memory.free(empty);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("2\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void CallocOfAnOverflowingCountStopsTheProgram()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.memory;

            i32 main() {
                i64* values = memory.calloc<i64>(9223372036854775807);
                return 0;
            }
            """);

        Assert.Equal(101, run.ExitCode);
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
