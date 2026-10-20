using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that take the address of a field, change fields and properties with ++ and --, and compare enum values.
public sealed class MemberOperatorIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public MemberOperatorIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void FieldAndPropertyChangesAndEnumComparisonsAreValid()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            enum Code : i32 { Low = -1, High = 1 }

            class A {
                public i32 x;
                public i32 Count { get; set; }
            }

            i32 main() {
                A a = new A();
                a.x = 4;
                a.x++;
                a.x--;
                a.x++;
                i32* p = &a.x;
                *p = *p + 10;
                a.Count = 2;
                a.Count++;
                io.println(a.x);
                io.println(a.Count);
                Code c = Code.Low;
                if (c == Code.Low) {
                    io.println(cast<i32>(c));
                }
                if (Code.High != c) {
                    io.println(1);
                }
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("15\n3\n-1\n1\n", run.Output.Replace("\r\n", "\n"));
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
