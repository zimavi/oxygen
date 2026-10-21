using Xunit;

namespace Oxygen.Cli.Tests;

// Runs the compiler with --target. The IR is written without linking, so these tests check both supported targets on any host.
public sealed class TargetIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public TargetIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("x86_64-pc-windows-msvc", "e-m:w-")]
    [InlineData("x86_64-unknown-linux-gnu", "e-m:e-")]
    public void EmittedIrNamesTheRequestedTarget(string target, string dataLayoutPrefix)
    {
        var source = WriteSource("i32 main() { return 0; }");
        var ir = Path.Combine(_directory, "program.ll");

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "-o", Path.Combine(_directory, "program.exe"), "--target", target, "--emit-ir", ir]);

        Assert.True(build.ExitCode == 0, build.Error);
        var text = File.ReadAllText(ir);
        Assert.Contains($"target triple = \"{target}\"", text);
        Assert.Contains($"target datalayout = \"{dataLayoutPrefix}", text);
    }

    [Fact]
    public void AnUnsupportedTargetIsReportedBeforeAnythingIsBuilt()
    {
        var source = WriteSource("i32 main() { return 0; }");
        var executable = Path.Combine(_directory, "program.exe");

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "-o", executable, "--target", "x86_64-w64-windows-gnu"]);

        Assert.Equal(1, build.ExitCode);
        Assert.Contains("Unsupported target 'x86_64-w64-windows-gnu'", build.Error);
        Assert.False(File.Exists(executable));
    }

    private string WriteSource(string text)
    {
        var path = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(path, text);
        return path;
    }
}
