using Xunit;

namespace Oxygen.Cli.Tests;

// A program linked against the shared runtime, oxygenrt.dll, through its import library. The DLL is copied next to the program, as a
// user would install it, and the program runs with it.
public sealed class SharedRuntimeTests : IDisposable
{
    private static readonly string TargetDirectory = Path.Combine(NativeProgram.RepositoryRoot, "runtime", "build",
        "x86_64-pc-windows-msvc");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public SharedRuntimeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void AProgramLinkedWithTheSharedRuntimeRunsAndItsFailuresExitWithTheRuntimeCode()
    {
        NativeProgram.RequireNativeToolchain();
        var importLibrary = Path.Combine(TargetDirectory, "oxygenrt.lib");
        var dll = Path.Combine(TargetDirectory, "oxygenrt.dll");
        Skip.IfNot(OperatingSystem.IsWindows() && File.Exists(importLibrary) && File.Exists(dll),
            "The shared runtime is not built. Run runtime/build.ps1 to build it.");

        var source = Path.Combine(_directory, "main.oxy");
        var executable = Path.Combine(_directory, "main.exe");
        File.WriteAllText(source, """
            import std.io;

            i32 main() {
                io.println("hello");
                i32[2] values = { 1, 2 };
                i32 index = 5;
                return values[index];
            }
            """);

        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "-o", executable, "-r", importLibrary]);
        Assert.True(build.ExitCode == 0, $"Build failed with exit code {build.ExitCode}:\n{build.Error}");
        File.Copy(dll, Path.Combine(_directory, "oxygenrt.dll"));

        var run = NativeProgram.RunProcess(executable, []);

        // The index is out of range, which is a runtime failure with exit code 101, after the first line was written.
        Assert.Equal(101, run.ExitCode);
        Assert.Equal("hello\n", run.Output);
    }
}
