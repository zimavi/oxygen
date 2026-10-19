using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that use compound assignment and checks the output of the native executable. The target of a compound
// assignment is evaluated once, so an index with side effects advances once.
public sealed class CompoundAssignmentIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public CompoundAssignmentIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void IndexedCompoundAssignmentEvaluatesItsIndexOnce()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 main() {
                i32[2] a = { 1, 2 };
                i32 i = 0;
                a[i++] += 5;
                io.print(i);
                io.print(a[0]);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("16", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void IndexedIncrementStatementEvaluatesItsIndexOnce()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 main() {
                i32[2] b = { 1, 2 };
                i32 j = 0;
                b[j++]++;
                io.print(j);
                io.print(b[0]);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("12", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void CompoundAssignmentsUpdateFieldsPropertiesAndNestedMembers()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class Box {
                i32 value;
            }

            class Counter {
                public i32 Count { get; set; }
            }

            class Holder {
                Box inner;
            }

            i32 main() {
                Box box = new Box();
                box.value = 2;
                box.value *= 3;
                io.print(box.value);

                Counter counter = new Counter();
                counter.Count = 1;
                counter.Count += 4;
                io.print(counter.Count);

                Holder holder = new Holder();
                holder.inner = box;
                holder.inner.value -= 1;
                io.print(holder.inner.value);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("655", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void CompoundShiftAndDivisionUseTheCurrentValueOfTheTarget()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 main() {
                i32 x = 3;
                x <<= 2;
                x -= 1;
                x /= 2;
                io.print(x);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("5", run.Output.Replace("\r\n", "\n"));
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
