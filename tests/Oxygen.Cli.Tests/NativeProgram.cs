using System.Diagnostics;
using Xunit;

namespace Oxygen.Cli.Tests;

// Compiles and runs Oxygen programs against the repository's runtime and standard library. The tests that use it
// need clang and the built runtime (runtime/build.ps1); they are skipped with the reason when those are missing.
// Set OXYGEN_REQUIRE_NATIVE_TESTS=1 to fail instead of skipping, which CI does so a missing toolchain cannot pass silently.
internal static class NativeProgram
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);
    private const string RequireNativeTestsVariable = "OXYGEN_REQUIRE_NATIVE_TESTS";

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

    // runtime/build.ps1 writes the archive into a folder named after the platform.
    // The archive for the host's default target, which runtime/build.ps1 builds into runtime/build/<triple>/.
    public static string RuntimeLibrary =>
        Path.Combine(RepositoryRoot, "runtime", "build",
            OperatingSystem.IsWindows() ? "x86_64-pc-windows-msvc" : "x86_64-unknown-linux-gnu", "liboxygenrt.a");

    public static string CompilerExecutable =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "oxygenc.exe" : "oxygenc");

    // Call first in every native test. Skips the test with the reason when the toolchain is missing, or fails it
    // when OXYGEN_REQUIRE_NATIVE_TESTS=1.
    public static void RequireNativeToolchain()
    {
        var missing = FindMissingToolchainPart();
        if (missing is null)
            return;

        if (Environment.GetEnvironmentVariable(RequireNativeTestsVariable) == "1")
            Assert.Fail($"Native tests are required but unavailable: {missing}");
        throw new SkipException(missing);
    }

    private static string? FindMissingToolchainPart()
    {
        if (!File.Exists(RuntimeLibrary))
            return $"Runtime archive not found at '{RuntimeLibrary}'.";
        if (!File.Exists(CompilerExecutable))
            return $"Compiler executable not found at '{CompilerExecutable}'.";
        if (FindOnPath("clang") is null)
            return "clang was not found on PATH.";
        return null;
    }

    // Looks for an executable on PATH, adding .exe on Windows, the same way the native build starts clang.
    private static string? FindOnPath(string name)
    {
        var fileNames = OperatingSystem.IsWindows() ? new[] { name + ".exe", name } : new[] { name };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            foreach (var fileName in fileNames)
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    // Compiles `source` as main.oxy in `directory` against the runtime, then runs the executable. `optimization` is
    // passed to clang as -O<level>; the default build uses -O2, which can hide stack growth that -O0 exposes.
    public static (int ExitCode, string Output, string Error) BuildAndRun(string directory, string source, string? optimization = null)
    {
        var sourcePath = Path.Combine(directory, "main.oxy");
        var executable = Path.Combine(directory, "main.exe");
        File.WriteAllText(sourcePath, source);

        var arguments = new List<string> { "build", sourcePath, "-o", executable, "-r", RuntimeLibrary };
        if (optimization is not null)
            arguments.Add($"-O{optimization}");
        var build = RunProcess(CompilerExecutable, arguments);
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
