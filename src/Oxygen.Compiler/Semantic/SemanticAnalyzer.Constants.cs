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
    private static SemanticType IntegerLiteralType(IntegerLiteralExpression literal)
    {
        var lexeme = literal.Lexeme;
        if (lexeme.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').Length != 0)
            return SemanticType.FromPrimitive(ParseNumericLiteralType(lexeme, false));
        if (!ulong.TryParse(lexeme, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw Error($"Integer literal '{lexeme}' does not fit any supported integer type", literal.Location,
                lexeme.Length);
        if (value <= int.MaxValue)
            return SemanticType.FromPrimitive(PrimitiveType.I32);
        return SemanticType.FromPrimitive(value <= long.MaxValue ? PrimitiveType.I64 : PrimitiveType.U64);
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
