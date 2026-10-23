using System.Numerics;
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
    bool IsSynthesized = false)
{
    public bool AcceptsArgumentCount(int count) => ParameterCounts.Accepts(Parameters, count);

    // A @nothrow constructor promises that creating an object with it never throws (docs/exceptions.md).
    public bool DeclaresNoThrow => Declaration.DeclaredAnnotations.Any(annotation => annotation.Name == "nothrow");
}
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

    // The static fields of the class: one value for the class, shared by all instances. Each is a global variable named by the
    // class, so a generic class has one per instantiation. They are not part of an instance's layout.
    public IReadOnlyList<GlobalVariableSymbol> StaticFields { get; init; } = [];

    // The static methods of the class: functions of the class with no receiver. They are called as `Class.method(...)`.
    public IReadOnlyList<FunctionSymbol> StaticMethods { get; init; } = [];

    // Whether the class declares the field, as opposed to inheriting it from its base class. Property storage
    // belongs to the class that declares the property.
    public bool DeclaresField(string name) =>
        Declaration.Fields.Any(field => field.Name == name) || DeclaredProperties.Any(property => property.Name == name);

    // The fields that the class declares itself, not the ones it inherits from its base class.
    public IEnumerable<ClassFieldSymbol> OwnFields => Fields.Where(item => DeclaresField(item.Name));
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
    public string QualifiedName => $"{ModuleName}.{Name}";
    public IReadOnlyList<string> DeclaredTypeParameters => TypeParameters ?? [];
    public IReadOnlyList<PropertySymbol> DeclaredProperties => Properties ?? [];
    public bool IsGeneric => DeclaredTypeParameters.Count != 0;
}

public sealed record EnumMemberSymbol(string Name, BigInteger Value, EnumMemberDeclaration Declaration);

public sealed record EnumSymbol(
    string Name,
    PrimitiveType UnderlyingType,
    IReadOnlyList<EnumMemberSymbol> Members,
    EnumDeclaration Declaration,
    Visibility Visibility)
{
    // The module that declares the enum. Its qualified name is the key of the enum in the model.
    public string ModuleName { get; init; } = "program";
    public string QualifiedName => $"{ModuleName}.{Name}";

    public IReadOnlyDictionary<string, EnumMemberSymbol> MembersByName =>
        Members.ToDictionary(member => member.Name, StringComparer.Ordinal);

    // A flags enum (`@flags`) has members that are single bits, or zero. Its values combine with |, &, ^ and ~.
    public bool IsFlags { get; init; }

    // The bits that the members use. The complement of a flags value is masked to these.
    public BigInteger FlagsMask => Members.Aggregate(BigInteger.Zero, (mask, member) => mask | member.Value);
}

public sealed record UnionCaseSymbol(string Name, SemanticType? PayloadType, UnionCaseDeclaration Declaration);
public sealed record UnionSymbol(
    string Name, IReadOnlyList<UnionCaseSymbol> Cases, UnionDeclaration Declaration, Visibility Visibility,
    IReadOnlyList<string>? TypeParameters = null)
{
    // The module that declares the union. Its qualified name is the key of the union in the model.
    public string ModuleName { get; init; } = "program";
    public string QualifiedName => $"{ModuleName}.{Name}";
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

    // A function declared @nothrow promises never to throw (docs/exceptions.md).
    public bool DeclaresNoThrow => Declaration.DeclaredAnnotations.Any(annotation => annotation.Name == "nothrow");

    // A function declared @noreturn never returns to its caller: every path ends in a throw, a call to a @noreturn function, or a loop that never ends.
    public bool DeclaresNoReturn => Declaration.DeclaredAnnotations.Any(annotation => annotation.Name == "noreturn");

    // A function declared @throws promises that it may throw. It is never inferred @nothrow, and the analysis checks the promise.
    public bool DeclaresThrows => Declaration.DeclaredAnnotations.Any(annotation => annotation.Name == "throws");
    public IReadOnlyList<string> DeclaredGenericParameters => GenericParameters ?? [];
    public bool IsGeneric => DeclaredGenericParameters.Count != 0;
    public bool IsSpecialized => SpecializationArguments is not null;

    public int RequiredParameterCount => ParameterCounts.Required(Parameters);

    public bool AcceptsArgumentCount(int count) => ParameterCounts.Accepts(Parameters, count);

    public string ArgumentCountDescription => ParameterCounts.Describe(Parameters);
}

// The argument counts that a parameter list accepts. Parameters with default values come last, so the required parameters
// are the leading ones without a default. A call may pass from that many up to all of them, and the omitted ones take
// their defaults.
public static class ParameterCounts
{
    public static int Required(IReadOnlyList<ParameterSymbol> parameters) =>
        parameters.TakeWhile(parameter => parameter.Declaration.DefaultValue is null).Count();

    public static bool Accepts(IReadOnlyList<ParameterSymbol> parameters, int count) =>
        count >= Required(parameters) && count <= parameters.Count;

    // For a diagnostic: "2", or "1 to 3" when some parameters have defaults.
    public static string Describe(IReadOnlyList<ParameterSymbol> parameters) =>
        Required(parameters) == parameters.Count ? $"{parameters.Count}" : $"{Required(parameters)} to {parameters.Count}";
}

// The protocol of a for-in loop over a collection with iter(). IteratorType is the type that iter() returns: a class, whose next()
// is called directly, or an interface, whose next() is the interface contract called through the interface table.
public sealed record IteratorProtocol(
    Expression Collection,
    CallExpression IterCall,
    FunctionSymbol Iter,
    FunctionSymbol Next,
    SemanticType ElementType,
    SemanticType OptionType,
    SemanticType IteratorType);

// A module-level variable. Value is its constant starting value: a bool, a BigInteger for an integer type, a double for a
// float type, or null when the variable starts as zero.
public sealed record GlobalVariableSymbol(
    string Name,
    string ModuleName,
    SemanticType Type,
    bool IsConst,
    object? Value,
    GlobalVariableDeclaration Declaration);

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
        IReadOnlyDictionary<ConstructorDeclaration, int>? constructorChainTargets = null,
        IReadOnlySet<VariableDeclarationStatement>? automaticallyFreedLocals = null,
        IReadOnlySet<VariableDeclarationStatement>? deletedLocals = null,
        IReadOnlySet<DeleteStatement>? runtimeCheckedDeletes = null,
        IReadOnlyList<GlobalVariableSymbol>? globals = null,
        IReadOnlyDictionary<Expression, GlobalVariableSymbol>? globalReferences = null,
        IReadOnlySet<CallExpression>? baseCalls = null,
        IReadOnlyDictionary<ClassConstructionExpression, CallExpression>? constructionCalls = null,
        IReadOnlyDictionary<SyntaxNode, int[]>? argumentOrders = null,
        IReadOnlyDictionary<TypeSyntax, SemanticType>? resolvedTypeSyntax = null,
        IReadOnlyDictionary<Expression, FunctionSymbol>? staticPropertyGetters = null,
        IReadOnlyDictionary<Expression, FunctionSymbol>? staticPropertySetters = null,
        IReadOnlySet<string>? externalModules = null,
        IReadOnlySet<Expression>? nonNullDereferences = null,
        IReadOnlySet<CatchClause>? freedCatchBindings = null,
        IReadOnlyDictionary<Expression, Expression>? indirectCallees = null,
        IReadOnlyDictionary<Expression, FunctionSymbol>? functionReferences = null,
        IReadOnlyDictionary<LambdaExpression, FunctionSymbol>? lambdaFunctions = null,
        IReadOnlySet<SyntaxNode>? inferredNoThrow = null)
    {
        InferredNoThrow = inferredNoThrow ?? new HashSet<SyntaxNode>(ReferenceEqualityComparer.Instance);
        FreedCatchBindings = freedCatchBindings ?? new HashSet<CatchClause>(ReferenceEqualityComparer.Instance);
        IndirectCallees = indirectCallees ?? new Dictionary<Expression, Expression>(ReferenceEqualityComparer.Instance);
        FunctionReferences = functionReferences ?? new Dictionary<Expression, FunctionSymbol>(ReferenceEqualityComparer.Instance);
        LambdaFunctions = lambdaFunctions ?? new Dictionary<LambdaExpression, FunctionSymbol>(ReferenceEqualityComparer.Instance);
        ExternalModules = externalModules ?? new HashSet<string>(StringComparer.Ordinal);
        NonNullDereferences = nonNullDereferences ?? new HashSet<Expression>(ReferenceEqualityComparer.Instance);
        ArgumentOrders = argumentOrders ?? new Dictionary<SyntaxNode, int[]>();
        ResolvedTypeSyntax = resolvedTypeSyntax ?? new Dictionary<TypeSyntax, SemanticType>(ReferenceEqualityComparer.Instance);
        ConstructionCalls = constructionCalls ?? new Dictionary<ClassConstructionExpression, CallExpression>();
        BaseCalls = baseCalls ?? new HashSet<CallExpression>();
        Globals = globals ?? [];
        GlobalReferences = globalReferences ?? new Dictionary<Expression, GlobalVariableSymbol>();
        StaticPropertyGetters = staticPropertyGetters ?? new Dictionary<Expression, FunctionSymbol>();
        StaticPropertySetters = staticPropertySetters ?? new Dictionary<Expression, FunctionSymbol>();
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
        AutomaticallyFreedLocals = automaticallyFreedLocals ?? new HashSet<VariableDeclarationStatement>();
        DeletedLocals = deletedLocals ?? new HashSet<VariableDeclarationStatement>();
        RuntimeCheckedDeletes = runtimeCheckedDeletes ?? new HashSet<DeleteStatement>();
    }

    public IReadOnlyDictionary<FunctionDeclaration, FunctionSymbol> Functions { get; }

    // The `Name(...)` expressions that call a function named Name, mapped to that call.
    public IReadOnlyDictionary<ClassConstructionExpression, CallExpression> ConstructionCalls { get; }

    // How each call, construction, or constructor initializer binds its arguments to parameters: for each parameter, the index
    // of the argument that fills it, or -1 for its default. Calls without named arguments are bound in position.
    // The modules that a native library provides. Their functions, constructors, destructors and type information are declared here but
    // defined by the library, so the program does not define them.
    public IReadOnlySet<string> ExternalModules { get; }

    // The pointer operands of dereferences that the analysis proved non-null, so the backend emits them without a null check. Every other
    // dereference is checked at run time. Compared by reference, since each is a node of the analyzed syntax.
    public IReadOnlySet<Expression> NonNullDereferences { get; }

    // The catch clauses whose exception object the catch frees when the body completes (see SemanticAnalyzer.Memory.cs).
    public IReadOnlySet<CatchClause> FreedCatchBindings { get; }

    // The callee of each call through a function pointer, keyed by the call node (a CallExpression or an IndirectCallExpression).
    public IReadOnlyDictionary<Expression, Expression> IndirectCallees { get; }

    // The function that each `&name` of a function names, keyed by the unary node.
    public IReadOnlyDictionary<Expression, FunctionSymbol> FunctionReferences { get; }

    // The function that each lambda is compiled to.
    public IReadOnlyDictionary<LambdaExpression, FunctionSymbol> LambdaFunctions { get; }

    // The declarations of the bodies that were inferred @nothrow: they cannot throw, though they are not declared @nothrow. A declared body
    // is checked with its own annotation (FunctionSymbol.DeclaresNoThrow). Keyed by reference: FunctionDeclaration or ConstructorDeclaration
    // or DestructorDeclaration.
    public IReadOnlySet<SyntaxNode> InferredNoThrow { get; }

    public IReadOnlyDictionary<SyntaxNode, int[]> ArgumentOrders { get; }

    // The calls written as base.name(...), which are direct calls to the base class's method.
    public IReadOnlySet<CallExpression> BaseCalls { get; }

    // Every module-level variable, in declaration order.
    public IReadOnlyList<GlobalVariableSymbol> Globals { get; }

    // The identifiers that name a module-level variable. A local or parameter of the same name is not in this map.
    public IReadOnlyDictionary<Expression, GlobalVariableSymbol> GlobalReferences { get; }

    // The getter that each read of a static property names, and the setter that each write names. The backend calls them.
    public IReadOnlyDictionary<Expression, FunctionSymbol> StaticPropertyGetters { get; }

    public IReadOnlyDictionary<Expression, FunctionSymbol> StaticPropertySetters { get; }

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

    // Locals holding a `new` object in an automatic function that is never deleted and never escapes: the compiler frees
    // the object when the local's scope ends.
    public IReadOnlySet<VariableDeclarationStatement> AutomaticallyFreedLocals { get; }

    // Locals that some `delete` releases. Each gets a runtime flag that says whether its object is still alive.
    public IReadOnlySet<VariableDeclarationStatement> DeletedLocals { get; }

    // Deletes that may run after their object was already deleted, so they test the runtime flag first.
    public IReadOnlySet<DeleteStatement> RuntimeCheckedDeletes { get; }
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

    // The type that the analyzer resolved for each type syntax, by node. A name in a module is resolved in that module's
    // scope, so the backend reads the resolved type rather than the name.
    public IReadOnlyDictionary<TypeSyntax, SemanticType> ResolvedTypeSyntax { get; }
}
