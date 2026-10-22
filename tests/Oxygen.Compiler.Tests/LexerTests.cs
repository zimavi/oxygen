using System.Linq;
using Oxygen.Compiler;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;
using Xunit;

namespace Oxygen.Compiler.Tests;

public sealed class LexerTests
{
    private readonly Tokenizer _lexer = new(Log.Logger);

    [Fact]
    public void TokenizesControlFlowKeywords()
    {
        var kinds = _lexer.Tokenize("if else while for break continue")
            .Select(token => token.Kind)
            .ToArray();

        Assert.Equal(
            [TokenKind.If, TokenKind.Else, TokenKind.While, TokenKind.For, TokenKind.Break, TokenKind.Continue,
                TokenKind.EndOfFile],
            kinds);
    }

    // `->` is one token, and `--` followed by `>` is still a decrement and a comparison.
    [Fact]
    public void TokenizesArrowAsOneTokenAndKeepsDecrementBeforeGreaterThan()
    {
        var kinds = _lexer.Tokenize("a->b a-->b")
            .Select(token => token.Kind)
            .ToArray();

        Assert.Equal(
            [TokenKind.Identifier, TokenKind.Arrow, TokenKind.Identifier,
                TokenKind.Identifier, TokenKind.MinusMinus, TokenKind.GreaterThan, TokenKind.Identifier,
                TokenKind.EndOfFile],
            kinds);
    }

    [Fact]
    public void TokenizesAsKeyword()
    {
        Assert.Equal(
            [TokenKind.As, TokenKind.Identifier, TokenKind.EndOfFile],
            _lexer.Tokenize("as Child").Select(token => token.Kind));
    }

    [Fact]
    public void TokenizesExceptionKeywords()
    {
        Assert.Equal(
            [TokenKind.Throw, TokenKind.Try, TokenKind.OpenBrace, TokenKind.Catch,
                TokenKind.OpenParenthesis, TokenKind.Identifier, TokenKind.Identifier,
                TokenKind.CloseParenthesis, TokenKind.OpenBrace, TokenKind.CloseBrace,
                TokenKind.Finally, TokenKind.OpenBrace, TokenKind.CloseBrace,
                TokenKind.EndOfFile],
            _lexer.Tokenize("throw try { catch (Exception error) { } finally { }").Select(token => token.Kind));
    }

    [Fact]
    public void TokenizesThisKeyword()
    {
        Assert.Equal(
            [TokenKind.This, TokenKind.Dot, TokenKind.Identifier, TokenKind.EndOfFile],
            _lexer.Tokenize("this.value").Select(token => token.Kind));
    }

    [Fact]
    public void TokenizesTypeKeyword()
    {
        var kinds = _lexer.Tokenize("type Name = u64;")
            .Select(token => token.Kind)
            .ToArray();

        Assert.Equal(
            [TokenKind.Type, TokenKind.Identifier, TokenKind.Equals, TokenKind.U64, TokenKind.Semicolon, TokenKind.EndOfFile],
            kinds);
    }

    [Fact]
    public void TokenizesEnumKeyword()
    {
        Assert.Equal(
            [TokenKind.Enum, TokenKind.Identifier, TokenKind.Colon, TokenKind.I32, TokenKind.OpenBrace,
                TokenKind.Identifier, TokenKind.CloseBrace, TokenKind.EndOfFile],
            _lexer.Tokenize("enum Color : i32 { Red }").Select(token => token.Kind));
    }

    [Fact]
    public void TokenizesClassInterfaceAndConstructionSyntax()
    {
        Assert.Equal(
            [TokenKind.Class, TokenKind.Identifier, TokenKind.Colon, TokenKind.Identifier, TokenKind.Comma,
                TokenKind.Identifier, TokenKind.OpenBrace, TokenKind.Tilde, TokenKind.Identifier, TokenKind.OpenParenthesis,
                TokenKind.CloseParenthesis, TokenKind.New, TokenKind.Identifier,
                TokenKind.OpenParenthesis, TokenKind.CloseParenthesis, TokenKind.CloseBrace, TokenKind.EndOfFile],
            _lexer.Tokenize("class Child : Base, Printable { ~Child() new Type() }").Select(token => token.Kind));
    }

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
    public void TokenizesConditionalExpressionPunctuation()
    {
        Assert.Equal(
            [TokenKind.True, TokenKind.QuestionMark, TokenKind.IntegerLiteral, TokenKind.Colon,
                TokenKind.IntegerLiteral, TokenKind.EndOfFile],
            _lexer.Tokenize("true ? 1 : 0").Select(token => token.Kind));
    }

    [Fact]
    public void TokenizesNumericSuffixesAndSizeTypes()
    {
        var tokens = _lexer.Tokenize("1u64 1f32 usize isize");
        Assert.Equal(
            [TokenKind.IntegerLiteral, TokenKind.FloatingPointLiteral, TokenKind.USize, TokenKind.ISize,
                TokenKind.EndOfFile],
            tokens.Select(token => token.Kind));
        Assert.Equal("1u64", tokens[0].Lexeme);
        Assert.Equal("1f32", tokens[1].Lexeme);
    }

    [Fact]
    public void TokenizesAddressOfWithoutConfusingLogicalAnd()
    {
        var tokens = _lexer.Tokenize("i32* value = &other && flag;");

        Assert.Contains(tokens, token => token.Kind == TokenKind.Ampersand);
        Assert.Contains(tokens, token => token.Kind == TokenKind.AmpersandAmpersand);
    }

    [Fact]
    public void TokenizesExtendedArithmeticOperators()
    {
        Assert.Equal(
            [
                TokenKind.PlusPlus, TokenKind.MinusMinus, TokenKind.PlusEquals, TokenKind.MinusEquals,
                TokenKind.StarEquals, TokenKind.SlashEquals, TokenKind.PercentEquals,
                TokenKind.AmpersandEquals, TokenKind.PipeEquals, TokenKind.CaretEquals,
                TokenKind.LessThan, TokenKind.LessThan, TokenKind.GreaterThan, TokenKind.GreaterThan,
                TokenKind.EndOfFile
            ],
            _lexer.Tokenize("++ -- += -= *= /= %= &= |= ^= << >>").Select(token => token.Kind));
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
        var exception = Assert.Throws<LexicalException>(() => _lexer.Tokenize("i32 #"));

        Assert.Equal(new SourceLocation(1, 5), exception.Location);
    }

    [Fact]
    public void AcceptsTheSupportedStringEscapes()
    {
        var token = _lexer.Tokenize("\"a\\n\\t\\\\\\\"\\/\\b\\f\\r\"")[0];

        Assert.Equal(TokenKind.StringLiteral, token.Kind);
    }

    [Fact]
    public void ReportsAnUnsupportedStringEscapeAtItsColumn()
    {
        var exception = Assert.Throws<LexicalException>(() => _lexer.Tokenize("\"bad \\q\""));

        Assert.Contains("Unsupported escape sequence '\\q'", exception.Message);
        Assert.Equal(new SourceLocation(1, 6), exception.Location);
    }

    [Fact]
    public void ReportsAUnicodeEscapeWithoutFourDigits()
    {
        var exception = Assert.Throws<LexicalException>(() => _lexer.Tokenize("\"\\u00G1\""));

        Assert.Contains("four hexadecimal digits", exception.Message);
    }

    [Fact]
    public void ReportsALoneSurrogateEscape()
    {
        var exception = Assert.Throws<LexicalException>(() => _lexer.Tokenize("\"\\uD800\""));

        Assert.Contains("surrogate", exception.Message);
    }

    [Fact]
    public void DecodesEscapesIncludingAnEmbeddedNulAndASurrogatePair()
    {
        var value = StringLiteralDecoder.Decode("\"a\\u0000b\\uD83D\\uDE00\"", new SourceLocation(1, 1));

        Assert.Equal("a\0b\uD83D\uDE00", value);
    }

    [Fact]
    public void TokenizesHexadecimalAndBinaryIntegersAsDecimalText()
    {
        var tokens = _lexer.Tokenize("0x1F 0xffu8 0b101").ToList();

        Assert.Equal(new[] { "31", "255u8", "5" }, tokens.Where(token => token.Kind == TokenKind.IntegerLiteral).Select(token => token.Lexeme));
    }

    [Fact]
    public void TokenizesExponentsAsFloatingPointLiterals()
    {
        var tokens = _lexer.Tokenize("1e5 2.5E-3 4e+2f32").ToList();

        Assert.All(tokens.Take(3), token => Assert.Equal(TokenKind.FloatingPointLiteral, token.Kind));
        Assert.Equal(new[] { "1e5", "2.5E-3", "4e+2f32" }, tokens.Take(3).Select(token => token.Lexeme));
    }

    [Fact]
    public void RadixLiteralWithoutDigitsIsRejected()
    {
        Assert.Throws<LexicalException>(() => _lexer.Tokenize("0x").ToList());
    }

    [Theory]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("2.5E-")]
    public void ExponentWithoutDigitsIsRejectedWithItsOwnCode(string source)
    {
        var exception = Assert.Throws<LexicalException>(() => _lexer.Tokenize(source).ToList());

        Assert.Equal("An exponent needs digits after 'e'", exception.Message);
        Assert.Equal(DiagnosticCode.ExponentDigits, exception.Code);
    }

    [Theory]
    [InlineData("'a'", "97u8")]
    [InlineData("' '", "32u8")]
    [InlineData("'\"'", "34u8")]
    [InlineData("'\\''", "39u8")]
    [InlineData("'\\\\'", "92u8")]
    [InlineData("'\\n'", "10u8")]
    [InlineData("'\\t'", "9u8")]
    public void CharacterLiteralIsTheU8ValueOfItsByte(string source, string expected)
    {
        var token = _lexer.Tokenize(source).First();

        Assert.Equal(TokenKind.IntegerLiteral, token.Kind);
        Assert.Equal(expected, token.Lexeme);
    }

    [Theory]
    [InlineData("''", "one character")]
    [InlineData("'ab'", "one character")]
    [InlineData("'é'", "ASCII character")]
    [InlineData("'\\q'", "Unknown escape")]
    [InlineData("'a", "Unterminated character literal")]
    [InlineData("'a\n'", "Unterminated character literal")]
    public void MalformedCharacterLiteralIsRejectedWithItsOwnCode(string source, string messageFragment)
    {
        var exception = Assert.Throws<LexicalException>(() => _lexer.Tokenize(source).ToList());

        Assert.Contains(messageFragment, exception.Message);
        Assert.Equal(DiagnosticCode.CharLiteral, exception.Code);
    }

    [Fact]
    public void MalformedCharacterLiteralIsReportedOnceAndLexingContinues()
    {
        var result = _lexer.TokenizeWithDiagnostics("i32 x = 'ab'; i32 y = 1;", "main.oxy");

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCode.CharLiteral, diagnostic.Code);
        Assert.Contains(result.Tokens, token => token.Lexeme == "y");
        Assert.Contains(result.Tokens, token => token.Lexeme == "1");
    }
}
