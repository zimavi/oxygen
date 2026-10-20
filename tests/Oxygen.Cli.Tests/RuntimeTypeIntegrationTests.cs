using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that check class and interface types at run time and checks how the native executable behaves.
public sealed class RuntimeTypeIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public RuntimeTypeIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void IsFindsEveryBaseClassAndAnInterfaceImplementedByAnAncestor()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            interface Named {
                i32 id();
            }

            class Base : Named {
                i32 id() { return 5; }
            }

            class Middle : Base {
            }

            class Leaf : Middle {
            }

            class Other : Named {
                i32 id() { return 6; }
            }

            i32 main() {
                Base b = new Leaf();
                if (b is Leaf) {
                    io.println(1);
                }
                if (b is Named) {
                    io.println(2);
                }
                Named n = b;
                io.println(n.id());
                Middle m = cast<Middle>(b);
                if (m is Leaf) {
                    io.println(3);
                }
                Middle plain = new Middle();
                if (plain is Leaf) {
                    io.println(4);
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("1\n2\n5\n3\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void CheckedCastToAnUnrelatedClassStopsTheProgram()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class Base { }
            class Leaf : Base { }
            class Other : Base { }

            i32 main() {
                Base b = new Other();
                Leaf leaf = cast<Leaf>(b);
                io.println(1);
                return 0;
            }
            """);

        Assert.Equal(101, run.ExitCode);
        Assert.Equal(string.Empty, run.Output);
        Assert.Contains("cannot cast", run.Error);
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
