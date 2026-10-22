using Xunit;

namespace Oxygen.Cli.Tests;

// A program linked against the shared runtime: oxygenrt.dll through its import library on Windows, and liboxygenrt.so on Linux. The
// program runs with the shared runtime found the way an installed program finds it: next to it on Windows, and through the library
// path on Linux.
public sealed class SharedRuntimeTests : IDisposable
{
    private static readonly string TargetDirectory = Path.Combine(NativeProgram.RepositoryRoot, "runtime", "build",
        OperatingSystem.IsWindows() ? "x86_64-pc-windows-msvc" : "x86_64-unknown-linux-gnu");

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
        // On Windows the program links the import library and needs the DLL beside it. On Linux it links the shared object itself.
        var library = Path.Combine(TargetDirectory, OperatingSystem.IsWindows() ? "oxygenrt.lib" : "liboxygenrt.so");
        var dll = Path.Combine(TargetDirectory, "oxygenrt.dll");
        Skip.IfNot(File.Exists(library) && (!OperatingSystem.IsWindows() || File.Exists(dll)),
            "The shared runtime is not built. Run runtime/build.ps1 or runtime/build.sh to build it.");

        var source = Path.Combine(_directory, "main.oxy");
        var executable = Path.Combine(_directory, OperatingSystem.IsWindows() ? "main.exe" : "main");
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
            ["build", source, "-o", executable, "-r", library]);
        Assert.True(build.ExitCode == 0, $"Build failed with exit code {build.ExitCode}:\n{build.Error}");
        if (OperatingSystem.IsWindows())
            File.Copy(dll, Path.Combine(_directory, "oxygenrt.dll"));

        var environment = OperatingSystem.IsWindows()
            ? null
            : new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = TargetDirectory };
        var run = NativeProgram.RunProcess(executable, [], environment);

        // The index is out of range, which is a runtime failure with exit code 101, after the first line was written.
        Assert.Equal(101, run.ExitCode);
        Assert.Equal("hello\n", run.Output);
    }
}
