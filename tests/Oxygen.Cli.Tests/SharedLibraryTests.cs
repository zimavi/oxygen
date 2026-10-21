using Xunit;

namespace Oxygen.Cli.Tests;

// A shared library built by the compiler, called from a C program that links it. The program needs no Oxygen code, so it checks the
// shared library itself: its exported function is callable, and it gets the shared runtime it depends on.
public sealed class SharedLibraryTests : IDisposable
{
    private const string Library = "module shapes; public i32 add(i32 a, i32 b) { return a + b; }";

    private const string Harness = """
        extern int _Ox_v1_m6shapes_f3add_p2_i32i32(int, int);

        int main(void) {
            return _Ox_v1_m6shapes_f3add_p2_i32i32(20, 22) == 42 ? 0 : 1;
        }
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public SharedLibraryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void AProgramCallsTheExportedFunctionOfASharedLibrary()
    {
        NativeProgram.RequireLibraryToolchain();
        var windows = OperatingSystem.IsWindows();
        var runtimeDirectory = Path.Combine(NativeProgram.RepositoryRoot, "runtime", "build",
            windows ? "x86_64-pc-windows-msvc" : "x86_64-unknown-linux-gnu");
        var sharedRuntime = Path.Combine(runtimeDirectory, windows ? "oxygenrt.lib" : "liboxygenrt.so");
        Skip.IfNot(File.Exists(sharedRuntime), "The shared runtime is not built. Run runtime/build.ps1 or runtime/build.sh.");

        var source = Path.Combine(_directory, "shapes.oxy");
        File.WriteAllText(source, Library);
        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "--library", "--dynamic", "-o", Path.Combine(_directory, "shapes")]);
        Assert.True(build.ExitCode == 0, $"Building the shared library failed with exit code {build.ExitCode}:\n{build.Error}");

        var harness = Path.Combine(_directory, "harness.c");
        File.WriteAllText(harness, Harness);
        var executable = Path.Combine(_directory, windows ? "harness.exe" : "harness");
        // Windows links the import library of the shared library. Linux links the shared object itself.
        var libraryPath = Path.Combine(_directory, windows ? "shapes.lib" : "shapes.so");
        var compile = NativeProgram.RunProcess("clang", windows
            ? new[] { harness, libraryPath, sharedRuntime, "-o", executable }
            : new[] { harness, libraryPath, sharedRuntime, "-lstdc++", "-lm", "-o", executable });
        Assert.True(compile.ExitCode == 0, $"Linking the harness failed:\n{compile.Error}");

        if (windows)
        {
            // The DLLs sit next to the program, as they would when installed.
            File.Copy(Path.Combine(runtimeDirectory, "oxygenrt.dll"), Path.Combine(_directory, "oxygenrt.dll"));
            var run = NativeProgram.RunProcess(executable, []);
            Assert.Equal(0, run.ExitCode);
            return;
        }

        var previousLibraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", string.Join(':', _directory, runtimeDirectory));
        try
        {
            Assert.Equal(0, NativeProgram.RunProcess(executable, []).ExitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", previousLibraryPath);
        }
    }
}
