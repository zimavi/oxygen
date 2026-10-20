using Xunit;

namespace Oxygen.Cli.Tests;

// Builds generic classes whose properties have bodies, runs them, and checks the values that they produce. Each instantiation has
// its own copy of the accessor bodies.
public sealed class GenericPropertyIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public GenericPropertyIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void GetterAndSetterOfEachInstantiationUseItsOwnType()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            class Box<T> {
                T item;

                T Value {
                    get { return item; }
                    set { item = value; }
                }
            }

            i32 main() {
                Box<i32> a = new Box<i32>();
                a.Value = 42;
                Box<i64> b = new Box<i64>();
                b.Value = 8;
                return a.Value + cast<i32>(b.Value);
            }
            """);

        Assert.Equal(50, run.ExitCode);
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
