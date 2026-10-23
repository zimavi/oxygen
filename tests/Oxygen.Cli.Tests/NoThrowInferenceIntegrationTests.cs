using Xunit;

namespace Oxygen.Cli.Tests;

// Inferred @nothrow in a native program (docs/exceptions.md): an inferred body is nothrow only when it cannot throw, so a catch in the caller
// still sees every exception that can come out of a call.
public sealed class NoThrowInferenceIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public NoThrowInferenceIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    // The base method cannot throw, so it is inferred nothrow, but its override throws. The call dispatches to the override, so the catch runs.
    [SkippableFact]
    public void ACatchSeesTheExceptionOfAnOverrideThatADispatchedCallReaches()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            class Base {
                virtual i32 value() {
                    return 1;
                }
            }

            class Derived : Base {
                override i32 value() {
                    throw new Exception("boom");
                }
            }

            i32 read(Base b) {
                return b.value();
            }

            i32 main() {
                try {
                    return read(new Derived());
                } catch (Exception error) {
                    return 10;
                }
            }
            """);

        Assert.Equal(10, run.ExitCode);
    }

    // An inferred function is called and returns its value.
    [SkippableFact]
    public void AnInferredFunctionReturnsItsValue()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            i32 add(i32 a, i32 b) {
                return a + b;
            }

            i32 main() {
                return add(1, 2);
            }
            """);

        Assert.Equal(3, run.ExitCode);
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
