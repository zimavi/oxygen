using Xunit;

namespace Oxygen.Cli.Tests;

// Compiles programs that call methods on receivers other than variables, such as call results, indexed values and nested
// fields, runs them, and checks the values they produce.
public sealed class ReceiverCallIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-receivers", Guid.NewGuid().ToString("N"));

    public ReceiverCallIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [SkippableFact]
    public void CallsMethodsOnCallResultsAndNestedFields()
    {
        NativeProgram.RequireNativeToolchain();
        var run = NativeProgram.BuildAndRun(_directory,
            """
            import std.io;

            class Inner {
                i32 value;

                Inner(i32 value) {
                    this.value = value;
                }

                i32 get() {
                    return value;
                }

                i32 add(i32 other) {
                    return value + other;
                }
            }

            class Outer {
                Inner inner;

                Outer(i32 value) {
                    this.inner = new Inner(value);
                }
            }

            Inner make(i32 value) {
                return new Inner(value);
            }

            u8 letter() {
                return 'Q';
            }

            i32 main() {
                Outer o = new Outer(7);
                io.println(o.inner.get());
                io.println(o.inner.add(5));
                io.println(make(3).get());
                io.println(make(4).add(make(10).get()));
                io.println(new Inner(9).get());
                io.println((o.inner).get());
                letter().write();
                io.println("");
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("7\n12\n3\n14\n9\n7\n81\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void CallsMethodsOfValueClassesThroughCallResultsAndFields()
    {
        NativeProgram.RequireNativeToolchain();
        var run = NativeProgram.BuildAndRun(_directory,
            """
            import std.io;

            @value class Point {
                i32 x;
                i32 y;

                Point(i32 x, i32 y) {
                    this.x = x;
                    this.y = y;
                }

                i32 sum() {
                    return x + y;
                }
            }

            class Holder {
                Point point;

                Holder() {
                    this.point = new Point(5, 6);
                }
            }

            Point make_point(i32 x) {
                return Point(x, 1);
            }

            i32 main() {
                Holder h = new Holder();
                Point local = Point(2, 3);
                io.println(local.sum());
                io.println(make_point(4).sum());
                io.println(h.point.sum());
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("5\n5\n11\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void RejectsAMethodThatTheReceiverTypeDoesNotHave()
    {
        NativeProgram.RequireNativeToolchain();
        var sourcePath = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(sourcePath,
            """
            class Inner {
                i32 value;

                Inner(i32 value) {
                    this.value = value;
                }
            }

            i32 main() {
                return new Inner(2).missing();
            }
            """);

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", sourcePath, "-o", Path.Combine(_directory, "main.exe"), "-r", NativeProgram.RuntimeLibrary]);

        Assert.NotEqual(0, build.ExitCode);
        Assert.Contains("Method 'missing' is not declared for a receiver of type Inner", build.Output + build.Error);
    }
}
