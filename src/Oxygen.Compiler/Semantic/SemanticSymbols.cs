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
    uint? Alignment = null,
    IReadOnlyList<string>? TypeParameters = null)
{
    public string QualifiedName => $"{ModuleName}.{Name}";
    public IReadOnlyList<string> DeclaredTypeParameters => TypeParameters ?? [];
}

public sealed record ClassFieldSymbol(string Name, SemanticType Type, ClassFieldDeclaration Declaration, Visibility Visibility);
public sealed record ConstructorSymbol(
    string ClassName,
    IReadOnlyList<ParameterSymbol> Parameters,
    ConstructorDeclaration Declaration,
    Visibility Visibility,
    bool IsSynthesized = false);
public sealed record ClassSymbol(
    string Name,
    string ModuleName,
    string? BaseTypeName,
    IReadOnlyList<string> InterfaceNames,
    IReadOnlyList<ClassFieldSymbol> Fields,
    IReadOnlyList<FunctionSymbol> Methods,
    IReadOnlyList<ConstructorSymbol> Constructors,
    DestructorDeclaration? Destructor,
    ClassDeclaration Declaration,
    Visibility Visibility,
    bool IsValue = false,
    IReadOnlyList<string>? TypeParameters = null)
{
    public string QualifiedName => $"{ModuleName}.{Name}";
    public IReadOnlyList<string> DeclaredTypeParameters => TypeParameters ?? [];
}
public sealed record InterfaceSymbol(
    string Name,
    string ModuleName,
    IReadOnlyList<FunctionSymbol> Methods,
    InterfaceDeclaration Declaration,
    Visibility Visibility,
    IReadOnlyList<string>? TypeParameters = null)
{
    public IReadOnlyList<string> DeclaredTypeParameters => TypeParameters ?? [];
}

public sealed record EnumMemberSymbol(string Name, int Value, EnumMemberDeclaration Declaration);

public sealed record EnumSymbol(
    string Name,
    PrimitiveType UnderlyingType,
    IReadOnlyList<EnumMemberSymbol> Members,
    EnumDeclaration Declaration,
    Visibility Visibility)
{
    public IReadOnlyDictionary<string, EnumMemberSymbol> MembersByName =>
        Members.ToDictionary(member => member.Name, StringComparer.Ordinal);
}

public sealed record UnionCaseSymbol(string Name, SemanticType? PayloadType, UnionCaseDeclaration Declaration);
public sealed record UnionSymbol(
    string Name, IReadOnlyList<UnionCaseSymbol> Cases, UnionDeclaration Declaration, Visibility Visibility,
    IReadOnlyList<string>? TypeParameters = null)
{
    public IReadOnlyList<string> DeclaredTypeParameters => TypeParameters ?? [];
    public IReadOnlyDictionary<string, UnionCaseSymbol> CasesByName =>
        Cases.ToDictionary(item => item.Name, StringComparer.Ordinal);
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
    CInteropFunctionMetadata? CInterop = null,
    IReadOnlyList<string>? GenericParameters = null,
    IReadOnlyList<SemanticType>? SpecializationArguments = null)
{
    public string QualifiedName => $"{ModuleName}.{Name}";
    public IReadOnlyList<string> DeclaredGenericParameters => GenericParameters ?? [];
    public bool IsGeneric => DeclaredGenericParameters.Count != 0;
    public bool IsSpecialized => SpecializationArguments is not null;
}

public sealed record IteratorProtocol(
    Expression Collection,
    CallExpression IterCall,
    FunctionSymbol Iter,
    FunctionSymbol Next,
    SemanticType ElementType,
    SemanticType OptionType);

public sealed class SemanticModel
{
    internal SemanticModel(
        IReadOnlyDictionary<FunctionDeclaration, FunctionSymbol> functions,
        IReadOnlyDictionary<Expression, SemanticType> expressionTypes,
        FunctionSymbol? mainFunction,
        IReadOnlyDictionary<string, IReadOnlyList<FunctionSymbol>> modules,
        IReadOnlyDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        IReadOnlyDictionary<string, StructSymbol> structs,
        IReadOnlyDictionary<string, EnumSymbol> enums,
        IReadOnlyDictionary<string, UnionSymbol>? unions = null,
        IReadOnlyDictionary<string, ClassSymbol>? classes = null,
        IReadOnlyDictionary<string, InterfaceSymbol>? interfaces = null,
        IReadOnlySet<VariableDeclarationStatement>? escapingClassLocals = null,
        IReadOnlyDictionary<ForInStatement, IteratorProtocol>? iteratorProtocols = null)
    {
        Functions = functions;
        ExpressionTypes = expressionTypes;
        MainFunction = mainFunction;
        Modules = modules;
        ResolvedCalls = resolvedCalls;
        Structs = structs;
        Enums = enums;
        Unions = unions ?? new Dictionary<string, UnionSymbol>();
        Classes = classes ?? new Dictionary<string, ClassSymbol>();
        Interfaces = interfaces ?? new Dictionary<string, InterfaceSymbol>();
        EscapingClassLocals = escapingClassLocals ?? new HashSet<VariableDeclarationStatement>();
        IteratorProtocols = iteratorProtocols ?? new Dictionary<ForInStatement, IteratorProtocol>();
    }

    public IReadOnlyDictionary<FunctionDeclaration, FunctionSymbol> Functions { get; }

    public IReadOnlyDictionary<Expression, SemanticType> ExpressionTypes { get; }

    public FunctionSymbol? MainFunction { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<FunctionSymbol>> Modules { get; }

    public IReadOnlyDictionary<CallExpression, FunctionSymbol> ResolvedCalls { get; }

    public IReadOnlyDictionary<string, StructSymbol> Structs { get; }
    public IReadOnlyDictionary<string, EnumSymbol> Enums { get; }
    public IReadOnlyDictionary<string, UnionSymbol> Unions { get; }
    public IReadOnlyDictionary<string, ClassSymbol> Classes { get; }
    public IReadOnlyDictionary<string, InterfaceSymbol> Interfaces { get; }
    public IReadOnlySet<VariableDeclarationStatement> EscapingClassLocals { get; }
    public IReadOnlyDictionary<ForInStatement, IteratorProtocol> IteratorProtocols { get; }
}
