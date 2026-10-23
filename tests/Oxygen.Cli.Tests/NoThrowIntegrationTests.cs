using Xunit;

namespace Oxygen.Cli.Tests;

// The behaviour of @nothrow in a native program (docs/exceptions.md).
public sealed class NoThrowIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public NoThrowIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    // A runtime failure in a @nothrow function stops the program, so the catch in its caller does not see it.
    [SkippableFact]
    public void ARuntimeFailureInANothrowFunctionIsNotCaughtByItsCaller()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            @nothrow
            i32 pick(i32 index) {
                i32[3] values = {1, 2, 3};
                return values[index];
            }

            i32 main() {
                try {
                    return pick(7);
                } catch (IndexOutOfRangeException error) {
                    return 9;
                }
            }
            """);

        Assert.Equal(101, run.ExitCode);
        Assert.Contains("array index 7 is out of bounds for length 3", run.Error.Replace("\r\n", "\n"));
    }

    // The same failure outside a @nothrow function is an exception, and the catch handles it.
    [SkippableFact]
    public void ARuntimeFailureOutsideANothrowFunctionIsCaught()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            i32 pick(i32 index) {
                i32[3] values = {1, 2, 3};
                return values[index];
            }

            i32 main() {
                try {
                    return pick(7);
                } catch (IndexOutOfRangeException error) {
                    return 9;
                }
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    // The standard library's print_char and math functions are @nothrow, so a @nothrow function may call them.
    [SkippableFact]
    public void ANothrowFunctionMayUseTheNothrowStandardLibraryFunctions()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.math;

            @nothrow
            f64 scaled(f64 value) {
                io.println_char('x');
                return math.max(math.abs(value), 1.5);
            }

            i32 main() {
                f64 result = scaled(-2.0);
                if (result == 2.0) {
                    return 3;
                }
                return 4;
            }
            """);

        Assert.Equal(3, run.ExitCode);
        Assert.Equal("x\n", run.Output.Replace("\r\n", "\n"));
    }

    // A lambda written in a @nothrow function keeps its own unwinding: its catch runs when the lambda is called later.
    [SkippableFact]
    public void ALambdaWrittenInANothrowFunctionKeepsItsOwnCatch()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            i32 helper() {
                throw new Exception("x");
            }

            fn i32 saved();

            @nothrow
            void store() {
                saved = fn i32 () {
                    try {
                        return helper();
                    } catch (Exception error) {
                        return 5;
                    }
                };
            }

            i32 main() {
                store();
                return saved();
            }
            """);

        Assert.Equal(5, run.ExitCode);
    }

    // A runtime failure is still caught when the last function of the program is @nothrow, since the failure's helper is not a @nothrow body.
    [SkippableFact]
    public void AFailureIsCaughtWhenANothrowFunctionIsTheLastOne()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            i32 pick(i32 index) {
                i32[3] values = {1, 2, 3};
                return values[index];
            }

            i32 main() {
                try {
                    return pick(7);
                } catch (IndexOutOfRangeException error) {
                    return 9;
                }
            }

            @nothrow
            i32 last() {
                return 1;
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    // Builds the source as main.oxy, then runs the executable.
    private (int ExitCode, string Output, string Error) BuildAndRun(string source)
    {
        var sourcePath = Path.Combine(_directory, "main.oxy");
        var executable = Path.Combine(_directory, "main.exe");
        File.WriteAllText(sourcePath, source);

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", sourcePath, "-o", executable, "-r", NativeProgram.RuntimeLibrary]);
        Assert.True(build.ExitCode == 0, $"Build failed with exit code {build.ExitCode}:\n{build.Error}");

        return NativeProgram.RunProcess(executable, []);
    }
}
