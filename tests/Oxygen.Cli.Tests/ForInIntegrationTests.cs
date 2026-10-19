using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that iterate with for-in and checks the output of the native executable.
public sealed class ForInIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public ForInIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void ForInOverAGenericClassUsesTheSpecializedIteratorMethods()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.collection;

            class PairIterator<T> : Iterator<T> {
                private Pair<T> self;
                private u64 pos = 0;

                public PairIterator(Pair<T> self) {
                    this.self = self;
                }

                Option<T> next() {
                    if (pos == 0) {
                        pos = 1;
                        return Option.Some(self.first);
                    }
                    if (pos == 1) {
                        pos = 2;
                        return Option.Some(self.second);
                    }
                    return Option<T>.None;
                }
            }

            class Pair<T> {
                T first;
                T second;

                Pair(T first, T second) {
                    this.first = first;
                    this.second = second;
                }

                PairIterator<T> iter() {
                    return new PairIterator<T>(this);
                }
            }

            i32 main() {
                Pair<i32> pair = Pair<i32>(4, 5);
                for (var value in pair)
                    io.println(value);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("4\n5\n", run.Output.Replace("\r\n", "\n"));
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
