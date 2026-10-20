namespace Oxygen.Compiler.Lexer;

public sealed class LexicalException : Exception
{
    public LexicalException(string message, SourceLocation location, string? code = null)
        : base(message)
    {
        Location = location;
        Code = code;
    }

    public SourceLocation Location { get; }

    // The diagnostic code of the error, which chooses its advice. Null when the error has no code.
    public string? Code { get; }

    // An error with a diagnostic code.
    public static LexicalException WithCode(string code, string message, SourceLocation location) =>
        new(message, location, code);
}
