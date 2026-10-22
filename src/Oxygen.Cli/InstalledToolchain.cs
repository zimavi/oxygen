using Oxygen.Llvm;

namespace Oxygen.Cli;

// An installed Oxygen toolchain, found from the folder of the running compiler. The layout is described in docs/toolchain-layout.md:
//
//   <prefix>/bin/oxygenc        the compiler (on Windows also oxygenrt.dll and oxygenstd.dll, which programs find through PATH)
//   <prefix>/lib/               the runtime and the standard library to link
//   <prefix>/share/oxygen/stdlib/   the standard library sources, for reading only
//
// A compiler that runs from a build folder (bin/Debug/net10.0, or a test's output folder) is not in such a layout, and Find gives null.
internal sealed class InstalledToolchain
{
    private const string SourcesMarker = "share/oxygen/stdlib";

    private readonly string _prefix;

    private InstalledToolchain(string prefix) => _prefix = prefix;

    // The toolchain whose bin folder is `compilerDirectory`, or null when the folder is not <prefix>/bin of an installation. An
    // installation is recognised by its stdlib sources, which every install step puts in place.
    public static InstalledToolchain? Find(string compilerDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(compilerDirectory));
        if (directory.Parent is null || !directory.Name.Equals("bin", StringComparison.OrdinalIgnoreCase))
            return null;
        return Directory.Exists(Path.Combine(directory.Parent.FullName, SourcesMarker))
            ? new InstalledToolchain(directory.Parent.FullName)
            : null;
    }

    // The runtime to link: the static archive, or the shared runtime (the import library on Windows).
    public string RuntimePath(OxygenTarget target, bool shared) =>
        Path.Combine(_prefix, "lib", shared ? BuildCommand.SharedRuntimeFileName(target) : "liboxygenrt.a");

    // The standard library has its metadata in a shared library, which is oxygenstd.dll in bin on Windows, and liboxygenstd.so in lib
    // elsewhere. The lib prefix matters on Linux: ldconfig caches only lib*.so names, so the loader finds the library through the cache.
    public string StandardLibraryPath(OxygenTarget target) =>
        IsWindows(target) ? Path.Combine(_prefix, "bin", "oxygenstd.dll") : Path.Combine(_prefix, "lib", "liboxygenstd.so");

    // The file the linker takes for the standard library: the import library oxygenstd.lib on Windows, and the shared library elsewhere.
    public string StandardLibraryLinkPath(OxygenTarget target) =>
        IsWindows(target) ? Path.Combine(_prefix, "lib", "oxygenstd.lib") : StandardLibraryPath(target);

    private static bool IsWindows(OxygenTarget target) => target.Triple.Contains("-windows-", StringComparison.Ordinal);
}
