using Xunit;

namespace Oxygen.Cli.Tests;

// Compiles programs that combine @flags enum values, runs them, and checks the result. The complement of a flags value keeps
// only the bits that its members use, so it names no bit outside them.
public sealed class FlagsEnumIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-flags", Guid.NewGuid().ToString("N"));

    public FlagsEnumIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [SkippableFact]
    public void FlagsCombineTestToggleAndComplement()
    {
        NativeProgram.RequireNativeToolchain();
        var run = NativeProgram.BuildAndRun(_directory,
            """
            @flags enum Access : i32 { Empty = 0, Read = 1, Write = 2, Exec = 4 }

            i32 main() {
                Access a = Access.Read | Access.Write;
                i32 r = 0;
                if ((a & Access.Read) != 0) {
                    r = r + 1;
                }
                if ((a & Access.Exec) == 0) {
                    r = r + 2;
                }
                Access b = a ^ Access.Write;
                if (b == Access.Read) {
                    r = r + 4;
                }
                Access c = ~a;
                if (c == Access.Exec) {
                    r = r + 8;
                }
                if (cast<i32>(~Access.Empty) == 7) {
                    r = r + 16;
                }
                a |= Access.Exec;
                if ((a & Access.Exec) != 0) {
                    r = r + 32;
                }
                return r;
            }
            """);

        Assert.Equal(63, run.ExitCode);
    }
}
