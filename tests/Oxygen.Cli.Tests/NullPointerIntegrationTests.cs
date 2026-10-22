using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that dereference a null pointer, and checks that the dereference throws NullReferenceException, which a catch handles,
// and that a program without std.exception stops with exit code 101.
public sealed class NullPointerIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public NullPointerIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void ANullPointerReadIsCaughtAsANullReferenceException()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            i32 read(i32* p) {
                return *p;
            }

            i32 main() {
                try {
                    return read(null);
                } catch (NullReferenceException error) {
                    return 9;
                }
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    [SkippableFact]
    public void ANullPointerWriteIsCaughtAsANullReferenceException()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            void write(i32* p) {
                *p = 3;
            }

            i32 main() {
                try {
                    write(null);
                } catch (NullReferenceException error) {
                    return 9;
                }
                return 0;
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    [SkippableFact]
    public void ANullPointerIndexIsCaughtAsANullReferenceException()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            i32 read(i32* p) {
                return p[1];
            }

            i32 main() {
                try {
                    return read(null);
                } catch (NullReferenceException error) {
                    return 9;
                }
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    // `p->x` and `(*p).x` dereference the same null pointer, so both are caught the same way.
    [SkippableFact]
    public void AnArrowAndAnExplicitDereferenceOfANullStructPointerAreCaughtTheSameWay()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.exception;

            struct Point {
                i32 x;
            }

            i32 viaArrow(Point* p) {
                return p->x;
            }

            i32 viaDereference(Point* p) {
                return (*p).x;
            }

            i32 main() {
                i32 caught = 0;
                try {
                    viaArrow(null);
                } catch (NullReferenceException error) {
                    caught = caught + 1;
                }
                try {
                    viaDereference(null);
                } catch (NullReferenceException error) {
                    caught = caught + 10;
                }
                return caught;
            }
            """);

        Assert.Equal(11, run.ExitCode);
    }

    // Without std.exception there is no exception to throw, so the program stops, as a null class reference stops it.
    [SkippableFact]
    public void AnUncaughtNullPointerWithoutStdStopsTheProgramWithExitCode101()
    {
        NativeProgram.RequireNativeToolchain();

        var source = Path.Combine(_directory, "main.oxy");
        var executable = Path.Combine(_directory, "main.exe");
        File.WriteAllText(source, """
            i32 read(i32* p) {
                return *p;
            }

            i32 main() {
                return read(null);
            }
            """);

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "--no-std", "-o", executable, "-r", NativeProgram.RuntimeLibrary]);
        Assert.True(build.ExitCode == 0, $"Build failed with exit code {build.ExitCode}:\n{build.Error}");

        var run = NativeProgram.RunProcess(executable, []);

        Assert.Equal(101, run.ExitCode);
        Assert.Contains("null pointer", run.Error);
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
