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
    public bool IsGeneric => DeclaredTypeParameters.Count != 0;
}

public sealed record ClassFieldSymbol(
    string Name,
    SemanticType Type,
    ClassFieldDeclaration Declaration,
    Visibility Visibility,
    bool IsConst = false);
public sealed record PropertySymbol(
    string Name,
    SemanticType Type,
    PropertyDeclaration Declaration,
    Visibility Visibility);
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
    IReadOnlyList<string>? TypeParameters = null,
    IReadOnlyList<PropertySymbol>? Properties = null,
    IReadOnlyList<SemanticType>? InterfaceTypes = null)
{
    public string QualifiedName => $"{ModuleName}.{Name}";
    public IReadOnlyList<string> DeclaredTypeParameters => TypeParameters ?? [];
    public bool IsGeneric => DeclaredTypeParameters.Count != 0;
    public IReadOnlyList<PropertySymbol> DeclaredProperties => Properties ?? [];
    public IReadOnlyList<SemanticType> DeclaredInterfaceTypes => InterfaceTypes ?? [];

    // Whether the class declares the field, as opposed to inheriting it from its base class. Property storage
    // belongs to the class that declares the property.
    public bool DeclaresField(string name) =>
        Declaration.Fields.Any(field => field.Name == name) || DeclaredProperties.Any(property => property.Name == name);
}
public sealed record InterfaceSymbol(
    string Name,
    string ModuleName,
    IReadOnlyList<FunctionSymbol> Methods,
    InterfaceDeclaration Declaration,
    Visibility Visibility,
    IReadOnlyList<string>? TypeParameters = null,
    IReadOnlyList<PropertySymbol>? Properties = null)
{
    public IReadOnlyList<string> DeclaredTypeParameters => TypeParameters ?? [];
    public IReadOnlyList<PropertySymbol> DeclaredProperties => Properties ?? [];
    public bool IsGeneric => DeclaredTypeParameters.Count != 0;
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
    public bool IsGeneric => DeclaredTypeParameters.Count != 0;
    public IReadOnlyDictionary<string, UnionCaseSymbol> CasesByName =>
        Cases.ToDictionary(item => item.Name, StringComparer.Ordinal);
}

public sealed record CInteropFunctionMetadata(
    string? ExternalName = null,
    string CallingConvention = "c");

// How a function frees heap objects. Automatic functions free objects when their lifetime ends; manual functions track
// lifetimes and report leaks, and free objects only through `delete`.
public enum MemoryMode
{
    Automatic,
    Manual
}

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
    IReadOnlyList<SemanticType>? SpecializationArguments = null,
    MemoryMode Memory = MemoryMode.Automatic)
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
        IReadOnlyDictionary<ForInStatement, IteratorProtocol>? iteratorProtocols = null,
        IReadOnlyDictionary<string, string>? aggregateSpecializations = null,
        IReadOnlyDictionary<CallExpression, SemanticType>? callReceiverTypes = null,
        IReadOnlySet<CallExpression>? receiverArgumentCalls = null,
        IReadOnlyDictionary<SyntaxNode, int>? constructorSelections = null,
        IReadOnlyDictionary<ConstructorDeclaration, int>? constructorChainTargets = null)
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
        AggregateSpecializations = aggregateSpecializations ?? new Dictionary<string, string>();
        CallReceiverTypes = callReceiverTypes ?? new Dictionary<CallExpression, SemanticType>();
        ReceiverArgumentCalls = receiverArgumentCalls ?? new HashSet<CallExpression>();
        ConstructorSelections = constructorSelections ?? new Dictionary<SyntaxNode, int>();
        ConstructorChainTargets = constructorChainTargets ?? new Dictionary<ConstructorDeclaration, int>();
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
    public IReadOnlyDictionary<string, string> AggregateSpecializations { get; }

    // The type of the variable a method call is made on (`receiver.method(...)`), keyed by the
    // call, so the backend never has to resolve receiver names itself.
    public IReadOnlyDictionary<CallExpression, SemanticType> CallReceiverTypes { get; }

    // Calls such as `value.write()` on a primitive or struct receiver. The receiver is passed as the first
    // argument of the resolved function.
    public IReadOnlySet<CallExpression> ReceiverArgumentCalls { get; }

    // The constructor selected by overload resolution for each `new C(...)` and `C(...)` expression, as an index
    // into the constructed class's Constructors.
    public IReadOnlyDictionary<SyntaxNode, int> ConstructorSelections { get; }

    // The constructor called by each constructor's `: this(...)`, `: base(...)`, or implicit base call, as an
    // index into Constructors of the class it targets: the declaring class for `this`, its base class otherwise.
    public IReadOnlyDictionary<ConstructorDeclaration, int> ConstructorChainTargets { get; }
}
