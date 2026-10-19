using System.Diagnostics;
using Xunit;

namespace Oxygen.Cli.Tests;

// Runs the compiler as a separate process, since its command line is the public interface being tested.
public sealed class BuildExitCodeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public BuildExitCodeTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void SuccessfulIrEmissionExitsWithZero()
    {
        var source = WriteSource("i32 main() { return 0; }");

        var exitCode = RunCompiler("build", source, "--emit-ir", Path.Combine(_directory, "program.ll"));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void SourceErrorExitsWithCompilationFailed()
    {
        var source = WriteSource("i32 main() { return missing_name; }");

        var exitCode = RunCompiler("build", source, "--emit-ir", Path.Combine(_directory, "program.ll"));

        Assert.Equal(ExitCodes.CompilationFailed, exitCode);
    }

    [Fact]
    public void MissingSourceFileExitsWithCompilationFailed()
    {
        var exitCode = RunCompiler("build", Path.Combine(_directory, "absent.oxy"), "--emit-ir",
            Path.Combine(_directory, "program.ll"));

        Assert.Equal(ExitCodes.CompilationFailed, exitCode);
    }

    private string WriteSource(string text)
    {
        var path = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(path, text);
        return path;
    }

    private static int RunCompiler(params string[] arguments) =>
        NativeProgram.RunProcess(NativeProgram.CompilerExecutable, arguments).ExitCode;
}
