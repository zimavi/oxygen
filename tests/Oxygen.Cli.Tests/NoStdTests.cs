using Xunit;

namespace Oxygen.Cli.Tests;

// --no-std: the standard library is not loaded. The standard library itself can then be built, and a program that uses it without
// the library gets a plain error, and a null class reference stops it with exit code 101.
public sealed class NoStdTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public NoStdTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void AnImportOfAStdModuleIsAnErrorWithoutTheStandardLibrary()
    {
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source, "import std.io;\ni32 main() { return 0; }\n");

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable, ["build", source, "--no-std"]);

        Assert.Equal(ExitCodes.SourceError, run.ExitCode);
        Assert.Contains("is not part of this build", run.Error);
        Assert.Contains("--no-std", run.Error);
    }

    [SkippableFact]
    public void TheStandardLibraryBuildsAsASharedLibrary()
    {
        NativeProgram.RequireLibraryToolchain();
        var windows = OperatingSystem.IsWindows();
        var runtime = Path.Combine(NativeProgram.RepositoryRoot, "runtime", "build",
            windows ? "x86_64-pc-windows-msvc" : "x86_64-unknown-linux-gnu", windows ? "oxygenrt.dll" : "liboxygenrt.so");
        if (!File.Exists(runtime))
            throw new SkipException("The shared runtime is not built. Run runtime/build.ps1 or runtime/build.sh.");
        var modules = Directory.GetFiles(Path.Combine(NativeProgram.RepositoryRoot, "stdlib"), "*.oxy");
        var output = Path.Combine(_directory, "std");
        var arguments = new List<string> { "build" };
        arguments.AddRange(modules);
        arguments.AddRange(["--library", "--dynamic", "--no-std", "-o", output]);

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable, arguments);

        Assert.True(run.ExitCode == 0, $"Building the standard library failed with exit code {run.ExitCode}:\n{run.Error}");
        Assert.True(File.Exists(output + (windows ? ".dll" : ".so")));
    }

    [SkippableFact]
    public void ANullClassReferenceStopsANoStdProgramWithExitCode101()
    {
        NativeProgram.RequireNativeToolchain();
        var source = Path.Combine(_directory, "main.oxy");
        var executable = Path.Combine(_directory, "main.exe");
        File.WriteAllText(source, """
            class Box {
                i32 value = 3;
            }

            i32 main() {
                Box box = null;
                return box.value;
            }
            """);

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "--no-std", "-o", executable, "-r", NativeProgram.RuntimeLibrary]);
        Assert.True(build.ExitCode == 0, $"Build failed with exit code {build.ExitCode}:\n{build.Error}");

        var run = NativeProgram.RunProcess(executable, []);

        Assert.Equal(101, run.ExitCode);
        Assert.Contains("null class reference", run.Error);
    }
}
