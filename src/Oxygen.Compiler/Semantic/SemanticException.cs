using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

public sealed class SemanticException : Exception
{
    public SemanticException(string message, SourceLocation location, int length = 1, string? code = null)
        : base(message)
    {
        Location = location;
        Length = Math.Max(1, length);
        Code = code;
    }

    // The diagnostic code of the error, which chooses its advice. Null when the error has no code.
    public string? Code { get; }

    public SourceLocation Location { get; }

    public int Length { get; }

    // Set when the error was raised while analyzing a declaration from another source file, such as
    // a generic instantiated from a caller elsewhere; the location belongs to that file.
    public bool IsSourceAttributed { get; init; }

    public string? SourceName { get; init; }
}
