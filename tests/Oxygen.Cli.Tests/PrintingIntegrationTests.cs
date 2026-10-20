using Xunit;

namespace Oxygen.Cli.Tests;

// Compiles a program against the repository's standard library and runtime, runs the executable, and checks
// what it prints. These tests need clang and the built runtime (runtime/build.ps1).
public sealed class PrintingIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-print", Guid.NewGuid().ToString("N"));

    public PrintingIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [SkippableFact]
    public void PrintsPrimitiveStringAndUserValuesThroughTheStandardLibrary()
    {
        NativeProgram.RequireNativeToolchain();
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

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "-o", executable, "-r", NativeProgram.RuntimeLibrary]);
        Assert.True(build.ExitCode == 0, build.Error);

        var run = NativeProgram.RunProcess(executable, []);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(
            "0\n-9223372036854775808\n18446744073709551615\n255\n0.3\n123.456789\n-2.5\n3\n1\n1.5\ntrue\ntext\n3,-4\n" +
            "79228162514264337593543950336\ninf\n-inf\nnan\n",
            run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void PrintsBytesAsCharactersOrAsNumbers()
    {
        NativeProgram.RequireNativeToolchain();
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source,
            """
            import std.io;

            i32 main() {
                io.println_char('!');
                io.print(97u8);
                io.print_char(' ');
                io.println_char('a');
                io.print_char('\'');
                io.println_char('"');
                return 0;
            }
            """);
        var executable = Path.Combine(_directory, "main.exe");

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "-o", executable, "-r", NativeProgram.RuntimeLibrary]);
        Assert.True(build.ExitCode == 0, build.Error);

        var run = NativeProgram.RunProcess(executable, []);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("!\n97 a\n'\"\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void PrintsTheSignOfNegativeZeroAndOfValuesThatRoundToZeroAndSpecialFloats()
    {
        NativeProgram.RequireNativeToolchain();
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source,
            """
            import std.io;

            i32 main() {
                f64 zero = 0.0;
                f64 negativeZero = zero * -1.0;
                io.println(negativeZero);
                io.println(-0.0000001);
                io.println(-2.5);
                io.println(zero);
                io.println(zero / zero);
                io.println(1.0 / zero);
                return 0;
            }
            """);
        var executable = Path.Combine(_directory, "main.exe");

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "-o", executable, "-r", NativeProgram.RuntimeLibrary]);
        Assert.True(build.ExitCode == 0, build.Error);

        var run = NativeProgram.RunProcess(executable, []);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("-0\n-0\n-2.5\n0\nnan\ninf\n", run.Output.Replace("\r\n", "\n"));
    }

    // Printing builds its digits in local arrays, so the printing functions must not call the allocator. The check reads the
    // emitted IR, because the program's output does not show allocations.
    [SkippableFact]
    public void PrintingNumbersDoesNotAllocate()
    {
        NativeProgram.RequireNativeToolchain();
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source,
            """
            import std.io;

            i32 main() {
                io.println(12345);
                io.println(-7);
                io.println(2.5);
                io.println(1e300);
                return 0;
            }
            """);
        var executable = Path.Combine(_directory, "main.exe");
        var ir = Path.Combine(_directory, "main.ll");

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "-o", executable, "-r", NativeProgram.RuntimeLibrary]);
        Assert.True(build.ExitCode == 0, build.Error);
        // --emit-ir writes the IR without linking, so it is a second build.
        var emit = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "-o", Path.Combine(_directory, "ir.exe"), "-r", NativeProgram.RuntimeLibrary, "--emit-ir", ir]);
        Assert.True(emit.ExitCode == 0, emit.Error);

        var lines = File.ReadAllLines(ir);
        foreach (var name in new[] { "write_unsigned", "write_exact_integer", "write_float" })
        {
            var body = FunctionBody(lines, name);
            Assert.NotEmpty(body);
            Assert.DoesNotContain(body, line => line.Contains("oxygen_alloc", StringComparison.Ordinal)
                || line.Contains("oxygen_calloc", StringComparison.Ordinal)
                || line.Contains("oxygen_realloc", StringComparison.Ordinal));
        }

        var run = NativeProgram.RunProcess(executable, []);
        Assert.Equal(0, run.ExitCode);
        var printed = run.Output.Replace("\r\n", "\n").Split('\n');
        Assert.Equal(["12345", "-7", "2.5"], printed[..3]);
        // 1e300 is an integer-valued double of more than 2^64, written exactly: 301 digits.
        Assert.Equal(301, printed[3].Length);
        Assert.True(printed[3].All(char.IsAsciiDigit));
    }

    // The lines of the IR function whose mangled name contains the Oxygen name, from its define line to its closing brace.
    private static List<string> FunctionBody(string[] lines, string name)
    {
        var start = Array.FindIndex(lines, line => line.StartsWith("define", StringComparison.Ordinal)
            && line.Contains($"f{name.Length}{name}_", StringComparison.Ordinal));
        if (start < 0)
            return [];
        var end = Array.IndexOf(lines, "}", start);
        return lines[start..end].ToList();
    }

    [SkippableFact]
    public void PrintsUsizeAndIsizeValuesPassedThroughFunctionParameters()
    {
        NativeProgram.RequireNativeToolchain();
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source,
            """
            import std.io;

            void show(usize count, isize offset) {
                io.println(count);
                io.println(offset);
            }

            i32 main() {
                show(cast<usize>(5), cast<isize>(-7));
                return 0;
            }
            """);
        var executable = Path.Combine(_directory, "main.exe");

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "-o", executable, "-r", NativeProgram.RuntimeLibrary]);
        Assert.True(build.ExitCode == 0, build.Error);

        var run = NativeProgram.RunProcess(executable, []);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("5\n-7\n", run.Output.Replace("\r\n", "\n"));
    }
}
