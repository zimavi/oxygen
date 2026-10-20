using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that use the call and statement forms the parser accepts, and checks the output of the native executable.
public sealed class SyntaxIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public SyntaxIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void ThisAndBaseCallsReachTheirMethods()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class A {
                virtual i32 f() { return 1; }
                i32 g() { return 10; }
            }

            class B : A {
                override i32 f() { return 2; }
                i32 h() { return base.f() + base.g() + this.f(); }
            }

            i32 main() {
                B b = new B();
                io.println(b.h());
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("13\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void IsTestsAreConditionsAndOperands()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class A { }
            class B : A { }

            i32 main() {
                i32 a = 1;
                i32 b = 2;
                i32 s = a << b;
                i32 t = 8 >> 1;
                A x = new B();
                bool both = x is B && x is A;
                if (x is B) {
                    io.println(1);
                }
                io.println(s + t);
                io.println(0);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("1\n8\n0\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void DefaultCaseFallsThroughIntoTheCaseWrittenAfterIt()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 main() {
                i32 n = 2;
                switch (n) {
                    default: io.println(9);
                    case 1: io.println(1);
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("9\n1\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void NamedPointerAndArrayLocalsAreDeclared()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class Point {
                i32 x;
            }

            i32 main() {
                Point* p = null;
                Point[3] items;
                io.println(7);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("7\n", run.Output.Replace("\r\n", "\n"));
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
