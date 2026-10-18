namespace Oxygen.Cli;

internal static class ExitCodes
{
    public const int Success = 0;

    // The program could not be compiled: errors in the source, unreadable inputs, or a native toolchain failure.
    public const int CompilationFailed = 1;

    // The compiler failed on a program that passed semantic analysis, or hit an internal error.
    public const int InternalError = 2;
}
