using Oxygen.Compiler.Ast;

namespace Oxygen.Compiler.Semantic;

public sealed record ParameterSymbol(string Name, SemanticType Type, ParameterDeclaration Declaration);

public sealed record FunctionSymbol(
    string Name,
    SemanticType ReturnType,
    IReadOnlyList<ParameterSymbol> Parameters,
    FunctionDeclaration Declaration);

public sealed class SemanticModel
{
    internal SemanticModel(
        IReadOnlyDictionary<FunctionDeclaration, FunctionSymbol> functions,
        IReadOnlyDictionary<Expression, SemanticType> expressionTypes,
        FunctionSymbol? mainFunction)
    {
        Functions = functions;
        ExpressionTypes = expressionTypes;
        MainFunction = mainFunction;
    }

    public IReadOnlyDictionary<FunctionDeclaration, FunctionSymbol> Functions { get; }

    public IReadOnlyDictionary<Expression, SemanticType> ExpressionTypes { get; }

    public FunctionSymbol? MainFunction { get; }
}
