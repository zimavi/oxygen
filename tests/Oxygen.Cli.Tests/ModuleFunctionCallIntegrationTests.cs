using Xunit;

namespace Oxygen.Cli.Tests;

// Builds classes whose methods, constructors and destructors call functions of their module, runs them, and checks the values
// that they return.
public sealed class ModuleFunctionCallIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public ModuleFunctionCallIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void MethodsConstructorsAndGenericConstructorsCallFunctionsOfTheirModule()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            i32 twice(i32 x) {
                return x * 2;
            }

            class Plain {
                i32 value;

                Plain(i32 item) {
                    value = twice(item);
                }

                i32 method() {
                    return twice(1);
                }
            }

            class Box<T> {
                T value;

                Box(T item) {
                    value = item;
                }

                i32 scaled() {
                    return twice(2);
                }
            }

            i32 main() {
                Plain p = new Plain(3);
                Box<i32> b = new Box<i32>(4);
                return p.value + p.method() + b.scaled();
            }
            """);

        // twice(3) + twice(1) + twice(2) = 6 + 2 + 4
        Assert.Equal(12, run.ExitCode);
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
