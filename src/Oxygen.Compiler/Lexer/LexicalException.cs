namespace Oxygen.Compiler.Lexer;

public sealed class LexicalException : Exception
{
    public LexicalException(string message, SourceLocation location)
        : base($"{message} at {location.Line}:{location.Column}.")
    {
        Location = location;
    }

    public SourceLocation Location { get; }
}