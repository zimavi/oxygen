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

    // Skips the rest of a statement that failed to parse. It stops after the statement's ';', or before the '}' that closes
    // the enclosing block. Braces inside the statement are skipped as a unit, so an inner block's '}' does not end it.
    private void SynchronizeStatement()
    {
        var depth = 0;
        while (!Check(TokenKind.EndOfFile))
        {
            if (depth == 0 && Check(TokenKind.CloseBrace))
                return;

            if (depth == 0 && Match(TokenKind.Semicolon))
                return;

            if (Check(TokenKind.OpenBrace))
                depth++;
            else if (Check(TokenKind.CloseBrace))
                depth--;

            Advance();
        }
    }

    // Records a syntax error in the diagnostics of ParseWithDiagnostics.
    private void Report(ParseException exception) =>
        _diagnostics!.Add(
            DiagnosticPhase.Parsing,
            exception.Message,
            exception.Token.Location,
            Math.Max(1, exception.Token.Lexeme.Length),
            _sourceName,
            exception.Code);
}
