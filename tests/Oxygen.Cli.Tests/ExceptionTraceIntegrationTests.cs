using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that read the stack trace of a caught exception, runs them, and checks what they print. A trace belongs to its catch
// body, the depth of the trace stack is restored by each catch, and the trace keeps every frame of a deep call chain.
public sealed class ExceptionTraceIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public ExceptionTraceIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void CatchBodyReadsItsTraceAndEachCatchRestoresTheDepth()
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
                    middle();
                } catch (RuntimeException e) {
                    io.println(e.stack_trace.count);
                }
                try {
                    inner();
                } catch (RuntimeException e) {
                    io.println(e.stack_trace.count);
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        // main, middle and inner; then main and inner once the first catch has restored the depth.
        Assert.Equal("3\n2\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void DeepRecursionKeepsEveryFrameOfTheTrace()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.exception;

            void deep(i32 n) {
                if (n == 0) {
                    throw new RuntimeException("deep");
                }
                deep(n - 1);
            }

            i32 main() {
                try {
                    deep(300);
                } catch (RuntimeException e) {
                    io.println(e.stack_trace.count);
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        // main and the 301 calls of deep, past the old limit of 256 frames.
        Assert.Equal("302\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void FramesAreNumberedFromTheOutermostCall()
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
                    io.println(e.stack_trace.frame_at(0).function_name);
                    io.println(e.stack_trace.frame_at(1).function_name);
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("main\ninner\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void ReadingAFrameOutsideTheTraceAbortsWithAMessage()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.exception;

            i32 main() {
                try {
                    throw new RuntimeException("x");
                } catch (RuntimeException e) {
                    io.println(e.stack_trace.frame_at(9).function_name);
                }
                return 0;
            }
            """);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("Oxygen stack trace frame 9 is out of bounds for 1 frames", run.Error.Replace("\r\n", "\n"));
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
