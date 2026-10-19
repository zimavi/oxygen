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

    // The value of an integer literal, with the sign of a unary minus applied. False for anything else.
    private static bool TryGetIntegerConstantValue(Expression expression, out BigInteger value)
    {
        switch (expression)
        {
            case IntegerLiteralExpression integer:
                value = ParseIntegerLiteralDigits(integer.Lexeme);
                return true;
            case UnaryExpression { Operator: TokenKind.Minus, Operand: IntegerLiteralExpression negated }:
                value = -ParseIntegerLiteralDigits(negated.Lexeme);
                return true;
            default:
                value = default;
                return false;
        }
    }

    private static PrimitiveType ParseNumericLiteralType(string lexeme, bool floating)
    {
        var suffix = lexeme.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.');
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

    private static bool TryGetConstantNumber(Expression expression, out double value)
    {
        static string WithoutSuffix(string lexeme)
        {
            foreach (var suffix in new[] { "usize", "isize", "f32", "f64", "i16", "i32", "i64", "u16", "u32", "u64", "i8", "u8" })
                if (lexeme.EndsWith(suffix, StringComparison.Ordinal))
                    return lexeme[..^suffix.Length];
            return lexeme;
        }
        switch (expression)
        {
            case IntegerLiteralExpression integer:
                return double.TryParse(WithoutSuffix(integer.Lexeme),
                    System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
            case FloatingPointLiteralExpression floating:
                return double.TryParse(WithoutSuffix(floating.Lexeme),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
            case UnaryExpression { Operator: TokenKind.Minus, Operand: var operand } when TryGetConstantNumber(operand, out var negated):
                value = -negated; return true;
            case UnaryExpression { Operator: TokenKind.Plus, Operand: var operand }:
                return TryGetConstantNumber(operand, out value);
            case BinaryExpression binary when TryGetConstantNumber(binary.Left, out var left)
                && TryGetConstantNumber(binary.Right, out var right):
                value = binary.Operator switch
                {
                    TokenKind.Plus => left + right,
                    TokenKind.Minus => left - right,
                    TokenKind.Star => left * right,
                    TokenKind.Slash when right != 0 => left / right,
                    TokenKind.Percent when right != 0 => left % right,
                    _ => double.NaN
                };
                return !double.IsNaN(value);
            default:
                value = 0; return false;
        }
    }

    private static bool TryGetConstantInteger(Expression expression, out int value)
    {
        if (TryGetConstantNumber(expression, out var number)
            && number >= int.MinValue && number <= int.MaxValue
            && number == Math.Truncate(number))
        {
            value = (int)number;
            return true;
        }
        value = 0;
        return false;
    }

}
