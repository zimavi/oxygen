using System.Diagnostics;
using Xunit;

namespace Oxygen.Cli.Tests;

// Runs a program whose reader of standard output goes away while it is still writing. The runtime must report the failed write
// and stop with the runtime exit code, instead of carrying on as if the output had been delivered.
public sealed class StandardOutputIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public StandardOutputIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void OutputIsWrittenAsBytesWithoutCarriageReturnsAndKeepsUtf8()
    {
        NativeProgram.RequireNativeToolchain();

        var run = NativeProgram.BuildAndRun(_directory,
            """
            import std.io;

            i32 main() {
                io.println("a");
                io.println("é");
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("a\né\n", run.Output);
    }

    [SkippableFact]
    public void AFailedWriteToStandardOutputStopsTheProgramWithExitCode101()
    {
        NativeProgram.RequireNativeToolchain();

        var sourcePath = Path.Combine(_directory, "main.oxy");
        var executable = Path.Combine(_directory, "main.exe");
        File.WriteAllText(sourcePath,
            """
            import std.io;

            i32 main() {
                i32 i = 0;
                while (i < 100000) {
                    io.println(i);
                    i = i + 1;
                }
                return 0;
            }
            """);
        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", sourcePath, "-o", executable, "-r", NativeProgram.RuntimeLibrary]);
        Assert.True(build.ExitCode == 0, $"Build failed with exit code {build.ExitCode}:\n{build.Error}");

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo)!;
        // Closing our end of the pipe makes the program's next write fail. The program writes 100000 lines, so it reaches a failed write.
        process.StandardOutput.BaseStream.Close();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60000), "The program did not stop after its standard output was closed.");

        Assert.Equal(101, process.ExitCode);
        Assert.Contains("Oxygen could not write to standard output", error.Replace("\r\n", "\n"));
    }
}
