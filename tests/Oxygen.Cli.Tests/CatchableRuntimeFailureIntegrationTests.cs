using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs whose runtime failures are exceptions that a catch handles, and checks the output of the native executable.
public sealed class CatchableRuntimeFailureIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public CatchableRuntimeFailureIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void AnArrayIndexOutOfBoundsIsCaughtAsIndexOutOfRangeException()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            i32 main() {
                i32[2] values = {1, 2};
                i32 index = 5;
                try {
                    return values[index];
                } catch (IndexOutOfRangeException error) {
                    return 9;
                }
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    [SkippableFact]
    public void AStringIndexOutOfBoundsIsCaughtAsIndexOutOfRangeException()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            i32 main() {
                string text = "ab";
                i32 index = 2;
                try {
                    return cast<i32>(text[index]);
                } catch (IndexOutOfRangeException error) {
                    return 9;
                }
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    [SkippableFact]
    public void IntegerDivisionByZeroIsCaughtAsDivideByZeroException()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            i32 divide(i32 value, i32 divisor) {
                return value / divisor;
            }

            i32 main() {
                try {
                    return divide(10, 0);
                } catch (DivideByZeroException error) {
                    return 9;
                }
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    // The failure classes derive from RuntimeException, so a catch for the base class handles them too.
    [SkippableFact]
    public void ARuntimeExceptionCatchHandlesAnIndexFailure()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            i32 main() {
                i32[2] values = {1, 2};
                i32 index = 3;
                try {
                    return values[index];
                } catch (RuntimeException error) {
                    return 9;
                }
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    [SkippableFact]
    public void AShiftOutOfRangeIsCaughtAsRuntimeException()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            i32 main() {
                i32 count = 40;
                try {
                    return 1 << count;
                } catch (RuntimeException error) {
                    return 9;
                }
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    [SkippableFact]
    public void AnInvalidEnumValueIsCaughtAsRuntimeException()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            enum Color { Red, Green }

            i32 main() {
                i32 value = 7;
                try {
                    Color color = cast<Color>(value);
                    return 0;
                } catch (RuntimeException error) {
                    return 9;
                }
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    // Uncaught, the failure reports its class and a message that shows the value, and the program ends with the runtime's exit code.
    [SkippableFact]
    public void AnUncaughtInvalidEnumValueReportsItsValue()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            enum Color { Red, Green }

            i32 main() {
                i32 value = 7;
                Color color = cast<Color>(value);
                return 0;
            }
            """);

        Assert.Equal(101, run.ExitCode);
        Assert.Contains("Unhandled RuntimeException: The value 7 is not a case of the enum.", run.Error);
    }

    [SkippableFact]
    public void AnInvalidCastIsCaughtAsInvalidCastException()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            class Base { }
            class Leaf : Base { }
            class Other : Base { }

            i32 main() {
                Base b = new Other();
                try {
                    Leaf leaf = cast<Leaf>(b);
                    return 0;
                } catch (InvalidCastException error) {
                    return 9;
                }
            }
            """);

        Assert.True(run.ExitCode == 9, $"Exit code {run.ExitCode}:\n{run.Error}");
    }

    // Uncaught, the failure reports its class and the two types, and the program ends with the runtime's exit code.
    [SkippableFact]
    public void AnUncaughtInvalidCastReportsTheSourceAndTargetTypes()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            class Base { }
            class Leaf : Base { }
            class Other : Base { }

            i32 main() {
                Base b = new Other();
                Leaf leaf = cast<Leaf>(b);
                return 0;
            }
            """);

        Assert.Equal(101, run.ExitCode);
        Assert.Contains("Unhandled InvalidCastException: Cannot cast an object of type", run.Error);
        Assert.Contains("to program.Leaf, because it is not an instance of program.Leaf.", run.Error);
    }

    // A null reference is not an instance of any class, so casting it fails, and the message says that it is null.
    [SkippableFact]
    public void ACastOfANullReferenceFailsWithAMessageThatSaysItIsNull()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            class Base { }
            class Leaf : Base { }

            i32 main() {
                Base b = null;
                Leaf leaf = cast<Leaf>(b);
                return 0;
            }
            """);

        Assert.Equal(101, run.ExitCode);
        Assert.Contains("Unhandled InvalidCastException: Cannot cast a null reference to program.Leaf.", run.Error);
    }

    // The failure happens in a function that the try calls, so the exception unwinds the call and reaches the catch.
    [SkippableFact]
    public void AFailureInACalledFunctionUnwindsToTheCatch()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            i32 read(i32[2] values, i32 index) {
                return values[index];
            }

            i32 main() {
                i32[2] values = {1, 2};
                try {
                    return read(values, 4);
                } catch (IndexOutOfRangeException error) {
                    return 9;
                }
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
