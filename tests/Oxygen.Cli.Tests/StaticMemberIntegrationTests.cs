using Xunit;

namespace Oxygen.Cli.Tests;

// Compiles programs with static fields and static methods, runs them, and checks the values they produce. A static field is one
// value per class, or per instance of a generic class; a static method has no receiver.
public sealed class StaticMemberIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-static", Guid.NewGuid().ToString("N"));

    public StaticMemberIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [SkippableFact]
    public void StaticFieldsAndMethodsAreSharedByTheClassAndItsInstances()
    {
        NativeProgram.RequireNativeToolchain();
        var run = NativeProgram.BuildAndRun(_directory,
            """
            import std.io;

            class Counter {
                static i32 count = 0;
                static const i32 limit = 3;
                i32 id;

                Counter() {
                    Counter.count = Counter.count + 1;
                    this.id = Counter.count;
                }

                static i32 next(i32 step = 1) {
                    count = count + step;
                    return count;
                }

                i32 get() {
                    return id;
                }
            }

            class Box<T> {
                static i32 made = 0;

                static i32 bump() {
                    made = made + 1;
                    return made;
                }
            }

            i32 main() {
                Counter a = new Counter();
                Counter b = new Counter();
                io.println(Counter.count);
                io.println(a.get());
                io.println(b.get());
                io.println(Counter.next());
                io.println(Counter.next(10));
                io.println(Counter.limit);
                io.println(Box<i32>.bump());
                io.println(Box<string>.bump());
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("2\n1\n2\n3\n13\n3\n1\n1\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void StaticMembersOfValueClassesAndPerInstanceFieldsOfGenericClasses()
    {
        NativeProgram.RequireNativeToolchain();
        var run = NativeProgram.BuildAndRun(_directory,
            """
            import std.io;

            @value class Point {
                static i32 made = 2;
                i32 x;

                Point(i32 x) {
                    this.x = x;
                }

                i32 scaled() {
                    return x * Point.factor();
                }

                static i32 factor() {
                    return made;
                }
            }

            class Box<T> {
                static i32 touches = 0;

                i32 touch() {
                    touches = touches + 1;
                    return touches;
                }

                static i32 total() {
                    return touches;
                }

                static i32 twice() {
                    return Box.total() * 2;
                }
            }

            i32 main() {
                Point p = Point(5);
                io.println(p.scaled());
                io.println(Point.factor());
                Box<i32> a = new Box<i32>();
                Box<i32> b = new Box<i32>();
                Box<string> c = new Box<string>();
                io.println(a.touch());
                io.println(b.touch());
                io.println(c.touch());
                io.println(Box<i32>.total());
                io.println(Box<string>.total());
                io.println(Box<i32>.twice());
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("10\n2\n1\n2\n1\n2\n1\n4\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void BaseClassStaticMembersAreReachedThroughASubclassNameAndAnObjectOfIt()
    {
        NativeProgram.RequireNativeToolchain();
        var run = NativeProgram.BuildAndRun(_directory,
            """
            import std.io;

            class Base {
                static i32 n = 4;

                static i32 twice() {
                    return n * 2;
                }
            }

            class Child : Base {
            }

            i32 main() {
                Child c = new Child();
                io.println(Child.n);
                io.println(c.twice());
                c.n = 7;
                io.println(Base.n);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("4\n8\n7\n", run.Output.Replace("\r\n", "\n"));
    }
}
