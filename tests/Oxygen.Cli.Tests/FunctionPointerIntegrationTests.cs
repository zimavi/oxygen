using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that use function pointers and lambdas, and checks the output of the native executable.
public sealed class FunctionPointerIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public FunctionPointerIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void AFunctionPointerLocalIsCalled()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            i32 add(i32 a, i32 b) {
                return a + b;
            }

            i32 main() {
                fn i32 op(i32, i32) = &add;
                return op(1, 2);
            }
            """);

        Assert.Equal(3, run.ExitCode);
    }

    [SkippableFact]
    public void AFunctionPointerParameterReceivesAFunctionOrALambda()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            i32 add(i32 a, i32 b) {
                return a + b;
            }

            i32 apply(fn i32 op(i32, i32), i32 a, i32 b) {
                return op(a, b);
            }

            i32 main() {
                i32 sum = apply(&add, 1, 2);
                i32 product = apply(fn i32 (i32 x, i32 y) { return x * y; }, 3, 4);
                return sum + product;
            }
            """);

        Assert.Equal(15, run.ExitCode);
    }

    [SkippableFact]
    public void AFunctionPointerFieldIsCalledThroughItsReceiver()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            i32 sub(i32 a, i32 b) {
                return a - b;
            }

            class Calculator {
                fn i32 op(i32, i32) = &sub;
            }

            i32 main() {
                Calculator calc = new Calculator();
                return calc.op(9, 4);
            }
            """);

        Assert.Equal(5, run.ExitCode);
    }

    [SkippableFact]
    public void AnArrayOfFunctionPointersIsCalledByIndex()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            i32 add(i32 a, i32 b) {
                return a + b;
            }

            i32 sub(i32 a, i32 b) {
                return a - b;
            }

            i32 main() {
                fn i32 ops[2](i32, i32) = { &add, &sub };
                return ops[1](5, 3);
            }
            """);

        Assert.Equal(2, run.ExitCode);
    }

    [SkippableFact]
    public void AModuleFunctionPointerStartsAtItsFunction()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            i32 add(i32 a, i32 b) {
                return a + b;
            }

            fn i32 handler(i32, i32) = &add;

            i32 main() {
                return handler(4, 5);
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    // A null function pointer that is called throws NullReferenceException, as a null pointer that is dereferenced does.
    [SkippableFact]
    public void ACallThroughANullFunctionPointerThrowsNullReferenceException()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            // The pointer is a parameter, so the analysis cannot prove it null, and the call checks it at run time.
            i32 call(fn i32 op(i32)) {
                return op(1);
            }

            i32 main() {
                try {
                    return call(null);
                } catch (NullReferenceException error) {
                    return 9;
                }
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    // A generic function takes a function pointer whose signature names its type parameter.
    [SkippableFact]
    public void AGenericFunctionTakesAFunctionPointerOfItsTypeParameter()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            i32 inc(i32 x) {
                return x + 1;
            }

            T twice<T>(T value, fn T step(T)) {
                return step(step(value));
            }

            i32 main() {
                return twice<i32>(5, &inc);
            }
            """);

        Assert.Equal(7, run.ExitCode);
    }

    // An overloaded name passed as an argument takes the overload that the parameter's function pointer type names.
    [SkippableFact]
    public void AnOverloadedAddressIsChosenByTheParameterItIsPassedTo()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            i32 pick(i32 a) {
                return a + 100;
            }

            i32 pick(bool b) {
                return 0;
            }

            i32 apply(fn i32 op(i32), i32 value) {
                return op(value);
            }

            i32 main() {
                return apply(&pick, 3);
            }
            """);

        Assert.Equal(103, run.ExitCode);
    }

    // `&first<i32>` is the address of one instance of a generic function, and its signature is the function pointer type.
    [SkippableFact]
    public void AGenericFunctionAddressIsTakenWithItsTypeArguments()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            T first<T>(T a, T b) {
                return a;
            }

            i32 main() {
                fn i32 op(i32, i32) = &first<i32>;
                return op(2, 3);
            }
            """);

        Assert.Equal(2, run.ExitCode);
    }

    // A lambda in a generic function is copied for each instance, so each instance's lambda has the type arguments of that instance.
    [SkippableFact]
    public void ALambdaInAGenericFunctionHasTheTypeArgumentsOfEachInstance()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            T pass<T>(T value) {
                fn T step(T) = fn T (T x) { return x; };
                return step(value);
            }

            i32 main() {
                if (pass<bool>(true)) {
                    return pass<i32>(6);
                }
                return 0;
            }
            """);

        Assert.Equal(6, run.ExitCode);
    }

    // The generic function takes a function pointer for each case, and each signature names the type parameter of the union.
    [SkippableFact]
    public void AGenericMatchTakesAFunctionPointerForEachCaseOfAUnion()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            union Maybe<T> {
                Some(T);
                None;
            }

            void match<T>(Maybe<T> option, fn void some(T), fn void none()) {
                switch (option) {
                    case Some(item): { some(item); break; }
                    case None: none();
                }
            }

            i32 main() {
                Maybe<i32> present = Maybe.Some(7);
                Maybe<i32> absent = Maybe.None();
                match<i32>(present, fn void (i32 value) { io.print("some "); }, fn void () { io.print("none "); });
                match<i32>(absent, fn void (i32 value) { io.print("some "); }, fn void () { io.print("none "); });
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("some none ", run.Output.Replace("\r\n", "\n"));
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
