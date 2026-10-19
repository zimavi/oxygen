using System.Diagnostics;
using Xunit;

namespace Oxygen.Cli.Tests;

// Compiles and runs Oxygen programs against the repository's runtime and standard library. The tests that use it
// need clang and the built runtime (runtime/build.ps1); they are skipped with the reason when those are missing.
internal static class NativeProgram
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);

    // The repository root is the nearest ancestor of the test output folder that contains the solution file.
    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oxygen.slnx")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException(
                $"Could not find Oxygen.slnx above '{AppContext.BaseDirectory}'.");
        }
    }

    public static string RuntimeLibrary =>
        Path.Combine(RepositoryRoot, "runtime", "build", "windows", "liboxygenrt.a");

    // Native tests run the compiler's apphost, which is only copied for Windows builds today.
    public static string CompilerExecutable => Path.Combine(AppContext.BaseDirectory, "oxygenc.exe");

    // Call first in every native test. Throws SkipException, which xUnit reports as skipped rather than passed or failed.
    public static void RequireNativeToolchain()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("Native integration tests currently target Windows x64 only.");
        if (!File.Exists(CompilerExecutable))
            throw Xunit.Sdk.SkipException.ForSkip($"Compiler apphost not found at '{CompilerExecutable}'.");
        if (!File.Exists(RuntimeLibrary))
            throw Xunit.Sdk.SkipException.ForSkip($"Runtime not built: '{RuntimeLibrary}' (run runtime/build.ps1).");
    }

    // Compiles `source` as main.oxy in `directory` against the runtime, then runs the executable.
    public static (int ExitCode, string Output, string Error) BuildAndRun(string directory, string source)
    {
        var sourcePath = Path.Combine(directory, "main.oxy");
        var executable = Path.Combine(directory, "main.exe");
        File.WriteAllText(sourcePath, source);

        var build = RunProcess(CompilerExecutable,
            ["build", sourcePath, "-o", executable, "-r", RuntimeLibrary]);
        Assert.True(build.ExitCode == 0, $"Build failed with exit code {build.ExitCode}:\n{build.Error}");

        return RunProcess(executable, []);
    }

    public static (int ExitCode, string Output, string Error) RunProcess(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            // The runtime writes UTF-8 bytes; decoding them with the console code page would garble non-ASCII output.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        // The compiler looks for the standard library under OXYGEN_PATH when the source is outside the repository.
        startInfo.Environment["OXYGEN_PATH"] = RepositoryRoot;

        using var process = Process.Start(startInfo)!;
        // Both streams are read concurrently: reading one to the end while the other fills its pipe can deadlock.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProcessTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{fileName}' did not exit within {ProcessTimeout.TotalSeconds} seconds.");
        }
        process.WaitForExit();
        return (process.ExitCode, outputTask.GetAwaiter().GetResult(), errorTask.GetAwaiter().GetResult());
    }
}
