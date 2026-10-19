using Xunit;

namespace Oxygen.Cli.Tests;

// Compiles programs whose constructors call `this(...)` or `base(...)`, runs them, and checks the order in which
// constructors initialize the object and the values they produce.
public sealed class ConstructorChainingIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-chaining", Guid.NewGuid().ToString("N"));

    public ConstructorChainingIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void RunsBaseAndThisConstructorsBeforeDerivedInitialization()
    {
        NativeProgram.RequireNativeToolchain();
        var run = BuildAndRun(
            """
            import std.io;

            class Base {
                i32 value = 1;

                Base() {
                    io.println("base default");
                    value = value + 10;
                }

                Base(i32 extra) : this() {
                    io.println("base extra");
                    value = value + extra;
                }
            }

            class Derived : Base {
                i32 own = 100;

                Derived(i32 extra) : base(extra) {
                    io.println("derived");
                    own = own + value;
                }
            }

            class Plain : Base {
                i32 x = 3;
            }

            i32 main() {
                Derived derived = new Derived(5);
                Plain plain = new Plain();
                return derived.own + plain.value;
            }
            """);

        Assert.Equal(127, run.ExitCode);
        Assert.Equal("base default\nbase extra\nderived\nbase default\n", run.Output.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ExceptionSubclassesPassMessagesThroughStandardConstructors()
    {
        NativeProgram.RequireNativeToolchain();
        var run = BuildAndRun(
            """
            import std.io;
            import std.exception;

            class MyError : Exception {
                i32 code = 7;

                MyError(string message, i32 code) : base(message) {
                    this.code = code;
                }
            }

            class Plain : Exception {
                i32 extra = 2;
            }

            i32 main() {
                MyError error = new MyError("boom", 3);
                io.println(error.message);
                Plain plain = new Plain();
                io.println(plain.message);
                return error.code + plain.extra;
            }
            """);

        Assert.Equal(5, run.ExitCode);
        Assert.Equal("boom\n\n", run.Output.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ChainsGenericBaseConstructorsAndSelectsOverloadsByArgumentType()
    {
        NativeProgram.RequireNativeToolchain();
        var run = BuildAndRun(
            """
            class Boxed<T> {
                T value;

                Boxed(T value) {
                    this.value = value;
                }
            }

            class Box<T> : Boxed<T> {
                i32 tag = 5;

                Box(T value) : base(value) {
                }

                T get() {
                    return value;
                }
            }

            @value class Point {
                i32 x;
                i32 y;

                Point() : this(1, 2) {
                }

                Point(i32 x, i32 y) {
                    this.x = x;
                    this.y = y;
                }
            }

            class Item {
                i32 v;

                Item(i32 a) {
                    v = a;
                }

                Item(string s) {
                    v = 20;
                }
            }

            i32 main() {
                Box<i32> box = new Box<i32>(4);
                Point point = Point();
                Item item = new Item("x");
                return box.get() + box.tag + point.x + point.y + item.v;
            }
            """);

        Assert.Equal(32, run.ExitCode);
    }

    private (int ExitCode, string Output) BuildAndRun(string source)
    {
        var sourcePath = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(sourcePath, source);
        var executable = Path.Combine(_directory, "main.exe");

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", sourcePath, "-o", executable, "-r", NativeProgram.RuntimeLibrary]);
        Assert.True(build.ExitCode == 0, build.Error);

        var run = NativeProgram.RunProcess(executable, []);
        return (run.ExitCode, run.Output);
    }
}
