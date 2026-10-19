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
    private readonly HashSet<VariableSymbol> _definitelyNullVariables = [];
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
    private ClassSymbol? _currentOwnerClass;
    private bool _currentIsConstructor;
    private int _loopDepth;
    private int _breakableDepth;
    // The breakable depth of the switch whose case body is being analyzed, and whether a break has left that switch.
    private int _switchExitDepth = -1;
    private bool _breakReached;
    // Definite assignment: the locals that may still be unassigned at the current point, and whether that point is reachable.
    private readonly HashSet<VariableDeclarationStatement> _unassignedLocals = new(ReferenceEqualityComparer.Instance);
    private bool _reachable = true;
    // The state at each `break` of the innermost loop or switch being analyzed, one list per breakable statement.
    private Stack<List<FlowState>> _breakStates = new();
    // For each switch being analyzed: the state where its cases start, and the state where each case ends.
    private Stack<(FlowState Start, List<FlowState> Ends)> _switchFlows = new();
    private Dictionary<FunctionDeclaration, FunctionSymbol>? _functionSymbols;
    private IReadOnlyDictionary<string, SemanticType> _genericTypes =
        new Dictionary<string, SemanticType>(StringComparer.Ordinal);

    public SemanticAnalyzer(ILogger? logger = null)
    {
        _logger = logger ?? new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();
    }

    private SemanticType AnalyzeCast(
        CastExpression cast,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var target = ResolveType(cast.TargetType);
        var operand = AnalyzeExpression(cast.Operand, scope, functions, moduleName, imports, expressionTypes, resolvedCalls,
            expectedType: target);
        if (target.IsEnum)
        {
            var enumSymbol = _enumsByName[target.EnumName!];
            var underlying = SemanticType.FromPrimitive(enumSymbol.UnderlyingType);
            if (operand != underlying)
                throw Error($"Cannot cast {operand} to {target}; expected {underlying}", cast.Operand.Location);
            if (TryGetConstantInteger(cast.Operand, out var value)
                && !enumSymbol.Members.Any(member => member.Value == value))
                throw Error($"Value {value} is not a member of enum '{enumSymbol.Name}'", cast.Operand.Location);
            return target;
        }

        if (operand.IsEnum)
        {
            var enumSymbol = _enumsByName[operand.EnumName!];
            if (target != SemanticType.FromPrimitive(enumSymbol.UnderlyingType))
                throw Error($"Enum '{enumSymbol.Name}' can only be cast to {enumSymbol.UnderlyingType}", cast.TargetType.Location);
            return target;
        }

        if (operand.IsNumeric && target.IsNumeric)
            return target;

        if (target.IsPointer && operand.IsNumeric && IsPointerInteger(operand))
            return target;
        if (operand.IsPointer && target.IsNumeric && IsPointerInteger(target))
            return target;

        if (target.IsPointer && operand.IsPointer &&
            (target.IsOpaquePointer || operand.IsOpaquePointer))
            return target;

        if (IsReferenceCast(operand, target, out _))
            return target;

        throw Error($"Unsupported cast from {operand} to {target}", cast.Location);
    }

    private static bool FitsUnderlying(int value, PrimitiveType underlying) =>
        underlying switch
        {
            PrimitiveType.I8 => value is >= sbyte.MinValue and <= sbyte.MaxValue,
            PrimitiveType.U8 => value is >= byte.MinValue and <= byte.MaxValue,
            PrimitiveType.I16 => value is >= short.MinValue and <= short.MaxValue,
            PrimitiveType.U16 => value is >= ushort.MinValue and <= ushort.MaxValue,
            PrimitiveType.I32 or PrimitiveType.U32 or PrimitiveType.I64 or PrimitiveType.U64 => value >= 0
                || underlying is PrimitiveType.I32 or PrimitiveType.I64,
            _ => false
        };

    public SemanticModel Analyze(IReadOnlyList<CompilationUnit> compilationUnits)
        => AnalyzeCore(compilationUnits, null, null);

    public SemanticModel Analyze(
        IReadOnlyList<CompilationUnit> compilationUnits,
        IReadOnlyList<ExternalModuleDeclaration> externalModules)
        => AnalyzeCore(compilationUnits, null, null, externalModules);

    private SemanticModel AnalyzeCore(
        IReadOnlyList<CompilationUnit> compilationUnits,
        DiagnosticBag? diagnostics,
        IReadOnlyList<string?>? sourceNames = null,
        IReadOnlyList<ExternalModuleDeclaration>? externalModules = null)
    {
        ArgumentNullException.ThrowIfNull(compilationUnits);
        _diagnostics = diagnostics;
        _usedVariables.Clear();
        _definitelyNullVariables.Clear();
        _usedFunctions.Clear();
        _declaredVariables.Clear();
        _escapingClassLocals.Clear();
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
        _functionContexts.Clear();
        _classContexts.Clear();
        _analyzedSpecializations.Clear();
        _fieldInitializerOverrides = null;
        _loopDepth = 0;
        _breakableDepth = 0;
        _sourceName = null;

        string? SourceNameAt(int unitIndex) =>
            sourceNames is not null && unitIndex < sourceNames.Count ? sourceNames[unitIndex] : null;

        var functions = new Dictionary<FunctionDeclaration, FunctionSymbol>();
        _functionSymbols = functions;
        for (var unitIndex = 0; unitIndex < compilationUnits.Count; unitIndex++)
        {
            var unit = compilationUnits[unitIndex];
            _sourceName = SourceNameAt(unitIndex);
            var moduleName = unit.ModuleName ?? "program";
            foreach (var declaration in unit.Enums)
            {
                if (_enumsByName.ContainsKey(declaration.Name) || _structsByName.ContainsKey(declaration.Name))
                    throw Error($"A type named '{declaration.Name}' is already declared", declaration.Location, declaration.Name.Length);
                var members = new List<EnumMemberSymbol>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                var values = new HashSet<int>();
                var next = 0;
                foreach (var member in declaration.Members)
                {
                    if (!names.Add(member.Name))
                        throw Error($"Enum '{declaration.Name}' contains duplicate member '{member.Name}'", member.Location, member.Name.Length);
                    var value = member.ExplicitValue ?? next;
                    if (!FitsUnderlying(value, declaration.UnderlyingType))
                        throw Error($"Enum member value {value} does not fit underlying type {declaration.UnderlyingType}",
                            member.Location);
                    if (!values.Add(value))
                        throw Error($"Enum '{declaration.Name}' contains duplicate value {value}", member.Location);
                    members.Add(new EnumMemberSymbol(member.Name, value, member));
                    if (value == int.MaxValue && member != declaration.Members[^1])
                        throw Error($"Enum '{declaration.Name}' member values overflow", member.Location);
                    next = value + 1;
                }
                _enumsByName.Add(declaration.Name,
                    new EnumSymbol(declaration.Name, declaration.UnderlyingType, members, declaration, declaration.Visibility));
            }
            foreach (var declaration in unit.Unions)
            {
                ValidateTypeParameters(declaration.DeclaredTypeParameters);
                if (_enumsByName.ContainsKey(declaration.Name) || _structsByName.ContainsKey(declaration.Name)
                    || _unionsByName.ContainsKey(declaration.Name))
                    throw Error($"A type named '{declaration.Name}' is already declared", declaration.Location, declaration.Name.Length);
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
                _unionsByName.Add(declaration.Name, new UnionSymbol(declaration.Name, cases, declaration,
                    declaration.Visibility, declaration.DeclaredTypeParameters.Select(parameter => parameter.Name).ToArray()));
            }
            foreach (var declaration in unit.Structs)
            {
                ValidateTypeParameters(declaration.DeclaredTypeParameters);
                if (_structsByName.ContainsKey(declaration.Name))
                    throw Error($"A struct named '{declaration.Name}' is already declared", declaration.Location, declaration.Name.Length);
                var annotations = ParseStructLayout(declaration);
                _declarationSourceNames[declaration.Name] = _sourceName;
                _structsByName.Add(declaration.Name,
                    new StructSymbol(declaration.Name, moduleName, [], declaration, declaration.Visibility,
                        annotations.IsPacked, annotations.Alignment,
                        declaration.DeclaredTypeParameters.Select(parameter => parameter.Name).ToArray()));
            }
            foreach (var declaration in unit.Interfaces)
            {
                ValidateTypeParameters(declaration.DeclaredTypeParameters);
                EnsureTypeNameAvailable(declaration.Name, declaration.Location);
                _interfacesByName.Add(declaration.Name,
                    new InterfaceSymbol(declaration.Name, moduleName, [], declaration, declaration.Visibility,
                        declaration.DeclaredTypeParameters.Select(parameter => parameter.Name).ToArray(),
                        declaration.DeclaredProperties.Select(property => new PropertySymbol(
                            property.Name, ResolveType(property.Type), property, property.Visibility)).ToArray()));
            }
            foreach (var declaration in unit.Classes)
            {
                ValidateTypeParameters(declaration.DeclaredTypeParameters);
                EnsureTypeNameAvailable(declaration.Name, declaration.Location);
                ValidateClassAnnotations(declaration);
                _declarationSourceNames[declaration.Name] = _sourceName;
                _classesByName.Add(declaration.Name,
                    new ClassSymbol(declaration.Name, moduleName, declaration.BaseType,
                        declaration.Interfaces, [], [], [], declaration.Destructor, declaration, declaration.Visibility,
                        declaration.IsValue,
                        declaration.DeclaredTypeParameters.Select(parameter => parameter.Name).ToArray(),
                        declaration.DeclaredProperties.Select(property => new PropertySymbol(
                            property.Name, ResolveType(property.Type), property, property.Visibility)).ToArray()));
            }
        }
        for (var unitIndex = 0; unitIndex < compilationUnits.Count; unitIndex++)
        {
            var unit = compilationUnits[unitIndex];
            _sourceName = SourceNameAt(unitIndex);
            foreach (var declaration in unit.Interfaces)
            {
                var methods = WithGenericTypes(declaration.DeclaredTypeParameters, () =>
                    declaration.Methods.Select(method =>
                        CreateFunctionSymbol(method, $"{unit.ModuleName ?? "program"}.{declaration.Name}")).ToArray());
                if (methods.GroupBy(MethodSignature).Any(group => group.Count() > 1
                    && group.Select(method => method.ReturnType).Distinct().Count() == 1))
                    throw Error($"Interface '{declaration.Name}' contains duplicate methods", declaration.Location);
                foreach (var property in declaration.DeclaredProperties)
                    ValidatePropertyDeclaration(property);
                _interfacesByName[declaration.Name] = _interfacesByName[declaration.Name] with { Methods = methods };
            }
            foreach (var declaration in unit.Classes)
            {
                var fields = WithGenericTypes(declaration.DeclaredTypeParameters, () =>
                    declaration.Fields.Select(field =>
                            new ClassFieldSymbol(field.Name, ResolveType(field.Type), field, field.Visibility, field.IsConst))
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
                    declaration.Methods.Select(method =>
                        CreateFunctionSymbol(method, $"{unit.ModuleName ?? "program"}.{declaration.Name}")).ToArray());
                if (methods.GroupBy(MethodSignature).Any(group => group.Count() > 1
                    && group.Select(method => method.ReturnType).Distinct().Count() == 1))
                    throw Error($"Class '{declaration.Name}' contains duplicate methods", declaration.Location);
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
                if (constructors.GroupBy(constructor => string.Join(",", constructor.Parameters.Select(parameter => parameter.Type)))
                    .Any(group => group.Count() > 1))
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
                        : declaration.Interfaces.Select(name => SemanticType.Interface(name)).ToList();
                    if (declaration.BaseTypeSyntax is { Count: > 0 }
                        && _interfacesByName.ContainsKey(declaration.BaseTypeSyntax[0].NamedType!))
                        types.Insert(0, ResolveType(declaration.BaseTypeSyntax[0]));
                    return types;
                });
                _ownClassFields[declaration.Name] = fields;
                _classesByName[declaration.Name] = _classesByName[declaration.Name] with
                {
                    Fields = fields,
                    Methods = methods,
                    Constructors = constructors,
                    InterfaceNames = interfaceTypes.Select(type => type.StructName!).ToArray(),
                    InterfaceTypes = interfaceTypes
                };
            }
        }
        _sourceName = null;
        ValidateClassHierarchy();
        foreach (var className in _classesByName.Keys.ToArray())
            _classesByName[className] = _classesByName[className] with
            {
                Fields = GetClassFields(className, [])
            };
        for (var unitIndex = 0; unitIndex < compilationUnits.Count; unitIndex++)
        {
            var unit = compilationUnits[unitIndex];
            _sourceName = SourceNameAt(unitIndex);
            foreach (var declaration in unit.Structs)
            {
                var fields = WithGenericTypes(declaration.DeclaredTypeParameters, () => declaration.Fields
                    .Select(field => new StructFieldSymbol(field.Name, ResolveType(field.Type), field))
                    .ToArray());
                if (fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
                    throw Error($"Struct '{declaration.Name}' contains duplicate fields", declaration.Location,
                        declaration.Name.Length);
                _structsByName[declaration.Name] = _structsByName[declaration.Name] with { Fields = fields };
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
            var importAliases = new HashSet<string>(StringComparer.Ordinal);
            foreach (var import in unit.Imports)
            {
                if (!availableModules.Contains(import.Name))
                {
                    throw Error($"Imported module '{import.Name}' could not be found", import.Location,
                        import.Name.Length);
                }

                if (import.Alias is not null && !importAliases.Add(import.Alias))
                    throw Error($"Import alias '{import.Alias}' is declared more than once", import.Location,
                        import.Alias.Length);
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
                        declaration.Parameters.Select(parameter =>
                            new ParameterSymbol(parameter.Name, ResolveType(parameter.Type), parameter)).ToArray(),
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
                    if (!overloads.Any(existing => HasSameParameterTypes(existing, symbol)))
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
                if (overloads.Any(existing => HasSameParameterTypes(existing, symbol)))
                {
                    ReportOrThrow(
                        diagnostics,
                        $"A function named '{symbol.Name}' with the same parameter types is already declared",
                        declaration.Location,
                        symbol.Name.Length,
                        sourceName);
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
                _classContexts[classDeclaration.Name] = new DeclarationContext(moduleName, unit.Imports, sourceName);
                foreach (var method in _classesByName[classDeclaration.Name].Methods)
                {
                    var key = method.QualifiedName;
                    if (!functionsByName.TryGetValue(key, out var overloads))
                        functionsByName[key] = overloads = [];
                    overloads.Add(method);
                    functions[method.Declaration] = method;
                    functionSources[method] = sourceName;
                    functionModules[method] = (method.ModuleName, unit.Imports);
                }
            }
        }

        FunctionSymbol? mainFunction = null;
        if (!functionsByName.TryGetValue("program.main", out var mainOverloads))
        {
            ReportOrThrow(
                diagnostics,
                "A parameterless 'main' function returning i32 or void is required",
                compilationUnits.Count > 0 ? compilationUnits[0].Location : new SourceLocation(1, 1),
                sourceName: sourceNames is { Count: > 0 } ? sourceNames[0] : null);
        }
        else
        {
            mainFunction = mainOverloads.FirstOrDefault(function => function.Parameters.Count == 0)
                ?? mainOverloads[0];
            ValidateMainFunction(mainFunction, diagnostics, functionSources[mainFunction]);
        }

        var expressionTypes = new Dictionary<Expression, SemanticType>(ReferenceEqualityComparer.Instance);
        foreach (var symbol in functionsByName.Values.SelectMany(overloads => overloads).ToList())
        {
            if (symbol.IsExternal)
                continue;
            var owner = _classesByName.Values.FirstOrDefault(cls =>
                cls.Methods.Any(method => ReferenceEquals(method, symbol)));
            var previousGenericTypes = _genericTypes;
            if (owner is not null)
                _genericTypes = owner.DeclaredTypeParameters.ToDictionary(
                    parameter => parameter,
                    SemanticType.GenericParameter,
                    StringComparer.Ordinal);
            try
            {
                _sourceName = functionSources.GetValueOrDefault(symbol);
                var module = functionModules[symbol];
                if (!symbol.IsGeneric)
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
                        : functionSources.GetValueOrDefault(symbol));
            }
            finally
            {
                _genericTypes = previousGenericTypes;
            }
        }
        for (var unitIndex = 0; unitIndex < compilationUnits.Count; unitIndex++)
        {
            var unit = compilationUnits[unitIndex];
            var moduleName = unit.ModuleName ?? "program";
            _sourceName = diagnostics is null || sourceNames is null || unitIndex >= sourceNames.Count
                ? null
                : sourceNames[unitIndex];
            foreach (var declaration in unit.Classes)
            {
                var classSymbol = _classesByName[declaration.Name];
                foreach (var constructor in classSymbol.Constructors)
                {
                    var previousGenericTypes = _genericTypes;
                    _genericTypes = classSymbol.DeclaredTypeParameters.ToDictionary(
                        parameter => parameter,
                        SemanticType.GenericParameter,
                        StringComparer.Ordinal);
                    try
                    {
                        AnalyzeFunction(
                            CreateConstructorFunctionSymbol(moduleName, constructor),
                            functionsByName,
                            $"{moduleName}.{declaration.Name}",
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
                    }
                }
                ValidateConstructorChains(classSymbol, diagnostics, sourceNames is not null && unitIndex < sourceNames.Count
                    ? sourceNames[unitIndex]
                    : null);

                if (classSymbol.Destructor is not null)
                {
                    try
                    {
                        AnalyzeFunction(
                            CreateDestructorFunctionSymbol(moduleName, classSymbol.Destructor),
                            functionsByName,
                            $"{moduleName}.{declaration.Name}",
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
                                    : null);
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
            new HashSet<DeleteStatement>(_runtimeCheckedDeletes, ReferenceEqualityComparer.Instance));
    }

    private static FunctionSymbol CreateConstructorFunctionSymbol(string moduleName, ConstructorSymbol constructor)
    {
        var declaration = new FunctionDeclaration(
            new TypeSyntax(PrimitiveType.Void, false, constructor.Declaration.Location),
            constructor.ClassName,
            constructor.Declaration.Parameters,
            constructor.Declaration.Body,
            constructor.Visibility,
            constructor.Declaration.Location);
        return new FunctionSymbol(
            constructor.ClassName,
            SemanticType.FromPrimitive(PrimitiveType.Void),
            constructor.Parameters,
            declaration,
            $"{moduleName}.{constructor.ClassName}",
            constructor.Visibility);
    }

    private static FunctionSymbol CreateDestructorFunctionSymbol(string moduleName, DestructorDeclaration destructor)
    {
        var declaration = new FunctionDeclaration(
            new TypeSyntax(PrimitiveType.Void, false, destructor.Location),
            destructor.ClassName,
            [],
            destructor.Body,
            destructor.Visibility,
            destructor.Location);
        return new FunctionSymbol(
            destructor.ClassName,
            SemanticType.FromPrimitive(PrimitiveType.Void),
            [],
            declaration,
            $"{moduleName}.{destructor.ClassName}",
            destructor.Visibility);
    }

    private void EnsureTypeNameAvailable(string name, SourceLocation location)
    {
        if (_enumsByName.ContainsKey(name) || _structsByName.ContainsKey(name) || _unionsByName.ContainsKey(name)
            || _classesByName.ContainsKey(name) || _interfacesByName.ContainsKey(name))
            throw Error($"A type named '{name}' is already declared", location, name.Length);
    }

    private static string MethodSignature(FunctionSymbol method) =>
        $"{method.Name}({string.Join(",", method.Parameters.Select(parameter => parameter.Type))})";

    private void ValidatePropertyDeclaration(PropertyDeclaration property)
    {
        if (!property.HasGetter && !property.HasSetter && !property.HasInitializer)
            throw Error($"Property '{property.Name}' must declare a getter, setter, or initializer",
                property.Location, property.Name.Length);
        if (property.HasSetter && property.HasInitializer)
            throw Error($"Property '{property.Name}' cannot declare both 'set' and 'init'",
                property.Location, property.Name.Length);
        foreach (var accessor in property.Accessors)
        {
            if (VisibilityRank(accessor.Visibility) > VisibilityRank(property.Visibility))
                throw Error($"Accessor for property '{property.Name}' cannot be more visible than the property",
                    accessor.Location, property.Name.Length);
        }
    }

    private static int VisibilityRank(Visibility visibility) =>
        visibility switch
        {
            Visibility.Private => 0,
            Visibility.Protected => 1,
            Visibility.Internal => 1,
            Visibility.Public => 2,
            _ => 0
        };

    private void ValidateClassHierarchy()
    {
        foreach (var classSymbol in _classesByName.Values)
        {
            if (classSymbol.IsValue)
            {
                if (classSymbol.BaseTypeName is not null)
                    throw DeclarationError(classSymbol.Name, $"Value class '{classSymbol.Name}' cannot inherit from another class",
                        classSymbol.Declaration.Location);
                if (classSymbol.InterfaceNames.Count != 0)
                    throw DeclarationError(classSymbol.Name, $"Value class '{classSymbol.Name}' cannot implement interfaces",
                        classSymbol.Declaration.Location);
                if (classSymbol.Methods.Any(method => method.Declaration.IsVirtual || method.Declaration.IsOverride))
                    throw DeclarationError(classSymbol.Name, $"Value class '{classSymbol.Name}' cannot declare virtual or override methods",
                        classSymbol.Declaration.Location);
                if (classSymbol.DeclaredProperties.Any(property =>
                        property.Declaration.IsVirtual || property.Declaration.IsOverride))
                    throw DeclarationError(classSymbol.Name, $"Value class '{classSymbol.Name}' cannot declare virtual or override properties",
                        classSymbol.Declaration.Location);
            }
            // A base name that is an interface is already part of InterfaceNames.
            if (classSymbol.BaseTypeName is not null
                && !_classesByName.ContainsKey(classSymbol.BaseTypeName)
                && !_interfacesByName.ContainsKey(classSymbol.BaseTypeName))
                throw DeclarationError(classSymbol.Name, $"Base class '{classSymbol.BaseTypeName}' is not declared", classSymbol.Declaration.Location);
            foreach (var interfaceName in classSymbol.InterfaceNames)
                if (!_interfacesByName.ContainsKey(interfaceName))
                    throw DeclarationError(classSymbol.Name, $"Interface '{interfaceName}' is not declared", classSymbol.Declaration.Location);
            var inherited = classSymbol.BaseTypeName is null || !_classesByName.ContainsKey(classSymbol.BaseTypeName)
                ? [] : GetClassMethods(classSymbol.BaseTypeName, []);
            var inheritedProperties = classSymbol.BaseTypeName is null || !_classesByName.ContainsKey(classSymbol.BaseTypeName)
                ? [] : GetClassProperties(classSymbol.BaseTypeName, []);
            foreach (var method in classSymbol.Methods)
            {
                var overridden = inherited.FirstOrDefault(candidate => MethodSignature(candidate) == MethodSignature(method));
                if (method.Declaration.IsOverride)
                {
                    if (overridden is null)
                        throw DeclarationError(classSymbol.Name, $"Method '{method.Name}' is marked 'override' but does not override a base method",
                            method.Declaration.Location, method.Name.Length);
                    if (!overridden.Declaration.IsVirtual && !overridden.Declaration.IsOverride)
                        throw DeclarationError(classSymbol.Name, $"Method '{method.Name}' cannot override non-virtual method '{overridden.Name}'",
                            method.Declaration.Location, method.Name.Length);
                }
                else if (overridden is not null && (overridden.Declaration.IsVirtual || overridden.Declaration.IsOverride))
                {
                    throw DeclarationError(classSymbol.Name, $"Method '{method.Name}' overrides a virtual method and must be marked 'override'",
                        method.Declaration.Location, method.Name.Length);
                }
            }
            foreach (var property in classSymbol.DeclaredProperties)
            {
                var overridden = inheritedProperties.FirstOrDefault(candidate =>
                    candidate.Name == property.Name && candidate.Type == property.Type);
                if (property.Declaration.IsOverride)
                {
                    if (overridden is null)
                        throw DeclarationError(classSymbol.Name, $"Property '{property.Name}' is marked 'override' but does not override a base property",
                            property.Declaration.Location, property.Name.Length);
                    if (!overridden.Declaration.IsVirtual && !overridden.Declaration.IsOverride)
                        throw DeclarationError(classSymbol.Name, $"Property '{property.Name}' cannot override a non-virtual property",
                            property.Declaration.Location, property.Name.Length);
                }
                else if (overridden is not null
                         && (overridden.Declaration.IsVirtual || overridden.Declaration.IsOverride))
                {
                    throw DeclarationError(classSymbol.Name, $"Property '{property.Name}' overrides a virtual property and must be marked 'override'",
                        property.Declaration.Location, property.Name.Length);
                }
            }
            foreach (var contract in GetInterfaceContracts(classSymbol, classSymbol.InterfaceNames))
            {
                var implementations = classSymbol.Methods.Concat(inherited).Where(method =>
                    method.Name == contract.Name && method.Parameters.Select(parameter => parameter.Type)
                        .SequenceEqual(contract.Parameters.Select(parameter => parameter.Type), SemanticTypeComparer.Instance)
                    && SemanticTypeComparer.Instance.Equals(method.ReturnType, contract.ReturnType)).ToList();
                if (implementations.Count == 0)
                    throw DeclarationError(classSymbol.Name, $"Class '{classSymbol.Name}' does not implement interface method '{contract.Name}'",
                        classSymbol.Declaration.Location);
                // Interface members are public, so the implementation that satisfies the contract must be public too.
                if (!implementations.Any(method => method.Visibility == Visibility.Public))
                    throw DeclarationError(classSymbol.Name, $"Interface method '{contract.Name}' must be implemented as public in class '{classSymbol.Name}'",
                        classSymbol.Declaration.Location);
            }
            foreach (var contract in classSymbol.InterfaceNames.SelectMany(name => _interfacesByName[name].DeclaredProperties))
            {
                var implementation = classSymbol.Fields.FirstOrDefault(field => field.Name == contract.Name);
                if (implementation is null || !SemanticTypeComparer.Instance.Equals(implementation.Type, contract.Type))
                    throw DeclarationError(classSymbol.Name, $"Class '{classSymbol.Name}' does not implement interface property '{contract.Name}'",
                        classSymbol.Declaration.Location);
                if (contract.Declaration.HasGetter
                    && !classSymbol.DeclaredProperties.Concat(
                            classSymbol.BaseTypeName is not null && _classesByName.TryGetValue(classSymbol.BaseTypeName, out var baseClass)
                                ? baseClass.DeclaredProperties
                                : [])
                        .Any(property => property.Name == contract.Name && property.Declaration.HasGetter))
                    throw DeclarationError(classSymbol.Name, $"Class '{classSymbol.Name}' does not provide a getter for interface property '{contract.Name}'",
                        classSymbol.Declaration.Location);
                if ((contract.Declaration.HasSetter || contract.Declaration.HasInitializer)
                    && !classSymbol.DeclaredProperties.Any(property =>
                        property.Name == contract.Name
                        && (property.Declaration.HasSetter || property.Declaration.HasInitializer)))
                    throw DeclarationError(classSymbol.Name, $"Class '{classSymbol.Name}' does not provide a setter for interface property '{contract.Name}'",
                        classSymbol.Declaration.Location);
            }
        }
    }

    private IReadOnlyList<FunctionSymbol> GetClassMethods(string name, HashSet<string> active)
    {
        if (!active.Add(name))
            throw Error($"Class inheritance cycle involving '{name}'", _classesByName[name].Declaration.Location);
        var cls = _classesByName[name];
        var methods = cls.BaseTypeName is not null && _classesByName.ContainsKey(cls.BaseTypeName)
            ? GetClassMethods(cls.BaseTypeName, active)
            : [];
        active.Remove(name);
        var result = methods.ToList();
        foreach (var method in cls.Methods)
        {
            var index = result.FindIndex(candidate => MethodSignature(candidate) == MethodSignature(method));
            if (index >= 0) result[index] = method;
            else result.Add(method);
        }
        return result;
    }

    private IReadOnlyList<PropertySymbol> GetClassProperties(string name, HashSet<string> active)
    {
        if (!active.Add(name))
            throw Error($"Class inheritance cycle involving '{name}'", _classesByName[name].Declaration.Location);
        var cls = _classesByName[name];
        var properties = cls.BaseTypeName is not null && _classesByName.ContainsKey(cls.BaseTypeName)
            ? GetClassProperties(cls.BaseTypeName, active).ToList()
            : [];
        active.Remove(name);
        foreach (var property in cls.DeclaredProperties)
        {
            var index = properties.FindIndex(candidate => candidate.Name == property.Name);
            if (index >= 0) properties[index] = property;
            else properties.Add(property);
        }
        return properties;
    }

    private IReadOnlyList<ClassFieldSymbol> GetClassFields(string name, HashSet<string> active)
    {
        if (!active.Add(name))
            throw DeclarationError(name, $"Class inheritance cycle involving '{name}'", _classesByName[name].Declaration.Location);
        var cls = _classesByName[name];
        var inherited = cls.BaseTypeName is not null && _classesByName.ContainsKey(cls.BaseTypeName)
            ? GetClassFields(cls.BaseTypeName, active)
            : [];
        active.Remove(name);
        var fields = inherited.ToList();
        var own = _ownClassFields.TryGetValue(name, out var declared) ? declared : cls.Fields;
        foreach (var field in own)
        {
            var inheritedIndex = fields.FindIndex(candidate => candidate.Name == field.Name);
            if (inheritedIndex < 0)
            {
                fields.Add(field);
                continue;
            }
            var overridingProperty = cls.DeclaredProperties.FirstOrDefault(property =>
                property.Name == field.Name && property.Declaration.IsOverride);
            if (overridingProperty is null)
                fields.Add(field);
            else
                fields[inheritedIndex] = field;
        }
        if (fields.GroupBy(field => field.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw DeclarationError(name, $"Class '{name}' contains a field that hides an inherited field", cls.Declaration.Location);
        return fields.ToArray();
    }

    private IReadOnlyList<FunctionSymbol> GetInterfaceMethods(string name, HashSet<string> active)
    {
        if (!active.Add(name))
            throw Error($"Interface inheritance cycle involving '{name}'", _interfacesByName[name].Declaration.Location);
        active.Remove(name);
        return _interfacesByName[name].Methods;
    }

    private IReadOnlyList<FunctionSymbol> GetInterfaceContracts(ClassSymbol classSymbol, IReadOnlyList<string> interfaceNames)
    {
        var contracts = new List<FunctionSymbol>();
        foreach (var interfaceType in classSymbol.DeclaredInterfaceTypes)
        {
            if (!_interfacesByName.TryGetValue(interfaceType.StructName!, out var interfaceSymbol))
                continue;

            var substitutions = interfaceSymbol.DeclaredTypeParameters
                .Zip(interfaceType.TypeArguments ?? [], (name, type) => (name, type))
                .ToDictionary(pair => pair.name, pair => pair.type, StringComparer.Ordinal);
            contracts.AddRange(interfaceSymbol.Methods.Select(method => SubstituteFunctionTypes(method, substitutions)));
        }

        foreach (var name in interfaceNames)
        {
            if (!classSymbol.DeclaredInterfaceTypes.Any(type => type.StructName == name))
                contracts.AddRange(GetInterfaceMethods(name, []));
        }

        return contracts;
    }

    private static FunctionSymbol SubstituteFunctionTypes(
        FunctionSymbol function,
        IReadOnlyDictionary<string, SemanticType> substitutions) =>
        function with
        {
            ReturnType = SubstituteGenericType(function.ReturnType, substitutions),
            Parameters = function.Parameters
                .Select(parameter => parameter with { Type = SubstituteGenericType(parameter.Type, substitutions) })
                .ToArray()
        };

    private sealed class SemanticTypeComparer : IEqualityComparer<SemanticType>
    {
        public static SemanticTypeComparer Instance { get; } = new();

        public bool Equals(SemanticType? left, SemanticType? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left is null || right is null
                || left.Kind != right.Kind
                || left.Primitive != right.Primitive
                || left.StructName != right.StructName
                || left.EnumName != right.EnumName
                || left.GenericParameterName != right.GenericParameterName
                || left.ArrayLength != right.ArrayLength)
                return false;
            if (!Equals(left.Pointee, right.Pointee) || !Equals(left.ElementType, right.ElementType))
                return false;
            return (left.TypeArguments ?? []).SequenceEqual(right.TypeArguments ?? [], this);
        }

        public int GetHashCode(SemanticType type)
        {
            var hash = new HashCode();
            hash.Add(type.Kind);
            hash.Add(type.Primitive);
            hash.Add(type.StructName);
            hash.Add(type.EnumName);
            hash.Add(type.GenericParameterName);
            hash.Add(type.ArrayLength);
            if (type.Pointee is not null)
                hash.Add(GetHashCode(type.Pointee));
            if (type.ElementType is not null)
                hash.Add(GetHashCode(type.ElementType));
            foreach (var argument in type.TypeArguments ?? [])
                hash.Add(GetHashCode(argument));
            return hash.ToHashCode();
        }
    }

    public SemanticResult AnalyzeWithDiagnostics(
        IReadOnlyList<CompilationUnit> compilationUnits,
        IReadOnlyList<string?>? sourceNames = null,
        IReadOnlyList<ExternalModuleDeclaration>? externalModules = null)
    {
        var diagnostics = new DiagnosticBag();
        try
        {
            return new SemanticResult(
                AnalyzeCore(compilationUnits, diagnostics, sourceNames, externalModules),
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
                    : _sourceName ?? (sourceNames is { Count: > 0 } ? sourceNames[0] : null));
            return new SemanticResult(null, diagnostics.Diagnostics);
        }
    }

    private FunctionSymbol CreateFunctionSymbol(FunctionDeclaration declaration, string moduleName)
    {
        var returnType = ResolveType(declaration.ReturnType);
        var parameters = new List<ParameterSymbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var parameter in declaration.Parameters)
        {
            if (!names.Add(parameter.Name))
            {
                throw Error($"Parameter '{parameter.Name}' is declared more than once", parameter.Location, parameter.Name.Length);
            }

            parameters.Add(new ParameterSymbol(parameter.Name, ResolveType(parameter.Type), parameter));
        }

        return new FunctionSymbol(
            declaration.Name,
            returnType,
            parameters,
            declaration,
            moduleName,
            declaration.Visibility,
            CInterop: ParseCInterop(declaration),
            GenericParameters: declaration.DeclaredTypeParameters.Select(parameter => parameter.Name).ToArray(),
            IsExternal: declaration.IsExternalDeclaration,
            Memory: ResolveMemoryMode(declaration));
    }

    // `@manual_memory` and `@auto_memory` select a function's memory mode; without either, the compiler default applies.
    private MemoryMode ResolveMemoryMode(FunctionDeclaration declaration)
    {
        var manual = declaration.DeclaredAnnotations.Any(annotation => annotation.Name == "manual_memory");
        var automatic = declaration.DeclaredAnnotations.Any(annotation => annotation.Name == "auto_memory");
        if (manual && automatic)
            throw Error($"Function '{declaration.Name}' cannot be both @manual_memory and @auto_memory", declaration.Location,
                declaration.Name.Length);
        if (manual)
            return MemoryMode.Manual;
        return automatic ? MemoryMode.Automatic : DefaultMemoryMode;
    }

    private static CInteropFunctionMetadata? ParseCInterop(FunctionDeclaration declaration)
    {
        string? externalName = null;
        var callingConvention = "c";
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var annotation in declaration.DeclaredAnnotations)
        {
            if (!seen.Add(annotation.Name))
                throw Error($"Annotation '@{annotation.Name}' may only appear once", annotation.Location, annotation.Name.Length + 1);

            switch (annotation.Name)
            {
                case "extern":
                    if (annotation.Arguments.Count != 1 ||
                        !TryUnquote(annotation.Arguments[0], out externalName) ||
                        string.IsNullOrWhiteSpace(externalName))
                        throw Error("Annotation '@extern' expects one string argument", annotation.Location);
                    break;
                case "calling_convention":
                    if (annotation.Arguments.Count != 1)
                        throw Error("Annotation '@calling_convention' expects one identifier or string argument", annotation.Location);
                    if (!TryUnquote(annotation.Arguments[0], out callingConvention))
                    {
                        if (!string.Equals(annotation.Arguments[0], "c", StringComparison.Ordinal))
                            throw Error("Annotation '@calling_convention' expects one identifier or string argument", annotation.Location);
                        callingConvention = annotation.Arguments[0];
                    }
                    if (!string.Equals(callingConvention, "c", StringComparison.Ordinal))
                        throw Error($"Unsupported calling convention '{callingConvention}'", annotation.Location);
                    break;
                case "manual_memory":
                case "auto_memory":
                    if (annotation.Arguments.Count != 0)
                        throw Error($"Annotation '@{annotation.Name}' does not accept arguments", annotation.Location);
                    break;
                case "packed":
                    if (annotation.Arguments.Count != 0)
                        throw Error("Annotation '@packed' does not accept arguments", annotation.Location);
                    throw Error("Annotation '@packed' is reserved for struct declarations, which are not implemented", annotation.Location);
                case "align":
                    if (annotation.Arguments.Count != 1 ||
                        !uint.TryParse(annotation.Arguments[0], out var alignment) ||
                        alignment == 0 ||
                        (alignment & (alignment - 1)) != 0 ||
                        alignment > 4096)
                        throw Error("Annotation '@align' expects one power-of-two byte alignment from 1 through 4096", annotation.Location);
                    throw Error($"Annotation '@{annotation.Name}' is reserved for struct declarations, which are not implemented", annotation.Location);
                default:
                    throw Error($"Unknown annotation '@{annotation.Name}'", annotation.Location, annotation.Name.Length + 1);
            }
        }

        return externalName is null && callingConvention == "c"
            ? null
            : new CInteropFunctionMetadata(externalName, callingConvention);
    }

    private static void ValidateClassAnnotations(ClassDeclaration declaration)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var annotation in declaration.DeclaredAnnotations)
        {
            if (!seen.Add(annotation.Name))
                throw Error($"Annotation '@{annotation.Name}' may only appear once", annotation.Location, annotation.Name.Length + 1);
            if (annotation.Name == "value" && annotation.Arguments.Count == 0)
                continue;
            throw Error(annotation.Name == "value"
                ? "Annotation '@value' does not accept arguments"
                : $"Unknown class annotation '@{annotation.Name}'", annotation.Location);
        }
    }

    private static bool TryUnquote(string value, out string unquoted)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            unquoted = value[1..^1];
            return true;
        }

        unquoted = string.Empty;
        return false;
    }

    private static void ValidateMainFunction(
        FunctionSymbol mainFunction,
        DiagnosticBag? diagnostics,
        string? sourceName)
    {
        if (mainFunction.Parameters.Count != 0)
        {
            ReportOrThrow(
                diagnostics,
                "The 'main' function cannot have parameters",
                mainFunction.Declaration.Location,
                mainFunction.Name.Length,
                sourceName);
        }

        if (mainFunction.IsExternal)
        {
            ReportOrThrow(
                diagnostics,
                "The 'main' function must be defined in Oxygen and cannot be extern",
                mainFunction.Declaration.Location,
                mainFunction.Name.Length,
                sourceName);
        }

        if (mainFunction.ReturnType != SemanticType.FromPrimitive(PrimitiveType.I32)
            && !mainFunction.ReturnType.IsVoid)
        {
            ReportOrThrow(
                diagnostics,
                $"The 'main' function must return i32 or void, got {mainFunction.ReturnType}",
                mainFunction.Declaration.Location,
                mainFunction.Name.Length,
                sourceName);
        }
    }

    private static void ReportOrThrow(
        DiagnosticBag? diagnostics,
        string message,
        SourceLocation location,
        int length = 1,
        string? sourceName = null)
    {
        if (diagnostics is null)
        {
            throw Error(message, location, length);
        }

        diagnostics.Add(DiagnosticPhase.Semantic, message, location, length, sourceName);
    }

    private SemanticType ResolveType(TypeSyntax syntax)
    {
        if (syntax.IsInferred)
        {
            throw Error("Type inference is not valid in this context", syntax.Location);
        }

        if (syntax.IsArray)
            return SemanticType.ArrayOf(ResolveType(syntax.ElementType!), syntax.ArrayLength!.Value);
        if (syntax.IsString)
            return syntax.IsPointer
                ? SemanticType.PointerTo(ResolveType(syntax.Pointee!))
                : SemanticType.String;

        if (syntax.NamedType is not null)
        {
            if (_genericTypes.TryGetValue(syntax.NamedType, out var generic))
            {
                if (syntax.DeclaredTypeArguments.Count != 0)
                    throw Error($"Generic parameter '{syntax.NamedType}' cannot have type arguments", syntax.Location);
                return syntax.IsPointer ? SemanticType.PointerTo(generic) : generic;
            }
            if (_enumsByName.ContainsKey(syntax.NamedType))
            {
                if (syntax.IsPointer)
                    return SemanticType.PointerTo(SemanticType.Enum(syntax.NamedType));
                return SemanticType.Enum(syntax.NamedType);
            }
            if (_unionsByName.ContainsKey(syntax.NamedType))
            {
                ValidateTypeArgumentCount(syntax, _unionsByName[syntax.NamedType].DeclaredTypeParameters.Count);
                var union = SemanticType.Union(syntax.NamedType, ResolveTypeArguments(syntax));
                if (syntax.IsPointer)
                    return SemanticType.PointerTo(union);
                return union;
            }
            if (_classesByName.ContainsKey(syntax.NamedType))
            {
                ValidateTypeArgumentCount(syntax, _classesByName[syntax.NamedType].DeclaredTypeParameters.Count);
                var namedClass = SemanticType.Class(syntax.NamedType, ResolveTypeArguments(syntax));
                if (namedClass.TypeArguments is not null)
                    ValidateTypeArgumentConstraints(_classesByName[syntax.NamedType].Declaration.DeclaredTypeParameters,
                        namedClass.TypeArguments, syntax.Location);
                return syntax.IsPointer ? SemanticType.PointerTo(namedClass) : namedClass;
            }
            if (_interfacesByName.ContainsKey(syntax.NamedType))
            {
                ValidateTypeArgumentCount(syntax, _interfacesByName[syntax.NamedType].DeclaredTypeParameters.Count);
                var namedInterface = SemanticType.Interface(syntax.NamedType, ResolveTypeArguments(syntax));
                return syntax.IsPointer ? SemanticType.PointerTo(namedInterface) : namedInterface;
            }
            if (!_structsByName.ContainsKey(syntax.NamedType))
                throw Error($"Struct '{syntax.NamedType}' is not declared", syntax.Location, syntax.NamedType.Length);
            ValidateTypeArgumentCount(syntax, _structsByName[syntax.NamedType].DeclaredTypeParameters.Count);
            var named = SemanticType.Struct(syntax.NamedType, ResolveTypeArguments(syntax));
            return syntax.IsPointer ? SemanticType.PointerTo(named) : named;
        }

        if (syntax.Primitive is null)
            return syntax.IsPointer
                ? SemanticType.PointerTo(ResolveType(syntax.Pointee!))
                : SemanticType.String;

        var type = SemanticType.FromPrimitive(syntax.Primitive.Value);
        return syntax.IsPointer ? SemanticType.PointerTo(type) : type;
    }

    private IReadOnlyList<SemanticType>? ResolveTypeArguments(TypeSyntax syntax) =>
        syntax.DeclaredTypeArguments.Count == 0
            ? null
            : syntax.DeclaredTypeArguments.Select(ResolveType).ToArray();

    private void ValidateTypeArgumentCount(TypeSyntax syntax, int expected)
    {
        if (syntax.DeclaredTypeArguments.Count != expected)
            throw Error($"Type '{syntax.NamedType}' expects {expected} type argument(s), but received " +
                        $"{syntax.DeclaredTypeArguments.Count}", syntax.Location);
    }

    private void ValidateTypeParameters(IReadOnlyList<TypeParameterSyntax> parameters)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
            if (!names.Add(parameter.Name))
                throw Error($"Duplicate generic type parameter '{parameter.Name}'", parameter.Location,
                    parameter.Name.Length);
    }

    private T WithGenericTypes<T>(IReadOnlyList<TypeParameterSyntax> parameters, Func<T> action)
    {
        var previous = _genericTypes;
        _genericTypes = parameters.ToDictionary(
            parameter => parameter.Name,
            parameter => SemanticType.GenericParameter(parameter.Name),
            StringComparer.Ordinal);
        try
        {
            return action();
        }
        finally
        {
            _genericTypes = previous;
        }
    }

    private static (bool IsPacked, uint? Alignment) ParseStructLayout(StructDeclaration declaration)
    {
        var packed = false;
        uint? alignment = null;
        foreach (var annotation in declaration.DeclaredAnnotations)
        {
            switch (annotation.Name)
            {
                case "packed" when annotation.Arguments.Count == 0:
                    packed = true;
                    break;
                case "align" when annotation.Arguments.Count == 1
                    && uint.TryParse(annotation.Arguments[0], out var value)
                    && value > 0 && (value & (value - 1)) == 0 && value <= 4096:
                    alignment = value;
                    break;
                case "packed":
                    throw Error("Annotation '@packed' does not accept arguments", annotation.Location);
                case "align":
                    throw Error("Annotation '@align' expects one power-of-two byte alignment from 1 through 4096", annotation.Location);
                default:
                    throw Error($"Unknown struct annotation '@{annotation.Name}'", annotation.Location);
            }
        }
        return (packed, alignment);
    }

    private void ValidateStructLayout(StructSymbol structure, HashSet<string> active)
    {
        if (!active.Add(structure.Name))
            throw DeclarationError(structure.Name, $"Struct '{structure.Name}' contains a recursive by-value field", structure.Declaration.Location,
                structure.Name.Length);
        foreach (var field in structure.Fields.Where(field => field.Type.IsStruct))
            ValidateStructLayout(_structsByName[field.Type.StructName!], active);
        active.Remove(structure.Name);
    }

    private void AnalyzeFunction(
        FunctionSymbol function,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        ConstructorDeclaration? constructor = null)
    {
        // A generic instance is analysed when a call to it is analysed, which can be inside the flow of another body. The
        // outer body's flow state is saved and restored, so the instance starts from a clean state.
        var outerFlow = SaveAndResetFlowContext();
        try
        {
            AnalyzeFunctionBody(function, functions, moduleName, imports, expressionTypes, resolvedCalls, constructor);
        }
        finally
        {
            RestoreFlowContext(outerFlow);
        }
    }

    private void AnalyzeFunctionBody(
        FunctionSymbol function,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        ConstructorDeclaration? constructor)
    {
        _declaredVariables.Clear();
        _definitelyNullVariables.Clear();
        var scope = new Scope(null, MarkVariableUsed);
        var className = function.ModuleName[(function.ModuleName.LastIndexOf('.') + 1)..];
        if (_classesByName.TryGetValue(className, out var ownerClass))
        {
            var ownerTypeArguments = ownerClass.DeclaredTypeParameters
                .Select(parameter => _genericTypes.TryGetValue(parameter, out var type)
                    ? type
                    : SemanticType.GenericParameter(parameter))
                .ToArray();
            scope = new Scope(null, MarkVariableUsed,
                SemanticType.Class(ownerClass.Name, ownerTypeArguments.Length == 0 ? null : ownerTypeArguments));
            foreach (var field in ownerClass.Fields)
                scope.Declare(field.Name, new VariableSymbol(SubstituteGenericType(field.Type, _genericTypes),
                    field.IsConst, field.Declaration, false));
        }
        _currentOwnerClass = _classesByName.GetValueOrDefault(className);
        _currentIsConstructor = constructor is not null;
        var parameterScope = new Scope(scope, MarkVariableUsed);
        // A constructor's initializer runs before the object's fields are initialized, so its arguments see the
        // parameters but neither the fields nor `this`.
        var initializerScope = new Scope(null, MarkVariableUsed,
            thisError: "'this' cannot be used in a constructor initializer");
        foreach (var parameter in function.Parameters)
        {
            var variable = new VariableSymbol(parameter.Type, false, parameter.Declaration, true);
            parameterScope.Declare(parameter.Name, variable);
            initializerScope.Declare(parameter.Name, variable);
            _declaredVariables.Add(variable);
        }

        _loopDepth = 0;
        _breakableDepth = 0;
        if (constructor is not null && _currentOwnerClass is not null)
        {
            AnalyzeFieldInitializers(_currentOwnerClass, scope, functions, moduleName, imports,
                expressionTypes, resolvedCalls);
            AnalyzeConstructorInitializer(constructor, _currentOwnerClass, initializerScope, functions, moduleName,
                imports, expressionTypes, resolvedCalls);
        }
        var hasReturn = AnalyzeBlock(function.Declaration.Body, parameterScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
        if (!function.ReturnType.IsVoid && !hasReturn)
        {
            throw Error($"Function '{function.Name}' must return a value", function.Declaration.Location);
        }

        CollectAutomaticFrees(function);
        if (_diagnostics is not null)
        {
            foreach (var variable in _declaredVariables)
            {
                if (!_usedVariables.Contains(variable))
                {
                    var name = variable.Declaration switch
                    {
                        ParameterDeclaration parameter => $"Parameter '{parameter.Name}' is never used",
                        VariableDeclarationStatement declaration => $"Variable '{declaration.Name}' is never used",
                        _ => "Declaration is never used"
                    };
                    var length = variable.Declaration switch
                    {
                        ParameterDeclaration parameter => parameter.Name.Length,
                        VariableDeclarationStatement declaration => declaration.Name.Length,
                        _ => 1
                    };
                    ReportWarning(name, variable.Declaration.Location, length);
                }
            }

            WarnAboutUnfreedAllocations(function);
        }
    }

    private void AnalyzeFieldInitializers(
        ClassSymbol ownerClass,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        // Inherited fields are initialized by the base class's constructor, which runs before this one.
        foreach (var field in ownerClass.Fields.Where(field => ownerClass.DeclaresField(field.Name)))
        {
            var initializer = _fieldInitializerOverrides?.GetValueOrDefault(field.Declaration)
                ?? field.Declaration.Initializer;
            if (initializer is null)
                continue;
            var fieldType = SubstituteGenericType(field.Type, _genericTypes);
            var valueType = AnalyzeExpression(initializer, scope, functions, moduleName, imports,
                expressionTypes, resolvedCalls, expectedType: fieldType);
            if (!CanAssign(fieldType, initializer, valueType))
                throw Error($"Cannot initialize field '{field.Name}' with {valueType}; expected {fieldType}",
                    initializer.Location);
        }
    }

    // Resolves the constructor that runs before a constructor's body: the one named by its `this(...)` or `base(...)`
    // initializer, or the parameterless base constructor when it has no initializer. Overloads are selected as for
    // `new` expressions, and the arguments are checked against the target's parameters.
    private void AnalyzeConstructorInitializer(
        ConstructorDeclaration constructor,
        ClassSymbol owner,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var initializer = constructor.Initializer;
        // An interface named after ':' is an implemented interface, so only a class counts as a base class.
        var baseClass = owner.BaseTypeName is not null && _classesByName.TryGetValue(owner.BaseTypeName, out var baseType)
            ? baseType
            : null;
        if (initializer?.Kind == ConstructorInitializerKind.Base && baseClass is null)
            throw Error($"Constructor of '{owner.Name}' cannot call 'base' because '{owner.Name}' has no base class",
                initializer.Location, "base".Length);
        if (initializer is null && baseClass is null)
            return;

        var targetsThis = initializer?.Kind == ConstructorInitializerKind.This;
        var target = targetsThis ? owner : baseClass!;
        var arguments = initializer?.Arguments ?? [];
        var argumentTypes = arguments.Select(argument =>
            AnalyzeExpression(argument, scope, functions, moduleName, imports, expressionTypes, resolvedCalls)).ToArray();
        // `this` takes the owner's own type arguments; `base` takes the arguments written in the base type.
        var typeArguments = targetsThis
            ? owner.DeclaredTypeParameters.Select(parameter => _genericTypes.TryGetValue(parameter, out var type)
                ? type
                : SemanticType.GenericParameter(parameter)).ToArray()
            : owner.Declaration.BaseTypeSyntax is { Count: > 0 } baseSyntax
                ? ResolveType(baseSyntax[0]).TypeArguments
                : null;
        var substitutions = target.DeclaredTypeParameters
            .Zip(typeArguments ?? [], (name, type) => (name, type))
            .ToDictionary(item => item.name, item => item.type, StringComparer.Ordinal);

        var matches = target.Constructors.Select((overload, position) => (overload, position))
            .Where(item => item.overload.Parameters.Count == arguments.Count)
            .Where(item => item.overload.Parameters.Select((parameter, index) => CanAssign(
                SubstituteGenericType(parameter.Type, substitutions), arguments[index], argumentTypes[index]))
                .All(match => match))
            .ToArray();
        var location = initializer?.Location ?? constructor.Location;
        if (matches.Length == 0)
        {
            if (initializer is null)
                throw Error(
                    $"Constructor of '{owner.Name}' must call a base constructor because '{target.Name}' has no " +
                    "parameterless constructor; add ': base(...)' to the constructor",
                    location);
            throw Error($"No constructor of '{target.Name}' matches the supplied arguments", location);
        }
        if (matches.Length > 1)
            throw Error($"Constructor call for '{target.Name}' is ambiguous", location);
        _constructorChainTargets[constructor] = matches[0].position;
    }

    private static int IndexOfConstructor(ClassSymbol cls, ConstructorSymbol constructor)
    {
        for (var index = 0; index < cls.Constructors.Count; index++)
        {
            if (ReferenceEquals(cls.Constructors[index], constructor))
                return index;
        }
        throw new InvalidOperationException($"Constructor is not declared by class '{cls.Name}'.");
    }

    // Constructors that call `this(...)` must not form a cycle. A cycle is reported once, at the first of its
    // constructors in declaration order; a chain that leads into a cycle without returning to its start is not.
    private void ValidateConstructorChains(ClassSymbol cls, DiagnosticBag? diagnostics, string? sourceName)
    {
        var declarations = cls.Constructors.Select(constructor => constructor.Declaration).ToArray();
        var reported = new HashSet<int>();
        for (var start = 0; start < declarations.Length; start++)
        {
            if (reported.Contains(start))
                continue;
            var path = new List<int> { start };
            var current = start;
            while (declarations[current].Initializer?.Kind == ConstructorInitializerKind.This
                   && _constructorChainTargets.TryGetValue(declarations[current], out var next))
            {
                if (next == start)
                {
                    ReportOrThrow(diagnostics,
                        $"Constructor chain of '{cls.Name}' forms a cycle",
                        declarations[start].Initializer!.Location,
                        "this".Length,
                        sourceName);
                    reported.UnionWith(path);
                    break;
                }
                if (path.Contains(next))
                    break;
                path.Add(next);
                current = next;
            }
        }
    }

    private bool AnalyzeBlock(
        BlockStatement block,
        Scope parent,
        FunctionSymbol function,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var scope = new Scope(parent, MarkVariableUsed);
        var hasReturn = false;

        foreach (var statement in block.Statements)
        {
            if (hasReturn)
            {
                ReportWarning("Unreachable statement", statement.Location);
            }

            hasReturn |= AnalyzeStatement(statement, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
        }

        return hasReturn;
    }

    // Whether entering a switch case returns on every path. A case that breaks out of the switch does not fall into the
    // next case, so only a case that neither returns nor breaks inherits the next case's answer.
    private bool CaseReturns(Func<bool> analyzeBody, bool nextCaseReturns)
    {
        var savedExitDepth = _switchExitDepth;
        var savedBreak = _breakReached;
        _switchExitDepth = _breakableDepth;
        _breakReached = false;
        // A case can be entered directly, so every case starts from the state before the switch. Its end is recorded for the join.
        var frame = _switchFlows.Peek();
        RestoreFlow(frame.Start);
        var returns = analyzeBody();
        frame.Ends.Add(SnapshotFlow());
        var breaks = _breakReached;
        _switchExitDepth = savedExitDepth;
        _breakReached = savedBreak;
        return returns || (!breaks && nextCaseReturns);
    }

    private bool AnalyzeStatement(
        Statement statement,
        Scope scope,
        FunctionSymbol function,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        switch (statement)
        {
            case BlockStatement block:
                return AnalyzeBlock(block, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
            case IfStatement conditional:
                var conditionType = AnalyzeExpression(conditional.Condition, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                RequireBoolean(conditionType, conditional.Condition.Location);
                var thenScope = scope;
                if (conditional.Condition is IsExpression { BindingName: { } binding, TargetType: var targetSyntax })
                {
                    var targetType = ResolveType(targetSyntax);
                    thenScope = new Scope(scope, MarkVariableUsed);
                    thenScope.Declare(binding, new VariableSymbol(targetType, false, conditional.Condition, false));
                }
                var beforeBranches = SnapshotDeleteState();
                var beforeFlow = SnapshotFlow();
                var thenReturns = AnalyzeStatement(conditional.Then, thenScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                var afterThen = SnapshotDeleteState();
                var afterThenFlow = SnapshotFlow();
                RestoreDeleteState(beforeBranches);
                RestoreFlow(beforeFlow);
                var elseReturns = conditional.Else is not null
                    && AnalyzeStatement(conditional.Else, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                var afterElse = SnapshotDeleteState();
                // A branch that returns does not reach the code after the if, so only the other branch's deletions count.
                if (thenReturns && !elseReturns)
                    RestoreDeleteState(afterElse);
                else if (elseReturns && !thenReturns)
                    RestoreDeleteState(afterThen);
                else if (!thenReturns && !elseReturns)
                    RestoreDeleteState(JoinDeleteStates(afterThen, afterElse));
                RestoreFlow(JoinFlows(afterThenFlow, SnapshotFlow()));
                return conditional.Else is not null && thenReturns && elseReturns;
            case WhileStatement whileStatement:
                var whileType = AnalyzeExpression(whileStatement.Condition, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                RequireBoolean(whileType, whileStatement.Condition.Location);
                var beforeWhile = BeginLoopDeletes();
                var flowBeforeWhile = SnapshotFlow();
                _breakStates.Push([]);
                _loopDepth++;
                _breakableDepth++;
                AnalyzeStatement(whileStatement.Body, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                _breakableDepth--;
                _loopDepth--;
                FinishLoopDeletes(beforeWhile);
                RestoreFlow(AfterLoopFlow(flowBeforeWhile, _breakStates.Pop(),
                    whileStatement.Condition is BooleanLiteralExpression { Value: true }));
                return false;
            case ForStatement forStatement:
                var loopScope = new Scope(scope, MarkVariableUsed);
                if (forStatement.Initializer is not null)
                    AnalyzeStatement(forStatement.Initializer, loopScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                if (forStatement.Condition is not null)
                {
                    var forType = AnalyzeExpression(forStatement.Condition, loopScope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                    RequireBoolean(forType, forStatement.Condition.Location);
                }
                var beforeFor = BeginLoopDeletes();
                var flowBeforeFor = SnapshotFlow();
                _breakStates.Push([]);
                _loopDepth++;
                _breakableDepth++;
                AnalyzeStatement(forStatement.Body, loopScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                if (forStatement.Increment is not null)
                    AnalyzeStatement(forStatement.Increment, loopScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                _breakableDepth--;
                _loopDepth--;
                FinishLoopDeletes(beforeFor);
                RestoreFlow(AfterLoopFlow(flowBeforeFor, _breakStates.Pop(),
                    forStatement.Condition is null or BooleanLiteralExpression { Value: true }));
                return false;
            case ForInStatement forInStatement:
                {
                    if (!forInStatement.VariableType.IsInferred)
                        throw Error("For-in iteration variables must use 'var'", forInStatement.VariableType.Location);
                    var collectionType = AnalyzeExpression(forInStatement.Collection, scope, functions, moduleName,
                        imports, expressionTypes, resolvedCalls);
                    SemanticType iterationElement;
                    if (collectionType.IsArray && collectionType.ElementType is not null)
                    {
                        // Fixed arrays use the intrinsic lowering path for now.
                        // User-defined collections below must satisfy iter/next.
                        iterationElement = collectionType.ElementType;
                    }
                    else if (!collectionType.IsClass
                             || !_classesByName.TryGetValue(collectionType.StructName!, out var collectionClass))
                        throw Error($"For-in requires a fixed array or iterator-protocol collection, got {collectionType}",
                            forInStatement.Collection.Location);
                    else
                    {
                        var iterCandidates = GetClassMethods(collectionClass.Name, [])
                            .Where(method => method.Name == "iter" && method.Parameters.Count == 0).ToArray();
                        if (iterCandidates.Length != 1)
                            throw Error($"Collection class '{collectionClass.Name}' must declare exactly one iter() method",
                                forInStatement.Collection.Location);
                        var iter = iterCandidates[0];
                        var iterCall = new CallExpression($"{collectionClass.Name}.iter", [],
                            forInStatement.Collection.Location);
                        var iteratorType = iter.ReturnType;
                        if (!iteratorType.IsClass || !_classesByName.TryGetValue(iteratorType.StructName!, out var iteratorClass))
                            throw Error("Iterator 'iter()' must return an iterator class", iterCall.Location);
                        // The receiver for next() is represented by the iterator value
                        // in the lowering; resolve the declared method directly here.
                        var nextCandidates = GetClassMethods(iteratorClass.Name, [])
                            .Where(method => method.Name == "next" && method.Parameters.Count == 0).ToArray();
                        if (nextCandidates.Length != 1)
                            throw Error($"Iterator class '{iteratorClass.Name}' must declare exactly one next() method",
                                forInStatement.Collection.Location);
                        var next = nextCandidates[0];
                        var option = next.ReturnType;
                        if (!option.IsUnion || !string.Equals(option.EnumName, "Option", StringComparison.Ordinal)
                            || option.TypeArguments is not { Count: 1 })
                            throw Error("Iterator next() must return Option<T> with the iteration element type",
                                forInStatement.Collection.Location);
                        iterationElement = option.TypeArguments[0];
                        _iteratorProtocols[forInStatement] = new IteratorProtocol(
                            forInStatement.Collection, iterCall, iter, next, iterationElement, option);
                        _usedFunctions.Add(iter);
                        _usedFunctions.Add(next);
                    }
                    var forInScope = new Scope(scope, MarkVariableUsed);
                    if (forInScope.ContainsInCurrentScope(forInStatement.VariableName))
                        throw Error($"Variable '{forInStatement.VariableName}' is already declared in this scope",
                            forInStatement.Location, forInStatement.VariableName.Length);
                    var variable = new VariableSymbol(iterationElement, false,
                        new VariableDeclarationStatement(forInStatement.VariableType, forInStatement.VariableName, false,
                            null, forInStatement.Location), false);
                    forInScope.Declare(forInStatement.VariableName, variable);
                    _declaredVariables.Add(variable);
                    var beforeForIn = BeginLoopDeletes();
                    var flowBeforeForIn = SnapshotFlow();
                    _breakStates.Push([]);
                    _loopDepth++;
                    _breakableDepth++;
                    AnalyzeStatement(forInStatement.Body, forInScope, function, functions, moduleName, imports,
                        expressionTypes, resolvedCalls);
                    _breakableDepth--;
                    _loopDepth--;
                    FinishLoopDeletes(beforeForIn);
                    RestoreFlow(AfterLoopFlow(flowBeforeForIn, _breakStates.Pop(), alwaysTrue: false));
                    return false;
                }
            case SwitchStatement switchStatement:
                {
                    var valueType = AnalyzeExpression(switchStatement.Value, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                    _switchFlows.Push((SnapshotFlow(), []));
                    _breakStates.Push([]);
                    _breakableDepth++;
                    try
                    {
                        if (valueType.IsUnion)
                        {
                            var union = _unionsByName[valueType.EnumName!];
                            var unionSubstitutions = union.DeclaredTypeParameters
                                .Zip(valueType.TypeArguments ?? [], (name, type) => (name, type))
                                .ToDictionary(item => item.name, item => item.type, StringComparer.Ordinal);
                            var seen = new HashSet<string>(StringComparer.Ordinal);
                            var caseReturns = new bool[switchStatement.Cases.Count];
                            for (var index = switchStatement.Cases.Count - 1; index >= 0; index--)
                            {
                                var item = switchStatement.Cases[index];
                                if (item.IsDefault || item.Value is not null)
                                    throw Error("Union switches require named union cases", item.Location);
                                if (item.CaseName is null || !seen.Add(item.CaseName)
                                    || !union.CasesByName.TryGetValue(item.CaseName, out var unionCase))
                                    throw Error($"Unknown or duplicate union case '{item.CaseName}'", item.Location);
                                if ((item.BindingName is null) != (unionCase.PayloadType is null))
                                    throw Error($"Union case '{item.CaseName}' {(unionCase.PayloadType is null ? "does not have" : "requires")} a payload binding", item.Location);
                                var caseScope = new Scope(scope, MarkVariableUsed);
                                if (item.BindingName is not null)
                                    caseScope.Declare(item.BindingName, new VariableSymbol(
                                        SubstituteGenericType(unionCase.PayloadType!, unionSubstitutions),
                                        false, item, false));
                                caseReturns[index] = CaseReturns(
                                    () => AnalyzeStatement(item.Body, caseScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls),
                                    index + 1 < caseReturns.Length && caseReturns[index + 1]);
                            }
                            if (seen.Count != union.Cases.Count)
                                throw Error($"Switch must handle every case of union '{union.Name}'", switchStatement.Location);
                            return caseReturns.Length > 0 && caseReturns[0];
                        }

                        if (switchStatement.Cases.Any(item => item.PatternType is not null))
                        {
                            if (!valueType.IsClass && !valueType.IsInterface)
                                throw Error("Type-pattern switches require a class or interface value", switchStatement.Value.Location);
                            var caseReturns = new bool[switchStatement.Cases.Count];
                            for (var index = switchStatement.Cases.Count - 1; index >= 0; index--)
                            {
                                var item = switchStatement.Cases[index];
                                if (item.IsDefault)
                                {
                                    if (item.PatternType is not null || item.BindingName is not null)
                                        throw Error("Default switch cases cannot bind a value", item.Location);
                                }
                                else
                                {
                                    if (item.PatternType is null || item.Value is not null || item.CaseName is not null)
                                        throw Error("Type-pattern switches require 'case is Type' labels", item.Location);
                                    var targetType = AnalyzeTypePattern(item.PatternType, valueType, item.Location);
                                    if (item.BindingName is not null)
                                    {
                                        var caseScope = new Scope(scope, MarkVariableUsed);
                                        caseScope.Declare(item.BindingName, new VariableSymbol(targetType, false, item, false));
                                        caseReturns[index] = CaseReturns(
                                            () => AnalyzeStatement(item.Body, caseScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls),
                                            index + 1 < caseReturns.Length && caseReturns[index + 1]);
                                    }
                                    else
                                    {
                                        caseReturns[index] = CaseReturns(
                                            () => AnalyzeStatement(item.Body, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls),
                                            index + 1 < caseReturns.Length && caseReturns[index + 1]);
                                    }
                                    continue;
                                }
                                caseReturns[index] = CaseReturns(
                                    () => AnalyzeStatement(item.Body, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls),
                                    index + 1 < caseReturns.Length && caseReturns[index + 1]);
                            }
                            return caseReturns.Length > 0 && caseReturns[0];
                        }

                        if (!IsSwitchComparable(valueType))
                            throw Error($"Switch requires a comparable value, got {valueType}", switchStatement.Value.Location);
                        var seenLabels = new HashSet<string>(StringComparer.Ordinal);
                        var hasDefault = false;
                        var caseReturnsForValue = new bool[switchStatement.Cases.Count];
                        for (var index = switchStatement.Cases.Count - 1; index >= 0; index--)
                        {
                            var item = switchStatement.Cases[index];
                            if (item.CaseName is not null || item.BindingName is not null)
                                throw Error("Value switches require constant case labels", item.Location);
                            if (item.IsDefault)
                            {
                                if (hasDefault)
                                    throw Error("A switch can contain only one default case", item.Location);
                                hasDefault = true;
                            }
                            else
                            {
                                if (item.Value is null)
                                    throw Error("Switch case is missing a value", item.Location);
                                var labelType = AnalyzeExpression(item.Value, scope, functions, moduleName, imports,
                                    expressionTypes, resolvedCalls);
                                if (!IsConstantSwitchLabel(item.Value, labelType, valueType)
                                    || (labelType != valueType
                                        && !(valueType.IsPointer && item.Value is NullLiteralExpression)
                                        && !CanAssign(valueType, item.Value, labelType)))
                                    throw Error($"Switch case value must be a constant comparable with {valueType}", item.Location);
                                if (!seenLabels.Add(SwitchLabelKey(item.Value)))
                                    throw Error("Duplicate switch case value", item.Location);
                            }

                            caseReturnsForValue[index] = CaseReturns(
                                () => AnalyzeStatement(item.Body, scope, function, functions, moduleName, imports,
                                    expressionTypes, resolvedCalls),
                                index + 1 < caseReturnsForValue.Length && caseReturnsForValue[index + 1]);
                        }
                        return hasDefault && caseReturnsForValue.Length > 0 && caseReturnsForValue[0];
                    }
                    finally
                    {
                        _breakableDepth--;
                        // After the switch: the end of every case that is reached, every break, and the state where no case
                        // matches. A union switch that names every case has no such path.
                        var (switchStart, caseEnds) = _switchFlows.Pop();
                        var breaks = _breakStates.Pop();
                        var exhaustive = switchStatement.Cases.Any(item => item.IsDefault) || valueType.IsUnion;
                        var afterSwitch = exhaustive ? UnreachableFlow() : switchStart;
                        foreach (var state in caseEnds.Concat(breaks))
                            afterSwitch = JoinFlows(afterSwitch, state);
                        RestoreFlow(afterSwitch);
                    }
                }
            case BreakStatement breakStatement when _breakableDepth == 0:
                throw Error("'break' is only valid inside a loop or switch", breakStatement.Location);
            case ContinueStatement continueStatement when _loopDepth == 0:
                throw Error("'continue' is only valid inside a loop", continueStatement.Location);
            case BreakStatement:
                // A break that leaves the switch being analyzed ends the case without falling into the next one.
                if (_breakableDepth == _switchExitDepth)
                    _breakReached = true;
                _breakStates.Peek().Add(SnapshotFlow());
                _reachable = false;
                return false;
            case ContinueStatement:
                _reachable = false;
                return false;
            case VariableDeclarationStatement declaration:
                AnalyzeVariableDeclaration(declaration, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                if (declaration.Initializer is null)
                    _unassignedLocals.Add(declaration);
                return false;
            case AssignmentStatement assignment:
                AnalyzeAssignment(assignment, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                return false;
            case ReturnStatement returnStatement:
                AnalyzeReturn(returnStatement, function, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                _reachable = false;
                return true;
            case DeleteStatement deleteStatement:
                AnalyzeDelete(deleteStatement, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                return false;
            case ThrowStatement throwStatement:
                {
                    var thrownType = AnalyzeExpression(throwStatement.Value, scope, functions, moduleName, imports,
                        expressionTypes, resolvedCalls);
                    if (!IsExceptionType(thrownType))
                        throw Error($"Only exception class values can be thrown, got {thrownType}",
                            throwStatement.Value.Location);
                    _reachable = false;
                    return true;
                }
            case TryStatement tryStatement:
                {
                    var beforeTry = SnapshotFlow();
                    var tryReturns = AnalyzeBlock(tryStatement.Body, scope, function, functions, moduleName, imports,
                        expressionTypes, resolvedCalls);
                    var afterCatches = SnapshotFlow();
                    var allCatchesReturn = true;
                    var catchAllSeen = false;
                    foreach (var (catchClause, index) in tryStatement.Catches.Select((item, index) => (item, index)))
                    {
                        SemanticType? catchType = null;
                        if (catchClause.ExceptionType is not null)
                        {
                            if (catchAllSeen)
                                throw Error("A typed catch cannot follow a catch-all clause", catchClause.Location);
                            catchType = ResolveType(catchClause.ExceptionType);
                            if (!IsExceptionType(catchType))
                                throw Error($"Catch type '{catchType}' must be an exception class",
                                    catchClause.ExceptionType.Location);
                        }
                        else
                        {
                            if (catchAllSeen)
                                throw Error("A try statement cannot contain multiple catch-all clauses", catchClause.Location);
                            catchAllSeen = true;
                            if (index != tryStatement.Catches.Count - 1)
                                throw Error("The catch-all clause must be last", catchClause.Location);
                        }

                        var catchScope = scope;
                        if (catchClause.BindingName is not null)
                        {
                            catchScope = new Scope(scope, MarkVariableUsed);
                            catchScope.Declare(catchClause.BindingName,
                                new VariableSymbol(catchType!, false, catchClause, false));
                        }

                        // A catch can be entered from any point in the try body, so it starts from the state before the try.
                        RestoreFlow(beforeTry);
                        var catchReturns = AnalyzeBlock(catchClause.Body, catchScope, function, functions, moduleName,
                            imports, expressionTypes, resolvedCalls);
                        allCatchesReturn &= catchReturns;
                        afterCatches = JoinFlows(afterCatches, SnapshotFlow());
                    }

                    var finallyReturns = false;
                    if (tryStatement.Finally is null)
                    {
                        RestoreFlow(afterCatches);
                    }
                    else
                    {
                        // The finally block also runs on the exceptional paths, so it starts from every state the try can leave.
                        // Code after the try is reached only if the normal paths reach it.
                        var finallyStart = new HashSet<VariableDeclarationStatement>(
                            beforeTry.Unassigned.Concat(afterCatches.Unassigned), ReferenceEqualityComparer.Instance);
                        RestoreFlow(new FlowState(finallyStart, beforeTry.Reachable));
                        finallyReturns = AnalyzeBlock(tryStatement.Finally, scope, function, functions, moduleName, imports,
                            expressionTypes, resolvedCalls);
                        var afterFinally = SnapshotFlow();
                        // The normal path is the one that leaves the try: the finally block's assignments apply to it.
                        var assignedByFinally = finallyStart.Where(declaration => !afterFinally.Unassigned.Contains(declaration));
                        var normalAfter = afterCatches.Unassigned.Where(declaration => !assignedByFinally.Contains(declaration));
                        RestoreFlow(new FlowState(
                            new HashSet<VariableDeclarationStatement>(normalAfter, ReferenceEqualityComparer.Instance),
                            afterCatches.Reachable && afterFinally.Reachable));
                    }
                    return finallyReturns || (tryReturns && allCatchesReturn);
                }
            case ExpressionStatement expressionStatement:
                AnalyzeExpression(
                    expressionStatement.Expression,
                    scope,
                    functions,
                    moduleName,
                    imports,
                    expressionTypes,
                    resolvedCalls,
                    allowVoid: true);
                return false;
            default:
                throw new InvalidOperationException($"Unsupported statement type {statement.GetType().Name}.");
        }
    }

    private void RequireBoolean(SemanticType type, SourceLocation location)
    {
        if (type.Primitive != PrimitiveType.Bool || type.Kind != SemanticTypeKind.Primitive)
            throw Error($"Loop and conditional conditions must be bool, got {type}", location);
    }

    private bool IsExceptionType(SemanticType type)
    {
        if (!type.IsClass || type.StructName is null || !_classesByName.ContainsKey(type.StructName))
            return false;
        return string.Equals(type.StructName, "Exception", StringComparison.Ordinal)
               || IsClassDerivedFrom(type.StructName, "Exception");
    }

    private void AnalyzeVariableDeclaration(
        VariableDeclarationStatement declaration,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        if (declaration.Type.IsInferred && declaration.Initializer is null)
        {
            throw Error($"Variable '{declaration.Name}' using 'var' requires an initializer", declaration.Location, declaration.Name.Length);
        }

        if (scope.ContainsInCurrentScope(declaration.Name))
        {
            throw Error($"Variable '{declaration.Name}' is already declared in this scope", declaration.Location, declaration.Name.Length);
        }

        var initializerType = declaration.Initializer is null
            ? (SemanticType?)null
            : AnalyzeExpression(declaration.Initializer, scope, functions, moduleName, imports, expressionTypes, resolvedCalls,
                expectedType: declaration.Type.IsInferred ? null : ResolveType(declaration.Type));
        var type = declaration.Type.IsInferred
            ? initializerType!
            : ResolveType(declaration.Type);

        if (type.IsVoid)
        {
            throw Error($"Variable '{declaration.Name}' cannot have type void", declaration.Location, declaration.Name.Length);
        }

        if (initializerType is { IsArray: true } && type.IsArray
            && initializerType.ArrayLength != type.ArrayLength)
        {
            throw Error(
                $"Array initializer for '{declaration.Name}' expects {type.ArrayLength} element(s), got {initializerType.ArrayLength}",
                declaration.Initializer!.Location);
        }

        if (initializerType is not null && !CanAssign(type, declaration.Initializer!, initializerType))
        {
            throw Error(
                $"Cannot initialize '{declaration.Name}' with {initializerType}; expected {type}",
                declaration.Initializer!.Location);
        }
        if (declaration.Initializer is NullLiteralExpression && type.IsPointer)
            expressionTypes[declaration.Initializer] = type;

        var variable = new VariableSymbol(type, declaration.IsConst, declaration, false);
        scope.Declare(declaration.Name, variable);
        _declaredVariables.Add(variable);
        if (type.IsPointer && declaration.Initializer is NullLiteralExpression)
            _definitelyNullVariables.Add(variable);
    }

    private void AnalyzeAssignment(
        AssignmentStatement assignment,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        // Assigning to a variable is not a use of it, so a deleted variable can be given a new value.
        var targetType = assignment.Target is IdentifierExpression assignedIdentifier
            ? AssignmentTargetType(assignedIdentifier, scope, expressionTypes)
            : AnalyzeExpression(assignment.Target, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        // A compound assignment reads its target before it writes it.
        if (assignment.Operator != TokenKind.Equals && assignment.Target is IdentifierExpression compoundTarget
            && scope.Lookup(compoundTarget.Name, compoundTarget.Location).Declaration is VariableDeclarationStatement compoundDeclaration)
            CheckAssigned(compoundDeclaration, compoundTarget.Name, compoundTarget.Location);
        var valueType = AnalyzeExpression(assignment.Value, scope, functions, moduleName, imports, expressionTypes, resolvedCalls,
            expectedType: targetType);
        if (assignment.Target is IdentifierExpression reassigned
            && scope.Lookup(reassigned.Name, reassigned.Location).Declaration is VariableDeclarationStatement reassignedDeclaration)
        {
            _deletedLocals.Remove(reassignedDeclaration);
            _maybeDeletedLocals.Remove(reassignedDeclaration);
            _unassignedLocals.Remove(reassignedDeclaration);
        }
        if (assignment.Operator != TokenKind.Equals)
        {
            if (targetType.IsPointer)
            {
                if (assignment.Operator is not (TokenKind.PlusEquals or TokenKind.MinusEquals)
                    || !valueType.IsInteger)
                    throw Error($"Operator '{assignment.Operator}' is not valid for pointer operands", assignment.Location);
            }
            else if (targetType.IsInteger && valueType.IsInteger)
            {
                // All integer compound operators are valid.
            }
            else if (targetType.IsNumeric && valueType.IsNumeric
                     && assignment.Operator is TokenKind.PlusEquals or TokenKind.MinusEquals
                        or TokenKind.StarEquals or TokenKind.SlashEquals)
            {
            }
            else
            {
                throw Error($"Operator '{assignment.Operator}' requires compatible operands", assignment.Location);
            }
        }
        else if (!CanAssign(targetType, assignment.Value, valueType))
        {
            throw Error($"Cannot assign {valueType} to target of type {targetType}", assignment.Value.Location);
        }

        ValidateWritableTarget(assignment.Target, scope);
        TrackPointerAssignment(assignment, assignment.Value, valueType, scope);
    }

    private void ValidateWritableTarget(Expression target, Scope scope)
    {
        if (target is IdentifierExpression identifier)
        {
            var variable = scope.Lookup(identifier.Name, identifier.Location);
            if (variable.IsConst)
                throw Error($"Cannot assign to const variable '{identifier.Name}'", target.Location, identifier.Name.Length);
        }
        else if (target is MemberAccessExpression member)
        {
            if (member.Target is IdentifierExpression memberIdentifier
                && scope.Lookup(memberIdentifier.Name, memberIdentifier.Location).IsConst)
                throw Error($"Cannot assign to a field of const variable '{memberIdentifier.Name}'", target.Location,
                    memberIdentifier.Name.Length);
            var targetClass = member.Target is ThisExpression && _currentOwnerClass is not null
                ? _currentOwnerClass
                : member.Target is IdentifierExpression classIdentifier
                    && scope.Lookup(classIdentifier.Name, classIdentifier.Location).Type.IsClass
                    ? _classesByName[scope.Lookup(classIdentifier.Name, classIdentifier.Location).Type.StructName!]
                    : null;
            var field = targetClass?.Fields.FirstOrDefault(candidate => candidate.Name == member.MemberName);
            if (field is not null && field.IsConst && !_currentIsConstructor)
                throw Error($"Cannot assign to const field '{field.Name}'", target.Location, field.Name.Length);
            ValidatePropertyWrite(member, scope);
        }
        else if (target is IndexExpression index
                 && index.Target is IdentifierExpression indexIdentifier
                 && scope.Lookup(indexIdentifier.Name, indexIdentifier.Location).IsConst)
            throw Error($"Cannot assign to an element of const variable '{indexIdentifier.Name}'", target.Location,
                indexIdentifier.Name.Length);
        else if (target is not (UnaryExpression { Operator: TokenKind.Star } or IndexExpression or MemberAccessExpression))
        {
            throw Error("Assignment target is not addressable", target.Location);
        }
    }

    private void ValidatePropertyWrite(MemberAccessExpression member, Scope scope)
    {
        var targetClass = member.Target switch
        {
            ThisExpression when _currentOwnerClass is not null => _currentOwnerClass,
            IdentifierExpression identifier when scope.Lookup(identifier.Name, identifier.Location).Type.IsClass
                => _classesByName[scope.Lookup(identifier.Name, identifier.Location).Type.StructName!],
            _ => null
        };
        if (targetClass is null)
            return;
        var property = FindProperty(targetClass, member.MemberName);
        if (property is null)
            return;
        var accessor = property.Declaration.Accessors.FirstOrDefault(candidate =>
            candidate.Kind == PropertyAccessorKind.Set)
            ?? property.Declaration.Accessors.FirstOrDefault(candidate =>
                candidate.Kind == PropertyAccessorKind.Init);
        if (accessor is null)
            throw Error($"Property '{property.Name}' is read-only", member.Location, property.Name.Length);
        if (accessor.Kind == PropertyAccessorKind.Init && !_currentIsConstructor)
            throw Error($"Property '{property.Name}' can only be assigned in a constructor", member.Location,
                property.Name.Length);
        EnsureAccessorVisible(property, accessor, member.Location);
    }

    private void TrackPointerAssignment(
        AssignmentStatement assignment,
        Expression value,
        SemanticType valueType,
        Scope scope)
    {
        if (assignment.Target is IdentifierExpression assignedIdentifier)
        {
            var variable = scope.Lookup(assignedIdentifier.Name, assignedIdentifier.Location);
            if (IsOwnedClass(valueType) && assignment.Value is IdentifierExpression sourceIdentifier)
            {
                var source = scope.Lookup(sourceIdentifier.Name, sourceIdentifier.Location);
                if (source.Declaration is VariableDeclarationStatement sourceDeclaration
                    && scope.LookupDepth(assignedIdentifier.Name, assignedIdentifier.Location)
                    > scope.LookupDepth(sourceIdentifier.Name, sourceIdentifier.Location))
                    _escapingClassLocals.Add(sourceDeclaration);
            }
            if (variable.Type.IsPointer && assignment.Operator == TokenKind.Equals)
            {
                if (value is NullLiteralExpression)
                    _definitelyNullVariables.Add(variable);
                else
                    _definitelyNullVariables.Remove(variable);
            }
        }
    }

    private void AnalyzeReturn(
        ReturnStatement returnStatement,
        FunctionSymbol function,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        if (returnStatement.Value is null)
        {
            if (!function.ReturnType.IsVoid)
            {
                throw Error($"Function '{function.Name}' must return a value of type {function.ReturnType}", returnStatement.Location);
            }

            return;
        }

        if (function.ReturnType.IsVoid)
        {
            throw Error($"Void function '{function.Name}' cannot return a value", returnStatement.Value.Location);
        }

        var valueType = AnalyzeExpression(returnStatement.Value, scope, functions, moduleName, imports, expressionTypes, resolvedCalls,
            expectedType: function.ReturnType);
        if (IsOwnedClass(valueType) && returnStatement.Value is IdentifierExpression identifier
            && scope.Lookup(identifier.Name, identifier.Location).Declaration is VariableDeclarationStatement declaration)
            _escapingClassLocals.Add(declaration);
        if (!CanAssign(function.ReturnType, returnStatement.Value, valueType))
        {
            throw Error(
                $"Cannot return {valueType} from function '{function.Name}'; expected {function.ReturnType}",
                returnStatement.Value.Location);
        }
        if (returnStatement.Value is NullLiteralExpression && function.ReturnType.IsPointer)
            expressionTypes[returnStatement.Value] = function.ReturnType;
    }

    // A loop body starts a list of its deletes; the deletion state before the loop is kept for the join after it.
    private DeleteState BeginLoopDeletes()
    {
        _loopDeletes.Push([]);
        return SnapshotDeleteState();
    }

    private void FinishLoopDeletes(DeleteState beforeLoop)
    {
        var afterBody = SnapshotDeleteState();
        var bodyDeletes = _loopDeletes.Pop();
        // A delete in the body runs again on the next iteration. If the body leaves its local deleted, that delete may repeat,
        // so it is checked at run time.
        foreach (var (statement, declaration) in bodyDeletes)
            if (afterBody.Definite.Contains(declaration) || afterBody.Maybe.Contains(declaration))
                _runtimeCheckedDeletes.Add(statement);
        if (_loopDeletes.Count > 0)
            foreach (var entry in bodyDeletes)
                _loopDeletes.Peek().Add(entry);
        // The body may run zero times, so after the loop only what is deleted on both paths is definitely deleted.
        RestoreDeleteState(JoinDeleteStates(beforeLoop, afterBody));
    }

    // The assignment state at one point: the locals that may be unassigned on some path to it, and whether it is reached at all.
    // A path that returns, throws, breaks or continues does not reach the code after it, so its state is not joined there.
    private sealed record FlowState(HashSet<VariableDeclarationStatement> Unassigned, bool Reachable);

    private FlowState SnapshotFlow() =>
        new(new HashSet<VariableDeclarationStatement>(_unassignedLocals, ReferenceEqualityComparer.Instance), _reachable);

    // The flow state of a body being analyzed, kept while another body is analyzed inside it.
    private sealed record SavedFlowContext(
        HashSet<VariableDeclarationStatement> Unassigned,
        bool Reachable,
        Stack<List<FlowState>> BreakStates,
        Stack<(FlowState Start, List<FlowState> Ends)> SwitchFlows);

    private SavedFlowContext SaveAndResetFlowContext()
    {
        var saved = new SavedFlowContext(
            new HashSet<VariableDeclarationStatement>(_unassignedLocals, ReferenceEqualityComparer.Instance),
            _reachable, _breakStates, _switchFlows);
        _unassignedLocals.Clear();
        _reachable = true;
        _breakStates = new();
        _switchFlows = new();
        return saved;
    }

    private void RestoreFlowContext(SavedFlowContext saved)
    {
        _unassignedLocals.Clear();
        _unassignedLocals.UnionWith(saved.Unassigned);
        _reachable = saved.Reachable;
        _breakStates = saved.BreakStates;
        _switchFlows = saved.SwitchFlows;
    }

    private void RestoreFlow(FlowState state)
    {
        _unassignedLocals.Clear();
        _unassignedLocals.UnionWith(state.Unassigned);
        _reachable = state.Reachable;
    }

    private static FlowState UnreachableFlow() =>
        new(new HashSet<VariableDeclarationStatement>(ReferenceEqualityComparer.Instance), false);

    // Where two paths meet, a local is unassigned if it is unassigned on either path. An unreachable path adds nothing.
    private static FlowState JoinFlows(FlowState first, FlowState second)
    {
        if (!first.Reachable)
            return second;
        if (!second.Reachable)
            return first;
        return new FlowState(new HashSet<VariableDeclarationStatement>(first.Unassigned.Concat(second.Unassigned),
            ReferenceEqualityComparer.Instance), true);
    }

    private void CheckAssigned(VariableDeclarationStatement declaration, string name, SourceLocation location)
    {
        if (_unassignedLocals.Contains(declaration))
            throw Error($"Variable '{name}' may be read before it is assigned", location, name.Length);
    }

    // After a loop: the path where the condition ends it, which does not exist when the condition is always true, joined with
    // every break. The body's own end state does not count, because the body may run zero times.
    private static FlowState AfterLoopFlow(FlowState beforeLoop, List<FlowState> breaks, bool alwaysTrue)
    {
        var after = alwaysTrue ? UnreachableFlow() : beforeLoop;
        foreach (var state in breaks)
            after = JoinFlows(after, state);
        return after;
    }

    // The deletion state of the locals at one point in the analysis: deleted on every path, and deleted on some path.
    private sealed record DeleteState(HashSet<VariableDeclarationStatement> Definite, HashSet<VariableDeclarationStatement> Maybe);

    private DeleteState SnapshotDeleteState() =>
        new(new HashSet<VariableDeclarationStatement>(_deletedLocals, ReferenceEqualityComparer.Instance),
            new HashSet<VariableDeclarationStatement>(_maybeDeletedLocals, ReferenceEqualityComparer.Instance));

    private void RestoreDeleteState(DeleteState state)
    {
        _deletedLocals.Clear();
        _deletedLocals.UnionWith(state.Definite);
        _maybeDeletedLocals.Clear();
        _maybeDeletedLocals.UnionWith(state.Maybe);
    }

    // Where two paths meet: a local is deleted on every path only if it is deleted on both; it is deleted on some path if
    // it is deleted on either.
    private static DeleteState JoinDeleteStates(DeleteState first, DeleteState second)
    {
        var definite = new HashSet<VariableDeclarationStatement>(first.Definite.Where(second.Definite.Contains),
            ReferenceEqualityComparer.Instance);
        var maybe = new HashSet<VariableDeclarationStatement>(
            first.Maybe.Concat(second.Maybe).Concat(first.Definite).Concat(second.Definite), ReferenceEqualityComparer.Instance);
        return new DeleteState(definite, maybe);
    }

    // Reads a variable. A deleted variable cannot be read, and reading its value (not only as a receiver) makes it escape.
    private SemanticType ReadIdentifier(IdentifierExpression identifier, Scope scope)
    {
        var symbol = scope.Lookup(identifier.Name, identifier.Location);
        if (symbol.Declaration is VariableDeclarationStatement declaration)
        {
            CheckAssigned(declaration, identifier.Name, identifier.Location);
            if (_deletedLocals.Contains(declaration))
                throw Error($"'{identifier.Name}' was deleted and cannot be used", identifier.Location, identifier.Name.Length);
            if (!_receiverUse)
                _valueUses.Add(declaration);
        }

        return symbol.Type;
    }

    private SemanticType AssignmentTargetType(IdentifierExpression target, Scope scope,
        IDictionary<Expression, SemanticType> expressionTypes)
    {
        var type = scope.Lookup(target.Name, target.Location).Type;
        expressionTypes[target] = type;
        return type;
    }

    // `delete target;` releases an object allocated with `new`: its destructor runs, then its memory is freed. Deleting a
    // variable marks it deleted; deleting it again, or reading it afterwards, is an error. Only a class object can be deleted.
    private void AnalyzeDelete(
        DeleteStatement statement,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        SemanticType targetType;
        if (statement.Target is IdentifierExpression identifier)
        {
            var symbol = scope.Lookup(identifier.Name, identifier.Location);
            targetType = AssignmentTargetType(identifier, scope, expressionTypes);
            if (symbol.Declaration is VariableDeclarationStatement declaration && IsOwnedClass(targetType))
            {
                if (_deletedLocals.Contains(declaration))
                    throw Error($"'{identifier.Name}' is already deleted", identifier.Location, identifier.Name.Length);
                // Deleting a local that may already have been deleted needs a runtime check, not a compile error.
                if (_maybeDeletedLocals.Contains(declaration))
                    _runtimeCheckedDeletes.Add(statement);
                _deletedAnywhere.Add(declaration);
                if (_loopDeletes.Count > 0)
                    _loopDeletes.Peek().Add((statement, declaration));
                _deletedLocals.Add(declaration);
                _maybeDeletedLocals.Add(declaration);
            }
        }
        else
        {
            targetType = AnalyzeExpression(statement.Target, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        }

        if (!IsOwnedClass(targetType))
            throw Error($"'delete' requires a reference to a class object, got {targetType}", statement.Target.Location);
    }

    // A local that holds an object from `new` leaves scope without a `delete` and without escaping, so its memory is leaked.
    // A `new` object held by a local that is never deleted and never escapes. Such an object is leaked unless the
    // function frees it, so it is what the leak warning and the automatic free are about.
    private bool IsUnfreedAllocation(VariableSymbol variable, out VariableDeclarationStatement declaration)
    {
        if (variable.Declaration is VariableDeclarationStatement { Initializer: NewExpression } found
            && IsOwnedClass(variable.Type)
            && !_deletedLocals.Contains(found)
            && !_maybeDeletedLocals.Contains(found)
            && !_escapingClassLocals.Contains(found)
            && !_valueUses.Contains(found))
        {
            declaration = found;
            return true;
        }

        declaration = null!;
        return false;
    }

    // A `new` object that the function frees at scope end: never read as a value, and not definitely deleted by the end of
    // the function. A local that may already be deleted at that point gets a runtime flag in the backend.
    private bool IsAutomaticallyFreedAllocation(VariableSymbol variable, out VariableDeclarationStatement declaration)
    {
        if (variable.Declaration is VariableDeclarationStatement { Initializer: NewExpression } found
            && IsOwnedClass(variable.Type)
            && !_deletedLocals.Contains(found)
            && !_escapingClassLocals.Contains(found)
            && !_valueUses.Contains(found))
        {
            declaration = found;
            return true;
        }

        declaration = null!;
        return false;
    }

    // In an automatic function, an unfreed allocation is freed when its scope ends. This is recorded for the backend.
    private void CollectAutomaticFrees(FunctionSymbol function)
    {
        if (function.Memory != MemoryMode.Automatic)
            return;
        foreach (var variable in _declaredVariables)
            if (IsAutomaticallyFreedAllocation(variable, out var declaration))
                _autoFreedLocals.Add(declaration);
    }

    // Only manual functions warn: an automatic function frees its objects itself, so a missing `delete` is not a leak there.
    private void WarnAboutUnfreedAllocations(FunctionSymbol function)
    {
        if (function.Memory != MemoryMode.Manual)
            return;
        foreach (var variable in _declaredVariables)
            if (IsUnfreedAllocation(variable, out var declaration))
                ReportWarning($"Memory allocated for '{declaration.Name}' is never freed", declaration.Location, declaration.Name.Length);
    }

    private SemanticType AnalyzeExpression(
        Expression expression,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        SemanticType? expectedType = null,
        bool allowVoid = false)
    {
        var type = expression switch
        {
            IdentifierExpression identifier => ReadIdentifier(identifier, scope),
            ThisExpression thisExpression => scope.LookupThis(thisExpression.Location),
            IntegerLiteralExpression integer => IntegerLiteralType(integer),
            FloatingPointLiteralExpression floating => SemanticType.FromPrimitive(ParseNumericLiteralType(floating.Lexeme, true)),
            StringLiteralExpression => SemanticType.String,
            BooleanLiteralExpression => SemanticType.FromPrimitive(PrimitiveType.Bool),
            NullLiteralExpression => SemanticType.PointerTo(SemanticType.FromPrimitive(PrimitiveType.Void)),
            IsExpression pattern => AnalyzeIsExpression(
                pattern, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            StructLiteralExpression literal => AnalyzeStructLiteral(
                literal, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            ArrayLiteralExpression literal => AnalyzeArrayLiteral(literal, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            IndexExpression index => AnalyzeIndex(index, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            MemberAccessExpression member => AnalyzeMemberAccess(
                member, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            CastExpression cast => AnalyzeCast(cast, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            SizeOfExpression sizeOf => AnalyzeSizeOf(sizeOf, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            UnionConstructionExpression construction => AnalyzeUnionConstruction(construction, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            UnaryExpression unary => AnalyzeUnary(unary, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            BinaryExpression binary => AnalyzeBinary(binary, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            ConditionalExpression conditional => AnalyzeConditional(
                conditional, scope, functions, moduleName, imports, expressionTypes, resolvedCalls, expectedType),
            CallExpression call => AnalyzeCall(call, scope, functions, moduleName, imports, expressionTypes, resolvedCalls, expectedType, allowVoid),
            ClassConstructionExpression creation => AnalyzeClassConstructionExpression(
                creation, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            NewExpression creation => AnalyzeNewExpression(
                creation, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            _ => throw new InvalidOperationException($"Unsupported expression type {expression.GetType().Name}.")
        };

        expressionTypes[expression] = type;
        return type;
    }

    private SemanticType AnalyzeIsExpression(
        IsExpression expression,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var operandType = AnalyzeExpression(expression.Operand, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        AnalyzeTypePattern(expression.TargetType, operandType, expression.Location);
        if (expression.BindingName is not null && scope.ContainsInCurrentScope(expression.BindingName))
            throw Error($"A variable named '{expression.BindingName}' is already declared in this scope", expression.Location);
        return SemanticType.FromPrimitive(PrimitiveType.Bool);
    }

    private SemanticType AnalyzeTypePattern(TypeSyntax syntax, SemanticType operandType, SourceLocation location)
    {
        var targetType = ResolveType(syntax);
        if (!targetType.IsClass && !targetType.IsInterface)
            throw Error($"The 'is' pattern target must be a class or interface, got {targetType}", location);
        if (!operandType.IsClass && !operandType.IsInterface)
            throw Error($"The 'is' pattern operand must be a class or interface, got {operandType}", location);
        if (!IsReferenceCast(operandType, targetType, out _)
            && !IsReferenceCast(targetType, operandType, out _))
            throw Error($"Types {operandType} and {targetType} are not related", location);
        return targetType;
    }

    private SemanticType AnalyzeSizeOf(
        SizeOfExpression expression, Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions, string moduleName,
        IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression, SemanticType> types,
        IDictionary<CallExpression, FunctionSymbol> calls)
    {
        var target = expression.TargetType is not null
            ? ResolveType(expression.TargetType)
            : expression.Operand is not null
                ? AnalyzeExpression(expression.Operand, scope, functions, moduleName, imports, types, calls)
                : throw Error("sizeof requires a type or expression", expression.Location);
        if (!IsSized(target))
            throw Error($"Type {target} cannot be used with sizeof", expression.Location);
        return SemanticType.FromPrimitive(PrimitiveType.U64);
    }

    private static bool IsSized(SemanticType type) =>
        !type.IsVoid;

    private SemanticType AnalyzeUnionConstruction(
        UnionConstructionExpression construction, Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions, string moduleName,
        IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression, SemanticType> types,
        IDictionary<CallExpression, FunctionSymbol> calls)
    {
        if (!_unionsByName.TryGetValue(construction.UnionName, out var union))
            throw Error($"Union '{construction.UnionName}' is not declared", construction.Location);
        if (!union.CasesByName.TryGetValue(construction.CaseName, out var item))
            throw Error($"Union '{construction.UnionName}' has no case '{construction.CaseName}'", construction.Location);
        if (item.PayloadType is null && construction.Arguments.Count != 0
            || item.PayloadType is not null && construction.Arguments.Count != 1)
            throw Error($"Union case '{construction.CaseName}' has invalid argument count", construction.Location);
        if (item.PayloadType is not null)
        {
            var actual = AnalyzeExpression(construction.Arguments[0], scope, functions, moduleName, imports, types, calls);
            if (!CanAssign(item.PayloadType, construction.Arguments[0], actual))
                throw Error($"Union case '{construction.CaseName}' expects {item.PayloadType}, got {actual}", construction.Location);
        }
        return SemanticType.Union(union.Name);
    }

    private SemanticType AnalyzeArrayLiteral(ArrayLiteralExpression literal, Scope scope, IReadOnlyDictionary<string, List<FunctionSymbol>> functions, string moduleName, IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression, SemanticType> types, IDictionary<CallExpression, FunctionSymbol> calls)
    {
        if (literal.Elements.Count == 0) throw Error("Array literals cannot be empty", literal.Location);
        var element = AnalyzeExpression(literal.Elements[0], scope, functions, moduleName, imports, types, calls);
        foreach (var value in literal.Elements.Skip(1))
        {
            var actual = AnalyzeExpression(value, scope, functions, moduleName, imports, types, calls);
            if (actual != element) throw Error("Array literal elements must have matching types", value.Location);
        }
        return SemanticType.ArrayOf(element, literal.Elements.Count);
    }

    private SemanticType AnalyzeIndex(IndexExpression index, Scope scope, IReadOnlyDictionary<string, List<FunctionSymbol>> functions, string moduleName, IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression, SemanticType> types, IDictionary<CallExpression, FunctionSymbol> calls)
    {
        var target = AnalyzeExpression(index.Target, scope, functions, moduleName, imports, types, calls);
        var indexType = AnalyzeExpression(index.Index, scope, functions, moduleName, imports, types, calls);
        if (!indexType.IsNumeric) throw Error("Index must be numeric", index.Index.Location);
        if (!target.IsArray && !target.IsPointer)
            throw Error("Indexing requires an array or pointer", index.Target.Location);
        if (target.IsOpaquePointer)
            throw Error("Cannot dereference an opaque void pointer; cast it to a typed pointer first",
                index.Target.Location);
        if (index.Index is IntegerLiteralExpression constant && long.TryParse(constant.Lexeme, out var value)
            && target.IsArray && (value < 0 || value >= target.ArrayLength))
            throw Error($"Array index {value} is out of bounds for length {target.ArrayLength}", index.Index.Location);
        return target.IsArray ? target.ElementType! : target.Pointee!;
    }

    private SemanticType AnalyzeStructLiteral(
        StructLiteralExpression literal,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        if (!_structsByName.TryGetValue(literal.TypeName, out var structure))
            throw Error($"Struct '{literal.TypeName}' is not declared", literal.Location, literal.TypeName.Length);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in literal.Fields)
        {
            if (!fields.Add(field.Name))
                throw Error($"Struct field '{field.Name}' is specified more than once", field.Location, field.Name.Length);
            var declaration = structure.Fields.FirstOrDefault(candidate => candidate.Name == field.Name)
                ?? throw Error($"Struct '{literal.TypeName}' has no field named '{field.Name}'", field.Location, field.Name.Length);
            var valueType = AnalyzeExpression(field.Value, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
            if (!CanAssign(declaration.Type, field.Value, valueType))
                throw Error($"Cannot initialize field '{field.Name}' with {valueType}; expected {declaration.Type}", field.Value.Location);
        }
        if (fields.Count != structure.Fields.Count)
            throw Error($"Struct literal '{literal.TypeName}' must initialize every field", literal.Location, literal.TypeName.Length);
        return SemanticType.Struct(literal.TypeName);
    }

    private SemanticType AnalyzeMemberAccess(
        MemberAccessExpression member,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        if (member.Target is IdentifierExpression enumIdentifier
            && _enumsByName.TryGetValue(enumIdentifier.Name, out var enumSymbol))
        {
            var memberSymbol = enumSymbol.Members.FirstOrDefault(candidate => candidate.Name == member.MemberName)
                ?? throw Error($"Enum '{enumSymbol.Name}' has no member named '{member.MemberName}'", member.Location, member.MemberName.Length);
            return SemanticType.Enum(enumSymbol.Name);
        }

        // A null literal has no type that could have members, so the access can be rejected at compile time.
        if (member.Target is NullLiteralExpression)
            throw Error($"Cannot access member '{member.MemberName}' of the null literal, which has no members",
                member.Location, member.MemberName.Length);

        // A variable that is only the receiver of a member access is used, but its value does not escape.
        var previousReceiver = _receiverUse;
        _receiverUse = member.Target is IdentifierExpression;
        var targetType = AnalyzeExpression(member.Target, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        _receiverUse = previousReceiver;
        if (targetType.IsClass)
        {
            var cls = _classesByName[targetType.StructName!];
            var substitutions = cls.DeclaredTypeParameters
                .Zip(targetType.TypeArguments ?? [], (name, type) => (name, type))
                .ToDictionary(item => item.name, item => item.type, StringComparer.Ordinal);
            var property = FindProperty(cls, member.MemberName);
            if (property is not null)
            {
                var getter = property.Declaration.Accessors.FirstOrDefault(
                    accessor => accessor.Kind == PropertyAccessorKind.Get);
                if (getter is null)
                    throw Error($"Property '{property.Name}' is write-only", member.Location, property.Name.Length);
                EnsureAccessorVisible(property, getter, member.Location);
                return SubstituteGenericType(property.Type, substitutions);
            }
            var field = cls.Fields.FirstOrDefault(candidate => candidate.Name == member.MemberName);
            if (field is not null)
            {
                EnsureMemberAccessible(field.Visibility, "Field", field.Name, cls, moduleName, member.Location, cls.Name);
                return SubstituteGenericType(field.Type, substitutions);
            }
            if (cls.Methods.Any(method => method.Name == member.MemberName))
                return SemanticType.PointerTo(SemanticType.FromPrimitive(PrimitiveType.Void));
            throw Error($"Class '{cls.Name}' has no member named '{member.MemberName}'", member.Location);
        }
        if (targetType.IsInterface)
        {
            var interfaceSymbol = _interfacesByName[targetType.StructName!];
            var substitutions = interfaceSymbol.DeclaredTypeParameters
                .Zip(targetType.TypeArguments ?? [], (name, type) => (name, type))
                .ToDictionary(item => item.name, item => item.type, StringComparer.Ordinal);
            var contract = interfaceSymbol.DeclaredProperties
                .FirstOrDefault(candidate => candidate.Name == member.MemberName);
            if (contract is null)
                throw Error($"Interface '{targetType.StructName}' has no member named '{member.MemberName}'",
                    member.Location);
            var getter = contract.Declaration.Accessors.FirstOrDefault(
                accessor => accessor.Kind == PropertyAccessorKind.Get)
                ?? throw Error($"Property '{contract.Name}' is write-only", member.Location, contract.Name.Length);
            EnsureAccessorVisible(contract, getter, member.Location);
            return SubstituteGenericType(contract.Type, substitutions);
        }
        if (!targetType.IsStruct)
            throw Error($"Member access requires a struct or class value, got {targetType}", member.Location);
        var structure = _structsByName[targetType.StructName!];
        return structure.Fields.FirstOrDefault(field => field.Name == member.MemberName)?.Type
            ?? throw Error($"Struct '{targetType.StructName}' has no field named '{member.MemberName}'", member.Location, member.MemberName.Length);
    }

    private PropertySymbol? FindProperty(ClassSymbol cls, string name)
    {
        var property = cls.DeclaredProperties.FirstOrDefault(candidate => candidate.Name == name);
        if (property is not null)
            return property;
        return cls.BaseTypeName is not null && _classesByName.TryGetValue(cls.BaseTypeName, out var baseClass)
            ? FindProperty(baseClass, name)
            : null;
    }

    // Reports a constructor that matches the arguments but is not visible from here, instead of the generic no-match error.
    private void EnsureConstructorAccessible(ClassSymbol cls, int argumentCount, string moduleName, SourceLocation location)
    {
        var hidden = cls.Constructors.FirstOrDefault(constructor =>
            constructor.Parameters.Count == argumentCount
            && !IsMemberAccessible(constructor.Visibility, cls.Name, cls.ModuleName, moduleName));
        if (hidden is not null)
            throw Error(
                $"No accessible constructor of '{cls.Name}' matches the supplied arguments; the matching constructor is " +
                $"{hidden.Visibility.ToString().ToLowerInvariant()}",
                location, cls.Name.Length);
    }

    // A method's module name ends with its owner class: `module.Class`.
    private static string MethodOwnerName(FunctionSymbol method) =>
        method.ModuleName[(method.ModuleName.LastIndexOf('.') + 1)..];

    private static string MethodOwnerModule(FunctionSymbol method) =>
        method.ModuleName[..Math.Max(0, method.ModuleName.LastIndexOf('.'))];

    // Whether a member of `ownerClassName` (declared in `ownerModuleName` with `visibility`) can be used from the code being
    // analyzed in `moduleName`. Public is open; internal needs the same module; protected needs the owner class or a
    // class derived from it; private needs the owner class itself.
    // A protected member is also limited by the receiver: through an expression of type R, the current class must be R
    // or a base of R, as in C#. A null receiver (a constructor) is not limited that way.
    private bool IsMemberAccessible(Visibility visibility, string ownerClassName, string ownerModuleName, string moduleName,
        string? receiverClassName = null) =>
        visibility switch
        {
            Visibility.Public => true,
            // Inside a class body the module name can carry the class name, so the class's own module is used there.
            Visibility.Internal => string.Equals(ownerModuleName, _currentOwnerClass?.ModuleName ?? moduleName, StringComparison.Ordinal),
            Visibility.Protected => _currentOwnerClass is not null
                && (_currentOwnerClass.Name == ownerClassName || IsClassDerivedFrom(_currentOwnerClass.Name, ownerClassName))
                && (receiverClassName is null || IsClassDerivedFrom(receiverClassName, _currentOwnerClass.Name)),
            _ => _currentOwnerClass is not null && _currentOwnerClass.Name == ownerClassName
        };

    private void EnsureMemberAccessible(Visibility visibility, string memberKind, string memberName, ClassSymbol owner,
        string moduleName, SourceLocation location, string? receiverClassName = null)
    {
        if (!IsMemberAccessible(visibility, owner.Name, owner.ModuleName, moduleName, receiverClassName))
            throw Error(
                $"{memberKind} '{memberName}' of class '{owner.Name}' is {visibility.ToString().ToLowerInvariant()} and cannot be used here",
                location, memberName.Length);
    }

    private void EnsureAccessorVisible(PropertySymbol property, PropertyAccessorDeclaration accessor,
        SourceLocation location)
    {
        var owner = _classesByName.Values.FirstOrDefault(candidate =>
            candidate.DeclaredProperties.Contains(property));
        if (owner is null)
            return;
        var visible = accessor.Visibility switch
        {
            Visibility.Public => true,
            Visibility.Internal => _currentOwnerClass is null
                || string.Equals(_currentOwnerClass.ModuleName, owner.ModuleName, StringComparison.Ordinal),
            Visibility.Protected => _currentOwnerClass is not null
                && (_currentOwnerClass.Name == owner.Name || IsClassDerivedFrom(_currentOwnerClass.Name, owner.Name)),
            _ => _currentOwnerClass is not null && _currentOwnerClass.Name == owner.Name
        };
        if (!visible)
            throw Error($"Accessor for property '{property.Name}' is not accessible", location, property.Name.Length);
    }

    private SemanticType AnalyzeNewExpression(
        NewExpression expression, Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions, string moduleName,
        IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression, SemanticType> types,
        IDictionary<CallExpression, FunctionSymbol> calls)
    {
        if (expression.Type.NamedType is null || !_classesByName.TryGetValue(expression.Type.NamedType, out var cls)
            || expression.Type.IsPointer || expression.Type.IsArray || expression.Type.IsString)
            throw Error("Only declared classes can be constructed with 'new'", expression.Location);
        var arguments = expression.Arguments.Select(argument =>
            AnalyzeExpression(argument, scope, functions, moduleName, imports, types, calls)).ToArray();
        var constructedType = expression.Type.DeclaredTypeArguments.Count == 0
            ? SemanticType.Class(cls.Name)
            : ResolveType(expression.Type);
        var substitutions = cls.DeclaredTypeParameters
            .Zip(constructedType.TypeArguments ?? [], (name, type) => (name, type))
            .ToDictionary(item => item.name, item => item.type, StringComparer.Ordinal);
        var matches = cls.Constructors.Where(constructor => IsMemberAccessible(constructor.Visibility, cls.Name, cls.ModuleName, moduleName))
            .Where(constructor => constructor.Parameters.Count == arguments.Length)
            .Where(constructor => constructor.Parameters.Select((parameter, index) =>
            {
                if (expression.Type.DeclaredTypeArguments.Count == 0)
                    InferClassTypeArguments(parameter.Type, arguments[index], cls.DeclaredTypeParameters, substitutions);
                return CanAssign(
                    SubstituteGenericType(parameter.Type, substitutions),
                    expression.Arguments[index],
                    arguments[index]);
            }).All(match => match)).ToArray();
        if (matches.Length == 0)
        {
            EnsureConstructorAccessible(cls, arguments.Length, moduleName, expression.Location);
            throw Error($"No constructor of '{cls.Name}' matches the supplied arguments", expression.Location);
        }
        if (matches.Length > 1)
            throw Error($"Constructor call for '{cls.Name}' is ambiguous", expression.Location);
        _constructorSelections[expression] = IndexOfConstructor(cls, matches[0]);
        if (substitutions.Count != cls.DeclaredTypeParameters.Count)
            throw Error($"Type '{cls.Name}' expects {cls.DeclaredTypeParameters.Count} type argument(s), but they could not be inferred",
                expression.Location);
        var inferredArguments = cls.DeclaredTypeParameters.Select(name => substitutions[name]).ToArray();
        ValidateTypeArgumentConstraints(cls.Declaration.DeclaredTypeParameters, inferredArguments, expression.Location);
        return SemanticType.Class(cls.Name,
            cls.DeclaredTypeParameters.Count == 0 ? null : inferredArguments);
    }

    private SemanticType AnalyzeClassConstructionExpression(
        ClassConstructionExpression expression, Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions, string moduleName,
        IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression, SemanticType> types,
        IDictionary<CallExpression, FunctionSymbol> calls)
    {
        if (!_classesByName.TryGetValue(expression.TypeName, out var cls))
            throw Error($"Class '{expression.TypeName}' is not declared", expression.Location);
        var arguments = expression.Arguments.Select(argument =>
            AnalyzeExpression(argument, scope, functions, moduleName, imports, types, calls)).ToArray();
        var substitutions = new Dictionary<string, SemanticType>(StringComparer.Ordinal);
        if (expression.ExplicitTypeArguments.Count > 0)
        {
            ValidateTypeArgumentCount(
                new TypeSyntax(null, false, expression.Location, NamedType: expression.TypeName,
                    TypeArguments: expression.ExplicitTypeArguments.ToArray()),
                cls.DeclaredTypeParameters.Count);
            foreach (var (name, type) in cls.DeclaredTypeParameters.Zip(
                         expression.ExplicitTypeArguments.Select(ResolveType)))
                substitutions[name] = type;
        }
        var matches = cls.Constructors.Where(constructor => IsMemberAccessible(constructor.Visibility, cls.Name, cls.ModuleName, moduleName))
            .Where(constructor => constructor.Parameters.Count == arguments.Length)
            .Where(constructor => constructor.Parameters.Select((parameter, index) =>
            {
                if (expression.ExplicitTypeArguments.Count == 0)
                    InferClassTypeArguments(parameter.Type, arguments[index], cls.DeclaredTypeParameters, substitutions);
                return CanAssign(
                    SubstituteGenericType(parameter.Type, substitutions),
                    expression.Arguments[index],
                    arguments[index]);
            }).All(match => match)).ToArray();
        if (matches.Length == 0)
        {
            EnsureConstructorAccessible(cls, arguments.Length, moduleName, expression.Location);
            throw Error($"No constructor of '{cls.Name}' matches the supplied arguments", expression.Location);
        }
        if (matches.Length > 1)
            throw Error($"Constructor call for '{cls.Name}' is ambiguous", expression.Location);
        _constructorSelections[expression] = IndexOfConstructor(cls, matches[0]);
        if (substitutions.Count != cls.DeclaredTypeParameters.Count)
            throw Error($"Type '{cls.Name}' expects {cls.DeclaredTypeParameters.Count} type argument(s), but they could not be inferred",
                expression.Location);
        var inferredArguments = cls.DeclaredTypeParameters.Select(name => substitutions[name]).ToArray();
        ValidateTypeArgumentConstraints(cls.Declaration.DeclaredTypeParameters, inferredArguments, expression.Location);
        return SemanticType.Class(
            expression.TypeName,
            cls.DeclaredTypeParameters.Count == 0 ? null : inferredArguments);
    }

    private static void InferClassTypeArguments(
        SemanticType pattern,
        SemanticType actual,
        IReadOnlyList<string> parameters,
        IDictionary<string, SemanticType> substitutions)
    {
        if (pattern.IsGenericParameter && parameters.Contains(pattern.GenericParameterName!, StringComparer.Ordinal))
        {
            if (substitutions.TryGetValue(pattern.GenericParameterName!, out var existing))
            {
                if (existing != actual)
                    substitutions.Remove(pattern.GenericParameterName!);
            }
            else
                substitutions[pattern.GenericParameterName!] = actual;
            return;
        }

        if (pattern.IsPointer && actual.IsPointer)
        {
            InferClassTypeArguments(pattern.Pointee!, actual.Pointee!, parameters, substitutions);
            return;
        }

        if (pattern.Kind != actual.Kind || pattern.StructName != actual.StructName)
            return;
        var patternArguments = pattern.TypeArguments ?? [];
        var actualArguments = actual.TypeArguments ?? [];
        foreach (var pair in patternArguments.Zip(actualArguments))
            InferClassTypeArguments(pair.First, pair.Second, parameters, substitutions);
    }

    private SemanticType AnalyzeUnary(
        UnaryExpression expression,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var operandType = AnalyzeExpression(expression.Operand, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        if (expression.Operator == TokenKind.Ampersand)
        {
            if (!IsAddressable(expression.Operand, scope))
                throw Error("The '&' operator requires an addressable operand", expression.Location);
            return SemanticType.PointerTo(operandType);
        }

        if (expression.Operator == TokenKind.Star)
        {
            if (expression.Operand is NullLiteralExpression
                || expression.Operand is IdentifierExpression identifier
                    && _definitelyNullVariables.Contains(scope.Lookup(identifier.Name, identifier.Location)))
                throw Error("Cannot dereference a null pointer", expression.Location);
            if (!operandType.IsPointer)
                throw Error("The '*' operator requires a pointer operand", expression.Location);
            if (operandType.IsOpaquePointer)
                throw Error("Cannot dereference an opaque void pointer; cast it to a typed pointer first", expression.Location);
            return operandType.Pointee!;
        }

        if (expression.Operator == TokenKind.Bang)
        {
            if (operandType != SemanticType.FromPrimitive(PrimitiveType.Bool))
            {
                throw Error("The '!' operator requires a bool operand", expression.Location);
            }

            return operandType;
        }

        if (expression.Operator == TokenKind.Tilde)
        {
            if (!operandType.IsInteger)
                throw Error($"The '~' operator requires an integer operand, got {operandType}", expression.Location);
            return operandType;
        }

        if (expression.Operator is TokenKind.PlusPlus or TokenKind.MinusMinus)
        {
            if (!IsAddressable(expression.Operand, scope))
                throw Error($"The '{expression.Operator}' operator requires an addressable operand", expression.Location);
            ValidateWritableTarget(expression.Operand, scope);
            if (!operandType.IsInteger && !operandType.IsPointer)
                throw Error($"The '{expression.Operator}' operator requires an integer or pointer operand", expression.Location);
            return operandType;
        }

        if (!operandType.IsNumeric)
        {
            throw Error($"The '{expression.Operator}' operator requires a numeric operand", expression.Location);
        }

        return operandType;
    }

    private SemanticType AnalyzeBinary(
        BinaryExpression expression,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var left = AnalyzeExpression(expression.Left, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        var right = AnalyzeExpression(expression.Right, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        var isLogical = expression.Operator is TokenKind.AmpersandAmpersand or TokenKind.PipePipe;
        var isComparison = expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals
            or TokenKind.LessThan or TokenKind.LessThanOrEquals or TokenKind.GreaterThan
            or TokenKind.GreaterThanOrEquals;

        if (isLogical)
        {
            RequireType(left, SemanticType.FromPrimitive(PrimitiveType.Bool), expression.Location, "Logical operators");
            RequireType(right, SemanticType.FromPrimitive(PrimitiveType.Bool), expression.Location, "Logical operators");
            return SemanticType.FromPrimitive(PrimitiveType.Bool);
        }

        if (left.IsPointer || right.IsPointer)
        {
            if (left.IsPointer && right.IsInteger
                && expression.Operator is TokenKind.Plus or TokenKind.Minus)
                return left;
            if (left.IsPointer && expression.Right is NullLiteralExpression
                && expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals)
            {
                expressionTypes[expression.Right] = left;
                return SemanticType.FromPrimitive(PrimitiveType.Bool);
            }
            if (right.IsPointer && expression.Left is NullLiteralExpression
                && expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals)
            {
                expressionTypes[expression.Left] = right;
                return SemanticType.FromPrimitive(PrimitiveType.Bool);
            }
            if (left.IsPointer && right.IsPointer
                && expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals)
                return SemanticType.FromPrimitive(PrimitiveType.Bool);
            throw Error($"Operator '{expression.Operator}' is not valid for pointer operands", expression.Location);
        }

        // Strings compare for equality by their bytes; ordering is not defined.
        if (left.Kind == SemanticTypeKind.String && right.Kind == SemanticTypeKind.String
            && (expression.Operator == TokenKind.EqualsEquals || expression.Operator == TokenKind.BangEquals))
            return SemanticType.FromPrimitive(PrimitiveType.Bool);

        // Booleans compare for equality only; ordering and arithmetic on bool are not defined.
        var boolean = SemanticType.FromPrimitive(PrimitiveType.Bool);
        if (expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals
            && left == boolean && right == boolean)
            return boolean;

        var requiresInteger = expression.Operator is TokenKind.Ampersand or TokenKind.Pipe or TokenKind.Caret
            or TokenKind.LessThanLessThan or TokenKind.GreaterThanGreaterThan or TokenKind.Percent;
        if (requiresInteger && (!left.IsInteger || !right.IsInteger))
            throw Error($"Operator '{expression.Operator}' requires integer operands", expression.Location);
        if (!left.IsNumeric || !right.IsNumeric)
            throw Error($"Binary operator '{expression.Operator}' requires numeric operands", expression.Location);

        var commonType = GetCommonNumericType(left, right, expression);

        if (isComparison)
        {
            return SemanticType.FromPrimitive(PrimitiveType.Bool);
        }

        return commonType;
    }

    private SemanticType GetCommonNumericType(SemanticType left, SemanticType right, Expression expression)
    {
        if (left == right)
            return left;
        if (CanAssign(left, ((BinaryExpression)expression).Right, right))
            return left;
        if (CanAssign(right, ((BinaryExpression)expression).Left, left))
            return right;
        throw Error($"Binary operator '{((BinaryExpression)expression).Operator}' requires compatible numeric types",
            expression.Location);
    }

    private SemanticType AnalyzeConditional(
        ConditionalExpression expression,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        SemanticType? expectedType)
    {
        var condition = AnalyzeExpression(
            expression.Condition, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        RequireBoolean(condition, expression.Condition.Location);

        var whenTrue = AnalyzeExpression(
            expression.WhenTrue, scope, functions, moduleName, imports, expressionTypes, resolvedCalls,
            expectedType);
        var whenFalse = AnalyzeExpression(
            expression.WhenFalse, scope, functions, moduleName, imports, expressionTypes, resolvedCalls,
            expectedType);
        if (whenTrue == whenFalse)
            return whenTrue;
        if (CanAssign(whenTrue, expression.WhenFalse, whenFalse))
            return whenTrue;
        if (CanAssign(whenFalse, expression.WhenTrue, whenTrue))
            return whenFalse;

        throw Error(
            $"Conditional branches must have compatible types, got {whenTrue} and {whenFalse}",
            expression.Location);
    }

    private SemanticType AnalyzeCall(
        CallExpression call,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        SemanticType? expectedType,
        bool allowVoid)
    {
        var unionParts = call.Name.Split('.');
        if (unionParts.Length == 2 && _unionsByName.TryGetValue(unionParts[0], out var union))
        {
            if (!union.CasesByName.TryGetValue(unionParts[1], out var item))
                throw Error($"Union '{union.Name}' has no case '{unionParts[1]}'", call.Location);
            if ((item.PayloadType is null && call.Arguments.Count != 0)
                || (item.PayloadType is not null && call.Arguments.Count != 1))
                throw Error($"Union case '{item.Name}' has invalid argument count", call.Location);
            if (item.PayloadType is not null)
            {
                var actual = AnalyzeExpression(call.Arguments[0], scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                var substitutions = union.DeclaredTypeParameters
                    .Select((name, index) => (name, type: call.ExplicitTypeArguments.Count > index
                        ? ResolveType(call.ExplicitTypeArguments[index])
                        : expectedType?.TypeArguments is { Count: > 0 } && expectedType.EnumName == union.Name
                            ? expectedType.TypeArguments[index]
                            : SemanticType.GenericParameter(name)))
                    .ToDictionary(item => item.name, item => item.type, StringComparer.Ordinal);
                if (!CanAssign(SubstituteGenericType(item.PayloadType, substitutions), call.Arguments[0], actual))
                    throw Error($"Union case '{item.Name}' expects {SubstituteGenericType(item.PayloadType, substitutions)}, got {actual}", call.Location);
            }
            var unionArguments = call.ExplicitTypeArguments.Count > 0
                ? call.ExplicitTypeArguments.Select(ResolveType).ToArray()
                : expectedType?.EnumName == union.Name ? expectedType.TypeArguments : null;
            return SemanticType.Union(union.Name, unionArguments);
        }
        if (call.Name == "length")
        {
            if (call.Arguments.Count != 1) throw Error("length(array) expects one argument", call.Location);
            var argumentType = AnalyzeExpression(call.Arguments[0], scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
            if (!argumentType.IsArray) throw Error("length() requires an array", call.Arguments[0].Location);
            return SemanticType.FromPrimitive(PrimitiveType.I32);
        }
        if (call.Name.Contains('.', StringComparison.Ordinal))
        {
            var separator = call.Name.LastIndexOf('.');
            var receiverName = call.Name[..separator];
            var methodName = call.Name[(separator + 1)..];
            VariableSymbol? receiver = null;
            try { receiver = scope.Lookup(receiverName, call.Location); }
            catch (SemanticException) { }
            if (receiver is not null)
            {
                var receiverType = receiver.Type;
                _callReceiverTypes[call] = receiverType;
                if (receiverType.IsClass && _classesByName.TryGetValue(receiverType.StructName!, out var receiverClass))
                {
                    // A method's owner is the class named by the last segment of its module name.
                    var namedMethods = GetClassMethods(receiverClass.Name, []).Where(method => method.Name == methodName).ToArray();
                    var methodCandidates = namedMethods.Where(method => IsMemberAccessible(method.Visibility,
                        MethodOwnerName(method), MethodOwnerModule(method), moduleName, receiverClass.Name)).ToArray();
                    if (methodCandidates.Length == 0 && namedMethods.Length > 0)
                        throw Error($"Method '{methodName}' of class '{receiverClass.Name}' cannot be used here", call.Location);
                    var substitutions = receiverClass.DeclaredTypeParameters
                        .Zip(receiverType.TypeArguments ?? [], (name, type) => (name, type))
                        .ToDictionary(item => item.name, item => item.type, StringComparer.Ordinal);
                    var methodArgumentTypes = call.Arguments.Select(argument =>
                        AnalyzeExpression(argument, scope, functions, moduleName, imports, expressionTypes, resolvedCalls)).ToArray();
                    // Same ranking as free functions: the closest candidates win, and the return type never selects one.
                    var methodRanked = methodCandidates.Where(method => method.Parameters.Count == methodArgumentTypes.Length)
                        .Select(method => (method, cost: CandidateCost(method, call, methodArgumentTypes, substitutions)))
                        .Where(item => item.cost is not null)
                        .Select(item => (item.method, cost: item.cost!.Value))
                        .ToList();
                    var methodMatches = methodRanked.Count == 0
                        ? Array.Empty<FunctionSymbol>()
                        : methodRanked
                            .Where(item => item.cost.CompareTo(methodRanked.Min(other => other.cost)) == 0)
                            .Select(item => item.method)
                            .ToArray();
                    if (methodMatches.Length == 1)
                    {
                        resolvedCalls[call] = methodMatches[0];
                        _usedFunctions.Add(methodMatches[0]);
                        return SubstituteGenericType(methodMatches[0].ReturnType, substitutions);
                    }
                    if (methodMatches.Length > 1)
                        throw Error($"Method call '{methodName}' is ambiguous: no overload is closer than the others", call.Location);
                    if (methodCandidates.Length > 0)
                        throw Error($"No overload of '{methodName}' matches the argument types", call.Location);
                }
                else if (receiverType.IsInterface && _interfacesByName.TryGetValue(receiverType.StructName!, out var receiverInterface))
                {
                    var methodCandidates = receiverInterface.Methods.Where(method => method.Name == methodName).ToArray();
                    var substitutions = receiverInterface.DeclaredTypeParameters
                        .Zip(receiverType.TypeArguments ?? [], (name, type) => (name, type))
                        .ToDictionary(item => item.name, item => item.type, StringComparer.Ordinal);
                    var methodArgumentTypes = call.Arguments.Select(argument =>
                        AnalyzeExpression(argument, scope, functions, moduleName, imports, expressionTypes, resolvedCalls)).ToArray();
                    var methodMatches = methodCandidates.Where(method => method.Parameters.Count == methodArgumentTypes.Length)
                        .Where(method => method.Parameters.Select((parameter, index) =>
                            CanAssign(SubstituteGenericType(parameter.Type, substitutions),
                                call.Arguments[index], methodArgumentTypes[index])).All(match => match)).ToArray();
                    if (methodMatches.Length == 1)
                    {
                        resolvedCalls[call] = methodMatches[0];
                        _usedFunctions.Add(methodMatches[0]);
                        return SubstituteGenericType(methodMatches[0].ReturnType, substitutions);
                    }
                    if (methodMatches.Length > 1)
                        throw Error($"Method call '{methodName}' is ambiguous: no overload is closer than the others", call.Location);
                    if (methodCandidates.Length > 0)
                        throw Error($"No overload of '{methodName}' matches the argument types", call.Location);
                }
                else if (!receiverType.IsClass && !receiverType.IsInterface)
                {
                    // A method call on a primitive, string, or struct receiver names a free function whose first
                    // parameter has exactly the receiver's type: `value.write()` calls `write(value)`. The receiver
                    // must match exactly, so an i32 receiver never selects an i64 overload through widening.
                    var receiverArguments = call.Arguments.Select(argument =>
                        AnalyzeExpression(argument, scope, functions, moduleName, imports, expressionTypes, resolvedCalls)).ToArray();
                    var receiverCandidates = ResolveFunctions(methodName, moduleName, imports, functions);
                    // Primitive and string receivers implement Printable through the private functions of std.io,
                    // which ordinary calls cannot name. The receiver syntax reaches them directly.
                    if (receiverType.Kind is SemanticTypeKind.Primitive or SemanticTypeKind.String
                        && functions.TryGetValue($"{PrintableModule}.{methodName}", out var builtin))
                        receiverCandidates = receiverCandidates.Concat(builtin).Distinct().ToList();
                    var functionMatches = receiverCandidates
                        .Where(function => function.Parameters.Count == receiverArguments.Length + 1
                            && function.Parameters[0].Type == receiverType)
                        .Where(function => function.Parameters.Skip(1).Select((parameter, index) =>
                            CanAssign(parameter.Type, call.Arguments[index], receiverArguments[index])).All(match => match))
                        .ToArray();
                    if (functionMatches.Length == 1)
                    {
                        resolvedCalls[call] = functionMatches[0];
                        _usedFunctions.Add(functionMatches[0]);
                        _receiverArgumentCalls.Add(call);
                        return functionMatches[0].ReturnType;
                    }
                    if (functionMatches.Length > 1)
                        throw Error($"Method call '{methodName}' is ambiguous for a receiver of type {receiverType}",
                            call.Location);
                    throw Error($"No function '{methodName}' accepts a receiver of type {receiverType}", call.Location);
                }
            }
        }
        var overloads = ResolveFunctions(call.Name, moduleName, imports, functions);
        if (overloads.Count == 0)
        {
            throw Error($"Function '{call.Name}' is not declared", call.Location, call.Name.Length);
        }

        var argumentTypes = call.Arguments
            .Select(argument => AnalyzeExpression(argument, scope, functions, moduleName, imports, expressionTypes, resolvedCalls))
            .ToList();
        foreach (var argument in call.Arguments)
        {
            if (argument is IdentifierExpression identifier
                && IsOwnedClass(scope.Lookup(identifier.Name, identifier.Location).Type)
                && scope.Lookup(identifier.Name, identifier.Location).Declaration is VariableDeclarationStatement declaration)
                _escapingClassLocals.Add(declaration);
        }
        if (call.ExplicitTypeArguments.Count != 0)
        {
            var generic = overloads.Where(candidate => candidate.IsGeneric).ToList();
            if (generic.Count == 0)
                throw Error($"Function '{call.Name}' does not accept explicit type arguments", call.Location);
            if (generic.Count > 1)
                throw Error($"Function '{call.Name}' has ambiguous generic overloads", call.Location);
            var genericFunction = generic[0];
            if (call.ExplicitTypeArguments.Count != genericFunction.DeclaredGenericParameters.Count)
                throw Error(
                    $"Function '{call.Name}' expects {genericFunction.DeclaredGenericParameters.Count} type argument(s), got {call.ExplicitTypeArguments.Count}",
                    call.Location);
            var substitutions = genericFunction.DeclaredGenericParameters
                .Select((name, index) => (name, type: ResolveType(call.ExplicitTypeArguments[index])))
                .ToDictionary(item => item.name, item => item.type, StringComparer.Ordinal);
            var specialized = SpecializeFunction(genericFunction, substitutions, functions, call.Location);
            AnalyzeSpecialization(specialized, genericFunction, functions, imports, expressionTypes, resolvedCalls);
            overloads = [specialized];
        }
        else
        {
            var inferred = overloads
                .Where(candidate => candidate.IsGeneric && candidate.Parameters.Count == argumentTypes.Count)
                .Select(candidate => (candidate, substitutions: InferGenericSubstitutions(candidate, argumentTypes, expectedType)))
                .Where(item => item.substitutions is not null)
                .ToList();
            var inferredMatches = expectedType is not null
                ? inferred.Where(item =>
                {
                    var returnType = SubstituteGenericType(item.candidate.ReturnType, item.substitutions!);
                    return returnType == expectedType;
                }).ToList()
                : inferred;
            // Specializations made by other calls are not candidates: the type arguments inferred here select the
            // one specialization this call uses. Otherwise an argument such as a float literal, which fits both f32
            // and f64, would match two specializations of the same generic.
            overloads = overloads.Where(overload => !overload.IsSpecialized).ToList();
            if (inferredMatches.Count == 1)
            {
                var specialized = SpecializeFunction(inferredMatches[0].candidate, inferredMatches[0].substitutions!, functions,
                    call.Location);
                AnalyzeSpecialization(specialized, inferredMatches[0].candidate, functions, imports, expressionTypes,
                    resolvedCalls);
                overloads = overloads.Append(specialized).ToList();
            }
        }
        var candidates = overloads
            .Where(function => function.Parameters.Count == argumentTypes.Count)
            .Where(function => !function.IsGeneric)
            .ToList();

        if (candidates.Count == 0)
        {
            if (overloads.Count == 1)
            {
                throw Error(
                    $"Function '{call.Name}' expects {overloads[0].Parameters.Count} argument(s), got {call.Arguments.Count}",
                    call.Location);
            }

            throw Error(
                $"Function '{call.Name}' has no overload accepting {call.Arguments.Count} argument(s)",
                call.Location);
        }

        // The closest candidates win: each is ranked by its worst argument conversion, and only those tied for the best
        // rank remain. The return type never selects an overload.
        var ranked = candidates
            .Select(function => (function, cost: CandidateCost(function, call, argumentTypes)))
            .Where(item => item.cost is not null)
            .Select(item => (item.function, cost: item.cost!.Value))
            .ToList();
        var matches = ranked.Count == 0
            ? new List<FunctionSymbol>()
            : ranked.Where(item => item.cost.CompareTo(ranked.Min(other => other.cost)) == 0)
                .Select(item => item.function)
                .ToList();
        if (matches.Select(match => match.ModuleName).Distinct(StringComparer.Ordinal).Count() > 1)
            throw Error($"Function '{call.Name}' is ambiguous; use a module prefix", call.Location, call.Name.Length);
        if (matches.Count > 1)
            throw Error($"Function '{call.Name}' is ambiguous: no overload is closer than the others", call.Location, call.Name.Length);
        if (matches.Count == 0)
        {
            if (candidates.Count == 1)
            {
                var mismatchIndex = candidates[0].Parameters
                    .Select((parameter, index) => (parameter, index))
                    .First(pair => pair.parameter.Type != argumentTypes[pair.index]);
                throw Error(
                    $"Argument {mismatchIndex.index + 1} of '{call.Name}' has type {argumentTypes[mismatchIndex.index]}; expected {mismatchIndex.parameter.Type}",
                    call.Arguments[mismatchIndex.index].Location);
            }

            throw Error($"No overload of '{call.Name}' matches the argument types", call.Location);
        }

        var function = matches[0];
        for (var index = 0; index < call.Arguments.Count; index++)
        {
            if (call.Arguments[index] is NullLiteralExpression
                && function.Parameters[index].Type.IsPointer)
                expressionTypes[call.Arguments[index]] = function.Parameters[index].Type;
        }
        resolvedCalls[call] = function;
        _usedFunctions.Add(function);
        if (function.ReturnType.IsVoid && !allowVoid)
        {
            throw Error($"Void function '{call.Name}' cannot be used as an expression", call.Location);
        }

        return function.ReturnType;
    }

    private bool CanAssign(SemanticType target, Expression value, SemanticType actual) =>
        ConversionCostOf(target, value, actual) is not null;

    // How far an implicit conversion is from an exact match; smaller is closer. The fields are compared in order: any
    // conversion loses to an exact match; a conversion within one kind (integer to integer, float to float) beats one
    // that crosses kinds; a narrowing conversion (only ever of a fitting constant) loses to a widening one; and a class
    // or interface is closer the fewer steps up the hierarchy it takes to reach it.
    private readonly record struct ConversionCost(int NotExact, int CrossKind, int Narrowing, int Distance)
        : IComparable<ConversionCost>
    {
        public static readonly ConversionCost Exact = new(0, 0, 0, 0);

        public int CompareTo(ConversionCost other) =>
            (NotExact, CrossKind, Narrowing, Distance).CompareTo((other.NotExact, other.CrossKind, other.Narrowing, other.Distance));
    }

    // The cost of implicitly converting an argument to a parameter type, or null when no implicit conversion exists.
    private ConversionCost? ConversionCostOf(SemanticType target, Expression value, SemanticType actual)
    {
        if (SemanticTypeComparer.Instance.Equals(target, actual))
            return ConversionCost.Exact;
        if (target.IsUnion && actual.IsUnion
            && target.EnumName == actual.EnumName
            && ((actual.TypeArguments is null || actual.TypeArguments.Count == 0)
                || SemanticTypeComparer.Instance.Equals(target, actual)))
            return ConversionCost.Exact;
        if (target.IsPointer && value is NullLiteralExpression)
            return ConversionCost.Exact;
        if (IsReferenceCast(actual, target, out var implicitConversion) && implicitConversion)
            return new ConversionCost(1, 0, 0, HierarchyDistance(actual.StructName!, target.StructName!));
        if (!target.IsNumeric || !actual.IsNumeric)
            return null;

        var crossKind = IsFloatingPointType(actual) != IsFloatingPointType(target) ? 1 : 0;
        if (IsImplicitlySafeNumericConversion(actual, target))
            return new ConversionCost(1, crossKind, 0, 0);
        // Only an integer constant that fits may narrow implicitly. A float never converts implicitly unless widening.
        if (IsFloatingPointType(actual))
            return null;
        if (!TryGetConstantNumber(value, out var constant) || !IsRepresentable(constant, target))
            return null;
        // An integer constant reaches f32 only when f32 holds its exact value.
        if (target.Primitive == PrimitiveType.F32 && (double)(float)constant != constant)
            return null;
        return new ConversionCost(1, crossKind, NumericBitWidth(target) < NumericBitWidth(actual) ? 1 : 0, 0);
    }

    // The number of steps from a class up to an ancestor class or interface: base classes and implemented interfaces
    // each count as one step. An interface reached through a base class is further than that base class.
    private int HierarchyDistance(string sourceName, string targetName)
    {
        var frontier = new List<string> { sourceName };
        var seen = new HashSet<string>(StringComparer.Ordinal) { sourceName };
        for (var depth = 0; frontier.Count > 0; depth++)
        {
            if (frontier.Contains(targetName, StringComparer.Ordinal))
                return depth;
            var next = new List<string>();
            foreach (var name in frontier)
            {
                if (!_classesByName.TryGetValue(name, out var cls))
                    continue;
                IEnumerable<string> parents = cls.BaseTypeName is null
                    ? cls.InterfaceNames
                    : cls.InterfaceNames.Prepend(cls.BaseTypeName);
                foreach (var parent in parents)
                    if (seen.Add(parent))
                        next.Add(parent);
            }

            frontier = next;
        }

        return int.MaxValue;
    }

    private static bool IsFloatingPointType(SemanticType type) =>
        type.Primitive is PrimitiveType.F32 or PrimitiveType.F64;

    private static int NumericBitWidth(SemanticType type) =>
        type.Primitive switch
        {
            PrimitiveType.F32 => 32,
            PrimitiveType.F64 => 64,
            { } primitive => IntegerBitWidth(primitive),
            null => throw new InvalidOperationException($"Type '{type}' is not numeric.")
        };

    // The cost of the whole call for one candidate: its worst argument, so a candidate is only as close as its furthest
    // argument. Returns null when some argument has no implicit conversion to its parameter.
    private ConversionCost? CandidateCost(
        FunctionSymbol function,
        CallExpression call,
        IReadOnlyList<SemanticType> argumentTypes,
        IReadOnlyDictionary<string, SemanticType>? substitutions = null)
    {
        var worst = ConversionCost.Exact;
        for (var index = 0; index < argumentTypes.Count; index++)
        {
            var parameterType = substitutions is null
                ? function.Parameters[index].Type
                : SubstituteGenericType(function.Parameters[index].Type, substitutions);
            var cost = ConversionCostOf(parameterType, call.Arguments[index], argumentTypes[index]);
            if (cost is null)
                return null;
            if (cost.Value.CompareTo(worst) > 0)
                worst = cost.Value;
        }

        return worst;
    }

    private bool IsOwnedClass(SemanticType type) =>
        type.IsClass
        && (!_classesByName.TryGetValue(type.StructName!, out var cls) || !cls.IsValue);

    private bool IsReferenceCast(SemanticType source, SemanticType target, out bool implicitConversion)
    {
        implicitConversion = false;
        if ((!source.IsClass && !source.IsInterface) || (!target.IsClass && !target.IsInterface))
            return false;

        if (source.IsClass && target.IsClass)
        {
            if (IsClassDerivedFrom(source.StructName!, target.StructName!))
            {
                implicitConversion = true;
                return true;
            }

            return IsClassDerivedFrom(target.StructName!, source.StructName!);
        }

        if (source.IsClass && target.IsInterface)
        {
            if (!ClassImplementsInterface(source.StructName!, target.StructName!))
                return false;

            implicitConversion = true;
            return true;
        }

        if (source.IsInterface && target.IsClass)
            return ClassImplementsInterface(target.StructName!, source.StructName!);

        return false;
    }

    private bool IsClassDerivedFrom(string sourceName, string targetName)
    {
        if (string.Equals(sourceName, targetName, StringComparison.Ordinal))
            return true;
        if (!_classesByName.TryGetValue(sourceName, out var source))
            return false;
        return source.BaseTypeName is not null
            && _classesByName.ContainsKey(source.BaseTypeName)
            && IsClassDerivedFrom(source.BaseTypeName, targetName);
    }

    private bool ClassImplementsInterface(string className, string interfaceName)
    {
        if (!_classesByName.TryGetValue(className, out var cls))
            return false;

        if (cls.InterfaceNames.Contains(interfaceName, StringComparer.Ordinal))
            return true;
        if (cls.BaseTypeName is not null)
        {
            if (_interfacesByName.ContainsKey(cls.BaseTypeName)
                && string.Equals(cls.BaseTypeName, interfaceName, StringComparison.Ordinal))
                return true;
            if (_classesByName.ContainsKey(cls.BaseTypeName)
                && ClassImplementsInterface(cls.BaseTypeName, interfaceName))
                return true;
        }

        return false;
    }

    private static bool IsPointerInteger(SemanticType type) =>
        type.Primitive is PrimitiveType.USize or PrimitiveType.ISize;

    private static bool IsImplicitlySafeNumericConversion(SemanticType source, SemanticType target)
    {
        if (source.Primitive == target.Primitive)
            return true;
        if (source.Primitive == PrimitiveType.F32 && target.Primitive == PrimitiveType.F64)
            return true;
        if (source.Primitive is not ({ } sourcePrimitive) || target.Primitive is not ({ } targetPrimitive))
            return false;
        // An integer converts to a float without loss when the float's significand holds every value of the integer:
        // 24 bits for f32 (up to 16-bit integers) and 53 bits for f64 (up to 32-bit integers).
        if (IsInteger(sourcePrimitive) && (targetPrimitive is PrimitiveType.F32 or PrimitiveType.F64))
            return IntegerBitWidth(sourcePrimitive) <= (targetPrimitive == PrimitiveType.F64 ? 32 : 16);
        if (!IsInteger(sourcePrimitive) || !IsInteger(targetPrimitive))
            return false;

        var sourceBits = IntegerBitWidth(sourcePrimitive);
        var targetBits = IntegerBitWidth(targetPrimitive);
        var sourceUnsigned = IsUnsigned(sourcePrimitive);
        var targetUnsigned = IsUnsigned(targetPrimitive);
        if (sourceUnsigned == targetUnsigned)
            return targetBits >= sourceBits;
        // Only an unsigned value fits a wider signed type; a negative signed value never fits an unsigned type.
        return sourceUnsigned && targetBits > sourceBits;
    }

    private static bool IsInteger(PrimitiveType type) =>
        type is PrimitiveType.I8 or PrimitiveType.I16 or PrimitiveType.I32 or PrimitiveType.I64
            or PrimitiveType.U8 or PrimitiveType.U16 or PrimitiveType.U32 or PrimitiveType.U64
            or PrimitiveType.USize or PrimitiveType.ISize;

    private static bool IsSwitchComparable(SemanticType type) =>
        type.IsInteger || type.IsEnum || type.Kind == SemanticTypeKind.String
        || type.Primitive == PrimitiveType.Bool || type.IsPointer;

    private static bool IsConstantSwitchLabel(Expression expression, SemanticType labelType, SemanticType selectorType)
    {
        if (selectorType.IsEnum)
            return expression is MemberAccessExpression && labelType == selectorType;
        if (selectorType.IsInteger)
            return labelType.IsInteger
                && expression is (IntegerLiteralExpression or UnaryExpression or BinaryExpression);
        if (selectorType.Primitive == PrimitiveType.Bool)
            return labelType.Primitive == PrimitiveType.Bool && expression is BooleanLiteralExpression;
        if (selectorType.Kind == SemanticTypeKind.String)
            return labelType.Kind == SemanticTypeKind.String && expression is StringLiteralExpression;
        return selectorType.IsPointer && expression is NullLiteralExpression;
    }

    private static string SwitchLabelKey(Expression expression)
    {
        return expression switch
        {
            IntegerLiteralExpression integer when TryGetConstantInteger(integer, out var value) => $"i:{value}",
            BooleanLiteralExpression boolean => $"b:{boolean.Value}",
            StringLiteralExpression literal => $"s:{literal.Lexeme}",
            MemberAccessExpression member => $"e:{member.Target}.{member.MemberName}",
            _ => expression.ToString() ?? string.Empty
        };
    }

    private static bool IsUnsigned(PrimitiveType type) =>
        type is PrimitiveType.U8 or PrimitiveType.U16 or PrimitiveType.U32 or PrimitiveType.U64 or PrimitiveType.USize;

    private static int IntegerBitWidth(PrimitiveType type) =>
        type is PrimitiveType.I8 or PrimitiveType.U8 ? 8
        : type is PrimitiveType.I16 or PrimitiveType.U16 ? 16
        : type is PrimitiveType.I32 or PrimitiveType.U32 ? 32
        : 64;

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

    private static bool IsRepresentable(double value, SemanticType target)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return false;
        return target.Primitive switch
        {
            PrimitiveType.I8 => value >= sbyte.MinValue && value <= sbyte.MaxValue && value == Math.Truncate(value),
            PrimitiveType.U8 => value >= byte.MinValue && value <= byte.MaxValue && value == Math.Truncate(value),
            PrimitiveType.I16 => value >= short.MinValue && value <= short.MaxValue && value == Math.Truncate(value),
            PrimitiveType.U16 => value >= ushort.MinValue && value <= ushort.MaxValue && value == Math.Truncate(value),
            PrimitiveType.I32 => value >= int.MinValue && value <= int.MaxValue && value == Math.Truncate(value),
            PrimitiveType.U32 => value >= uint.MinValue && value <= uint.MaxValue && value == Math.Truncate(value),
            PrimitiveType.I64 => value >= long.MinValue && value <= long.MaxValue && value == Math.Truncate(value),
            PrimitiveType.U64 => value >= 0 && value <= ulong.MaxValue && value == Math.Truncate(value),
            PrimitiveType.ISize => value >= long.MinValue && value <= long.MaxValue && value == Math.Truncate(value),
            PrimitiveType.USize => value >= 0 && value <= ulong.MaxValue && value == Math.Truncate(value),
            PrimitiveType.F32 => value >= -float.MaxValue && value <= float.MaxValue,
            PrimitiveType.F64 => true,
            _ => false
        };
    }

    private static IReadOnlyDictionary<string, SemanticType>? InferGenericSubstitutions(
        FunctionSymbol generic,
        IReadOnlyList<SemanticType> argumentTypes,
        SemanticType? expectedReturnType)
    {
        var substitutions = new Dictionary<string, SemanticType>(StringComparer.Ordinal);
        for (var index = 0; index < generic.Parameters.Count; index++)
        {
            var syntax = generic.Declaration.Parameters[index].Type;
            var actual = argumentTypes[index];
            if (!ContainsGenericType(syntax, generic.DeclaredGenericParameters))
            {
                if (generic.Parameters[index].Type != actual)
                    return null;
                continue;
            }
            while (syntax.IsPointer)
            {
                if (!actual.IsPointer || actual.Pointee is null || syntax.Pointee is null)
                    return null;

                syntax = syntax.Pointee;
                actual = actual.Pointee;
            }

            if (!TryInferGenericType(syntax, actual, generic.DeclaredGenericParameters, substitutions))
                return null;
        }

        if (expectedReturnType is not null)
        {
            if (!ContainsGenericType(generic.Declaration.ReturnType, generic.DeclaredGenericParameters))
            {
                if (generic.ReturnType != expectedReturnType)
                    return null;
            }
            else if (!TryInferGenericType(generic.Declaration.ReturnType, expectedReturnType,
                         generic.DeclaredGenericParameters, substitutions))
                return null;
        }
        return substitutions.Count == generic.DeclaredGenericParameters.Count ? substitutions : null;
    }

    private static bool ContainsGenericType(TypeSyntax syntax, IReadOnlyList<string> genericParameters) =>
        syntax.NamedType is not null && genericParameters.Contains(syntax.NamedType)
        || syntax.Pointee is not null && ContainsGenericType(syntax.Pointee, genericParameters)
        || syntax.ElementType is not null && ContainsGenericType(syntax.ElementType, genericParameters)
        || syntax.TypeArguments is not null && syntax.TypeArguments.Any(argument =>
            ContainsGenericType(argument, genericParameters));

    private static bool TryInferGenericType(
        TypeSyntax syntax,
        SemanticType actual,
        IReadOnlyList<string> genericParameters,
        IDictionary<string, SemanticType> substitutions)
    {
        while (syntax.IsPointer)
        {
            if (!actual.IsPointer || actual.Pointee is null || syntax.Pointee is null)
                return false;
            syntax = syntax.Pointee;
            actual = actual.Pointee;
        }

        if (syntax.NamedType is null || !genericParameters.Contains(syntax.NamedType))
            return false;
        if (substitutions.TryGetValue(syntax.NamedType, out var existing))
            return existing == actual;
        substitutions[syntax.NamedType] = actual;
        return true;
    }

    private static SemanticType SubstituteGenericType(
        SemanticType type,
        IReadOnlyDictionary<string, SemanticType> substitutions) =>
        type.IsGenericParameter && substitutions.TryGetValue(type.GenericParameterName!, out var replacement)
            ? replacement
            : type.IsPointer && type.Pointee is not null
                ? SemanticType.PointerTo(SubstituteGenericType(type.Pointee, substitutions))
            : type.IsArray && type.ElementType is not null
                ? SemanticType.ArrayOf(SubstituteGenericType(type.ElementType, substitutions), type.ArrayLength)
            : type.TypeArguments is { Count: > 0 }
                ? type with
                {
                    TypeArguments = type.TypeArguments
                        .Select(argument => SubstituteGenericType(argument, substitutions))
                        .ToArray()
                }
                : type;

    private FunctionSymbol SpecializeFunction(
        FunctionSymbol generic,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        SourceLocation location)
    {
        ValidateTypeArgumentConstraints(generic.Declaration.DeclaredTypeParameters,
            generic.DeclaredGenericParameters.Select(name => substitutions[name]).ToArray(), location);
        var declaration = CloneFunction(generic.Declaration, substitutions);
        var previous = _genericTypes;
        _genericTypes = substitutions;
        var specializationArguments = generic.DeclaredGenericParameters
            .Select(parameter => substitutions[parameter])
            .ToArray();
        var symbol = new FunctionSymbol(
            generic.Name,
            ResolveType(declaration.ReturnType),
            declaration.Parameters.Select(parameter =>
                new ParameterSymbol(parameter.Name, ResolveType(parameter.Type), parameter)).ToArray(),
            declaration, generic.ModuleName, generic.Visibility, generic.NativeName, false, generic.CInterop, [],
            specializationArguments);
        _genericTypes = previous;
        _usedFunctions.Add(generic);
        // Inside a generic body (e.g. `alloc<T>` in `List<T>`) the arguments are still type
        // parameters. The signature is enough to type-check that body; the concrete
        // specialization is created when the enclosing generic is instantiated.
        if (IsOpenSpecialization(symbol))
            return symbol;
        var mutable = (Dictionary<string, List<FunctionSymbol>>)functions;
        var list = mutable[generic.QualifiedName];
        var existing = list.FirstOrDefault(candidate =>
            candidate.IsSpecialized
            && candidate.SpecializationArguments!.SequenceEqual(specializationArguments)
            && HasSameParameterTypes(candidate, symbol));
        if (existing is not null)
            return existing;
        list.Add(symbol);
        _functionSymbols?.Add(symbol.Declaration, symbol);
        return symbol;
    }

    private static FunctionDeclaration CloneFunction(
        FunctionDeclaration source, IReadOnlyDictionary<string, SemanticType> substitutions) =>
        new InstantiationCloner(substitutions).Function(source, substitutesOwnTypeParameters: true);

    private static bool IsAddressable(Expression expression, Scope scope) =>
        expression switch
        {
            IdentifierExpression identifier => scope.Lookup(identifier.Name, identifier.Location) is not null,
            ThisExpression => true,
            IndexExpression => true,
            UnaryExpression { Operator: TokenKind.Star, Operand: var operand } =>
                true,
            _ => false
        };

    private static List<FunctionSymbol> ResolveFunctions(
        string name,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions)
    {
        var keys = new List<string>();
        if (!name.Contains('.', StringComparison.Ordinal))
            keys.Add($"{moduleName}.{name}");

        // A fully qualified reference is always valid when its module is in the
        // compilation, regardless of whether that module was imported.
        keys.Add(name);

        foreach (var import in imports)
        {
            if (!name.Contains('.', StringComparison.Ordinal))
            {
                // Functions in the imported module itself do not need a prefix.
                keys.Add($"{import.Name}.{name}");
            }

            if (import.Alias is not null && name.StartsWith(import.Alias + ".", StringComparison.Ordinal))
            {
                // An alias can explicitly qualify the imported module and its
                // descendants without changing the canonical module name.
                keys.Add($"{import.Name}.{name[(import.Alias.Length + 1)..]}");
            }
            else if (name.Contains('.', StringComparison.Ordinal))
            {
                var leaf = import.Name[(import.Name.LastIndexOf('.') + 1)..];
                if (name.StartsWith(leaf + ".", StringComparison.Ordinal))
                {
                    // The legacy module-leaf prefix remains valid:
                    // import stdlib.io; io.print(...) -> stdlib.io.print(...).
                    keys.Add($"{import.Name}.{name[(leaf.Length + 1)..]}");
                }
                else
                {
                    // Only the portion below the imported module is required:
                    // import math; float.pow(...) -> math.float.pow(...).
                    keys.Add($"{import.Name}.{name}");
                }
            }
        }

        var candidates = new List<FunctionSymbol>();
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            if (functions.TryGetValue(key, out var resolved))
                candidates.AddRange(resolved.Where(symbol =>
                    symbol.Visibility == Visibility.Public || symbol.ModuleName == moduleName));
        }

        return candidates
            .GroupBy(symbol => symbol.QualifiedName + ":" + string.Join(",", symbol.Parameters.Select(p => p.Type))
                + ":" + symbol.ReturnType)
            .Select(group => group.First())
            .ToList();
    }

    private static bool HasSameParameterTypes(FunctionSymbol left, FunctionSymbol right) =>
        left.Parameters.Select(parameter => parameter.Type)
            .SequenceEqual(right.Parameters.Select(parameter => parameter.Type));

    private static void RequireType(SemanticType actual, SemanticType expected, SourceLocation location, string context)
    {
        if (actual != expected)
        {
            throw Error($"{context} require {expected} operands, got {actual}", location);
        }
    }

    private static SemanticException Error(string message, SourceLocation location, int length = 1) =>
        new(message, location, length);

    // An error about a class or struct belongs to the file that declares it, even when it is found in a phase that has no
    // current source, such as the class hierarchy checks that run after all declarations are registered.
    private SemanticException DeclarationError(string declarationName, string message, SourceLocation location, int length = 1) =>
        new(message, location, length)
        {
            IsSourceAttributed = true,
            SourceName = _declarationSourceNames.GetValueOrDefault(declarationName)
        };

    private void MarkVariableUsed(VariableSymbol variable) => _usedVariables.Add(variable);

    private void ReportWarning(
        string message,
        SourceLocation location,
        int length = 1,
        string? sourceName = null)
    {
        _diagnostics?.AddWarning(
            DiagnosticPhase.Semantic,
            message,
            location,
            length,
            sourceName);
    }

}
