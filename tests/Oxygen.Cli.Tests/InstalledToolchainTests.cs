using Oxygen.Llvm;
using Xunit;

namespace Oxygen.Cli.Tests;

// An installed compiler (<prefix>/bin/oxygenc) finds the runtime and the standard library in its own prefix, with no OXYGEN_PATH
// (docs/toolchain-layout.md).
public sealed class InstalledToolchainTests : IClassFixture<InstalledToolchainTests.StagedPrefix>, IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    private readonly StagedPrefix _staged;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public InstalledToolchainTests(StagedPrefix staged)
    {
        _staged = staged;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void ABuildFolderIsNotAnInstallation()
    {
        Assert.Null(InstalledToolchain.Find(AppContext.BaseDirectory));
    }

    [Fact]
    public void AnInstallationIsAFolderCalledBinWithTheStandardLibrarySourcesBesideIt()
    {
        var prefix = Path.Combine(_directory, "prefix");
        var bin = Path.Combine(prefix, "bin");
        Directory.CreateDirectory(bin);
        Assert.Null(InstalledToolchain.Find(bin));

        Directory.CreateDirectory(Path.Combine(prefix, "share", "oxygen", "stdlib"));
        var toolchain = InstalledToolchain.Find(bin);

        Assert.NotNull(toolchain);
        Assert.Null(InstalledToolchain.Find(Path.Combine(prefix, "share")));
    }

    [Fact]
    public void TheFilesOfAnInstallationFollowTheTargetsLibraryNames()
    {
        var prefix = Path.Combine(_directory, "prefix");
        Directory.CreateDirectory(Path.Combine(prefix, "bin"));
        Directory.CreateDirectory(Path.Combine(prefix, "share", "oxygen", "stdlib"));
        var toolchain = InstalledToolchain.Find(Path.Combine(prefix, "bin"))!;

        var windows = OxygenTarget.WindowsMsvcX64;
        Assert.Equal(Path.Combine(prefix, "lib", "oxygenrt.lib"), toolchain.RuntimePath(windows, shared: true));
        Assert.Equal(Path.Combine(prefix, "lib", "liboxygenrt.a"), toolchain.RuntimePath(windows, shared: false));
        Assert.Equal(Path.Combine(prefix, "bin", "std.dll"), toolchain.StandardLibraryPath(windows));
        Assert.Equal(Path.Combine(prefix, "lib", "std.lib"), toolchain.StandardLibraryLinkPath(windows));

        var linux = OxygenTarget.LinuxGnuX64;
        Assert.Equal(Path.Combine(prefix, "lib", "liboxygenrt.so"), toolchain.RuntimePath(linux, shared: true));
        Assert.Equal(Path.Combine(prefix, "lib", "std.so"), toolchain.StandardLibraryPath(linux));
        Assert.Equal(Path.Combine(prefix, "lib", "std.so"), toolchain.StandardLibraryLinkPath(linux));
    }

    // The installed compiler gets no OXYGEN_PATH and no --runtime or --stdlib. It links the installation's shared runtime and its std, and
    // it does not compile the standard library's source, so the program's IR only declares the functions of std. (A generic function is the exception: the program instantiates it.)
    [SkippableFact]
    public void AnInstalledCompilerBuildsAProgramAgainstItsOwnRuntimeAndStandardLibrary()
    {
        var prefix = _staged.Create();
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source, """
            import std.io;

            i32 main() {
                io.println_char('i');
                return 7;
            }
            """);

        var ir = Path.Combine(_directory, "main.ll");
        var emit = Run(prefix, ["build", source, "--emit-ir", ir]);
        Assert.True(emit.ExitCode == 0, $"Emitting IR failed with exit code {emit.ExitCode}:\n{emit.Error}");
        Assert.Matches(@"declare [^\n]*println_char", File.ReadAllText(ir));
        Assert.DoesNotMatch(@"define [^\n]*println_char", File.ReadAllText(ir));

        var executable = Path.Combine(_directory, Windows ? "main.exe" : "main");
        var build = Run(prefix, ["build", source, "-o", executable]);
        Assert.True(build.ExitCode == 0, $"Build failed with exit code {build.ExitCode}:\n{build.Error}");

        // The program finds the shared libraries the way an installed program does: through PATH on Windows, and the library path on Linux.
        var run = NativeProgram.RunProcess(executable, [], LibraryPathOf(prefix));
        Assert.Equal(7, run.ExitCode);
        Assert.Equal("i\n", run.Output.Replace("\r\n", "\n"));
    }

    // The installation is for one platform. A target whose standard library it lacks stops the build with the path it looked for, as a
    // toolchain failure, and does not fall back to the repository's sources.
    [SkippableFact]
    public void AnInstallationWithoutTheStandardLibraryOfTheTargetStopsTheBuildAndNamesTheFile()
    {
        var prefix = _staged.Create();
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source, "i32 main() { return 0; }");
        var foreignTarget = Windows ? "x86_64_unknown_linux_gnu" : "x86_64_pc_windows_msvc";
        var missingFile = Windows ? Path.Combine(prefix, "lib", "std.so") : Path.Combine(prefix, "bin", "std.dll");

        var run = Run(prefix, ["build", source, "--target", foreignTarget, "-o", Path.Combine(_directory, "main.out")]);

        Assert.Equal(3, run.ExitCode);
        Assert.Contains(missingFile, run.Error);
    }

    // --no-std builds without the installed standard library, as it does for a compiler that runs from a build folder.
    [SkippableFact]
    public void NoStdLeavesOutTheInstalledStandardLibrary()
    {
        var prefix = _staged.Create();
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source, "i32 main() { return 0; }");
        var ir = Path.Combine(_directory, "main.ll");

        var run = Run(prefix, ["build", source, "--no-std", "--emit-ir", ir]);

        Assert.True(run.ExitCode == 0, $"Emitting IR failed with exit code {run.ExitCode}:\n{run.Error}");
    }

    // The compiler of the installation, with no OXYGEN_PATH.
    private static (int ExitCode, string Output, string Error) Run(string prefix, IEnumerable<string> arguments) =>
        NativeProgram.RunProcess(
            Path.Combine(prefix, "bin", Windows ? "oxygenc.exe" : "oxygenc"),
            arguments,
            unset: ["OXYGEN_PATH"]);

    private static Dictionary<string, string> LibraryPathOf(string prefix) => Windows
        ? new Dictionary<string, string> { ["PATH"] = Path.Combine(prefix, "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") }
        : new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = Path.Combine(prefix, "lib") };

    // Builds one installation in a temporary folder, the first time a test asks for it, and removes it when the tests are done. The
    // compiler is a copy of the test project's own, so it has the same LLVM library. The runtime comes from runtime/build, and the
    // standard library is built by that compiler, as stdlib/build.ps1 does.
    public sealed class StagedPrefix : IDisposable
    {
        private readonly object _lock = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "oxygenc-tests", "prefix-" + Guid.NewGuid().ToString("N"));
        private string? _prefix;

        public string Create()
        {
            lock (_lock)
                return _prefix ??= Stage();
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private string Stage()
        {
            NativeProgram.RequireLibraryToolchain();
            var runtimeDirectory = Path.Combine(NativeProgram.RepositoryRoot, "runtime", "build",
                Windows ? "x86_64-pc-windows-msvc" : "x86_64-unknown-linux-gnu");
            var sharedRuntime = Windows ? "oxygenrt.dll" : "liboxygenrt.so";
            var runtimeLink = Windows ? "oxygenrt.lib" : "liboxygenrt.so";
            Skip.IfNot(File.Exists(Path.Combine(runtimeDirectory, sharedRuntime)) &&
                       File.Exists(Path.Combine(runtimeDirectory, runtimeLink)) &&
                       File.Exists(Path.Combine(runtimeDirectory, "liboxygenrt.a")),
                "The runtime is not built. Run runtime/build.ps1 or runtime/build.sh to build it.");

            var prefix = Path.Combine(_root, "prefix");
            var bin = Path.Combine(prefix, "bin");
            var lib = Path.Combine(prefix, "lib");
            var sources = Path.Combine(prefix, "share", "oxygen", "stdlib");
            Directory.CreateDirectory(bin);
            Directory.CreateDirectory(lib);
            Directory.CreateDirectory(sources);

            CopyDirectory(AppContext.BaseDirectory, bin);
            File.Copy(Path.Combine(runtimeDirectory, "liboxygenrt.a"), Path.Combine(lib, "liboxygenrt.a"));
            File.Copy(Path.Combine(runtimeDirectory, runtimeLink), Path.Combine(lib, runtimeLink));
            // The runtime's DLL is found through PATH, so it sits in bin on Windows. The shared object is both the link file and the
            // loaded file elsewhere.
            if (Windows)
                File.Copy(Path.Combine(runtimeDirectory, sharedRuntime), Path.Combine(bin, sharedRuntime));

            var standardLibrarySources = Directory.GetFiles(Path.Combine(NativeProgram.RepositoryRoot, "stdlib"), "*.oxy");
            foreach (var file in standardLibrarySources)
                File.Copy(file, Path.Combine(sources, Path.GetFileName(file)));

            var built = Path.Combine(_root, "std");
            var arguments = new List<string> { "build" };
            arguments.AddRange(standardLibrarySources);
            arguments.AddRange(["--library", "--dynamic", "--no-std", "-o", built]);
            var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable, arguments);
            Assert.True(build.ExitCode == 0, $"Building the standard library failed with exit code {build.ExitCode}:\n{build.Error}");
            if (Windows)
            {
                File.Move(built + ".dll", Path.Combine(bin, "std.dll"));
                File.Move(built + ".lib", Path.Combine(lib, "std.lib"));
            }
            else
            {
                File.Move(built + ".so", Path.Combine(lib, "std.so"));
            }
            return prefix;
        }

        private static void CopyDirectory(string source, string destination)
        {
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }
}
