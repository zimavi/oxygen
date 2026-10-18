using System.Diagnostics;
using Xunit;

namespace Oxygen.Cli.Tests;

// Compiles a program against the repository's standard library and runtime, runs the executable, and checks
// what it prints. These tests need clang and the built runtime (runtime/build.ps1).
public sealed class PrintingIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-print", Guid.NewGuid().ToString("N"));

    public PrintingIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void PrintsPrimitiveStringAndUserValuesThroughTheStandardLibrary()
    {
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source,
            """
            import std.io;

            class Point : Printable {
                i32 x;
                i32 y;

                Point(i32 x, i32 y) {
                    this.x = x;
                    this.y = y;
                }

                void write() {
                    io.print(x);
                    io.print(",");
                    io.print(y);
                }
            }

            i32 main() {
                io.println(0);
                io.println(-9223372036854775807 - 1);
                io.println(18446744073709551615);
                io.println(255);
                io.println(0.1 + 0.2);
                io.println(123.456789);
                io.println(-2.5);
                io.println(3.0);
                io.println(0.9999999);
                io.println(cast<f32>(1.5));
                io.println(true);
                io.println("text");
                io.println(new Point(3, -4));
                io.println(cast<f64>(4294967296) * cast<f64>(4294967296) * cast<f64>(4294967296));
                io.println(1.0 / 0.0);
                io.println(-1.0 / 0.0);
                io.println(0.0 / 0.0);
                return 0;
            }
            """);
        var executable = Path.Combine(_directory, "main.exe");

        var build = RunProcess(Path.Combine(AppContext.BaseDirectory, "oxygenc.exe"),
            ["build", source, "-o", executable, "-r", RuntimeLibrary]);
        Assert.True(build.ExitCode == 0, build.Error);

        var run = RunProcess(executable, []);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(
            "0\n-9223372036854775808\n18446744073709551615\n255\n0.3\n123.456789\n-2.5\n3\n1\n1.5\ntrue\ntext\n3,-4\n" +
            "79228162514264337593543950336\ninf\n-inf\nnan\n",
            run.Output.Replace("\r\n", "\n"));
    }

    // The repository root is five levels above the test assembly's output folder (bin/Debug/net10.0).
    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string RuntimeLibrary =>
        Path.Combine(RepositoryRoot, "runtime", "build", "windows", "liboxygenrt.a");

    private static (int ExitCode, string Output, string Error) RunProcess(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        // The compiler looks for the standard library under OXYGEN_PATH when the source is outside the repository.
        startInfo.Environment["OXYGEN_PATH"] = RepositoryRoot;

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }
}
