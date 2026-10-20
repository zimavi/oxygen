using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Parsing;

public sealed partial class Parser
{
    private Token Consume(TokenKind kind, string message)
    {
        if (Check(kind))
            return Advance();

        throw Error(DiagnosticCode.ExpectedToken, $"{message} Found {Describe(Current)} instead.", Current);
    }

    private bool Match(TokenKind kind)
    {
        if (!Check(kind))
            return false;

        Advance();
        return true;
    }

    private bool Check(TokenKind kind) => Current.Kind == kind;

    private Token Advance()
    {
        var token = Current;
        if (_position < _tokens.Count - 1)
            _position++;

        return token;
    }

    private Token Peek(int offset) =>
        _position + offset < _tokens.Count ? _tokens[_position + offset] : _tokens[^1];

    private Token Current => _tokens[_position];

    private Token Previous => _tokens[Math.Max(0, _position - 1)];

    private static ParseException Error(string message, Token token) => new(message, token);

    // A parse error with a diagnostic code, which chooses its advice.
    private static ParseException Error(string code, string message, Token token) => new(message, token, code);

    private static string Describe(Token token) =>
        token.Kind == TokenKind.EndOfFile
            ? "the end of the file"
            : $"'{token.Lexeme}' ({token.Kind})";

    private void Synchronize()
    {
        while (!Check(TokenKind.EndOfFile))
        {
            if (Match(TokenKind.CloseBrace))
                return;

            if (IsType(Current.Kind) || Check(TokenKind.Void))
                return;

            Advance();
        }
    }
}
