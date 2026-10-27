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
    public void SourceErrorExitsWithSourceError()
    {
        var source = WriteSource("i32 main() { return missing_name; }");

        var exitCode = RunCompiler("build", source, "--emit-ir", Path.Combine(_directory, "program.ll"));

        Assert.Equal(ExitCodes.SourceError, exitCode);
    }

    [Fact]
    public void MissingSourceFileExitsWithSourceError()
    {
        var exitCode = RunCompiler("build", Path.Combine(_directory, "absent.oxy"), "--emit-ir",
            Path.Combine(_directory, "program.ll"));

        Assert.Equal(ExitCodes.SourceError, exitCode);
    }

    [Fact]
    public void MalformedMetadataExitsWithSourceError()
    {
        var source = WriteSource("i32 main() { return 0; }");
        // A stand-in library whose metadata section is not JSON: the metadata is invalid input, not a crash.
        var library = Path.Combine(_directory, "library.o");
        NativeLibraryFixture.WriteObject(library, Oxygen.Llvm.OxygenTarget.Host.Triple, "{ this is not json"u8.ToArray());

        var exitCode = RunCompiler("build", source, "--stdlib", library, "--emit-ir", Path.Combine(_directory, "program.ll"));

        Assert.Equal(ExitCodes.SourceError, exitCode);
    }

    // A folder under the source folder that cannot be read is a warning: the build of the program still succeeds.
    [SkippableFact]
    public void AnUnreadableSubfolderDoesNotStopTheBuild()
    {
        var source = WriteSource("i32 main() { return 0; }");
        var locked = Path.Combine(_directory, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "hidden.oxy"), "module hidden;\ni32 unused() { return 1; }");

        using var folder = UnreadableFolder.Create(locked);
        Skip.IfNot(folder.Effective, "The current user can still read the folder, so it cannot be made unreadable here.");
        var exitCode = RunCompiler("build", source, "--emit-ir", Path.Combine(_directory, "program.ll"));

        Assert.Equal(0, exitCode);
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
