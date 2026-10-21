namespace Oxygen.Cli;

internal static class ExitCodes
{
    public const int Success = 0;

    // The program or its inputs are invalid: errors in the source, an unreadable source file, or unusable native metadata.
    public const int SourceError = 1;

    // The compiler failed on a program that passed semantic analysis, or hit an internal error.
    public const int InternalError = 2;

    // The native toolchain could not build the program: the runtime archive is missing, a native tool failed or did not finish,
    // or the host has no default target.
    public const int ToolchainFailure = 3;

    // The build was interrupted with Ctrl+C. 130 is the conventional exit code for an interrupt.
    public const int Interrupted = 130;
}
