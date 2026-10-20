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

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
