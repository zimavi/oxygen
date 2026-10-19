using System.Globalization;
using System.Text;

namespace Oxygen.Compiler.Lexer;

// Decodes the text of a string literal, including its quotes. Escapes are \" \\ \/ \b \f \n \r \t and \uXXXX. A
// \uXXXX escape of a surrogate must form a valid pair, so the decoded value is always well-formed UTF-16. The lexer
// validates every literal with this decoder, so the backend can decode a lexed literal without checking it again.
public static class StringLiteralDecoder
{
    public static string Decode(string lexeme, SourceLocation location)
    {
        var contentEnd = lexeme.Length - 1;
        var value = new StringBuilder(lexeme.Length);
        for (var index = 1; index < contentEnd; index++)
        {
            var character = lexeme[index];
            if (character != '\\')
            {
                value.Append(character);
                continue;
            }

            var escapeLocation = new SourceLocation(location.Line, location.Column + index);
            if (index + 1 >= contentEnd)
                throw new LexicalException("Unterminated string literal", location);

            switch (lexeme[index + 1])
            {
                case '"':
                case '\\':
                case '/':
                    value.Append(lexeme[index + 1]);
                    index++;
                    break;
                case 'b':
                    value.Append('\b');
                    index++;
                    break;
                case 'f':
                    value.Append('\f');
                    index++;
                    break;
                case 'n':
                    value.Append('\n');
                    index++;
                    break;
                case 'r':
                    value.Append('\r');
                    index++;
                    break;
                case 't':
                    value.Append('\t');
                    index++;
                    break;
                case 'u':
                    var unit = ReadUnicodeEscape(lexeme, index, location);
                    if (char.IsLowSurrogate(unit))
                        throw new LexicalException(
                            "A low surrogate \\u escape must follow a high surrogate \\u escape", escapeLocation);
                    if (char.IsHighSurrogate(unit))
                    {
                        var pairStart = index + 6;
                        if (pairStart + 1 >= contentEnd || lexeme[pairStart] != '\\' || lexeme[pairStart + 1] != 'u')
                            throw new LexicalException(
                                "A high surrogate \\u escape must be followed by a low surrogate \\u escape", escapeLocation);
                        var low = ReadUnicodeEscape(lexeme, pairStart, location);
                        if (!char.IsLowSurrogate(low))
                            throw new LexicalException(
                                "A high surrogate \\u escape must be followed by a low surrogate \\u escape", escapeLocation);
                        value.Append(unit).Append(low);
                        index = pairStart + 5;
                    }
                    else
                    {
                        value.Append(unit);
                        index += 5;
                    }
                    break;
                default:
                    throw new LexicalException(
                        $"Unsupported escape sequence '\\{lexeme[index + 1]}' in string literal", escapeLocation);
            }
        }

        return value.ToString();
    }

    // Reads the four hexadecimal digits of the \uXXXX escape whose backslash is at `start`.
    private static char ReadUnicodeEscape(string lexeme, int start, SourceLocation location)
    {
        var digits = start + 2;
        var contentEnd = lexeme.Length - 1;
        var escapeLocation = new SourceLocation(location.Line, location.Column + start);
        if (digits + 4 > contentEnd
            || !int.TryParse(lexeme.AsSpan(digits, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                out var code))
            throw new LexicalException("A \\u escape requires four hexadecimal digits", escapeLocation);
        return (char)code;
    }
}
