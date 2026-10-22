using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that use generic structs and checks the output of the native executable.
public sealed class GenericStructIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public GenericStructIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    // The type arguments of a field's type come from the struct's use: a field of T in Box<i32> is an i32.
    [SkippableFact]
    public void AFieldOfAGenericStructHasTheTypeOfItsUse()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            struct Box<T> { T item; }

            i32 main() {
                Box<i32> box = {0};
                box.item = 7;
                return box.item;
            }
            """);

        Assert.Equal(7, run.ExitCode);
    }

    // The literal names no type arguments, so they come from the declared type it initializes.
    [SkippableFact]
    public void AGenericStructLiteralTakesItsArgumentsFromTheDeclaredType()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            struct Box<T> { T item; }

            i32 main() {
                Box<i32> box = Box { item: 5 };
                return box.item;
            }
            """);

        Assert.Equal(5, run.ExitCode);
    }

    [SkippableFact]
    public void AGenericStructPassesThroughAParameterAndAReturnValue()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            struct Box<T> { T item; }

            Box<i32> bump(Box<i32> box) {
                return Box { item: box.item + 1 };
            }

            i32 main() {
                Box<i32> box = {0};
                box.item = 4;
                Box<i32> next = bump(box);
                return next.item;
            }
            """);

        Assert.Equal(5, run.ExitCode);
    }

    // Two type parameters, and a narrow field that an integer literal initializes: the literal is converted to the field's type.
    [SkippableFact]
    public void AGenericStructWithTwoParametersAndANarrowFieldIsInitializedByLiterals()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            struct Pair<A, B> { A first; B second; }

            i32 main() {
                Pair<i32, u8> pair = Pair { first: 3, second: 4 };
                return pair.first + pair.second;
            }
            """);

        Assert.Equal(7, run.ExitCode);
    }

    [SkippableFact]
    public void AGenericStructHoldingAGenericStructReadsThroughBoth()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            struct Box<T> { T item; }
            struct Wrap<T> { Box<T> inner; }

            i32 main() {
                Wrap<i32> wrap = Wrap { inner: Box { item: 9 } };
                return wrap.inner.item;
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    // A narrow integer field of a plain struct is initialized by a literal, as a generic struct's narrow field is.
    [SkippableFact]
    public void ANarrowIntegerFieldOfAStructIsInitializedByALiteral()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            struct Pair { i32 first; u8 second; }

            i32 main() {
                Pair pair = Pair { first: 3, second: 4 };
                return pair.first + pair.second;
            }
            """);

        Assert.Equal(7, run.ExitCode);
    }

    // A generic literal names no type arguments, so with no declared type it has none to take.
    [Fact]
    public void AGenericStructLiteralWithNoDeclaredTypeIsAnError()
    {
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source, """
            struct Box<T> { T item; }

            i32 main() {
                return Box { item: 5 }.item;
            }
            """);

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "--no-std", "--emit-ir", Path.Combine(_directory, "main.ll")]);

        Assert.Equal(ExitCodes.SourceError, run.ExitCode);
        Assert.Contains("is generic, so the literal needs a declared type", run.Error);
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
