using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs whose caught exceptions are released when their catch ends, and checks the output of the native executable. A
// destructor of the exception class prints when the object is destroyed, so the output shows when the object is freed.
public sealed class CaughtExceptionIntegrationTests : IDisposable
{
    private const string FailureClass = """
        class Failure : RuntimeException {
            Failure(string message) : base(message) {}

            ~Failure() {
                io.print("destroyed");
            }
        }
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public CaughtExceptionIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void ACaughtExceptionIsDestroyedAtTheEndOfItsCatch()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            $$"""
            import std.io;
            import std.exception;

            {{FailureClass}}

            i32 main() {
                try {
                    throw new Failure("boom");
                } catch (Failure error) {
                    io.print("caught ");
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("caught destroyed", run.Output.Replace("\r\n", "\n"));
    }

    // A return from the catch releases the exception too, since the body's exits all release the binding.
    [SkippableFact]
    public void AReturnFromACatchReleasesTheException()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            $$"""
            import std.io;
            import std.exception;

            {{FailureClass}}

            i32 main() {
                try {
                    throw new Failure("boom");
                } catch (Failure error) {
                    io.print("caught ");
                    return 3;
                }
                return 0;
            }
            """);

        Assert.Equal(3, run.ExitCode);
        Assert.Equal("caught destroyed", run.Output.Replace("\r\n", "\n"));
    }

    // Each iteration catches and releases its own exception.
    [SkippableFact]
    public void ExceptionsCaughtInALoopAreReleasedEachTime()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            $$"""
            import std.io;
            import std.exception;

            {{FailureClass}}

            i32 main() {
                for (i32 i = 0; i < 3; i++) {
                    try {
                        throw new Failure("again");
                    } catch (Failure error) {
                        io.print("c");
                    }
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("cdestroyedcdestroyedcdestroyed", run.Output.Replace("\r\n", "\n"));
    }

    // A catch that stores its exception in a variable of an outer scope keeps the exception, so the catch does not free it.
    [SkippableFact]
    public void AnExceptionKeptOutsideItsCatchIsNotReleasedByTheCatch()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            $$"""
            import std.io;
            import std.exception;

            {{FailureClass}}

            i32 main() {
                Failure kept;
                try {
                    throw new Failure("boom");
                } catch (Failure error) {
                    kept = error;
                    io.print("caught ");
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("caught ", run.Output.Replace("\r\n", "\n"));
    }

    // A rethrow keeps the exception, so the inner catch does not release it. The outer catch, which ends the exception, releases it once.
    [SkippableFact]
    public void ARethrownExceptionIsReleasedOnceByTheCatchThatEndsIt()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            $$"""
            import std.io;
            import std.exception;

            {{FailureClass}}

            i32 main() {
                try {
                    try {
                        throw new Failure("boom");
                    } catch (Failure inner) {
                        io.print("inner ");
                        throw inner;
                    }
                } catch (Failure outer) {
                    io.print("outer ");
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("inner outer destroyed", run.Output.Replace("\r\n", "\n"));
    }

    // A failure of the runtime is released with its object and its text, so a loop that makes many of them runs to the end.
    [SkippableFact]
    public void ManyRuntimeFailuresCaughtInALoopAreReleased()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            i32 main() {
                i32[2] values = {1, 2};
                i32 total = 0;
                for (i32 i = 0; i < 1000; i++) {
                    try {
                        total = total + values[i + 2];
                    } catch (IndexOutOfRangeException error) {
                        total = total + 1;
                    }
                }
                return total % 256;
            }
            """);

        Assert.Equal(232, run.ExitCode);
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
