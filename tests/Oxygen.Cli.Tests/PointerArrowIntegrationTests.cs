using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that use `->` on a pointer to a struct or a class, and checks the output of the native executable.
public sealed class PointerArrowIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public PointerArrowIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void AnArrowReadsWritesAndCompoundAssignsAStructField()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            struct Point {
                i32 x;
            }

            i32 main() {
                Point point = {0};
                Point* p = &point;
                p->x = 5;
                p->x += 2;
                p->x++;
                return p->x;
            }
            """);

        Assert.Equal(8, run.ExitCode);
    }

    // `p->x` and `(*p).x` are the same member, so a write through one is read through the other.
    [SkippableFact]
    public void AnArrowAndAnExplicitDereferenceReadAndWriteTheSameField()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            struct Point {
                i32 x;
            }

            i32 main() {
                Point point = {0};
                Point* p = &point;
                p->x = 3;
                (*p).x = (*p).x + 4;
                return p->x;
            }
            """);

        Assert.Equal(7, run.ExitCode);
    }

    [SkippableFact]
    public void AnArrowCallsMethodsOfAClass()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            class Counter {
                i32 value;

                public void add(i32 amount) {
                    value = value + amount;
                }

                public i32 get() {
                    return value;
                }
            }

            i32 main() {
                Counter counter = new Counter();
                Counter* p = &counter;
                p->add(4);
                p->add(3);
                return p->get();
            }
            """);

        Assert.Equal(7, run.ExitCode);
    }

    [SkippableFact]
    public void AnArrowSetsAndReadsAPropertyOfAClass()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            class Holder {
                public i32 Count { get; set; }
            }

            i32 main() {
                Holder holder = new Holder();
                Holder* p = &holder;
                p->Count = 4;
                p->Count += 1;
                return p->Count;
            }
            """);

        Assert.Equal(5, run.ExitCode);
    }

    // An arrow chains through pointer fields: `o->inner->x` reads `inner` as a pointer, then `x` through it.
    [SkippableFact]
    public void AnArrowChainsThroughPointerFields()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            struct Inner {
                i32 x;
            }

            struct Outer {
                Inner* inner;
            }

            i32 main() {
                Inner inner = {0};
                Outer outer = {0};
                outer.inner = &inner;
                Outer* o = &outer;
                o->inner->x = 9;
                return inner.x;
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    // A field of a value class is part of the value of its variable, so the write goes to the variable through the pointer.
    [SkippableFact]
    public void AnArrowWritesAFieldOfAValueClassThroughThePointer()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            @value class Point {
                i32 x;
            }

            i32 main() {
                var point = Point();
                Point* p = &point;
                p->x = 6;
                return point.x;
            }
            """);

        Assert.Equal(6, run.ExitCode);
    }

    // A pointer to a null class reference reaches the class's member access, which throws NullReferenceException. The exception is
    // caught, so the test does not depend on how an uncaught one ends the process. A null pointer itself is not checked, so it is not used.
    [SkippableFact]
    public void AnArrowThroughAPointerToANullClassReferenceThrowsNullReference()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            class Counter {
                public i32 get() {
                    return 0;
                }
            }

            i32 read(Counter* p) {
                return p->get();
            }

            i32 main() {
                Counter counter = null;
                try {
                    return read(&counter);
                } catch (NullReferenceException error) {
                    return 9;
                }
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
