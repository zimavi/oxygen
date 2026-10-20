using Xunit;

namespace Oxygen.Cli.Tests;

// Compiles a program whose generic uses take their type arguments from their arguments and the expected type, runs it, and
// checks what it prints.
public sealed class InferenceIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-inference", Guid.NewGuid().ToString("N"));

    public InferenceIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [SkippableFact]
    public void InfersTypeArgumentsFromArgumentsAndExpectedTypes()
    {
        NativeProgram.RequireNativeToolchain();
        var run = NativeProgram.BuildAndRun(_directory,
            """
            import std.io;
            import std.memory;
            import std.collection;

            class Empty : Iterator<i32> {
                Option<i32> next() {
                    return Option.None();
                }
            }

            class Items : Iterable<i32> {
                Iterator<i32> iter() {
                    return new Empty();
                }
            }

            class Wrap<T> {
                T value;

                Wrap(T value) {
                    this.value = value;
                }

                T get() {
                    return value;
                }
            }

            class Animal {
                i32 legs;

                Animal(i32 legs) {
                    this.legs = legs;
                }
            }

            class Dog : Animal {
                Dog() : base(4) {
                }
            }

            class Cat : Animal {
                Cat() : base(4) {
                }
            }

            T pick<T>(T first, T second) {
                return first;
            }

            Option<i32> none() {
                return Option.None();
            }

            i32 main() {
                Items items = new Items();
                Option<Iterable<i32>> widened = Option.Some(items);
                var named = Option.Some("hello");
                io.println(Wrap(4).get());
                io.println(new Wrap(5).get());
                io.println(Wrap<i64>(6).get());
                Animal a = pick(new Dog(), new Cat());
                io.println(a.legs);
                i64* values = memory.alloc<i64>(2);
                values = memory.realloc(values, 4);
                memory.free(values);
                io.println("done");
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("4\n5\n6\n4\ndone\n", run.Output.Replace("\r\n", "\n"));
    }
}
