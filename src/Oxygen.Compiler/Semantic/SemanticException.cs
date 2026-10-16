using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

public sealed class SemanticException : Exception
{
    public SemanticException(string message, SourceLocation location, int length = 1)
        : base(message)
    {
        Location = location;
        Length = Math.Max(1, length);
    }

    public SourceLocation Location { get; }

    public int Length { get; }
}
