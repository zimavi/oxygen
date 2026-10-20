using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Parsing;

public sealed class ParseException : Exception
{
    public ParseException(string message, Token token, string? code = null)
        : base(message)
    {
        Token = token;
        Code = code;
    }

    public Token Token { get; }

    // The diagnostic code of the error, which chooses its advice. Null when the error has no code.
    public string? Code { get; }
}
