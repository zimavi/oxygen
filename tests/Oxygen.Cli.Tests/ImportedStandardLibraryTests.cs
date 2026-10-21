using Xunit;

namespace Oxygen.Cli.Tests;

// --stdlib takes the modules of a shared standard library from the library's metadata. A program declares the library's functions and
// does not compile the standard library's source, so the program's IR declares them and does not define them. The program then links
// the library and the shared runtime, and runs with both beside it.
public sealed class ImportedStandardLibraryTests : IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();
    private static readonly string TargetDirectory = Path.Combine(NativeProgram.RepositoryRoot, "runtime", "build",
        Windows ? "x86_64-pc-windows-msvc" : "x86_64-unknown-linux-gnu");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public ImportedStandardLibraryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void AProgramDeclaresTheFunctionsOfASharedStandardLibraryAndRunsWithIt()
    {
        NativeProgram.RequireLibraryToolchain();
        // The program links the runtime's import library on Windows, and its DLL is copied beside the program to run.
        var runtime = Path.Combine(TargetDirectory, Windows ? "oxygenrt.lib" : "liboxygenrt.so");
        var runtimeFile = Path.Combine(TargetDirectory, Windows ? "oxygenrt.dll" : "liboxygenrt.so");
        Skip.IfNot(File.Exists(runtime) && File.Exists(runtimeFile),
            "The shared runtime is not built. Run runtime/build.ps1 or runtime/build.sh to build it.");
        var library = BuildStandardLibrary();
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source, """
            import std.io;

            i32 main() {
                io.print_char('h');
                io.println_char('i');
                return 0;
            }
            """);

        var ir = Path.Combine(_directory, "main.ll");
        var emit = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "--stdlib", library, "--emit-ir", ir]);
        Assert.True(emit.ExitCode == 0, $"Emitting IR failed with exit code {emit.ExitCode}:\n{emit.Error}");
        var text = File.ReadAllText(ir);
        // The library defines print_char, so the program declares it, and its own IR has no definition of it.
        Assert.Matches("declare [^\\n]*print_char", text);
        Assert.DoesNotMatch("define [^\\n]*print_char", text);

        var executable = Path.Combine(_directory, Windows ? "main.exe" : "main");
        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "--stdlib", library, "-r", runtime, "-o", executable]);
        Assert.True(build.ExitCode == 0, $"Build failed with exit code {build.ExitCode}:\n{build.Error}");

        // The library is built beside the program, and the runtime is copied there too. The program finds both: on Windows through the
        // DLL search, and on Linux through the library path.
        File.Copy(runtimeFile, Path.Combine(_directory, Path.GetFileName(runtimeFile)), overwrite: true);
        var previousLibraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (!Windows)
            Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", _directory);
        try
        {
            var run = NativeProgram.RunProcess(executable, []);

            Assert.Equal(0, run.ExitCode);
            Assert.Equal("hi\n", run.Output.Replace("\r\n", "\n"));
        }
        finally
        {
            if (!Windows)
                Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", previousLibraryPath);
        }
    }

    // Builds the standard library as a shared library in the test's folder, and returns the path of the shared library.
    private string BuildStandardLibrary()
    {
        var output = Path.Combine(_directory, "std");
        var arguments = new List<string> { "build" };
        arguments.AddRange(Directory.GetFiles(Path.Combine(NativeProgram.RepositoryRoot, "stdlib"), "*.oxy"));
        arguments.AddRange(["--library", "--dynamic", "--no-std", "-o", output]);

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable, arguments);

        Assert.True(run.ExitCode == 0, $"Building the standard library failed with exit code {run.ExitCode}:\n{run.Error}");
        return output + (Windows ? ".dll" : ".so");
    }

}
