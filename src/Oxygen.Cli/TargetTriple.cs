using Oxygen.Llvm;

namespace Oxygen.Cli;

// The targets that --target accepts. System.CommandLine suggests these names and rejects any other value. Each name is the
// triple with underscores, since a C# identifier cannot contain a hyphen.
public enum TargetTriple
{
    x86_64_pc_windows_msvc,
    x86_64_unknown_linux_gnu
}

public static class TargetTripleExtensions
{
    // The target for the enum value, which is always one that OxygenTarget supports.
    public static OxygenTarget ToTarget(this TargetTriple triple) => OxygenTarget.Parse(triple switch
    {
        TargetTriple.x86_64_pc_windows_msvc => "x86_64-pc-windows-msvc",
        TargetTriple.x86_64_unknown_linux_gnu => "x86_64-unknown-linux-gnu",
        _ => throw new ArgumentOutOfRangeException(nameof(triple), triple, "Not a supported target.")
    });
}
