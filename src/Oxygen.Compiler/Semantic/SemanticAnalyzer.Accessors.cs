using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

// Property accessors with bodies. A getter or setter body is analysed like a method of its class, so its types and flow are
// checked. The backend builds its own symbols for the accessors, but the statements and expressions are the same nodes.
public sealed partial class SemanticAnalyzer
{
    private void AnalyzeAccessorBodies(
        ClassSymbol cls,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        DiagnosticBag? diagnostics,
        string? sourceName)
    {
        foreach (var property in cls.DeclaredProperties)
        {
            foreach (var accessor in property.Declaration.Accessors)
            {
                if (accessor.Body is null || accessor.Kind is not (PropertyAccessorKind.Get or PropertyAccessorKind.Set))
                    continue;
                var symbol = CreateAccessorSymbol(cls, property, accessor, moduleName);
                try
                {
                    AnalyzeFunction(symbol, functions, moduleName, imports, expressionTypes, resolvedCalls);
                }
                catch (SemanticException exception) when (diagnostics is not null)
                {
                    diagnostics.Add(DiagnosticPhase.Semantic, exception.Message, exception.Location, exception.Length,
                        sourceName: exception.IsSourceAttributed ? exception.SourceName : sourceName, code: exception.Code);
                }
            }
        }
    }

    // The function that a getter or setter is analysed as: `get_<name>()` returns the property's type, and `set_<name>(value)`
    // takes the value and returns void.
    private static FunctionSymbol CreateAccessorSymbol(
        ClassSymbol cls,
        PropertySymbol property,
        PropertyAccessorDeclaration accessor,
        string moduleName)
    {
        var isGet = accessor.Kind == PropertyAccessorKind.Get;
        var name = isGet ? $"get_{property.Name}" : $"set_{property.Name}";
        IReadOnlyList<ParameterSymbol> parameters = isGet
            ? []
            : [new ParameterSymbol("value", property.Type,
                new ParameterDeclaration(property.Declaration.Type, "value", accessor.Location))];
        var declaration = new FunctionDeclaration(
            isGet ? property.Declaration.Type : new TypeSyntax(PrimitiveType.Void, false, accessor.Location),
            name,
            parameters.Select(parameter => parameter.Declaration).ToArray(),
            accessor.Body!,
            accessor.Visibility,
            accessor.Location,
            IsVirtual: property.Declaration.IsVirtual,
            IsOverride: property.Declaration.IsOverride);
        return new FunctionSymbol(name,
            isGet ? property.Type : SemanticType.FromPrimitive(PrimitiveType.Void),
            parameters, declaration, $"{moduleName}.{cls.Name}", accessor.Visibility);
    }
}
