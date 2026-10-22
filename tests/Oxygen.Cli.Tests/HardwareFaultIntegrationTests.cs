using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that fault in hardware (an access violation, a stack overflow) inside try statements, and checks how the native
// executable ends. A hardware fault is a runtime failure on Windows: no catch clause or finally block runs for it.
public sealed class HardwareFaultIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public HardwareFaultIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    // A null pointer is checked before it is dereferenced (see NullPointerIntegrationTests), so this write is to an address that is not
    // null and not mapped, which the hardware reports as a fault.
    [SkippableFact]
    public void AWriteThroughAnUnmappedPointerIsNotCaughtAndStopsTheProgram()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Hardware faults are reported by the Windows fault handler.");
        NativeProgram.RequireNativeToolchain();

        var run = NativeProgram.BuildAndRun(_directory,
            """
            import std.io;

            i32 main() {
                try {
                    i32* p = cast<i32*>(cast<usize>(8));
                    p[0] = 5;
                    io.println(0);
                } catch {
                    io.println(1);
                } finally {
                    io.println(3);
                }
                io.println(2);
                return 0;
            }
            """);

        Assert.Equal(101, run.ExitCode);
        Assert.Equal(string.Empty, run.Output);
        Assert.Contains("Oxygen stopped after a hardware fault", run.Error);
    }

    [SkippableFact]
    public void AStackOverflowInsideATryIsNotCaughtAndStopsTheProgram()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Hardware faults are reported by the Windows fault handler.");
        NativeProgram.RequireNativeToolchain();

        var run = NativeProgram.BuildAndRun(_directory,
            """
            import std.io;

            i32 down(i32 n) {
                return down(n + 1) + 1;
            }

            i32 main() {
                try {
                    io.println(down(0));
                } catch {
                    io.println(1);
                }
                io.println(2);
                return 0;
            }
            """, optimization: "0");

        // The standard error is part of the message, so a run that fails in CI shows what the fault handler wrote before it stopped.
        Assert.True(run.ExitCode == 101,
            $"Expected exit code 101, got {run.ExitCode}. The standard error of the run was:\n{run.Error}");
        Assert.Equal(string.Empty, run.Output);
        Assert.Contains("Oxygen stopped after a hardware fault", run.Error);
    }
}
