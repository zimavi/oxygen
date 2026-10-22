using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that use type aliases, and checks the output of the native executable.
public sealed class TypeAliasIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public TypeAliasIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void AnAliasOfU64IsTheSameTypeInASignature()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            type UserId = u64;

            UserId next(UserId id) {
                return id + 1;
            }

            i32 main() {
                UserId id = 41;
                return cast<i32>(next(id));
            }
            """);

        Assert.Equal(42, run.ExitCode);
    }

    // A class alias names the class, so it constructs an object of the class and calls its methods.
    [SkippableFact]
    public void AClassAliasConstructsTheClass()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            class Counter {
                i32 value;

                public Counter(i32 start) {
                    value = start;
                }

                public i32 get() {
                    return value;
                }
            }

            type Tally = Counter;

            i32 main() {
                Tally tally = new Tally(5);
                return tally.get();
            }
            """);

        Assert.Equal(5, run.ExitCode);
    }

    [SkippableFact]
    public void AnAliasOfAPointerIsAPointer()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            type IntPointer = i32*;

            i32 main() {
                i32 value = 7;
                IntPointer pointer = &value;
                return *pointer;
            }
            """);

        Assert.Equal(7, run.ExitCode);
    }

    // A generic struct instantiated with an alias is the instance for the type that the alias names, so the alias and the target are one type.
    [SkippableFact]
    public void AnAliasIsTheTypeOfAGenericArgument()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            type Count = i32;

            struct Box<T> {
                T item;
            }

            i32 main() {
                Box<Count> box = {0};
                box.item = 9;
                i32 value = box.item;
                return value;
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
