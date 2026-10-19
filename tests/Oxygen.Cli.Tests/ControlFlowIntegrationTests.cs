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

    [Fact]
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

    [Fact]
    public void IntegerDivisionByZeroReportsAFailureInsteadOfContinuing()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun("i32 main() { i32 d = 0; return 10 / d; }");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("integer division by zero", run.Error);
    }

    [Fact]
    public void SignedDivisionOverflowIsReported()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun("i32 main() { i32 m = -2147483647 - 1; i32 d = -1; return m / d; }");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("signed integer division overflowed", run.Error);
    }

    [Fact]
    public void ShiftCountOutOfRangeIsReported()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun("i32 main() { i32 s = 40; return 1 << s; }");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("shift count 40 is out of range", run.Error);
    }

    [Fact]
    public void IfWhoseArmsBothReturnRunsTheSelectedArm()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun("i32 main() { i32 x = 1; if (x == 1) { return 7; } else { return 8; } }");

        Assert.Equal(7, run.ExitCode);
    }

    [Fact]
    public void InnerDeclarationShadowsWithoutChangingTheOuterLocal()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun("i32 main() { i32 a = 1; { i32 a = 2; } return a; }");

        Assert.Equal(1, run.ExitCode);
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
