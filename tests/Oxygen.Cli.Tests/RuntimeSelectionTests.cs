using Oxygen.Llvm;
using Xunit;

namespace Oxygen.Cli.Tests;

// --runtime takes the static archive or the shared runtime, and a build that links the shared runtime rejects the static archive. The
// checks run before anything is linked, so these tests need no toolchain.
public sealed class RuntimeSelectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public RuntimeSelectionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("liboxygenrt.a", "x86_64-pc-windows-msvc", NativeDependencyKind.Static)]
    [InlineData("liboxygenrt.a", "x86_64-unknown-linux-gnu", NativeDependencyKind.Static)]
    [InlineData("oxygenrt.lib", "x86_64-pc-windows-msvc", NativeDependencyKind.Dynamic)]
    [InlineData("liboxygenrt.so", "x86_64-unknown-linux-gnu", NativeDependencyKind.Dynamic)]
    [InlineData("oxygenrt.dll", "x86_64-pc-windows-msvc", null)]
    [InlineData("oxygenrt.lib", "x86_64-unknown-linux-gnu", null)]
    [InlineData("liboxygenrt.so", "x86_64-pc-windows-msvc", null)]
    [InlineData("program.exe", "x86_64-pc-windows-msvc", null)]
    public void ARuntimeFileIsStaticOrSharedForItsTarget(string file, string triple, NativeDependencyKind? expected)
    {
        var target = triple == OxygenTarget.WindowsMsvcX64.Triple ? OxygenTarget.WindowsMsvcX64 : OxygenTarget.LinuxGnuX64;

        Assert.Equal(expected, BuildCommand.RuntimeKindOf(Path.Combine("runtime", file), target));
    }

    [Fact]
    public void AnExecutableRejectsARuntimeThatIsNeitherStaticNorShared()
    {
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source, "i32 main() { return 0; }\n");
        var runtime = Path.Combine(_directory, "runtime.txt");
        File.WriteAllText(runtime, "not a runtime");

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable, ["build", source, "--no-std", "-r", runtime]);

        Assert.Equal(ExitCodes.SourceError, run.ExitCode);
        Assert.Contains("--runtime must be a static archive (.a) or the shared runtime", run.Error);
    }

    [Fact]
    public void AProgramThatUsesTheStandardLibraryRejectsTheStaticArchive()
    {
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source, "i32 main() { return 0; }\n");
        var standardLibrary = Path.Combine(_directory, "std.so");

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "--stdlib", standardLibrary, "-r", NativeProgram.RuntimeLibrary]);

        Assert.Equal(ExitCodes.SourceError, run.ExitCode);
        Assert.Contains("--stdlib links the shared runtime", run.Error);
    }

    [Fact]
    public void ASharedLibraryRejectsTheStaticArchive()
    {
        var source = Path.Combine(_directory, "shapes.oxy");
        File.WriteAllText(source, "class Shape { i32 sides; }\n");

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "--library", "--dynamic", "-r", NativeProgram.RuntimeLibrary]);

        Assert.Equal(ExitCodes.SourceError, run.ExitCode);
        Assert.Contains("--dynamic links the shared runtime", run.Error);
    }
}
