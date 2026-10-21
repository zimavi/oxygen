namespace Oxygen.Llvm;

// A native target the backend generates code for. The target fixes the module's triple and data layout, and the exception model:
// an MSVC Windows target uses SEH funclets, and the other supported target uses the Itanium landing pads. The exception model
// follows from the triple, not from the platform the compiler runs on.
public sealed record OxygenTarget
{
    private const string WindowsDataLayout = "e-m:w-p270:32:32-p271:32:32-p272:64:64-i64:64-f80:128-n8:16:32:64-S128";
    private const string ElfDataLayout = "e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-f80:128-n8:16:32:64-S128";

    public static readonly OxygenTarget WindowsMsvcX64 = new()
    {
        Triple = "x86_64-pc-windows-msvc",
        DataLayout = WindowsDataLayout,
        UsesSeh = true
    };

    public static readonly OxygenTarget LinuxGnuX64 = new()
    {
        Triple = "x86_64-unknown-linux-gnu",
        DataLayout = ElfDataLayout,
        UsesSeh = false
    };

    public required string Triple { get; init; }
    public required string DataLayout { get; init; }
    public required bool UsesSeh { get; init; }

    public static IReadOnlyList<string> SupportedTriples { get; } =
        [WindowsMsvcX64.Triple, LinuxGnuX64.Triple];

    // The target of the platform the compiler runs on. It is used when no target is given.
    public static OxygenTarget Host => OperatingSystem.IsWindows()
        ? WindowsMsvcX64
        : OperatingSystem.IsLinux()
            ? LinuxGnuX64
            : throw new PlatformNotSupportedException(
                "This platform has no default target. Pass --target with one of: " + string.Join(", ", SupportedTriples) + ".");

    // The target for a triple, which must be one of SupportedTriples exactly.
    public static OxygenTarget Parse(string triple)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triple);
        if (triple == WindowsMsvcX64.Triple)
            return WindowsMsvcX64;
        if (triple == LinuxGnuX64.Triple)
            return LinuxGnuX64;
        throw new ArgumentException(
            $"Unsupported target '{triple}'. Supported targets are: {string.Join(", ", SupportedTriples)}.", nameof(triple));
    }
}
