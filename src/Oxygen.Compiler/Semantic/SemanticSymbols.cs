using Oxygen.Compiler.Ast;

namespace Oxygen.Compiler.Semantic;

public sealed record ParameterSymbol(string Name, SemanticType Type, ParameterDeclaration Declaration);

public sealed record StructFieldSymbol(string Name, SemanticType Type, StructFieldDeclaration Declaration);

public sealed record StructSymbol(
    string Name,
    string ModuleName,
    IReadOnlyList<StructFieldSymbol> Fields,
    StructDeclaration Declaration,
    Visibility Visibility,
    bool IsPacked = false,
    uint? Alignment = null)
{
    public string QualifiedName => $"{ModuleName}.{Name}";
}

public sealed record CInteropFunctionMetadata(
    string? ExternalName = null,
    string CallingConvention = "c");

public sealed record FunctionSymbol(
    string Name,
    SemanticType ReturnType,
    IReadOnlyList<ParameterSymbol> Parameters,
    FunctionDeclaration Declaration,
    string ModuleName,
    Visibility Visibility,
    string? NativeName = null,
    bool IsExternal = false,
    CInteropFunctionMetadata? CInterop = null)
{
    public string QualifiedName => $"{ModuleName}.{Name}";
}

public sealed class SemanticModel
{
    internal SemanticModel(
        IReadOnlyDictionary<FunctionDeclaration, FunctionSymbol> functions,
        IReadOnlyDictionary<Expression, SemanticType> expressionTypes,
        FunctionSymbol? mainFunction,
        IReadOnlyDictionary<string, IReadOnlyList<FunctionSymbol>> modules,
        IReadOnlyDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        IReadOnlyDictionary<string, StructSymbol> structs)
    {
        Functions = functions;
        ExpressionTypes = expressionTypes;
        MainFunction = mainFunction;
        Modules = modules;
        ResolvedCalls = resolvedCalls;
        Structs = structs;
    }

    public IReadOnlyDictionary<FunctionDeclaration, FunctionSymbol> Functions { get; }

    public IReadOnlyDictionary<Expression, SemanticType> ExpressionTypes { get; }

    public FunctionSymbol? MainFunction { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<FunctionSymbol>> Modules { get; }

    public IReadOnlyDictionary<CallExpression, FunctionSymbol> ResolvedCalls { get; }

    public IReadOnlyDictionary<string, StructSymbol> Structs { get; }
}
