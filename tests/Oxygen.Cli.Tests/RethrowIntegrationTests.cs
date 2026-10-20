using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that rethrow inside a catch clause and checks the output of the native executable.
public sealed class RethrowIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public RethrowIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void RethrowInsideACatchReachesTheOuterCatch()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.exception;

            class E : Exception {
                E(string message) : base(message) {
                }
            }

            i32 main() {
                try {
                    try {
                        throw new E("inner");
                    } catch (E error) {
                        io.println(1);
                        throw;
                    }
                } catch (E error) {
                    io.println(2);
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("1\n2\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void ThrowInsideACatchKeepsTheOriginalTraceAndMessage()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.exception;

            void inner() {
                throw new RuntimeException("boom");
            }

            void middle() {
                inner();
            }

            i32 main() {
                try {
                    try {
                        middle();
                    } catch (RuntimeException e) {
                        io.println(e.stack_trace.count);
                        throw;
                    }
                } catch (RuntimeException e) {
                    io.println(e.stack_trace.count);
                    io.println(e.message);
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        // The outer catch sees the trace of the original throw, main, middle and inner, not a new capture at the rethrow.
        Assert.Equal("3\n3\nboom\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void FinallyRunsWhileAThrowInsideACatchUnwinds()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.exception;

            i32 main() {
                try {
                    try {
                        throw new RuntimeException("x");
                    } catch (RuntimeException e) {
                        io.println(1);
                        throw;
                    } finally {
                        io.println(2);
                    }
                } catch (RuntimeException e) {
                    io.println(3);
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("1\n2\n3\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void UncaughtThrowInsideACatchReportsTheOriginalTrace()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.exception;

            void inner() {
                throw new RuntimeException("boom");
            }

            i32 main() {
                try {
                    inner();
                } catch (RuntimeException e) {
                    io.println("caught");
                    throw;
                }
                return 0;
            }
            """);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("Unhandled RuntimeException: boom", run.Error.Replace("\r\n", "\n"));
        Assert.Contains("at inner", run.Error);
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
