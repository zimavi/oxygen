using System.Numerics;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

public sealed partial class SemanticAnalyzer
{
    private readonly ILogger _logger;

    private DiagnosticBag? _diagnostics;

    private readonly HashSet<VariableSymbol> _usedVariables = [];

    // The nullness of each pointer local at the current point. A pointer that is not in the map is unknown. See SemanticAnalyzer.Pointers.cs.
    private readonly Dictionary<VariableSymbol, PointerNullness> _pointerStates = new(ReferenceEqualityComparer.Instance);

    // The pointer operands of dereferences that the analysis proved non-null, and those that it could not. A dereference that is proved
    // non-null needs no runtime check. An operand that is recorded in both is checked, since one of its analyses could not prove it.
    private readonly HashSet<Expression> _provenNonNullDereferences = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Expression> _unprovenDereferences = new(ReferenceEqualityComparer.Instance);

    private readonly HashSet<FunctionSymbol> _usedFunctions = [];

    private readonly List<VariableSymbol> _declaredVariables = [];

    // The memory mode of functions that carry no memory annotation (set by the `--manual-memory` compiler flag).
    public MemoryMode DefaultMemoryMode { get; set; } = MemoryMode.Automatic;

    private readonly HashSet<VariableDeclarationStatement> _escapingClassLocals = new(ReferenceEqualityComparer.Instance);

    // Locals released by `delete`; locals whose value is read (not only used as the receiver of a member access); and
    // whether the identifier being analyzed is only a receiver.
    private readonly HashSet<VariableDeclarationStatement> _deletedLocals = new(ReferenceEqualityComparer.Instance);

    // Locals deleted on some path to the current point but not on every path.
    private readonly HashSet<VariableDeclarationStatement> _maybeDeletedLocals = new(ReferenceEqualityComparer.Instance);

    // Every local that is deleted somewhere, and the deletes that may run after the object was already deleted.
    private readonly HashSet<VariableDeclarationStatement> _deletedAnywhere = new(ReferenceEqualityComparer.Instance);

    private readonly HashSet<DeleteStatement> _runtimeCheckedDeletes = new(ReferenceEqualityComparer.Instance);

    // For each loop being analyzed, the deletes in its body. A body runs again, so those deletes can repeat.
    private readonly Stack<List<(DeleteStatement Statement, VariableDeclarationStatement Declaration)>> _loopDeletes = new();

    private readonly HashSet<VariableDeclarationStatement> _valueUses = new(ReferenceEqualityComparer.Instance);

    private readonly HashSet<VariableDeclarationStatement> _autoFreedLocals = new(ReferenceEqualityComparer.Instance);

    private bool _receiverUse;

    private readonly Dictionary<ForInStatement, IteratorProtocol> _iteratorProtocols =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<CallExpression, SemanticType> _callReceiverTypes =
        new(ReferenceEqualityComparer.Instance);

    // Calls written as `receiver.name(...)` that resolve to a free function with the receiver as its first argument.
    private readonly HashSet<CallExpression> _receiverArgumentCalls = new(ReferenceEqualityComparer.Instance);

    // Constructor chosen for each `new C(...)` and `C(...)` expression, and for each constructor's chained call.
    private readonly Dictionary<SyntaxNode, int> _constructorSelections = new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<ConstructorDeclaration, int> _constructorChainTargets =
        new(ReferenceEqualityComparer.Instance);

    private string? _sourceName;

    private readonly Dictionary<string, StructSymbol> _structsByName = new(StringComparer.Ordinal);

    private readonly Dictionary<string, EnumSymbol> _enumsByName = new(StringComparer.Ordinal);

    private readonly Dictionary<string, UnionSymbol> _unionsByName = new(StringComparer.Ordinal);

    private readonly Dictionary<string, ClassSymbol> _classesByName = new(StringComparer.Ordinal);

    // Each class's own fields as declared. _classesByName holds the full inherited list after GetClassFields runs, so
    // inheritance is computed from this table, which never changes.
    private readonly Dictionary<string, IReadOnlyList<ClassFieldSymbol>> _ownClassFields = new(StringComparer.Ordinal);

    // The file that declares each class and struct, so an error found while checking it is reported in that file.
    private readonly Dictionary<string, string?> _declarationSourceNames = new(StringComparer.Ordinal);

    private readonly Dictionary<string, InterfaceSymbol> _interfacesByName = new(StringComparer.Ordinal);
    // Suffixed literals that are the operand of a unary minus. They are range-checked once, with their sign.
    private readonly HashSet<IntegerLiteralExpression> _negatedLiterals = new(ReferenceEqualityComparer.Instance);

    private ClassSymbol? _currentOwnerClass;

    private bool _currentIsConstructor;

    private int _loopDepth;

    private int _breakableDepth;

    // The breakable depth of the switch whose case body is being analyzed, and whether a break has left that switch.
    private int _switchExitDepth = -1;

    private bool _breakReached;

    // Definite assignment: the parts of locals that may still be unassigned at the current point, and whether that point is reachable.
    private readonly HashSet<UnassignedSlot> _unassignedParts = [];

    // The expressions of a struct place's walk (the local and the struct fields above a field read or written). Their own reads are
    // not checked, because the field path that the walk leads to is checked instead.
    private readonly HashSet<Expression> _skippedPartReads = new(ReferenceEqualityComparer.Instance);

    private bool _reachable = true;

    // The state at each `break` of the innermost loop or switch being analyzed, one list per breakable statement.
    private Stack<List<FlowState>> _breakStates = new();

    // For each switch being analyzed: the state where its cases start, and the state where each case ends.
    private Stack<(FlowState Start, List<(int Index, FlowState State)> Ends)> _switchFlows = new();

    private Dictionary<FunctionDeclaration, FunctionSymbol>? _functionSymbols;

    private IReadOnlyDictionary<string, SemanticType> _genericTypes =
        new Dictionary<string, SemanticType>(StringComparer.Ordinal);

    // The constraint interfaces of the type parameters in scope while a generic body is analyzed, by parameter name.
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _typeParameterConstraints =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    public SemanticAnalyzer(ILogger? logger = null)
    {
        _logger = logger ?? new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Console()
            .CreateLogger();
    }

    public SemanticModel Analyze(IReadOnlyList<CompilationUnit> compilationUnits)
        => AnalyzeCore(compilationUnits, null, null)!;

    public SemanticModel Analyze(
        IReadOnlyList<CompilationUnit> compilationUnits,
        IReadOnlyList<ExternalModuleDeclaration> externalModules)
        => AnalyzeCore(compilationUnits, null, null, externalModules)!;

    private SemanticModel? AnalyzeCore(
        IReadOnlyList<CompilationUnit> compilationUnits,
        DiagnosticBag? diagnostics,
        IReadOnlyList<string?>? sourceNames = null,
        IReadOnlyList<ExternalModuleDeclaration>? externalModules = null,
        bool requireMain = true)
    {
        ArgumentNullException.ThrowIfNull(compilationUnits);
        _diagnostics = diagnostics;
        _usedVariables.Clear();
        _pointerStates.Clear();
        _provenNonNullDereferences.Clear();
        _unprovenDereferences.Clear();
        _usedFunctions.Clear();
        _declaredVariables.Clear();
        _escapingClassLocals.Clear();
        _stackObjectLocals.Clear();
        _deletedLocals.Clear();
        _maybeDeletedLocals.Clear();
        _deletedAnywhere.Clear();
        _runtimeCheckedDeletes.Clear();
        _loopDeletes.Clear();
        _autoFreedLocals.Clear();
        _valueUses.Clear();
        _iteratorProtocols.Clear();
        _callReceiverTypes.Clear();
        _receiverArgumentCalls.Clear();
        _constructorSelections.Clear();
        _constructorChainTargets.Clear();
        _structsByName.Clear();
        _enumsByName.Clear();
        _unionsByName.Clear();
        _classesByName.Clear();
        _interfacesByName.Clear();
        _moduleScopes.Clear();
        _globalSymbols.Clear();
        _globalReferences.Clear();
        _staticPropertyGetters.Clear();
        _staticPropertySetters.Clear();
        _globals.Clear();
        _baseCalls.Clear();
        _constructionCalls.Clear();
        _functionContexts.Clear();
        _typeContexts.Clear();
        _resolvedTypeSyntax.Clear();
        _analyzedSpecializations.Clear();
        _fieldInitializerOverrides = null;
        _loopDepth = 0;
        _breakableDepth = 0;
        _sourceName = null;

        string? SourceNameAt(int unitIndex) =>
            sourceNames is not null && unitIndex < sourceNames.Count ? sourceNames[unitIndex] : null;

        RegisterAliasNames(compilationUnits, SourceNameAt);
        var functions = new Dictionary<FunctionDeclaration, FunctionSymbol>(ReferenceEqualityComparer.Instance);
        _functionSymbols = functions;
        for (var unitIndex = 0; unitIndex < compilationUnits.Count; unitIndex++)
        {
            var unit = compilationUnits[unitIndex];
            _sourceName = SourceNameAt(unitIndex);
            var moduleName = unit.ModuleName ?? "program";
            _typeScopeModule = moduleName;
            _typeScopeImports = unit.Imports;
            foreach (var declaration in unit.Enums)
            {
                var key = QualifiedTypeName(moduleName, declaration.Name);
                if (_enumsByName.ContainsKey(key) || _structsByName.ContainsKey(key))
                    throw ErrorWithCode(DiagnosticCode.AlreadyDeclared, $"A type named '{declaration.Name}' is already declared", declaration.Location, declaration.Name.Length);
                var isFlags = ValidateEnumAnnotations(declaration);
                var members = new List<EnumMemberSymbol>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                var values = new HashSet<BigInteger>();
                BigInteger next = 0;
                foreach (var member in declaration.Members)
                {
                    if (!names.Add(member.Name))
                        throw Error($"Enum '{declaration.Name}' contains duplicate member '{member.Name}'", member.Location, member.Name.Length);
                    var value = member.ExplicitValue ?? next;
                    // An implicit value that goes past the underlying type's range is reported here too.
                    if (!FitsUnderlying(value, declaration.UnderlyingType))
                        throw Error($"Enum member value {value} does not fit underlying type {declaration.UnderlyingType}",
                            member.Location);
                    // A flags member is 0 or one bit, so that the members never overlap when they are combined.
                    if (isFlags && value != 0 && !value.IsPowerOfTwo)
                        throw ErrorWithCode(DiagnosticCode.FlagsMember,
                            $"Flags enum '{declaration.Name}' member '{member.Name}' must be 0 or a single bit, got {value}",
                            member.Location, member.Name.Length);
                    if (!values.Add(value))
                        throw Error($"Enum '{declaration.Name}' contains duplicate value {value}", member.Location);
                    members.Add(new EnumMemberSymbol(member.Name, value, member));
                    next = value + 1;
                }
                _enumsByName.Add(key,
                    new EnumSymbol(declaration.Name, declaration.UnderlyingType, members, declaration, declaration.Visibility)
                    {
                        IsFlags = isFlags,
                        ModuleName = moduleName
                    });
            }
            foreach (var declaration in unit.Unions)
            {
                ValidateTypeParameters(declaration.DeclaredTypeParameters);
                var key = QualifiedTypeName(moduleName, declaration.Name);
                if (_enumsByName.ContainsKey(key) || _structsByName.ContainsKey(key)
                    || _unionsByName.ContainsKey(key))
                    throw ErrorWithCode(DiagnosticCode.AlreadyDeclared, $"A type named '{declaration.Name}' is already declared", declaration.Location, declaration.Name.Length);
                var names = new HashSet<string>(StringComparer.Ordinal);
                var cases = WithGenericTypes(declaration.DeclaredTypeParameters, () =>
                {
                    var result = new List<UnionCaseSymbol>();
                    foreach (var item in declaration.Cases)
                    {
                        if (!names.Add(item.Name))
                            throw Error($"Union '{declaration.Name}' contains duplicate case '{item.Name}'", item.Location);
                        var payloadType = item.PayloadType is null ? null : ResolveType(item.PayloadType);
                        if (payloadType is not null && !payloadType.IsGenericParameter
                            && payloadType != SemanticType.FromPrimitive(PrimitiveType.I32))
                            throw Error("Union payloads currently support only i32", item.Location);
                        result.Add(new UnionCaseSymbol(item.Name, payloadType, item));
                    }
                    return result;
                });
                _unionsByName.Add(key, new UnionSymbol(declaration.Name, cases, declaration,
                    declaration.Visibility, declaration.DeclaredTypeParameters.Select(parameter => parameter.Name).ToArray())
                {
                    ModuleName = moduleName
                });
            }
            foreach (var declaration in unit.Structs)
            {
                ValidateTypeParameters(declaration.DeclaredTypeParameters);
                var key = QualifiedTypeName(moduleName, declaration.Name);
                if (_structsByName.ContainsKey(key))
                    throw ErrorWithCode(DiagnosticCode.AlreadyDeclared, $"A struct named '{declaration.Name}' is already declared", declaration.Location, declaration.Name.Length);
                var annotations = ParseStructLayout(declaration);
                _declarationSourceNames[key] = _sourceName;
                _structsByName.Add(key,
                    new StructSymbol(declaration.Name, moduleName, [], declaration, declaration.Visibility,
                        annotations.IsPacked, annotations.Alignment,
                        declaration.DeclaredTypeParameters.Select(parameter => parameter.Name).ToArray()));
            }
            foreach (var declaration in unit.Interfaces)
            {
                ValidateTypeParameters(declaration.DeclaredTypeParameters);
                var key = QualifiedTypeName(moduleName, declaration.Name);
                EnsureTypeNameAvailable(declaration.Name, declaration.Location, key);
                _interfacesByName.Add(key,
                    new InterfaceSymbol(declaration.Name, moduleName, [], declaration, declaration.Visibility,
                        declaration.DeclaredTypeParameters.Select(parameter => parameter.Name).ToArray(), []));
            }
            ValidateFunctionOnlyAnnotations(unit);
            foreach (var declaration in unit.Classes)
            {
                ValidateTypeParameters(declaration.DeclaredTypeParameters);
                var key = QualifiedTypeName(moduleName, declaration.Name);
                EnsureTypeNameAvailable(declaration.Name, declaration.Location, key);
                ValidateClassAnnotations(declaration);
                _declarationSourceNames[key] = _sourceName;
                _classesByName.Add(key,
                    new ClassSymbol(declaration.Name, moduleName, declaration.BaseType,
                        declaration.Interfaces, [], [], [], declaration.Destructor, declaration, declaration.Visibility,
                        declaration.IsValue,
                        declaration.DeclaredTypeParameters.Select(parameter => parameter.Name).ToArray(),
                        []));
            }
        }
        for (var unitIndex = 0; unitIndex < compilationUnits.Count; unitIndex++)
        {
            var unit = compilationUnits[unitIndex];
            _sourceName = SourceNameAt(unitIndex);
            _typeScopeModule = unit.ModuleName ?? "program";
            _typeScopeImports = unit.Imports;
            // Every type of the unit is declared in its module and sees the unit's imports, so its context is the unit's.
            var unitContext = new DeclarationContext(_typeScopeModule, unit.Imports, SourceNameAt(unitIndex));
            foreach (var name in unit.Enums.Select(declaration => declaration.Name)
                .Concat(unit.Unions.Select(declaration => declaration.Name))
                .Concat(unit.Structs.Select(declaration => declaration.Name))
                .Concat(unit.Interfaces.Select(declaration => declaration.Name))
                .Concat(unit.Classes.Select(declaration => declaration.Name)))
                _typeContexts[QualifiedTypeName(_typeScopeModule, name)] = unitContext;
            foreach (var declaration in unit.Interfaces)
            {
                var interfaceKey = QualifiedTypeName(_typeScopeModule, declaration.Name);
                var methods = WithGenericTypes(declaration.DeclaredTypeParameters, () =>
                    declaration.Methods.Select(method =>
                        CreateFunctionSymbol(method, $"{unit.ModuleName ?? "program"}.{declaration.Name}")).ToArray());
                if (methods.GroupBy(MethodSignature).Any(group => group.Count() > 1
                    && group.Select(method => method.ReturnType).Distinct().Count() == 1))
                    throw Error($"Interface '{declaration.Name}' contains duplicate methods", declaration.Location);
                foreach (var property in declaration.DeclaredProperties)
                    ValidatePropertyDeclaration(property);
                var interfaceProperties = WithGenericTypes(declaration.DeclaredTypeParameters, () =>
                    declaration.DeclaredProperties.Select(property => new PropertySymbol(
                        property.Name, ResolveType(property.Type), property, property.Visibility)).ToArray());
                _interfacesByName[interfaceKey] = _interfacesByName[interfaceKey] with
                {
                    Methods = methods,
                    Properties = interfaceProperties
                };
            }
            foreach (var declaration in unit.Classes)
            {
                var classKey = QualifiedTypeName(_typeScopeModule, declaration.Name);
                var fields = WithGenericTypes(declaration.DeclaredTypeParameters, () =>
                    declaration.Fields.Where(field => !field.IsStatic).Select(field =>
                            new ClassFieldSymbol(field.Name, ResolveValueType(field.Type, $"Field '{field.Name}'", field.Location), field, field.Visibility, field.IsConst))
                        .Concat(declaration.DeclaredProperties
                            .Where(property => property.Accessors.Any(accessor => accessor.Kind is PropertyAccessorKind.Get
                                or PropertyAccessorKind.Set or PropertyAccessorKind.Init))
                            .Select(property => new ClassFieldSymbol(property.Name,
                                ResolveType(property.Type),
                                new ClassFieldDeclaration(property.Type, property.Name, property.Visibility,
                                    property.Location),
                                property.Visibility)))
                        .ToArray());
                if (fields.GroupBy(field => field.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
                    throw Error($"Class '{declaration.Name}' contains duplicate fields", declaration.Location);
                foreach (var property in declaration.DeclaredProperties)
                    ValidatePropertyDeclaration(property);
                var methods = WithGenericTypes(declaration.DeclaredTypeParameters, () =>
                    declaration.Methods.Where(method => !method.IsStatic).Select(method =>
                        CreateFunctionSymbol(method, $"{unit.ModuleName ?? "program"}.{declaration.Name}")).ToArray());
                var staticMethods = WithGenericTypes(declaration.DeclaredTypeParameters, () =>
                    declaration.Methods.Where(method => method.IsStatic).Select(method =>
                        CreateFunctionSymbol(method, $"{unit.ModuleName ?? "program"}.{declaration.Name}")).ToArray());
                if (staticMethods.GroupBy(method => method.Name).Any(group => HasConflictingPair(group.ToList(), method => method.Parameters)))
                    throw Error($"Class '{declaration.Name}' contains static methods whose overloads conflict: a call could match both", declaration.Location);
                if (methods.GroupBy(MethodSignature).Any(group => group.Count() > 1
                    && group.Select(method => method.ReturnType).Distinct().Count() == 1))
                    throw Error($"Class '{declaration.Name}' contains duplicate methods", declaration.Location);
                if (methods.GroupBy(method => method.Name).Any(group => HasConflictingPair(group.ToList(), method => method.Parameters)))
                    throw Error($"Class '{declaration.Name}' contains methods whose overloads conflict: a call could match both", declaration.Location);
                var constructors = WithGenericTypes(declaration.DeclaredTypeParameters, () => declaration.Constructors.Select(constructor =>
                {
                    var parameterNames = new HashSet<string>(StringComparer.Ordinal);
                    if (constructor.Parameters.Any(parameter => !parameterNames.Add(parameter.Name)))
                        throw Error($"Constructor '{declaration.Name}' contains duplicate parameters", constructor.Location);
                    return new ConstructorSymbol(declaration.Name,
                        constructor.Parameters.Select(parameter => new ParameterSymbol(
                            parameter.Name, ResolveType(parameter.Type), parameter)).ToArray(),
                        constructor, constructor.Visibility);
                }).ToArray());
                if (HasConflictingPair(constructors, constructor => constructor.Parameters))
                    throw Error($"Class '{declaration.Name}' contains duplicate constructors", declaration.Location);
                if (constructors.Length == 0)
                    constructors = [new ConstructorSymbol(declaration.Name, [],
                        new ConstructorDeclaration(declaration.Name, [], new BlockStatement([], declaration.Location),
                            Visibility.Public, declaration.Location), Visibility.Public, true)];
                if (declaration.Destructor is not null && declaration.Destructor.ClassName != declaration.Name)
                    throw Error($"Destructor name must be '{declaration.Name}'", declaration.Destructor.Location);
                // The parser cannot tell a base class from an interface written after ':', so when the
                // first name is an interface it is an implemented interface too. Both lists are built here
                // so that every consumer sees the same implemented interfaces.
                var interfaceTypes = WithGenericTypes(declaration.DeclaredTypeParameters, () =>
                {
                    var types = declaration.InterfaceTypeSyntaxes is { Count: > 0 }
                        ? declaration.InterfaceTypeSyntaxes.Select(ResolveType).ToList()
                        : declaration.Interfaces.Select(name => SemanticType.Interface(FindTypeName(name, declaration.Location) ?? name)).ToList();
                    if (declaration.BaseTypeSyntax is { Count: > 0 }
                        && FindTypeName(declaration.BaseTypeSyntax[0].NamedType!, declaration.Location) is { } baseInterface
                        && _interfacesByName.ContainsKey(baseInterface))
                        types.Insert(0, ResolveType(declaration.BaseTypeSyntax[0]));
                    return types;
                });
                // Property types are resolved here, after every type is registered, so a property may name a type declared later.
                var properties = WithGenericTypes(declaration.DeclaredTypeParameters, () =>
                    declaration.DeclaredProperties.Select(property => new PropertySymbol(
                        property.Name, ResolveType(property.Type), property, property.Visibility)).ToArray());
                _ownClassFields[classKey] = fields;
                var baseTypeName = declaration.BaseType is null
                    ? null
                    : FindTypeName(declaration.BaseType, declaration.Location) ?? declaration.BaseType;
                _classesByName[classKey] = _classesByName[classKey] with
                {
                    BaseTypeName = baseTypeName,
                    Fields = fields,
                    Properties = properties,
                    Methods = methods,
                    StaticMethods = staticMethods,
                    Constructors = constructors,
                    InterfaceNames = interfaceTypes.Select(type => type.StructName!).ToArray(),
                    InterfaceTypes = interfaceTypes
                };
            }
        }
        ResolveAliases(compilationUnits, SourceNameAt, (externalModules ?? []).Select(module => module.ModuleName).ToHashSet(StringComparer.Ordinal));
        _sourceName = null;
        // A class with a declaration error is not analysed further, so no model is built when one is reported.
        if (!ValidateClassHierarchy())
            return null;
        foreach (var className in _classesByName.Keys.ToArray())
            _classesByName[className] = _classesByName[className] with
            {
                Fields = GetClassFields(className, [])
            };
        for (var unitIndex = 0; unitIndex < compilationUnits.Count; unitIndex++)
        {
            var unit = compilationUnits[unitIndex];
            _sourceName = SourceNameAt(unitIndex);
            _typeScopeModule = unit.ModuleName ?? "program";
            _typeScopeImports = unit.Imports;
            foreach (var declaration in unit.Structs)
            {
                var structKey = QualifiedTypeName(_typeScopeModule, declaration.Name);
                var fields = WithGenericTypes(declaration.DeclaredTypeParameters, () => declaration.Fields
                    .Select(field => new StructFieldSymbol(field.Name, ResolveValueType(field.Type, $"Field '{field.Name}'", field.Location), field))
                    .ToArray());
                if (fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
                    throw Error($"Struct '{declaration.Name}' contains duplicate fields", declaration.Location,
                        declaration.Name.Length);
                _structsByName[structKey] = _structsByName[structKey] with { Fields = fields };
            }
        }
        _sourceName = null;
        foreach (var structure in _structsByName.Values)
            ValidateStructLayout(structure, []);
        var functionsByName = new Dictionary<string, List<FunctionSymbol>>(StringComparer.Ordinal);
        var functionSources = new Dictionary<FunctionSymbol, string?>();
        var functionModules = new Dictionary<FunctionSymbol, (string Name, IReadOnlyList<ImportDeclaration> Imports)>();
        // Semantic facts belong to syntax node instances, not to structurally equal nodes: two
        // identical expressions in different files or different instantiations can differ in type.
        var resolvedCalls = new Dictionary<CallExpression, FunctionSymbol>(ReferenceEqualityComparer.Instance);
        var availableModules = compilationUnits
            .Select(unit => unit.ModuleName ?? "program")
            .Concat((externalModules ?? []).Select(module => module.ModuleName))
            .ToHashSet(StringComparer.Ordinal);
        for (var unitIndex = 0; unitIndex < compilationUnits.Count; unitIndex++)
        {
            var unit = compilationUnits[unitIndex];
            var moduleName = unit.ModuleName ?? "program";
            var sourceName = diagnostics is null || sourceNames is null || unitIndex >= sourceNames.Count
                ? null
                : sourceNames[unitIndex];
            _sourceName = sourceName;
            _typeScopeModule = moduleName;
            _typeScopeImports = unit.Imports;
            var importAliases = new HashSet<string>(StringComparer.Ordinal);
            foreach (var import in unit.Imports)
            {
                if (!availableModules.Contains(import.Name))
                {
                    throw Error($"Imported module '{import.Name}' could not be found", import.Location,
                        import.Name.Length);
                }

                if (import.Alias is not null && !importAliases.Add(import.Alias))
                    throw ErrorWithCode(DiagnosticCode.AlreadyDeclared, $"Import alias '{import.Alias}' is declared more than once", import.Location,
                        import.Alias.Length);
                if (import.Symbols is not null)
                    ValidateSymbolImport(import, moduleName, compilationUnits, externalModules);
            }
            foreach (var externalModule in externalModules ?? [])
            {
                foreach (var external in externalModule.Functions)
                {
                    var declaration = new FunctionDeclaration(
                        new TypeSyntax(external.ReturnType.Primitive, false, new SourceLocation(1, 1), external.ReturnType.Kind == SemanticTypeKind.String),
                        external.Name,
                        external.ParameterTypes.Select((type, index) =>
                            new ParameterDeclaration(
                                new TypeSyntax(type.Primitive, false, new SourceLocation(1, 1), type.Kind == SemanticTypeKind.String),
                                $"arg{index}",
                                new SourceLocation(1, 1))).ToArray(),
                        new BlockStatement([], new SourceLocation(1, 1)),
                        external.Visibility,
                        new SourceLocation(1, 1));
                    var symbol = new FunctionSymbol(
                        external.Name,
                        external.ReturnType,
                        // The parameter types are the metadata's own types. A syntax type cannot name every one of them (a pointer or an
                        // array, for instance), so it is not used here.
                        external.ParameterTypes.Select((type, index) =>
                            new ParameterSymbol(declaration.Parameters[index].Name, type, declaration.Parameters[index])).ToArray(),
                        declaration,
                        externalModule.ModuleName,
                        external.Visibility,
                        external.NativeName,
                        true,
                        external.CInterop);
                    var key = symbol.QualifiedName;
                    if (!functionsByName.TryGetValue(key, out var overloads))
                    {
                        overloads = [];
                        functionsByName.Add(key, overloads);
                    }
                    if (!overloads.Any(existing => OverloadsConflict(existing.Parameters, symbol.Parameters)))
                    {
                        overloads.Add(symbol);
                        functions[declaration] = symbol;
                        functionSources[symbol] = null;
                        functionModules[symbol] = (externalModule.ModuleName, []);
                    }
                }
            }
            foreach (var declaration in unit.Functions)
            {
                _genericTypes = declaration.DeclaredTypeParameters
                    .ToDictionary(parameter => parameter.Name, parameter => SemanticType.GenericParameter(parameter.Name), StringComparer.Ordinal);
                var symbol = CreateFunctionSymbol(declaration, moduleName);
                _genericTypes = new Dictionary<string, SemanticType>(StringComparer.Ordinal);
                if (declaration.IsExternalDeclaration)
                {
                    var nativeName = symbol.CInterop?.ExternalName ?? declaration.Name;
                    symbol = symbol with
                    {
                        NativeName = nativeName,
                        IsExternal = true,
                        CInterop = symbol.CInterop ?? new CInteropFunctionMetadata()
                    };
                }
                var key = symbol.QualifiedName;
                if (!functionsByName.TryGetValue(key, out var overloads))
                {
                    overloads = [];
                    functionsByName.Add(key, overloads);
                }

                // Overloads are told apart by parameter types only; a return type never distinguishes them.
                var conflicting = overloads.FirstOrDefault(existing => OverloadsConflict(existing.Parameters, symbol.Parameters));
                if (conflicting is not null)
                {
                    ReportOrThrow(
                        diagnostics,
                        OverloadConflictMessage(symbol.Name, conflicting.Parameters, symbol.Parameters),
                        declaration.Location,
                        symbol.Name.Length,
                        sourceName,
                        DiagnosticCode.OverloadConflict);
                    continue;
                }

                overloads.Add(symbol);
                functions[declaration] = symbol;
                functionSources[symbol] = sourceName;
                functionModules[symbol] = (moduleName, unit.Imports);
                _functionContexts[declaration] = new DeclarationContext(moduleName, unit.Imports, sourceName);
            }
            foreach (var classDeclaration in unit.Classes)
            {
                var classKey = QualifiedTypeName(moduleName, classDeclaration.Name);
                _typeContexts[classKey] = new DeclarationContext(moduleName, unit.Imports, sourceName);
                foreach (var method in _classesByName[classKey].Methods.Concat(_classesByName[classKey].StaticMethods))
                {
                    var key = method.QualifiedName;
                    if (!functionsByName.TryGetValue(key, out var overloads))
                        functionsByName[key] = overloads = [];
                    overloads.Add(method);
                    functions[method.Declaration] = method;
                    functionSources[method] = sourceName;
                    functionModules[method] = (moduleName, unit.Imports);
                }
            }
        }

        // A library has no entry point, so it does not need main. A program does.
        FunctionSymbol? mainFunction = null;
        if (functionsByName.TryGetValue("program.main", out var mainOverloads))
        {
            mainFunction = mainOverloads.FirstOrDefault(function => function.Parameters.Count == 0)
                ?? mainOverloads[0];
            ValidateMainFunction(mainFunction, diagnostics, functionSources[mainFunction]);
        }
        else if (requireMain)
        {
            ReportOrThrow(
                diagnostics,
                "A parameterless 'main' function returning i32 or void is required",
                compilationUnits.Count > 0 ? compilationUnits[0].Location : new SourceLocation(1, 1),
                sourceName: sourceNames is { Count: > 0 } ? sourceNames[0] : null);
        }

        var expressionTypes = new Dictionary<Expression, SemanticType>(ReferenceEqualityComparer.Instance);
        DeclareGlobalVariables(compilationUnits, sourceNames, functionsByName, expressionTypes, resolvedCalls);
        DeclareStaticFields(compilationUnits, sourceNames, functionsByName, expressionTypes, resolvedCalls);
        ValidateDefaultParameters(functionsByName, functionSources, functionModules, expressionTypes, resolvedCalls, diagnostics);
        // A function of a native library was checked when the library was built, so its body is not checked again here. Its generic
        // instances are checked where the program uses them.
        var libraryModules = (externalModules ?? []).Select(module => module.ModuleName).ToHashSet(StringComparer.Ordinal);
        foreach (var symbol in functionsByName.Values.SelectMany(overloads => overloads).ToList())
        {
            if (symbol.IsExternal || libraryModules.Contains(functionModules[symbol].Name))
                continue;
            var owner = _classesByName.Values.FirstOrDefault(cls =>
                cls.Methods.Concat(cls.StaticMethods).Any(method => ReferenceEquals(method, symbol)));
            var previousGenericTypes = _genericTypes;
            var previousConstraints = _typeParameterConstraints;
            try
            {
                _sourceName = functionSources.GetValueOrDefault(symbol);
                var module = functionModules[symbol];
                // A generic body is analyzed once, with its type parameters opaque: its members are those of its constraints.
                if (owner is not null)
                {
                    _genericTypes = owner.DeclaredTypeParameters.ToDictionary(
                        parameter => parameter,
                        SemanticType.GenericParameter,
                        StringComparer.Ordinal);
                    _typeParameterConstraints = InTypeScope(TypeScopeModuleOf(module.Name), module.Imports,
                        () => ConstraintsFor(owner.Declaration.DeclaredTypeParameters));
                }
                else if (symbol.IsGeneric)
                {
                    _genericTypes = symbol.Declaration.DeclaredTypeParameters.ToDictionary(
                        parameter => parameter.Name,
                        parameter => SemanticType.GenericParameter(parameter.Name),
                        StringComparer.Ordinal);
                    _typeParameterConstraints = InTypeScope(TypeScopeModuleOf(module.Name), module.Imports,
                        () => ConstraintsFor(symbol.Declaration.DeclaredTypeParameters));
                }
                AnalyzeFunction(symbol, functionsByName, module.Name, module.Imports, expressionTypes, resolvedCalls);
            }
            catch (SemanticException exception) when (diagnostics is not null)
            {
                diagnostics.Add(
                    DiagnosticPhase.Semantic,
                    exception.Message,
                    exception.Location,
                    exception.Length,
                    sourceName: exception.IsSourceAttributed
                        ? exception.SourceName
                        : functionSources.GetValueOrDefault(symbol),
                        code: exception.Code);
            }
            finally
            {
                _genericTypes = previousGenericTypes;
                _typeParameterConstraints = previousConstraints;
            }
        }
        for (var unitIndex = 0; unitIndex < compilationUnits.Count; unitIndex++)
        {
            var unit = compilationUnits[unitIndex];
            var moduleName = unit.ModuleName ?? "program";
            _sourceName = diagnostics is null || sourceNames is null || unitIndex >= sourceNames.Count
                ? null
                : sourceNames[unitIndex];
            _typeScopeModule = moduleName;
            _typeScopeImports = unit.Imports;
            // The bodies of a class of a native library were checked when the library was built, so they are not analyzed again here.
            var fromLibrary = libraryModules.Contains(moduleName);
            foreach (var declaration in unit.Classes)
            {
                var classSymbol = _classesByName[QualifiedTypeName(moduleName, declaration.Name)];
                if (!fromLibrary)
                    AnalyzeAccessorBodies(classSymbol, moduleName, unit.Imports, functionsByName, expressionTypes,
                        resolvedCalls, diagnostics, SourceNameAt(unitIndex));
                foreach (var constructor in classSymbol.Constructors.Where(_ => !fromLibrary))
                {
                    var previousGenericTypes = _genericTypes;
                    var previousConstraints = _typeParameterConstraints;
                    _genericTypes = classSymbol.DeclaredTypeParameters.ToDictionary(
                        parameter => parameter,
                        SemanticType.GenericParameter,
                        StringComparer.Ordinal);
                    try
                    {
                        _typeParameterConstraints = ConstraintsFor(classSymbol.Declaration.DeclaredTypeParameters);
                        AnalyzeFunction(
                            CreateConstructorFunctionSymbol(moduleName, constructor),
                            functionsByName,
                            moduleName,
                            unit.Imports,
                            expressionTypes,
                            resolvedCalls,
                            constructor.Declaration);
                    }
                    catch (SemanticException exception) when (diagnostics is not null)
                    {
                        diagnostics.Add(
                            DiagnosticPhase.Semantic,
                            exception.Message,
                            exception.Location,
                            exception.Length,
                            sourceName: exception.IsSourceAttributed
                                ? exception.SourceName
                                : sourceNames is not null && unitIndex < sourceNames.Count
                                    ? sourceNames[unitIndex]
                                    : null);
                    }
                    finally
                    {
                        _genericTypes = previousGenericTypes;
                        _typeParameterConstraints = previousConstraints;
                    }
                }
                ValidateConstructorChains(classSymbol, diagnostics, sourceNames is not null && unitIndex < sourceNames.Count
                    ? sourceNames[unitIndex]
                    : null);

                if (classSymbol.Destructor is not null && !fromLibrary)
                {
                    try
                    {
                        AnalyzeFunction(
                            CreateDestructorFunctionSymbol(moduleName, classSymbol.Destructor),
                            functionsByName,
                            moduleName,
                            unit.Imports,
                            expressionTypes,
                            resolvedCalls);
                    }
                    catch (SemanticException exception) when (diagnostics is not null)
                    {
                        diagnostics.Add(
                            DiagnosticPhase.Semantic,
                            exception.Message,
                            exception.Location,
                            exception.Length,
                            sourceName: exception.IsSourceAttributed
                                ? exception.SourceName
                                : sourceNames is not null && unitIndex < sourceNames.Count
                                    ? sourceNames[unitIndex]
                                    : null,
                            code: exception.Code);
                    }
                }
            }
        }

        var aggregateSpecializations = MonomorphizeAggregates(
            compilationUnits, functions, functionsByName, expressionTypes, resolvedCalls, mainFunction);

        if (diagnostics is not null)
        {
            foreach (var symbol in functionsByName.Values.SelectMany(overloads => overloads))
            {
                // The standard library's private helpers are not reported: a program needs only the overloads it uses.
                if (symbol.QualifiedName != "program.main"
                    && symbol.Visibility != Visibility.Public
                    && !symbol.IsExternal
                    && !IsStandardLibraryModule(symbol.ModuleName)
                    && !_usedFunctions.Contains(symbol))
                {
                    ReportWarning(
                        $"Function '{symbol.Name}' is never called",
                        symbol.Declaration.Location,
                        symbol.Name.Length,
                        functionSources.GetValueOrDefault(symbol));
                }
            }
        }

        _logger.Information("Analyzed {FunctionCount} function(s).", functions.Count);
        _sourceName = null;
        _functionSymbols = null;
        var modules = functionsByName
            .GroupBy(pair => pair.Key[..pair.Key.LastIndexOf('.')], StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<FunctionSymbol>)group.SelectMany(pair => pair.Value).ToList(),
                StringComparer.Ordinal);
        return new SemanticModel(functions, expressionTypes, mainFunction, modules, resolvedCalls, _structsByName, _enumsByName, _unionsByName,
            _classesByName, _interfacesByName, _escapingClassLocals, _iteratorProtocols,
            aggregateSpecializations,
            new Dictionary<CallExpression, SemanticType>(_callReceiverTypes, ReferenceEqualityComparer.Instance),
            new HashSet<CallExpression>(_receiverArgumentCalls, ReferenceEqualityComparer.Instance),
            new Dictionary<SyntaxNode, int>(_constructorSelections, ReferenceEqualityComparer.Instance),
            new Dictionary<ConstructorDeclaration, int>(_constructorChainTargets, ReferenceEqualityComparer.Instance),
            new HashSet<VariableDeclarationStatement>(_autoFreedLocals, ReferenceEqualityComparer.Instance),
            new HashSet<VariableDeclarationStatement>(_deletedAnywhere, ReferenceEqualityComparer.Instance),
            new HashSet<DeleteStatement>(_runtimeCheckedDeletes, ReferenceEqualityComparer.Instance),
            _globals.ToList(),
            new Dictionary<Expression, GlobalVariableSymbol>(_globalReferences, ReferenceEqualityComparer.Instance),
            new HashSet<CallExpression>(_baseCalls, ReferenceEqualityComparer.Instance),
            new Dictionary<ClassConstructionExpression, CallExpression>(_constructionCalls, ReferenceEqualityComparer.Instance),
            new Dictionary<SyntaxNode, int[]>(_argumentOrders, ReferenceEqualityComparer.Instance),
            new Dictionary<TypeSyntax, SemanticType>(_resolvedTypeSyntax, ReferenceEqualityComparer.Instance),
            new Dictionary<Expression, FunctionSymbol>(_staticPropertyGetters, ReferenceEqualityComparer.Instance),
            new Dictionary<Expression, FunctionSymbol>(_staticPropertySetters, ReferenceEqualityComparer.Instance),
            libraryModules,
            // A dereference is unchecked only when every analysis of it proved its operand non-null.
            new HashSet<Expression>(
                _provenNonNullDereferences.Where(node => !_unprovenDereferences.Contains(node)),
                ReferenceEqualityComparer.Instance),
            new HashSet<CatchClause>(_freedCatchBindings, ReferenceEqualityComparer.Instance),
            new Dictionary<Expression, Expression>(_indirectCallees, ReferenceEqualityComparer.Instance),
            new Dictionary<Expression, FunctionSymbol>(_functionReferences, ReferenceEqualityComparer.Instance),
            new Dictionary<LambdaExpression, FunctionSymbol>(_lambdaFunctions, ReferenceEqualityComparer.Instance));
    }

    public SemanticResult AnalyzeWithDiagnostics(
        IReadOnlyList<CompilationUnit> compilationUnits,
        IReadOnlyList<string?>? sourceNames = null,
        IReadOnlyList<ExternalModuleDeclaration>? externalModules = null,
        bool requireMain = true)
    {
        var diagnostics = new DiagnosticBag();
        try
        {
            return new SemanticResult(
                AnalyzeCore(compilationUnits, diagnostics, sourceNames, externalModules, requireMain),
                diagnostics.Diagnostics);
        }
        catch (SemanticException exception)
        {
            diagnostics.Add(
                DiagnosticPhase.Semantic,
                exception.Message,
                exception.Location,
                exception.Length,
                exception.IsSourceAttributed
                    ? exception.SourceName
                    : _sourceName ?? (sourceNames is { Count: > 0 } ? sourceNames[0] : null),
                code: exception.Code);
            return new SemanticResult(null, diagnostics.Diagnostics);
        }
    }

}
