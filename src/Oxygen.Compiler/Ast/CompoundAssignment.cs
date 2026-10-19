using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Ast;

// Compound assignment operators, such as +=, and the binary operator each applies to its target and value.
public static class CompoundAssignment
{
    public static bool IsCompound(TokenKind operatorKind) => operatorKind != TokenKind.Equals;

    public static TokenKind BinaryOperator(TokenKind operatorKind) => operatorKind switch
    {
        TokenKind.PlusEquals => TokenKind.Plus,
        TokenKind.MinusEquals => TokenKind.Minus,
        TokenKind.StarEquals => TokenKind.Star,
        TokenKind.SlashEquals => TokenKind.Slash,
        TokenKind.PercentEquals => TokenKind.Percent,
        TokenKind.AmpersandEquals => TokenKind.Ampersand,
        TokenKind.PipeEquals => TokenKind.Pipe,
        TokenKind.CaretEquals => TokenKind.Caret,
        TokenKind.LessThanLessThanEquals => TokenKind.LessThanLessThan,
        TokenKind.GreaterThanGreaterThanEquals => TokenKind.GreaterThanGreaterThan,
        _ => throw new InvalidOperationException($"'{operatorKind}' is not a compound assignment operator.")
    };
}
