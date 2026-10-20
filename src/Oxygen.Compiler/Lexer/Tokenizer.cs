using Oxygen.Compiler.Diagnostics;
using Serilog;

namespace Oxygen.Compiler.Lexer;

public sealed class Tokenizer
{
    private static readonly IReadOnlyDictionary<string, TokenKind> Keywords =
        new Dictionary<string, TokenKind>(StringComparer.Ordinal)
        {
            ["true"] = TokenKind.True,
            ["false"] = TokenKind.False,
            ["null"] = TokenKind.Null,
            ["return"] = TokenKind.Return,
            ["if"] = TokenKind.If,
            ["else"] = TokenKind.Else,
            ["while"] = TokenKind.While,
            ["for"] = TokenKind.For,
            ["in"] = TokenKind.In,
            ["break"] = TokenKind.Break,
            ["continue"] = TokenKind.Continue,
            ["throw"] = TokenKind.Throw,
            ["delete"] = TokenKind.Delete,
            ["try"] = TokenKind.Try,
            ["catch"] = TokenKind.Catch,
            ["finally"] = TokenKind.Finally,
            ["this"] = TokenKind.This,
            ["base"] = TokenKind.Base,
            ["module"] = TokenKind.Module,
            ["import"] = TokenKind.Import,
            ["public"] = TokenKind.Public,
            ["private"] = TokenKind.Private,
            ["protected"] = TokenKind.Protected,
            ["internal"] = TokenKind.Internal,
            ["var"] = TokenKind.Var,
            ["const"] = TokenKind.Const,
            ["extern"] = TokenKind.Extern,
            ["virtual"] = TokenKind.Virtual,
            ["override"] = TokenKind.Override,
            ["i8"] = TokenKind.I8,
            ["i16"] = TokenKind.I16,
            ["i32"] = TokenKind.I32,
            ["i64"] = TokenKind.I64,
            ["u8"] = TokenKind.U8,
            ["u16"] = TokenKind.U16,
            ["u32"] = TokenKind.U32,
            ["u64"] = TokenKind.U64,
            ["usize"] = TokenKind.USize,
            ["isize"] = TokenKind.ISize,
            ["f32"] = TokenKind.F32,
            ["f64"] = TokenKind.F64,
            ["bool"] = TokenKind.Bool,
            ["void"] = TokenKind.Void,
            ["string"] = TokenKind.String,
            ["struct"] = TokenKind.Struct,
            ["class"] = TokenKind.Class,
            ["interface"] = TokenKind.Interface,
            ["new"] = TokenKind.New,
            ["enum"] = TokenKind.Enum,
            ["union"] = TokenKind.Union,
            ["switch"] = TokenKind.Switch,
            ["case"] = TokenKind.Case,
            ["default"] = TokenKind.Default,
            ["is"] = TokenKind.Is,
            ["as"] = TokenKind.As,
            ["cast"] = TokenKind.Cast,
            ["sizeof"] = TokenKind.SizeOf
        };

    private readonly ILogger _logger;

    public Tokenizer(ILogger? logger = null)
    {
        _logger = logger ?? new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Console()
            .CreateLogger();
    }

    public IReadOnlyList<Token> Tokenize(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            return TokenizeCore(source);
        }
        catch (LexicalException exception)
        {
            _logger.Error(exception, "Lexical error at {Line}:{Column}", exception.Location.Line,
                exception.Location.Column);
            throw;
        }
    }

    public TokenizationResult TokenizeWithDiagnostics(string source, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var diagnostics = new DiagnosticBag();
        return new TokenizationResult(TokenizeCore(source, diagnostics, sourceName), diagnostics.Diagnostics);
    }

    private IReadOnlyList<Token> TokenizeCore(
        string source,
        DiagnosticBag? diagnostics = null,
        string? sourceName = null)
    {
        var tokens = new List<Token>();
        var position = 0;
        var line = 1;
        var column = 1;

        while (position < source.Length)
        {
            if (char.IsWhiteSpace(source[position]))
            {
                Advance(source[position], ref position, ref line, ref column);
                continue;
            }

            var location = new SourceLocation(line, column);

            if (source[position] == '/' && Peek(source, position + 1) == '/')
            {
                SkipLineComment(source, ref position, ref line, ref column);
                continue;
            }

            if (source[position] == '/' && Peek(source, position + 1) == '*')
            {
                try
                {
                    SkipBlockComment(source, ref position, ref line, ref column, location);
                }
                catch (LexicalException exception) when (diagnostics is not null)
                {
                    diagnostics.Add(
                        DiagnosticPhase.Lexing,
                        exception.Message,
                        exception.Location,
                        sourceName: sourceName, code: exception.Code);
                    if (position < source.Length)
                        Advance(source[position], ref position, ref line, ref column);
                }

                continue;
            }

            try
            {
                Token token;
                if (IsIdentifierStart(source[position]))
                {
                    token = ReadIdentifierOrKeyword(source, ref position, ref line, ref column, location);
                }
                else if (char.IsDigit(source[position]))
                {
                    token = ReadNumber(source, ref position, ref line, ref column, location);
                }
                else if (source[position] == '"')
                {
                    token = ReadString(source, ref position, ref line, ref column, location);
                }
                else
                {
                    token = ReadOperatorOrPunctuation(source, ref position, ref line, ref column, location);
                }

                tokens.Add(token);
                _logger.Debug("Lexed {TokenKind} '{Lexeme}' at {Line}:{Column}", token.Kind, token.Lexeme,
                    token.Location.Line, token.Location.Column);
            }
            catch (LexicalException exception) when (diagnostics is not null)
            {
                diagnostics.Add(DiagnosticPhase.Lexing, exception.Message, exception.Location, sourceName: sourceName, code: exception.Code);
                if (position < source.Length)
                {
                    Advance(source[position], ref position, ref line, ref column);
                }
            }
        }

        var end = new Token(TokenKind.EndOfFile, string.Empty, new SourceLocation(line, column));
        tokens.Add(end);
        _logger.Information("Lexed {TokenCount} tokens.", tokens.Count - 1);
        return tokens;
    }

    private Token ReadIdentifierOrKeyword(
        string source, ref int position, ref int line, ref int column, SourceLocation location)
    {
        var start = position;
        while (position < source.Length && IsIdentifierPart(source[position]))
        {
            Advance(source[position], ref position, ref line, ref column);
        }

        var lexeme = source[start..position];
        return new Token(Keywords.GetValueOrDefault(lexeme, TokenKind.Identifier), lexeme, location);
    }

    private static Token ReadNumber(
        string source, ref int position, ref int line, ref int column, SourceLocation location)
    {
        var start = position;
        if (source[position] == '0' && Peek(source, position + 1) is 'x' or 'X' or 'b' or 'B')
            return ReadRadixInteger(source, ref position, ref line, ref column, location);
        while (position < source.Length && char.IsDigit(source[position]))
        {
            Advance(source[position], ref position, ref line, ref column);
        }

        var kind = TokenKind.IntegerLiteral;
        if (Peek(source, position) == '.')
        {
            kind = TokenKind.FloatingPointLiteral;
            Advance(source[position], ref position, ref line, ref column);
            if (!char.IsDigit(Peek(source, position)))
            {
                throw LexicalException.WithCode(DiagnosticCode.FloatingPointDigits, "A floating-point literal requires digits after the decimal point", location);
            }

            while (position < source.Length && char.IsDigit(source[position]))
            {
                Advance(source[position], ref position, ref line, ref column);
            }
        }

        // An exponent makes a floating-point literal, as in 1e5 or 2.5E-3. An `e` with no digits after it is an error,
        // not an identifier suffix, so the message names the missing digits.
        if (Peek(source, position) is 'e' or 'E')
        {
            var signed = Peek(source, position + 1) is '+' or '-';
            if (!char.IsDigit(Peek(source, signed ? position + 2 : position + 1)))
                throw LexicalException.WithCode(DiagnosticCode.ExponentDigits, "An exponent needs digits after 'e'", new SourceLocation(line, column));
            kind = TokenKind.FloatingPointLiteral;
            Advance(source[position], ref position, ref line, ref column);
            if (signed)
                Advance(source[position], ref position, ref line, ref column);
            while (position < source.Length && char.IsDigit(source[position]))
            {
                Advance(source[position], ref position, ref line, ref column);
            }
        }

        if (IsIdentifierStart(Peek(source, position)))
        {
            var suffixStart = position;
            while (position < source.Length && IsIdentifierPart(source[position]))
                Advance(source[position], ref position, ref line, ref column);
            var suffix = source[suffixStart..position];
            if (suffix is "f32" or "f64")
                kind = TokenKind.FloatingPointLiteral;
            var valid = kind == TokenKind.FloatingPointLiteral
                ? suffix is "f32" or "f64"
                : suffix is "i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64"
                    or "usize" or "isize";
            if (!valid)
                throw LexicalException.WithCode(DiagnosticCode.NumericSuffix, "Invalid numeric literal suffix", new SourceLocation(line, column));
        }

        return new Token(kind, source[start..position], location);
    }

    // A hexadecimal (0x) or binary (0b) integer. Its digits are converted to decimal text, which the rest of the compiler reads,
    // so the token's lexeme is decimal digits followed by the suffix.
    private static Token ReadRadixInteger(
        string source, ref int position, ref int line, ref int column, SourceLocation location)
    {
        var hexadecimal = char.ToLowerInvariant(Peek(source, position + 1)) == 'x';
        Advance(source[position], ref position, ref line, ref column);
        Advance(source[position], ref position, ref line, ref column);
        var digitsStart = position;
        while (position < source.Length && (hexadecimal ? Uri.IsHexDigit(source[position]) : source[position] is '0' or '1'))
            Advance(source[position], ref position, ref line, ref column);
        if (position == digitsStart)
            throw LexicalException.WithCode(DiagnosticCode.RadixDigits, "A hexadecimal or binary literal needs digits after its prefix", location);

        var value = System.Numerics.BigInteger.Zero;
        foreach (var digit in source[digitsStart..position])
            value = value * (hexadecimal ? 16 : 2) + Convert.ToInt32(digit.ToString(), 16);

        var suffixStart = position;
        while (position < source.Length && IsIdentifierPart(source[position]))
            Advance(source[position], ref position, ref line, ref column);
        var suffix = source[suffixStart..position];
        if (suffix.Length != 0 && suffix is not ("i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64"
                or "usize" or "isize"))
            throw LexicalException.WithCode(DiagnosticCode.NumericSuffix, "Invalid numeric literal suffix", location);
        return new Token(TokenKind.IntegerLiteral,
            value.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix, location);
    }

    private static Token ReadString(
        string source, ref int position, ref int line, ref int column, SourceLocation location)
    {
        var start = position;
        Advance(source[position], ref position, ref line, ref column);
        while (position < source.Length && source[position] != '"')
        {
            if (source[position] is '\r' or '\n')
            {
                throw LexicalException.WithCode(DiagnosticCode.UnterminatedString, "Unterminated string literal", location);
            }

            if (source[position] == '\\')
            {
                Advance(source[position], ref position, ref line, ref column);
                if (position >= source.Length)
                {
                    throw LexicalException.WithCode(DiagnosticCode.UnterminatedString, "Unterminated string literal", location);
                }
            }

            Advance(source[position], ref position, ref line, ref column);
        }

        if (position >= source.Length)
        {
            throw LexicalException.WithCode(DiagnosticCode.UnterminatedString, "Unterminated string literal", location);
        }

        Advance(source[position], ref position, ref line, ref column);
        var text = source[start..position];
        // Reports unsupported escapes here, so the backend can decode any lexed literal without failing.
        StringLiteralDecoder.Decode(text, location);
        return new Token(TokenKind.StringLiteral, text, location);
    }

    private static Token ReadOperatorOrPunctuation(
        string source, ref int position, ref int line, ref int column, SourceLocation location)
    {
        var first = source[position];
        var second = Peek(source, position + 1);
        var third = Peek(source, position + 2);
        if ((first, second, third) is ('<', '<', '=') or ('>', '>', '='))
        {
            var compoundShiftKind = first == '<' ? TokenKind.LessThanLessThanEquals : TokenKind.GreaterThanGreaterThanEquals;
            Advance(first, ref position, ref line, ref column);
            Advance(second, ref position, ref line, ref column);
            Advance(third, ref position, ref line, ref column);
            return new Token(compoundShiftKind, source[(position - 3)..position], location);
        }
        if ((first, second) is ('+', '+') or ('-', '-') or ('+', '=') or ('-', '=')
            or ('*', '=') or ('/', '=') or ('%', '=') or ('&', '=') or ('|', '=') or ('^', '='))
        {
            var compoundKind = (first, second) switch
            {
                ('+', '+') => TokenKind.PlusPlus,
                ('-', '-') => TokenKind.MinusMinus,
                ('+', '=') => TokenKind.PlusEquals,
                ('-', '=') => TokenKind.MinusEquals,
                ('*', '=') => TokenKind.StarEquals,
                ('/', '=') => TokenKind.SlashEquals,
                ('%', '=') => TokenKind.PercentEquals,
                ('&', '=') => TokenKind.AmpersandEquals,
                ('|', '=') => TokenKind.PipeEquals,
                _ => TokenKind.CaretEquals
            };
            Advance(first, ref position, ref line, ref column);
            Advance(second, ref position, ref line, ref column);
            return new Token(compoundKind, source[(position - 2)..position], location);
        }
        var kind = (first, second) switch
        {
            ('=', '=') => TokenKind.EqualsEquals,
            ('!', '=') => TokenKind.BangEquals,
            ('<', '=') => TokenKind.LessThanOrEquals,
            ('>', '=') => TokenKind.GreaterThanOrEquals,
            ('&', '&') => TokenKind.AmpersandAmpersand,
            ('|', '|') => TokenKind.PipePipe,
            _ => first switch
            {
                '(' => TokenKind.OpenParenthesis,
                ')' => TokenKind.CloseParenthesis,
                '[' => TokenKind.OpenBracket,
                ']' => TokenKind.CloseBracket,
                '@' => TokenKind.At,
                '{' => TokenKind.OpenBrace,
                '}' => TokenKind.CloseBrace,
                ',' => TokenKind.Comma,
                ':' => TokenKind.Colon,
                '?' => TokenKind.QuestionMark,
                '.' => TokenKind.Dot,
                '~' => TokenKind.Tilde,
                ';' => TokenKind.Semicolon,
                '+' => TokenKind.Plus,
                '-' => TokenKind.Minus,
                '&' => TokenKind.Ampersand,
                '|' => TokenKind.Pipe,
                '^' => TokenKind.Caret,
                '*' => TokenKind.Star,
                '/' => TokenKind.Slash,
                '%' => TokenKind.Percent,
                '=' => TokenKind.Equals,
                '!' => TokenKind.Bang,
                '<' => TokenKind.LessThan,
                '>' => TokenKind.GreaterThan,
                _ => throw LexicalException.WithCode(DiagnosticCode.UnexpectedCharacter, $"Unexpected character '{first}'", location)
            }
        };

        Advance(first, ref position, ref line, ref column);
        if (kind is TokenKind.EqualsEquals or TokenKind.BangEquals or TokenKind.LessThanOrEquals
            or TokenKind.GreaterThanOrEquals or TokenKind.AmpersandAmpersand or TokenKind.PipePipe)
        {
            Advance(second, ref position, ref line, ref column);
        }

        var length = kind is TokenKind.EqualsEquals or TokenKind.BangEquals or TokenKind.LessThanOrEquals
            or TokenKind.GreaterThanOrEquals or TokenKind.AmpersandAmpersand or TokenKind.PipePipe
            ? 2
            : 1;
        return new Token(kind, source[(position - length)..position], location);
    }

    private static void SkipLineComment(string source, ref int position, ref int line, ref int column)
    {
        while (position < source.Length && source[position] is not '\r' and not '\n')
        {
            Advance(source[position], ref position, ref line, ref column);
        }
    }

    private static void SkipBlockComment(
        string source, ref int position, ref int line, ref int column, SourceLocation location)
    {
        Advance(source[position], ref position, ref line, ref column);
        Advance(source[position], ref position, ref line, ref column);
        while (position < source.Length && !(source[position] == '*' && Peek(source, position + 1) == '/'))
        {
            Advance(source[position], ref position, ref line, ref column);
        }

        if (position >= source.Length)
        {
            throw LexicalException.WithCode(DiagnosticCode.UnterminatedComment, "Unterminated block comment", location);
        }

        Advance(source[position], ref position, ref line, ref column);
        Advance(source[position], ref position, ref line, ref column);
    }

    private static bool IsIdentifierStart(char value) => char.IsLetter(value) || value == '_';

    private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static char Peek(string source, int position) =>
        position < source.Length ? source[position] : '\0';

    private static void Advance(char value, ref int position, ref int line, ref int column)
    {
        position++;
        if (value == '\n')
        {
            line++;
            column = 1;
        }
        else
        {
            column++;
        }
    }
}
