using System.Linq;
using Oxygen.Compiler;
using Oxygen.Compiler.Lexer;
using Serilog;
using Xunit;

namespace Oxygen.Compiler.Tests;

public sealed class LexerTests
{
    private readonly Tokenizer _lexer = new(Log.Logger);

    [Fact]
    public void TokenizesMainFunctionAndExpression()
    {
        var tokens = _lexer.Tokenize("i32 main() { return 1 + 2; }");

        Assert.Equal(
            [
                TokenKind.I32,
                TokenKind.Identifier,
                TokenKind.OpenParenthesis,
                TokenKind.CloseParenthesis,
                TokenKind.OpenBrace,
                TokenKind.Return,
                TokenKind.IntegerLiteral,
                TokenKind.Plus,
                TokenKind.IntegerLiteral,
                TokenKind.Semicolon,
                TokenKind.CloseBrace,
                TokenKind.EndOfFile
            ],
            tokens.Select(token => token.Kind));
    }

    [Fact]
    public void TokenizesPrimitiveTypesAndOperators()
    {
        var tokens = _lexer.Tokenize("u64 f64 bool void == != <= >= && ||");

        Assert.Equal(
            [
                TokenKind.U64,
                TokenKind.F64,
                TokenKind.Bool,
                TokenKind.Void,
                TokenKind.EqualsEquals,
                TokenKind.BangEquals,
                TokenKind.LessThanOrEquals,
                TokenKind.GreaterThanOrEquals,
                TokenKind.AmpersandAmpersand,
                TokenKind.PipePipe,
                TokenKind.EndOfFile
            ],
            tokens.Select(token => token.Kind));
    }

    [Fact]
    public void SkipsCommentsAndTracksLocations()
    {
        var tokens = _lexer.Tokenize("// ignored\n/* ignored */ i32 value;");

        Assert.Equal(new SourceLocation(2, 15), tokens[0].Location);
        Assert.Equal(new SourceLocation(2, 19), tokens[1].Location);
    }

    [Fact]
    public void ReportsInvalidCharacters()
    {
        var exception = Assert.Throws<LexicalException>(() => _lexer.Tokenize("i32 @"));

        Assert.Equal(new SourceLocation(1, 5), exception.Location);
    }
}
