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

    // The iteration variable can be declared with the element type, as well as with var.
    [SkippableFact]
    public void ForInWithAnExplicitElementTypeIteratesTheArrayElements()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 main() {
                i32[3] values = { 1, 2, 3 };
                i32 total = 0;
                for (i32 value in values) {
                    total += value;
                }
                io.print(total);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("6", run.Output.Replace("\r\n", "\n"));
    }

    // An explicit type is not converted, so an iteration variable of another type is an error, as an implicit conversion would be.
    [Fact]
    public void ForInWithAnExplicitTypeThatIsNotTheElementTypeIsAnError()
    {
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source, """
            i32 main() {
                i32[2] values = { 1, 2 };
                for (i64 value in values) {
                }
                return 0;
            }
            """);

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable, ["build", source, "--no-std", "--emit-ir", Path.Combine(_directory, "main.ll")]);

        Assert.Equal(ExitCodes.SourceError, run.ExitCode);
        Assert.Contains("has type i64, but the elements are i32", run.Error);
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
