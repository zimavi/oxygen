using System.Globalization;
using System.Numerics;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

// Constants: literal types and the compile-time constant values used by checks.
public sealed partial class SemanticAnalyzer
{
    // A suffixed literal has its suffix's type. An unsuffixed one is i32 when its value fits, otherwise the
    // narrowest of i64 and u64 that holds it. A value that fits none of them is an error, not a truncation.
    private SemanticType IntegerLiteralType(IntegerLiteralExpression literal)
    {
        var lexeme = literal.Lexeme;
        if (lexeme.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').Length != 0)
        {
            var suffixedType = ParseNumericLiteralType(lexeme, false);
            // A negated literal is checked with its sign by AnalyzeUnary, because -128i8 is in range.
            if (!_negatedLiterals.Contains(literal))
                RequireLiteralFits(literal.Lexeme, ParseIntegerLiteralDigits(lexeme), suffixedType, literal.Location);
            return SemanticType.FromPrimitive(suffixedType);
        }
        if (!ulong.TryParse(lexeme, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw Error($"Integer literal '{lexeme}' does not fit any supported integer type", literal.Location,
                lexeme.Length);
        if (value <= int.MaxValue)
            return SemanticType.FromPrimitive(PrimitiveType.I32);
        return SemanticType.FromPrimitive(value <= long.MaxValue ? PrimitiveType.I64 : PrimitiveType.U64);
    }

    // The value of an integer literal's digits, without its suffix.
    private static BigInteger ParseIntegerLiteralDigits(string lexeme)
    {
        var suffix = lexeme.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return BigInteger.Parse(lexeme[..(lexeme.Length - suffix.Length)], CultureInfo.InvariantCulture);
    }

    // The smallest and largest value of an integer type. Floating-point types have no integer range.
    private static bool TryGetIntegerRange(PrimitiveType type, out BigInteger minimum, out BigInteger maximum)
    {
        (minimum, maximum) = type switch
        {
            PrimitiveType.I8 => ((BigInteger)sbyte.MinValue, (BigInteger)sbyte.MaxValue),
            PrimitiveType.I16 => ((BigInteger)short.MinValue, (BigInteger)short.MaxValue),
            PrimitiveType.I32 => ((BigInteger)int.MinValue, (BigInteger)int.MaxValue),
            PrimitiveType.I64 or PrimitiveType.ISize => ((BigInteger)long.MinValue, (BigInteger)long.MaxValue),
            PrimitiveType.U8 => (BigInteger.Zero, (BigInteger)byte.MaxValue),
            PrimitiveType.U16 => (BigInteger.Zero, (BigInteger)ushort.MaxValue),
            PrimitiveType.U32 => (BigInteger.Zero, (BigInteger)uint.MaxValue),
            PrimitiveType.U64 or PrimitiveType.USize => (BigInteger.Zero, (BigInteger)ulong.MaxValue),
            _ => (BigInteger.Zero, BigInteger.Zero)
        };
        return type is not (PrimitiveType.F32 or PrimitiveType.F64 or PrimitiveType.Bool or PrimitiveType.Void);
    }

    // A suffixed literal must hold a value of its own type. The text is what the programmer wrote, for the message.
    private static void RequireLiteralFits(string text, BigInteger value, PrimitiveType type, SourceLocation location)
    {
        if (!TryGetIntegerRange(type, out var minimum, out var maximum) || (value >= minimum && value <= maximum))
            return;
        var suffix = text.TrimStart('-', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        throw Error($"'{text}' does not fit in {suffix}", location, text.Length);
    }

    // The exact value of an integer constant expression: integer literals, unary plus and minus, and + - * / % over them.
    // Arithmetic is exact, so no value is rounded. A division or remainder by zero is not a constant.
    private static bool TryGetConstantInteger(Expression expression, out BigInteger value)
    {
        switch (expression)
        {
            case IntegerLiteralExpression integer:
                value = ParseIntegerLiteralDigits(integer.Lexeme);
                return true;
            case UnaryExpression { Operator: TokenKind.Minus, Operand: var negated } when TryGetConstantInteger(negated, out var operand):
                value = -operand;
                return true;
            case UnaryExpression { Operator: TokenKind.Plus, Operand: var positive }:
                return TryGetConstantInteger(positive, out value);
            case BinaryExpression binary when TryGetConstantInteger(binary.Left, out var left)
                && TryGetConstantInteger(binary.Right, out var right):
                return TryFoldArithmetic(binary.Operator, left, right, out value);
            default:
                value = default;
                return false;
        }
    }

    private static bool TryFoldArithmetic(TokenKind operatorKind, BigInteger left, BigInteger right, out BigInteger value)
    {
        value = default;
        switch (operatorKind)
        {
            case TokenKind.Plus: value = left + right; return true;
            case TokenKind.Minus: value = left - right; return true;
            case TokenKind.Star: value = left * right; return true;
            case TokenKind.Slash when !right.IsZero: value = left / right; return true;
            case TokenKind.Percent when !right.IsZero: value = left % right; return true;
            default: return false;
        }
    }

    // The type suffix of a numeric literal, or the empty string. It is found from the end, so that an exponent does not hide it.
    private static string NumericSuffix(string lexeme) =>
        new[] { "usize", "isize", "f32", "f64", "i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64" }
            .FirstOrDefault(known => lexeme.EndsWith(known, StringComparison.Ordinal)) ?? "";

    private static PrimitiveType ParseNumericLiteralType(string lexeme, bool floating)
    {
        var suffix = NumericSuffix(lexeme);
        if (suffix.Length != 0)
            return suffix switch
            {
                "f32" => PrimitiveType.F32,
                "f64" => PrimitiveType.F64,
                "i8" => PrimitiveType.I8,
                "i16" => PrimitiveType.I16,
                "i32" => PrimitiveType.I32,
                "i64" => PrimitiveType.I64,
                "u8" => PrimitiveType.U8,
                "u16" => PrimitiveType.U16,
                "u32" => PrimitiveType.U32,
                "u64" => PrimitiveType.U64,
                "usize" => PrimitiveType.USize,
                "isize" => PrimitiveType.ISize,
                _ => floating ? PrimitiveType.F64 : PrimitiveType.I32
            };
        return floating ? PrimitiveType.F64 : PrimitiveType.I32;
    }

}
