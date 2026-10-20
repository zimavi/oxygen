using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that use the optional and result unions of std.collection, and checks the output of the native executable.
public sealed class CollectionIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public CollectionIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void OptionQueriesAndDefaultsReadTheCaseThatIsPresent()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.collection;

            i32 main() {
                Option<i32> some = Option.Some(4);
                Option<i32> none = Option.None();
                io.println(collection.is_some(some));
                io.println(collection.is_none(none));
                io.println(collection.unwrap_or(some, 9));
                io.println(collection.unwrap_or(none, 9));
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("true\ntrue\n4\n9\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void ResultQueriesAndDefaultsReadTheCaseThatIsPresent()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.collection;

            i32 main() {
                Result<i32, string> ok = Result.Ok(7);
                Result<i32, string> bad = Result.Err("no");
                io.println(collection.is_ok(ok));
                io.println(collection.is_err(bad));
                io.println(collection.is_err(ok));
                io.println(collection.unwrap_or(ok, 3));
                io.println(collection.unwrap_or(bad, 3));
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("true\ntrue\nfalse\n7\n3\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void ResultCasesWithDifferentPayloadTypesEachKeepTheirOwnValue()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.collection;

            void describe(Result<i32, string> value) {
                switch (value) {
                    case Ok(number): { io.println(number); break; }
                    case Err(message): { io.println(message); break; }
                }
            }

            i32 main() {
                describe(Result<i32, string>.Ok(42));
                describe(Result<i32, string>.Err("failed"));
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("42\nfailed\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void UnwrapReturnsTheValueOfASomeOrOkCase()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.collection;

            i32 main() {
                io.println(collection.unwrap(Option.Some(4)));
                io.println(collection.unwrap(Result<i32, string>.Ok(7)));
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("4\n7\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void UnwrapOfAnAbsentValueThrowsARuntimeExceptionThatACatchHandles()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.collection;
            import std.exception;

            i32 main() {
                Option<i32> none = Option.None();
                Result<i32, string> bad = Result<i32, string>.Err("no");
                try {
                    io.println(collection.unwrap(none));
                } catch (RuntimeException error) {
                    io.println(error.message);
                }
                try {
                    io.println(collection.unwrap(bad));
                } catch (RuntimeException error) {
                    io.println(error.message);
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("unwrap called on None\nunwrap called on Err\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void UnwrapOfANoneThatNothingCatchesEndsTheProgramWithItsMessage()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.collection;

            i32 main() {
                Option<i32> none = Option.None();
                io.println(collection.unwrap(none));
                return 0;
            }
            """);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("Unhandled RuntimeException: unwrap called on None", run.Error.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void ListGrowsWhenFullAndReadsByIndexAndPop()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.collection;

            i32 main() {
                List<i32> items = new List<i32>();
                i32 n = 0;
                while (n < 10) {
                    items.append(n * 10);
                    n = n + 1;
                }
                io.println(items.length);
                io.println(items.capacity);
                io.println(collection.unwrap_or(items.get(9), -1));
                io.println(collection.unwrap_or(items.get(10), -1));
                io.println(collection.unwrap_or(items.pop(), -1));
                io.println(items.length);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        // Capacity doubles from 4 to 8 to 16 as the tenth value is added.
        Assert.Equal("10\n16\n90\n-1\n90\n9\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void ForInVisitsEveryValueOfAListInOrder()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.collection;

            i32 main() {
                List<i32> items = new List<i32>();
                items.append(3);
                items.append(4);
                items.append(5);
                for (var value in items) {
                    io.println(value);
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("3\n4\n5\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void PopOfAnEmptyListAndGetPastTheEndAreNone()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.collection;

            i32 main() {
                List<string> names = new List<string>();
                io.println(collection.is_none(names.pop()));
                io.println(collection.is_none(names.get(0)));
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("true\ntrue\n", run.Output.Replace("\r\n", "\n"));
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
