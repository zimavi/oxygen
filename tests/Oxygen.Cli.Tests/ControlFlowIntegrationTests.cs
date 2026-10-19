using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs whose behaviour is defined by the language rules for control flow, arithmetic checks, string literals
// and exceptions, and checks what the native executable does.
public sealed class ControlFlowIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public ControlFlowIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void ShortCircuitOperatorsSkipTheRightOperandWhenTheLeftDecides()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            i32 main() {
                i32 n = 0;
                if (n != 0 && 10 / n > 1) {
                    return 1;
                }
                if (n == 0 || 5 / n > 0) {
                    return 4;
                }
                return 5;
            }
            """);

        Assert.Equal(4, run.ExitCode);
    }

    [SkippableFact]
    public void IntegerDivisionByZeroReportsAFailureInsteadOfContinuing()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun("i32 main() { i32 d = 0; return 10 / d; }");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("integer division by zero", run.Error);
    }

    [SkippableFact]
    public void SignedDivisionOverflowIsReported()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun("i32 main() { i32 m = -2147483647 - 1; i32 d = -1; return m / d; }");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("signed integer division overflowed", run.Error);
    }

    [SkippableFact]
    public void ShiftCountOutOfRangeIsReported()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun("i32 main() { i32 s = 40; return 1 << s; }");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("shift count 40 is out of range", run.Error);
    }

    [SkippableFact]
    public void IfWhoseArmsBothReturnRunsTheSelectedArm()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun("i32 main() { i32 x = 1; if (x == 1) { return 7; } else { return 8; } }");

        Assert.Equal(7, run.ExitCode);
    }

    [SkippableFact]
    public void InnerDeclarationShadowsWithoutChangingTheOuterLocal()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun("i32 main() { i32 a = 1; { i32 a = 2; } return a; }");

        Assert.Equal(1, run.ExitCode);
    }

    [SkippableFact]
    public void StringSwitchMatchesOnlyTheWholeLabel()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            i32 main() {
                string s = "ab";
                switch (s) {
                    case "abc": return 1;
                    case "a": return 2;
                    case "ab": return 3;
                    default: return 4;
                }
            }
            """);

        Assert.Equal(3, run.ExitCode);
    }

    [SkippableFact]
    public void StringLiteralsKeepEmbeddedNulAndDecodeEscapes()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 main() {
                io.print("a\u0000b|");
                io.print("tab\there \"q\" \\ é\n");
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("a\0b|tab\there \"q\" \\ é\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void FinallyRunsOnNormalCompletionAndBeforeAReturnLeavesTheTry()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 f() {
                try {
                    return 1;
                } finally {
                    io.print("F");
                }
            }

            i32 main() {
                io.print(f());
                try {
                    io.print("B");
                } finally {
                    io.print("G");
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("F1BG", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void FinallyRunsAfterACaughtExceptionsCatchBody()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.exception;

            class E : Exception {
                E(string message) : base(message) {
                }
            }

            i32 main() {
                try {
                    io.print("T");
                    throw new E("x");
                } catch (E error) {
                    io.print("C");
                } finally {
                    io.print("F");
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("TCF", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void ReturnFromACatchBodyRunsFinallyFirst()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.exception;

            class E : Exception {
                E(string message) : base(message) {
                }
            }

            i32 g() {
                try {
                    throw new E("x");
                } catch (E error) {
                    return 5;
                } finally {
                    io.print("F");
                }
            }

            i32 main() {
                io.print(g());
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("F5", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void UncaughtExceptionIsReportedWithItsMessage()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.exception;

            class E : Exception {
                E(string message) : base(message) {
                }
            }

            i32 main() {
                io.print("before");
                throw new E("boom");
            }
            """);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Equal("before", run.Output);
        Assert.Contains("Unhandled E: boom", run.Error);
    }

    [SkippableFact]
    public void ReturnInsideAConstructorEndsTheConstructorBody()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class A {
                i32 value;

                A(i32 x) {
                    this.value = x;
                    return;
                }
            }

            i32 main() {
                A b = new A(3);
                io.print(b.value);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("3", run.Output);
    }

    [SkippableFact]
    public void StringEqualityComparesBytesAndLength()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            i32 main() {
                string a = "ab";
                string b = "ab";
                string c = "abc";
                if (a == b && a != c && !(a == c) && c != b) {
                    return 1;
                }
                return 2;
            }
            """);

        Assert.Equal(1, run.ExitCode);
    }

    [SkippableFact]
    public void DeleteRunsTheDestructorBeforeFreeingTheObject()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class Box {
                i32 value = 3;

                ~Box() {
                    io.print("destroyed");
                }
            }

            i32 main() {
                Box box = new Box();
                io.print(box.value);
                delete box;
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("3destroyed", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void AutomaticFunctionFreesAnAllocationWhenItsScopeEnds()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class Box {
                i32 value = 3;

                ~Box() {
                    io.print("gone");
                }
            }

            void f() {
                Box box = new Box();
                io.print(box.value);
            }

            i32 main() {
                f();
                io.print("end");
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("3goneend", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void DeletingABaseTypedReferenceRunsTheDerivedDestructor()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class Base {
                ~Base() {
                    io.print("base");
                }
            }

            class Derived : Base {
                ~Derived() {
                    io.print("derived");
                }
            }

            i32 main() {
                Base item = new Derived();
                delete item;
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("derived", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void RuntimeCheckedDeleteReleasesAnObjectOnlyOnce()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class Box {
                i32 value = 1;

                ~Box() {
                    io.print("d");
                }
            }

            i32 main() {
                Box b = new Box();
                i32 c = 1;
                if (c == 1) {
                    delete b;
                }
                delete b;
                io.print("end");
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("dend", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void ScopeEndFreeSkipsAnObjectDeletedOnThatPath()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class Box {
                i32 value = 1;

                ~Box() {
                    io.print("d");
                }
            }

            void f(i32 c) {
                Box b = new Box();
                if (c == 1) {
                    delete b;
                }
            }

            i32 main() {
                f(1);
                io.print("a");
                f(0);
                io.print("b");
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("dadb", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void SizeOfIncludesPadding()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            struct Pair {
                i8 a;
                i64 b;
            }

            i32 main() {
                return cast<i32>(sizeof<Pair>());
            }
            """);

        Assert.Equal(16, run.ExitCode);
    }

    [SkippableFact]
    public void UnsignedIntegersConvertToFloatingPointAsUnsignedValues()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 main() {
                u32 value = 4294967295;
                io.print(cast<i64>(cast<f64>(value)));
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("4294967295", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void WhileLoopRunsContinueAndBreakInTheRightPlaces()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 main() {
                i32 value = 0;
                i32 total = 0;
                while (value < 10) {
                    value = value + 1;
                    if (value == 2) {
                        continue;
                    }
                    if (value == 5) {
                        break;
                    }
                    total = total + value;
                }
                io.print(total);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("8", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void ForLoopRunsItsIncrementAfterContinue()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 main() {
                i32 total = 0;
                for (i32 index = 0; index < 4; index = index + 1) {
                    if (index == 1) {
                        continue;
                    }
                    total = total + index;
                }
                io.print(total);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("5", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void SwitchCaseFallsThroughUntilABreak()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 main() {
                i32 value = 1;
                switch (value) {
                    case 1: { io.print("a"); }
                    case 2: { io.print("b"); break; }
                    case 3: { io.print("c"); }
                    default: { io.print("d"); }
                }
                switch (value) {
                    case 1: break;
                    default: return 2;
                }
                io.print("!");
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("ab!", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void ConditionalExpressionEvaluatesOnlyTheChosenArm()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 main() {
                i32 n = 2;
                i32 zero = 0;
                io.print(n == 2 ? 10 : 1 / zero);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("10", run.Output.Replace("\r\n", "\n"));
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
