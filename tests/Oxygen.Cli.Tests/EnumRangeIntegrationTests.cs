using Xunit;

namespace Oxygen.Cli.Tests;

// Builds enums whose members use the full range of a 64-bit underlying type, runs them, and checks the values that they produce.
public sealed class EnumRangeIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public EnumRangeIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void MemberAboveTheI32RangeKeepsItsValue()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            enum Big : u64 {
                Small = 1,
                Large = 4294967296
            }

            i32 main() {
                Big b = Big.Large;
                return cast<i32>(cast<u64>(b) >> 32);
            }
            """);

        Assert.Equal(1, run.ExitCode);
    }

    [SkippableFact]
    public void FlagsMaskOfA64BitEnumKeepsItsHighBit()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            @flags enum Bits : u64 {
                Low = 1,
                High = 9223372036854775808
            }

            i32 main() {
                Bits b = Bits.High | Bits.Low;
                Bits c = ~Bits.Low;
                if ((b & Bits.High) != 0) {
                    if ((c & Bits.Low) == 0) {
                        return cast<i32>(cast<u64>(Bits.High) >> 60);
                    }
                }
                return 1;
            }
            """);

        Assert.Equal(8, run.ExitCode);
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
