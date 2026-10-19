using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;


internal static class MonomorphizationExtensions
{
    public static IReadOnlyList<T>? ToArrayOrNull<T>(this IEnumerable<T> values)
    {
        var result = values.ToArray();
        return result.Length == 0 ? null : result;
    }
}
