using System.Diagnostics;

namespace Oxygen.Cli.Tests;

// Removes the current user's read access to a folder, for a test that needs a folder the compiler cannot list. Dispose restores the access,
// so the folder can be deleted. Effective is false when the lock did not take hold, as when the tests run with the rights to read everything.
internal sealed class UnreadableFolder : IDisposable
{
    private readonly string _path;

    private UnreadableFolder(string path, bool effective)
    {
        _path = path;
        Effective = effective;
    }

    public bool Effective { get; }

    public static UnreadableFolder Create(string path)
    {
        if (OperatingSystem.IsWindows())
            Run("icacls", $"\"{path}\" /deny \"{Environment.UserName}:(R)\"");
        else
            File.SetUnixFileMode(path, UnixFileMode.None);
        return new UnreadableFolder(path, !CanList(path));
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            Run("icacls", $"\"{_path}\" /remove:d \"{Environment.UserName}\"");
        else
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static bool CanList(string path)
    {
        try
        {
            Directory.GetFiles(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Run(string program, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(program, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"Could not start {program}.");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
    }
}
