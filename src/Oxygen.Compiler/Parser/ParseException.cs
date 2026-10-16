using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Parsing;

public sealed class ParseException : Exception
{
    public ParseException(string message, Token token)
        : base($"{message} at {token.Location.Line}:{token.Location.Column}.")
    {
        Token = token;
    }

    public Token Token { get; }
}
