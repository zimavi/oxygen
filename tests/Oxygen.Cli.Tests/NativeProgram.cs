using System.Diagnostics;

namespace Oxygen.Cli.Tests;

// Compiles and runs Oxygen programs against the repository's runtime and standard library. The tests that use it
// need clang and the built runtime (runtime/build.ps1).
internal static class NativeProgram
{
    // The repository root is five levels above the test assembly's output folder (bin/Debug/net10.0).
    public static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    public static string RuntimeLibrary =>
        Path.Combine(RepositoryRoot, "runtime", "build", "windows", "liboxygenrt.a");

    public static (int ExitCode, string Output, string Error) RunProcess(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        // The compiler looks for the standard library under OXYGEN_PATH when the source is outside the repository.
        startInfo.Environment["OXYGEN_PATH"] = RepositoryRoot;

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }
}
