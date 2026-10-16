namespace Oxygen.Compiler.Lexer;

public readonly record struct Token(
    TokenKind Kind,
    string Lexeme,
    SourceLocation Location);