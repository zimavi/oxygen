using System.Text;
using System.Text.RegularExpressions;

namespace Oxygen.Compiler.Diagnostics;

public sealed class DiagnosticFormatter
{
    public string Format(Diagnostic diagnostic, SourceText source, bool useColor = false)
    {
        var lines = source.Text.Replace("\r\n", "\n").Split('\n');
        var lineIndex = Math.Clamp(diagnostic.Span.Start.Line - 1, 0, Math.Max(0, lines.Length - 1));
        var start = Math.Clamp(diagnostic.Span.Start.Column - 1, 0, lines[lineIndex].Length);
        var sourceLine = lines[lineIndex];
        var underlineLength = Math.Min(
            Math.Max(1, diagnostic.Span.Length),
            Math.Max(1, sourceLine.Length - start));
        var lineNumber = diagnostic.Span.Start.Line.ToString();
        var prefix = $"{source.Name}:{diagnostic.Span.Start.Line}:{diagnostic.Span.Start.Column}: error: ";
        var errorColor = "\u001b[31m";
        var reset = "\u001b[0m";
        var gutterColor = "\u001b[90m";
        var helpColor = "\u001b[36m";
        var builder = new StringBuilder();

        if (useColor)
        {
            builder.Append(errorColor).Append(prefix).Append(diagnostic.Message).Append(reset);
        }
        else
        {
            builder.Append(prefix).Append(diagnostic.Message);
        }

        builder.AppendLine();
        if (lineIndex > 0)
        {
            AppendSourceLine(builder, lines[lineIndex - 1], lineIndex, useColor, gutterColor, reset);
            builder.AppendLine();
        }

        AppendSourceLine(builder, sourceLine, diagnostic.Span.Start.Line, useColor, gutterColor, reset,
            start, underlineLength, errorColor);
        builder.AppendLine();

        AppendGutter(builder, lineNumber, useColor, gutterColor, reset);
        builder.Append(' ', start)
            .Append(useColor ? errorColor : string.Empty)
            .Append('^')
            .Append('~', Math.Max(0, underlineLength - 1))
            .Append(useColor ? reset : string.Empty);
        if (lineIndex + 1 < lines.Length)
        {
            builder.AppendLine();
            AppendSourceLine(builder, lines[lineIndex + 1], lineIndex + 2, useColor, gutterColor, reset);
        }

        if (!string.IsNullOrWhiteSpace(diagnostic.Explanation))
        {
            builder.AppendLine()
                .Append(useColor ? helpColor : string.Empty)
                .Append("help: ")
                .Append(diagnostic.Explanation)
                .Append(useColor ? reset : string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(diagnostic.Suggestion))
        {
            builder.AppendLine()
                .Append(useColor ? helpColor : string.Empty)
                .Append("fix: ")
                .Append(diagnostic.Suggestion)
                .Append(useColor ? reset : string.Empty);
        }

        return builder.ToString();
    }

    public string FormatAll(
        IEnumerable<Diagnostic> diagnostics,
        SourceText source,
        bool useColor = false) =>
        string.Join(Environment.NewLine, diagnostics.Select(diagnostic => Format(diagnostic, source, useColor)));

    private static void AppendSourceLine(
        StringBuilder builder,
        string sourceLine,
        int lineNumber,
        bool useColor,
        string gutterColor,
        string reset,
        int? errorStart = null,
        int errorLength = 0,
        string errorColor = "")
    {
        AppendGutter(builder, lineNumber.ToString(), useColor, gutterColor, reset);
        if (!useColor)
        {
            builder.Append(sourceLine);
            return;
        }

        var errorEnd = errorStart.GetValueOrDefault() + errorLength;
        var index = 0;
        while (index < sourceLine.Length)
        {
            var tokenEnd = NextTokenEnd(sourceLine, index);
            var tokenColor = TokenColor(sourceLine[index..tokenEnd]);
            AppendStyledRange(builder, sourceLine, index, tokenEnd, errorStart, errorEnd, tokenColor, errorColor, reset);
            index = tokenEnd;
        }
    }

    private static void AppendGutter(
        StringBuilder builder,
        string lineNumber,
        bool useColor,
        string gutterColor,
        string reset)
    {
        if (useColor)
        {
            builder.Append(gutterColor).Append("  ").Append(lineNumber).Append(" | ").Append(reset);
        }
        else
        {
            builder.Append("  ").Append(lineNumber).Append(" | ");
        }
    }

    private static void AppendStyledRange(
        StringBuilder builder,
        string line,
        int start,
        int end,
        int? errorStart,
        int errorEnd,
        string tokenColor,
        string errorColor,
        string reset)
    {
        var cursor = start;
        while (cursor < end)
        {
            var inError = errorStart.HasValue && cursor >= errorStart.Value && cursor < errorEnd;
            var next = inError
                ? Math.Min(end, errorEnd)
                : errorStart.HasValue && errorStart.Value > cursor && errorStart.Value < end
                    ? errorStart.Value
                    : end;
            var color = inError ? errorColor : tokenColor;
            builder.Append(color).Append(line[cursor..next]).Append(reset);
            cursor = next;
        }
    }

    private static int NextTokenEnd(string line, int start)
    {
        if (line[start] == '/' && start + 1 < line.Length && line[start + 1] == '/')
        {
            return line.Length;
        }

        if (line[start] == '"')
        {
            var end = start + 1;
            while (end < line.Length && line[end] != '"')
            {
                end += line[end] == '\\' ? 2 : 1;
            }

            return Math.Min(line.Length, end + 1);
        }

        if (char.IsLetter(line[start]) || line[start] == '_')
        {
            var end = start + 1;
            while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
            {
                end++;
            }

            return end;
        }

        if (char.IsDigit(line[start]))
        {
            var end = start + 1;
            while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '.'))
            {
                end++;
            }

            return end;
        }

        return start + 1;
    }

    private static string TokenColor(string token)
    {
        if (token.StartsWith("//", StringComparison.Ordinal))
        {
            return "\u001b[90m";
        }

        if (token.StartsWith('"'))
        {
            return "\u001b[32m";
        }

        if (Regex.IsMatch(token, @"^\d"))
        {
            return "\u001b[33m";
        }

        if (token is "true" or "false")
        {
            return "\u001b[33m";
        }

        if (token is "return" or "var" or "const")
        {
            return "\u001b[35m";
        }

        if (token is "i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64"
            or "f32" or "f64" or "bool" or "void")
        {
            return "\u001b[36m";
        }

        if (token.Length == 1 && "{}(),;+-*/%=!<>|&".Contains(token[0]))
        {
            return "\u001b[37m";
        }

        return string.Empty;
    }
}
