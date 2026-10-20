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

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
