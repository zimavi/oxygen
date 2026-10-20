using Xunit;

namespace Oxygen.Cli.Tests;

// Compiles programs that pass arguments by name, runs them, and checks the values they produce. Named arguments reach
// free functions, methods, constructors, and constructor initializers.
public sealed class NamedArgumentIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-named", Guid.NewGuid().ToString("N"));

    public NamedArgumentIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [SkippableFact]
    public void NamedArgumentsBindToParametersInAnyOrderAndOmittedOnesTakeTheirDefaults()
    {
        NativeProgram.RequireNativeToolchain();
        var run = NativeProgram.BuildAndRun(_directory,
            """
            import std.io;

            i32 scale(i32 value, i32 factor = 2) {
                return value * factor;
            }

            i32 pair(i32 a, i32 b) {
                return a - b;
            }

            class Box {
                i32 value;
                i32 extra;

                Box(i32 value, i32 extra = 10) {
                    this.value = value;
                    this.extra = extra;
                }

                i32 total(i32 scale = 1) {
                    return value * scale + extra;
                }
            }

            class Child : Box {
                Child(i32 value) : base(extra: 3, value: value) {
                }
            }

            i32 main() {
                io.println(scale(factor: 3, value: 5));
                io.println(scale(value: 4));
                io.println(pair(b: 1, a: 10));
                Box box = new Box(extra: 1, value: 2);
                io.println(box.total(scale: 3));
                io.println(box.total());
                io.println(Box(value: 4).total());
                io.println(new Child(value: 5).total());
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("15\n8\n9\n7\n3\n14\n8\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void ABoundParameterGivenTwiceIsACompileError()
    {
        NativeProgram.RequireNativeToolchain();
        var sourcePath = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(sourcePath,
            """
            i32 scale(i32 value, i32 factor = 2) {
                return value * factor;
            }

            i32 main() {
                return scale(5, value: 4);
            }
            """);

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", sourcePath, "-o", Path.Combine(_directory, "main.exe"), "-r", NativeProgram.RuntimeLibrary]);

        Assert.NotEqual(0, build.ExitCode);
        Assert.Contains("Parameter 'value' of 'scale' is given more than once", build.Output + build.Error);
    }
}
