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
    private readonly HashSet<VariableDeclarationStatement> _escapingClassLocals = [];
    private readonly Dictionary<ForInStatement, IteratorProtocol> _iteratorProtocols = [];
    private string? _sourceName;
    private readonly Dictionary<string, StructSymbol> _structsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnumSymbol> _enumsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UnionSymbol> _unionsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClassSymbol> _classesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InterfaceSymbol> _interfacesByName = new(StringComparer.Ordinal);
    private int _loopDepth;
    private int _breakableDepth;
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
        var operand = AnalyzeExpression(cast.Operand, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
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
        _iteratorProtocols.Clear();
        _structsByName.Clear();
        _enumsByName.Clear();
        _unionsByName.Clear();
        _classesByName.Clear();
        _interfacesByName.Clear();
        _loopDepth = 0;
        _breakableDepth = 0;

        var functions = new Dictionary<FunctionDeclaration, FunctionSymbol>();
        _functionSymbols = functions;
        foreach (var unit in compilationUnits)
        {
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
                        declaration.DeclaredTypeParameters.Select(parameter => parameter.Name).ToArray()));
            }
            foreach (var declaration in unit.Classes)
            {
                ValidateTypeParameters(declaration.DeclaredTypeParameters);
                EnsureTypeNameAvailable(declaration.Name, declaration.Location);
                _classesByName.Add(declaration.Name,
                    new ClassSymbol(declaration.Name, moduleName, declaration.BaseType,
                        declaration.Interfaces, [], [], [], declaration.Destructor, declaration, declaration.Visibility,
                        declaration.DeclaredTypeParameters.Select(parameter => parameter.Name).ToArray()));
            }
        }
        foreach (var unit in compilationUnits)
        {
            foreach (var declaration in unit.Interfaces)
            {
                var methods = WithGenericTypes(declaration.DeclaredTypeParameters, () =>
                    declaration.Methods.Select(method =>
                        CreateFunctionSymbol(method, $"{unit.ModuleName ?? "program"}.{declaration.Name}")).ToArray());
                if (methods.GroupBy(MethodSignature).Any(group => group.Count() > 1))
                    throw Error($"Interface '{declaration.Name}' contains duplicate methods", declaration.Location);
                _interfacesByName[declaration.Name] = _interfacesByName[declaration.Name] with { Methods = methods };
            }
            foreach (var declaration in unit.Classes)
            {
                var fields = WithGenericTypes(declaration.DeclaredTypeParameters, () =>
                    declaration.Fields.Select(field =>
                        new ClassFieldSymbol(field.Name, ResolveType(field.Type), field, field.Visibility)).ToArray());
                if (fields.GroupBy(field => field.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
                    throw Error($"Class '{declaration.Name}' contains duplicate fields", declaration.Location);
                var methods = WithGenericTypes(declaration.DeclaredTypeParameters, () =>
                    declaration.Methods.Select(method =>
                        CreateFunctionSymbol(method, $"{unit.ModuleName ?? "program"}.{declaration.Name}")).ToArray());
                if (methods.GroupBy(MethodSignature).Any(group => group.Count() > 1))
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
                _classesByName[declaration.Name] = _classesByName[declaration.Name] with
                {
                    Fields = fields, Methods = methods, Constructors = constructors
                };
            }
        }
        ValidateClassHierarchy();
        foreach (var className in _classesByName.Keys.ToArray())
            _classesByName[className] = _classesByName[className] with
            {
                Fields = GetClassFields(className, [])
            };
        foreach (var unit in compilationUnits)
        {
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
        foreach (var structure in _structsByName.Values)
            ValidateStructLayout(structure, []);
        var functionsByName = new Dictionary<string, List<FunctionSymbol>>(StringComparer.Ordinal);
        var functionSources = new Dictionary<FunctionSymbol, string?>();
        var functionModules = new Dictionary<FunctionSymbol, (string Name, IReadOnlyList<ImportDeclaration> Imports)>();
        var resolvedCalls = new Dictionary<CallExpression, FunctionSymbol>();
        for (var unitIndex = 0; unitIndex < compilationUnits.Count; unitIndex++)
        {
            var unit = compilationUnits[unitIndex];
            var moduleName = unit.ModuleName ?? "program";
            var importAliases = new HashSet<string>(StringComparer.Ordinal);
            foreach (var import in unit.Imports)
            {
                if (import.Alias is not null && !importAliases.Add(import.Alias))
                    throw Error($"Import alias '{import.Alias}' is declared more than once", import.Location,
                        import.Alias.Length);
            }
            var sourceName = diagnostics is null || sourceNames is null || unitIndex >= sourceNames.Count
                ? null
                : sourceNames[unitIndex];
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
            }
            foreach (var classDeclaration in unit.Classes)
            {
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

        var expressionTypes = new Dictionary<Expression, SemanticType>();
        foreach (var symbol in functionsByName.Values.SelectMany(overloads => overloads).ToList())
        {
            if (symbol.IsExternal)
                continue;
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
                    sourceName: functionSources.GetValueOrDefault(symbol));
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
                    try
                    {
                        AnalyzeFunction(
                            CreateConstructorFunctionSymbol(moduleName, constructor),
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
                            sourceName: sourceNames is not null && unitIndex < sourceNames.Count
                                ? sourceNames[unitIndex]
                                : null);
                    }
                }

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
                            sourceName: sourceNames is not null && unitIndex < sourceNames.Count
                                ? sourceNames[unitIndex]
                                : null);
                    }
                }
            }
        }

        if (diagnostics is not null)
        {
            foreach (var symbol in functionsByName.Values.SelectMany(overloads => overloads))
            {
                if (symbol.QualifiedName != "program.main"
                    && symbol.Visibility != Visibility.Public
                    && !symbol.IsExternal
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
            _classesByName, _interfacesByName, _escapingClassLocals, _iteratorProtocols);
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

    private void ValidateClassHierarchy()
    {
        foreach (var classSymbol in _classesByName.Values)
        {
            var interfaceNames = classSymbol.InterfaceNames.ToList();
            if (classSymbol.BaseTypeName is not null
                && !_classesByName.ContainsKey(classSymbol.BaseTypeName)
                && _interfacesByName.ContainsKey(classSymbol.BaseTypeName))
                interfaceNames.Insert(0, classSymbol.BaseTypeName);
            else if (classSymbol.BaseTypeName is not null && !_classesByName.ContainsKey(classSymbol.BaseTypeName))
                throw Error($"Base class '{classSymbol.BaseTypeName}' is not declared", classSymbol.Declaration.Location);
            foreach (var interfaceName in interfaceNames)
                if (!_interfacesByName.ContainsKey(interfaceName))
                    throw Error($"Interface '{interfaceName}' is not declared", classSymbol.Declaration.Location);
            var inherited = classSymbol.BaseTypeName is null || !_classesByName.ContainsKey(classSymbol.BaseTypeName)
                ? [] : GetClassMethods(classSymbol.BaseTypeName, []);
            foreach (var method in classSymbol.Methods)
            {
                var overridden = inherited.FirstOrDefault(candidate => MethodSignature(candidate) == MethodSignature(method));
                if (method.Declaration.IsOverride)
                {
                    if (overridden is null)
                        throw Error($"Method '{method.Name}' is marked 'override' but does not override a base method",
                            method.Declaration.Location, method.Name.Length);
                    if (!overridden.Declaration.IsVirtual && !overridden.Declaration.IsOverride)
                        throw Error($"Method '{method.Name}' cannot override non-virtual method '{overridden.Name}'",
                            method.Declaration.Location, method.Name.Length);
                }
                else if (overridden is not null && (overridden.Declaration.IsVirtual || overridden.Declaration.IsOverride))
                {
                    throw Error($"Method '{method.Name}' overrides a virtual method and must be marked 'override'",
                        method.Declaration.Location, method.Name.Length);
                }
            }
            foreach (var contract in interfaceNames.SelectMany(name => GetInterfaceMethods(name, [])))
            {
                if (!classSymbol.Methods.Concat(inherited).Any(method =>
                        method.Name == contract.Name && method.Parameters.Select(parameter => parameter.Type)
                            .SequenceEqual(contract.Parameters.Select(parameter => parameter.Type))
                        && method.ReturnType == contract.ReturnType))
                    throw Error($"Class '{classSymbol.Name}' does not implement interface method '{contract.Name}'",
                        classSymbol.Declaration.Location);
            }
        }
    }

    private IReadOnlyList<FunctionSymbol> GetClassMethods(string name, HashSet<string> active)
    {
        if (!active.Add(name))
            throw Error($"Class inheritance cycle involving '{name}'", _classesByName[name].Declaration.Location);
        var cls = _classesByName[name];
        var methods = cls.BaseTypeName is null ? [] : GetClassMethods(cls.BaseTypeName, active);
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

    private IReadOnlyList<ClassFieldSymbol> GetClassFields(string name, HashSet<string> active)
    {
        if (!active.Add(name))
            throw Error($"Class inheritance cycle involving '{name}'", _classesByName[name].Declaration.Location);
        var cls = _classesByName[name];
        var inherited = cls.BaseTypeName is not null && _classesByName.ContainsKey(cls.BaseTypeName)
            ? GetClassFields(cls.BaseTypeName, active)
            : [];
        active.Remove(name);
        var fields = inherited.Concat(cls.Fields).ToArray();
        if (fields.GroupBy(field => field.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw Error($"Class '{name}' contains a field that hides an inherited field", cls.Declaration.Location);
        return fields;
    }

    private IReadOnlyList<FunctionSymbol> GetInterfaceMethods(string name, HashSet<string> active)
    {
        if (!active.Add(name))
            throw Error($"Interface inheritance cycle involving '{name}'", _interfacesByName[name].Declaration.Location);
        active.Remove(name);
        return _interfacesByName[name].Methods;
    }

    public SemanticResult AnalyzeWithDiagnostics(
        IReadOnlyList<CompilationUnit> compilationUnits,
        IReadOnlyList<string?>? sourceNames = null,
        IReadOnlyList<ExternalModuleDeclaration>? externalModules = null)
    {
        var diagnostics = new DiagnosticBag();
        return new SemanticResult(AnalyzeCore(compilationUnits, diagnostics, sourceNames, externalModules), diagnostics.Diagnostics);
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
            GenericParameters: declaration.DeclaredTypeParameters.Select(parameter => parameter.Name).ToArray());
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
            throw Error($"Struct '{structure.Name}' contains a recursive by-value field", structure.Declaration.Location,
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
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        _declaredVariables.Clear();
        _definitelyNullVariables.Clear();
        var scope = new Scope(null, MarkVariableUsed);
        var className = function.ModuleName[(function.ModuleName.LastIndexOf('.') + 1)..];
        if (_classesByName.TryGetValue(className, out var ownerClass))
        {
            scope = new Scope(null, MarkVariableUsed, SemanticType.Class(ownerClass.Name));
            foreach (var field in ownerClass.Fields)
                scope.Declare(field.Name, new VariableSymbol(field.Type, false, field.Declaration, false));
        }
        var parameterScope = new Scope(scope, MarkVariableUsed);
        foreach (var parameter in function.Parameters)
        {
            var variable = new VariableSymbol(parameter.Type, false, parameter.Declaration, true);
            parameterScope.Declare(parameter.Name, variable);
            _declaredVariables.Add(variable);
        }

        _loopDepth = 0;
        _breakableDepth = 0;
        var hasReturn = AnalyzeBlock(function.Declaration.Body, parameterScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
        if (!function.ReturnType.IsVoid && !hasReturn)
        {
            throw Error($"Function '{function.Name}' must return a value", function.Declaration.Location);
        }

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
                var thenReturns = AnalyzeStatement(conditional.Then, thenScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                var elseReturns = conditional.Else is not null
                    && AnalyzeStatement(conditional.Else, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                return conditional.Else is not null && thenReturns && elseReturns;
            case WhileStatement whileStatement:
                var whileType = AnalyzeExpression(whileStatement.Condition, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                RequireBoolean(whileType, whileStatement.Condition.Location);
                _loopDepth++;
                _breakableDepth++;
                AnalyzeStatement(whileStatement.Body, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                _breakableDepth--;
                _loopDepth--;
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
                _loopDepth++;
                _breakableDepth++;
                AnalyzeStatement(forStatement.Body, loopScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                if (forStatement.Increment is not null)
                    AnalyzeStatement(forStatement.Increment, loopScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                _breakableDepth--;
                _loopDepth--;
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
                _loopDepth++;
                _breakableDepth++;
                AnalyzeStatement(forInStatement.Body, forInScope, function, functions, moduleName, imports,
                    expressionTypes, resolvedCalls);
                _breakableDepth--;
                _loopDepth--;
                return false;
            }
            case SwitchStatement switchStatement:
            {
                var valueType = AnalyzeExpression(switchStatement.Value, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                _breakableDepth++;
                try
                {
                    if (valueType.IsUnion)
                    {
                        var union = _unionsByName[valueType.EnumName!];
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
                                caseScope.Declare(item.BindingName, new VariableSymbol(unionCase.PayloadType!, false, item, false));
                            var bodyReturns = AnalyzeStatement(item.Body, caseScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                            caseReturns[index] = bodyReturns || (index + 1 < caseReturns.Length && caseReturns[index + 1]);
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
                                    caseReturns[index] = AnalyzeStatement(item.Body, caseScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                                }
                                else
                                {
                                    caseReturns[index] = AnalyzeStatement(item.Body, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                                }
                                caseReturns[index] |= index + 1 < caseReturns.Length && caseReturns[index + 1];
                                continue;
                            }
                            caseReturns[index] = AnalyzeStatement(item.Body, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls)
                                || (index + 1 < caseReturns.Length && caseReturns[index + 1]);
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
                                    && !(valueType.IsPointer && item.Value is IntegerLiteralExpression { Lexeme: "0" })
                                    && !CanAssign(valueType, item.Value, labelType)))
                                throw Error($"Switch case value must be a constant comparable with {valueType}", item.Location);
                            if (!seenLabels.Add(SwitchLabelKey(item.Value)))
                                throw Error("Duplicate switch case value", item.Location);
                        }

                        var bodyReturns = AnalyzeStatement(item.Body, scope, function, functions, moduleName, imports,
                            expressionTypes, resolvedCalls);
                        caseReturnsForValue[index] = bodyReturns
                            || (index + 1 < caseReturnsForValue.Length && caseReturnsForValue[index + 1]);
                    }
                    return hasDefault && caseReturnsForValue.Length > 0 && caseReturnsForValue[0];
                }
                finally
                {
                    _breakableDepth--;
                }
            }
            case BreakStatement breakStatement when _breakableDepth == 0:
                throw Error("'break' is only valid inside a loop or switch", breakStatement.Location);
            case ContinueStatement continueStatement when _loopDepth == 0:
                throw Error("'continue' is only valid inside a loop", continueStatement.Location);
            case BreakStatement:
            case ContinueStatement:
                return false;
            case VariableDeclarationStatement declaration:
                AnalyzeVariableDeclaration(declaration, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                return false;
            case AssignmentStatement assignment:
                AnalyzeAssignment(assignment, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                return false;
            case ReturnStatement returnStatement:
                AnalyzeReturn(returnStatement, function, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                return true;
            case ThrowStatement throwStatement:
            {
                var thrownType = AnalyzeExpression(throwStatement.Value, scope, functions, moduleName, imports,
                    expressionTypes, resolvedCalls);
                if (!IsExceptionType(thrownType))
                    throw Error($"Only exception class values can be thrown, got {thrownType}",
                        throwStatement.Value.Location);
                return true;
            }
            case TryStatement tryStatement:
            {
                var tryReturns = AnalyzeBlock(tryStatement.Body, scope, function, functions, moduleName, imports,
                    expressionTypes, resolvedCalls);
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

                    var catchReturns = AnalyzeBlock(catchClause.Body, catchScope, function, functions, moduleName,
                        imports, expressionTypes, resolvedCalls);
                    allCatchesReturn &= catchReturns;
                }

                var finallyReturns = tryStatement.Finally is not null
                    && AnalyzeBlock(tryStatement.Finally, scope, function, functions, moduleName, imports,
                        expressionTypes, resolvedCalls);
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
            : AnalyzeExpression(declaration.Initializer, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
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
        if (declaration.Initializer is IntegerLiteralExpression { Lexeme: "0" } && type.IsPointer)
            expressionTypes[declaration.Initializer] = type;

        var variable = new VariableSymbol(type, declaration.IsConst, declaration, false);
        scope.Declare(declaration.Name, variable);
        _declaredVariables.Add(variable);
        if (type.IsPointer && declaration.Initializer is IntegerLiteralExpression { Lexeme: "0" })
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
        var targetType = AnalyzeExpression(assignment.Target, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        var valueType = AnalyzeExpression(assignment.Value, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
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

    private void TrackPointerAssignment(
        AssignmentStatement assignment,
        Expression value,
        SemanticType valueType,
        Scope scope)
    {
        if (assignment.Target is IdentifierExpression assignedIdentifier)
        {
            var variable = scope.Lookup(assignedIdentifier.Name, assignedIdentifier.Location);
            if (valueType.IsClass && assignment.Value is IdentifierExpression sourceIdentifier)
            {
                var source = scope.Lookup(sourceIdentifier.Name, sourceIdentifier.Location);
                if (source.Declaration is VariableDeclarationStatement sourceDeclaration
                    && scope.LookupDepth(assignedIdentifier.Name, assignedIdentifier.Location)
                    > scope.LookupDepth(sourceIdentifier.Name, sourceIdentifier.Location))
                    _escapingClassLocals.Add(sourceDeclaration);
            }
            if (variable.Type.IsPointer && assignment.Operator == TokenKind.Equals)
            {
                if (value is IntegerLiteralExpression { Lexeme: "0" })
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

        var valueType = AnalyzeExpression(returnStatement.Value, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        if (valueType.IsClass && returnStatement.Value is IdentifierExpression identifier
            && scope.Lookup(identifier.Name, identifier.Location).Declaration is VariableDeclarationStatement declaration)
            _escapingClassLocals.Add(declaration);
        if (!CanAssign(function.ReturnType, returnStatement.Value, valueType))
        {
            throw Error(
                $"Cannot return {valueType} from function '{function.Name}'; expected {function.ReturnType}",
                returnStatement.Value.Location);
        }
        if (returnStatement.Value is IntegerLiteralExpression { Lexeme: "0" } && function.ReturnType.IsPointer)
            expressionTypes[returnStatement.Value] = function.ReturnType;
    }

    private SemanticType AnalyzeExpression(
        Expression expression,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        bool allowVoid = false)
    {
        var type = expression switch
        {
            IdentifierExpression identifier => scope.Lookup(identifier.Name, identifier.Location).Type,
            ThisExpression thisExpression => scope.LookupThis(thisExpression.Location),
            IntegerLiteralExpression integer => SemanticType.FromPrimitive(ParseNumericLiteralType(integer.Lexeme, false)),
            FloatingPointLiteralExpression floating => SemanticType.FromPrimitive(ParseNumericLiteralType(floating.Lexeme, true)),
            StringLiteralExpression => SemanticType.String,
            BooleanLiteralExpression => SemanticType.FromPrimitive(PrimitiveType.Bool),
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
                conditional, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            CallExpression call => AnalyzeCall(call, scope, functions, moduleName, imports, expressionTypes, resolvedCalls, allowVoid),
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
        !type.IsVoid && !type.IsGenericParameter;

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

    private SemanticType AnalyzeArrayLiteral(ArrayLiteralExpression literal, Scope scope, IReadOnlyDictionary<string,List<FunctionSymbol>> functions, string moduleName, IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression,SemanticType> types, IDictionary<CallExpression,FunctionSymbol> calls)
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

    private SemanticType AnalyzeIndex(IndexExpression index, Scope scope, IReadOnlyDictionary<string,List<FunctionSymbol>> functions, string moduleName, IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression,SemanticType> types, IDictionary<CallExpression,FunctionSymbol> calls)
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

        var targetType = AnalyzeExpression(member.Target, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        if (targetType.IsClass)
        {
            var cls = _classesByName[targetType.StructName!];
            var field = cls.Fields.FirstOrDefault(candidate => candidate.Name == member.MemberName);
            if (field is not null)
                return field.Type;
            if (cls.Methods.Any(method => method.Name == member.MemberName))
                return SemanticType.PointerTo(SemanticType.FromPrimitive(PrimitiveType.Void));
            throw Error($"Class '{cls.Name}' has no member named '{member.MemberName}'", member.Location);
        }
        if (!targetType.IsStruct)
            throw Error($"Member access requires a struct or class value, got {targetType}", member.Location);
        var structure = _structsByName[targetType.StructName!];
        return structure.Fields.FirstOrDefault(field => field.Name == member.MemberName)?.Type
            ?? throw Error($"Struct '{targetType.StructName}' has no field named '{member.MemberName}'", member.Location, member.MemberName.Length);
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
        var matches = cls.Constructors.Where(constructor => constructor.Parameters.Count == arguments.Length)
            .Where(constructor => constructor.Parameters.Select((parameter, index) =>
                CanAssign(parameter.Type, expression.Arguments[index], arguments[index])).All(match => match)).ToArray();
        if (matches.Length == 0)
            throw Error($"No constructor of '{cls.Name}' matches the supplied arguments", expression.Location);
        if (matches.Length > 1)
            throw Error($"Constructor call for '{cls.Name}' is ambiguous", expression.Location);
        return SemanticType.Class(cls.Name);
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
            if (expression.Operand is IntegerLiteralExpression { Lexeme: "0" }
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
            if (left.IsPointer && expression.Right is IntegerLiteralExpression { Lexeme: "0" }
                && expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals)
            {
                expressionTypes[expression.Right] = left;
                return SemanticType.FromPrimitive(PrimitiveType.Bool);
            }
            if (right.IsPointer && expression.Left is IntegerLiteralExpression { Lexeme: "0" }
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

        if (expression.Operator is TokenKind.Ampersand or TokenKind.Pipe or TokenKind.Caret
            or TokenKind.LessThanLessThan or TokenKind.GreaterThanGreaterThan)
        {
            if (!left.IsInteger || !right.IsInteger)
                throw Error($"Operator '{expression.Operator}' requires integer operands", expression.Location);
            return left;
        }

        if (expression.Operator == TokenKind.Percent && (!left.IsInteger || !right.IsInteger))
            throw Error("The '%' operator requires integer operands", expression.Location);

        if (left != right)
        {
            if (left.IsNumeric && right.IsNumeric)
            {
                if (!CanAssign(left, expression.Right, right) && !CanAssign(right, expression.Left, left))
                    throw Error($"Binary operator '{expression.Operator}' requires matching operand types", expression.Location);
                return isComparison ? SemanticType.FromPrimitive(PrimitiveType.Bool)
                    : CanAssign(left, expression.Right, right) ? left : right;
            }

            throw Error($"Binary operator '{expression.Operator}' requires matching operand types", expression.Location);
        }

        if (isComparison)
        {
            if (expression.Operator is not (TokenKind.EqualsEquals or TokenKind.BangEquals) && !left.IsNumeric)
            {
                throw Error($"Operator '{expression.Operator}' requires numeric operands", expression.Location);
            }

            return SemanticType.FromPrimitive(PrimitiveType.Bool);
        }

        if (!left.IsNumeric)
        {
            throw Error($"Operator '{expression.Operator}' requires numeric operands", expression.Location);
        }

        return left;
    }

    private SemanticType AnalyzeConditional(
        ConditionalExpression expression,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var condition = AnalyzeExpression(
            expression.Condition, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        RequireBoolean(condition, expression.Condition.Location);

        var whenTrue = AnalyzeExpression(
            expression.WhenTrue, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        var whenFalse = AnalyzeExpression(
            expression.WhenFalse, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
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
                if (!CanAssign(item.PayloadType, call.Arguments[0], actual))
                    throw Error($"Union case '{item.Name}' expects {item.PayloadType}, got {actual}", call.Location);
            }
            return SemanticType.Union(union.Name);
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
                if (receiverType.IsClass && _classesByName.TryGetValue(receiverType.StructName!, out var receiverClass))
                {
                    var methodCandidates = GetClassMethods(receiverClass.Name, []).Where(method => method.Name == methodName).ToArray();
                    var methodArgumentTypes = call.Arguments.Select(argument =>
                        AnalyzeExpression(argument, scope, functions, moduleName, imports, expressionTypes, resolvedCalls)).ToArray();
                    var methodMatches = methodCandidates.Where(method => method.Parameters.Count == methodArgumentTypes.Length)
                        .Where(method => method.Parameters.Select((parameter, index) =>
                            CanAssign(parameter.Type, call.Arguments[index], methodArgumentTypes[index])).All(match => match)).ToArray();
                    if (methodMatches.Length == 1)
                    {
                        resolvedCalls[call] = methodMatches[0];
                        _usedFunctions.Add(methodMatches[0]);
                        return methodMatches[0].ReturnType;
                    }
                    if (methodCandidates.Length > 0)
                        throw Error($"No overload of '{methodName}' matches the argument types", call.Location);
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
                && scope.Lookup(identifier.Name, identifier.Location).Type.IsClass
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
            var specialized = SpecializeFunction(genericFunction, substitutions, functions);
            // Analyze the cloned body in the generic declaration's module so
            // private helpers used by the generic remain visible.
            AnalyzeFunction(specialized, functions, genericFunction.ModuleName, imports, expressionTypes, resolvedCalls);
            overloads = [specialized];
        }
        else
        {
            var inferred = overloads
                .Where(candidate => candidate.IsGeneric && candidate.Parameters.Count == argumentTypes.Count)
                .Select(candidate => (candidate, substitutions: InferGenericSubstitutions(candidate, argumentTypes)))
                .Where(item => item.substitutions is not null)
                .ToList();
            if (inferred.Count == 1)
            {
                var specialized = SpecializeFunction(inferred[0].candidate, inferred[0].substitutions!, functions);
                AnalyzeFunction(specialized, functions, inferred[0].candidate.ModuleName, imports, expressionTypes, resolvedCalls);
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

        var matches = candidates
            .Where(function => function.Parameters
                .Select((parameter, index) => CanAssign(parameter.Type, call.Arguments[index], argumentTypes[index]))
                .All(match => match))
            .ToList();
        if (matches.Select(match => match.ModuleName).Distinct(StringComparer.Ordinal).Count() > 1)
            throw Error($"Function '{call.Name}' is ambiguous; use a module prefix", call.Location, call.Name.Length);
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
            if (call.Arguments[index] is IntegerLiteralExpression { Lexeme: "0" }
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

    private bool CanAssign(SemanticType target, Expression value, SemanticType actual)
    {
        if (target == actual)
            return true;
        if (target.IsUnion && actual.IsUnion
            && target.EnumName == actual.EnumName
            && (actual.TypeArguments is null || actual.TypeArguments.Count == 0))
            return true;
        if (target.IsPointer && actual.IsNumeric && TryGetConstantInteger(value, out var nullValue) && nullValue == 0)
            return true;
        if (IsReferenceCast(actual, target, out var implicitConversion) && implicitConversion)
            return true;
        if (!target.IsNumeric || !actual.IsNumeric)
            return false;
        if (IsImplicitlySafeNumericConversion(actual, target))
            return true;
        return TryGetConstantNumber(value, out var constant) && IsRepresentable(constant, target);
    }

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
        if (!IsInteger(sourcePrimitive) || !IsInteger(targetPrimitive))
            return false;

        var sourceBits = IntegerBitWidth(sourcePrimitive);
        var targetBits = IntegerBitWidth(targetPrimitive);
        var sourceUnsigned = IsUnsigned(sourcePrimitive);
        var targetUnsigned = IsUnsigned(targetPrimitive);
        return sourceUnsigned == targetUnsigned
            ? targetBits >= sourceBits
            : !sourceUnsigned && !targetUnsigned
                ? targetBits >= sourceBits
                : sourceUnsigned && !targetUnsigned && targetBits > sourceBits;
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
        return selectorType.IsPointer && expression is IntegerLiteralExpression { Lexeme: "0" };
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
                    TokenKind.Plus => left + right, TokenKind.Minus => left - right,
                    TokenKind.Star => left * right, TokenKind.Slash when right != 0 => left / right,
                    TokenKind.Percent when right != 0 => left % right, _ => double.NaN
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
        IReadOnlyList<SemanticType> argumentTypes)
    {
        var substitutions = new Dictionary<string, SemanticType>(StringComparer.Ordinal);
        for (var index = 0; index < generic.Parameters.Count; index++)
        {
            var syntax = generic.Declaration.Parameters[index].Type;
            var actual = argumentTypes[index];
            while (syntax.IsPointer)
            {
                if (!actual.IsPointer || actual.Pointee is null || syntax.Pointee is null)
                    return null;

                syntax = syntax.Pointee;
                actual = actual.Pointee;
            }

            if (syntax.NamedType is null || !generic.DeclaredGenericParameters.Contains(syntax.NamedType))
                return null;
            if (substitutions.TryGetValue(syntax.NamedType, out var existing) && existing != actual)
                return null;
            substitutions[syntax.NamedType] = actual;
        }

        return substitutions.Count == generic.DeclaredGenericParameters.Count ? substitutions : null;
    }

    private FunctionSymbol SpecializeFunction(
        FunctionSymbol generic,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions)
    {
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
        _usedFunctions.Add(generic);
        return symbol;
    }

    private static FunctionDeclaration CloneFunction(
        FunctionDeclaration source, IReadOnlyDictionary<string, SemanticType> substitutions)
    {
        var types = substitutions;
        TypeSyntax FromSemantic(SemanticType value, SourceLocation location) =>
            value.IsPointer
                ? new TypeSyntax(null, false, location, Pointee: FromSemantic(value.Pointee!, location))
                : value.Kind == SemanticTypeKind.Primitive
                    ? new TypeSyntax(value.Primitive, false, location)
                    : value.Kind == SemanticTypeKind.String
                        ? new TypeSyntax(null, false, location, IsString: true)
                        : new TypeSyntax(null, false, location,
                            NamedType: value.StructName ?? value.EnumName ?? value.GenericParameterName,
                            TypeArguments: value.TypeArguments?.Select(argument => FromSemantic(argument, location)).ToArray());
        TypeSyntax Type(TypeSyntax syntax) =>
            syntax.NamedType is not null && types.TryGetValue(syntax.NamedType, out var substituted)
                ? FromSemantic(substituted, syntax.Location)
                : syntax with
                {
                    Pointee = syntax.Pointee is null ? null : Type(syntax.Pointee),
                    ElementType = syntax.ElementType is null ? null : Type(syntax.ElementType),
                    TypeArguments = syntax.TypeArguments?.Select(Type).ToArray()
                };
        Expression Expr(Expression expression) => expression switch
        {
            UnaryExpression e => e with { Operand = Expr(e.Operand) },
            BinaryExpression e => e with { Left = Expr(e.Left), Right = Expr(e.Right) },
            IsExpression e => e with { Operand = Expr(e.Operand) },
            ConditionalExpression e => e with
            {
                Condition = Expr(e.Condition),
                WhenTrue = Expr(e.WhenTrue),
                WhenFalse = Expr(e.WhenFalse)
            },
            CallExpression e => e with { Arguments = e.Arguments.Select(Expr).ToArray(), TypeArguments = e.TypeArguments?.Select(Type).ToArray() },
            CastExpression e => e with { TargetType = Type(e.TargetType), Operand = Expr(e.Operand) },
            SizeOfExpression e => e with { TargetType = e.TargetType is null ? null : Type(e.TargetType), Operand = e.Operand is null ? null : Expr(e.Operand) },
            LengthExpression e => e with { Target = Expr(e.Target) },
            MemberAccessExpression e => e with { Target = Expr(e.Target) },
            IndexExpression e => e with { Target = Expr(e.Target), Index = Expr(e.Index) },
            ArrayLiteralExpression e => e with { Elements = e.Elements.Select(Expr).ToArray() },
            StructLiteralExpression e => e with { Fields = e.Fields.Select(f => f with { Value = Expr(f.Value) }).ToArray() },
            UnionConstructionExpression e => e with { Arguments = e.Arguments.Select(Expr).ToArray() },
            _ => expression
        };
        Statement Stmt(Statement statement) => statement switch
        {
            BlockStatement s => s with { Statements = s.Statements.Select(Stmt).ToArray() },
            VariableDeclarationStatement s => s with { Type = Type(s.Type), Initializer = s.Initializer is null ? null : Expr(s.Initializer) },
            AssignmentStatement s => s with { Target = Expr(s.Target), Value = Expr(s.Value) },
            ReturnStatement s => s with { Value = s.Value is null ? null : Expr(s.Value) },
            ExpressionStatement s => s with { Expression = Expr(s.Expression) },
            IfStatement s => s with { Condition = Expr(s.Condition), Then = Stmt(s.Then), Else = s.Else is null ? null : Stmt(s.Else) },
            WhileStatement s => s with { Condition = Expr(s.Condition), Body = Stmt(s.Body) },
            ForStatement s => s with { Initializer = s.Initializer is null ? null : Stmt(s.Initializer), Condition = s.Condition is null ? null : Expr(s.Condition), Increment = s.Increment is null ? null : Stmt(s.Increment), Body = Stmt(s.Body) },
            ForInStatement s => s with { VariableType = Type(s.VariableType), Collection = Expr(s.Collection), Body = Stmt(s.Body) },
            SwitchStatement s => s with { Value = Expr(s.Value), Cases = s.Cases.Select(c => c with { Body = Stmt(c.Body) }).ToArray() },
            _ => statement
        };
        return source with
        {
            ReturnType = Type(source.ReturnType),
            Parameters = source.Parameters.Select(p => p with { Type = Type(p.Type) }).ToArray(),
            Body = (BlockStatement)Stmt(source.Body),
            TypeParameters = []
        };
    }

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
            .GroupBy(symbol => symbol.QualifiedName + ":" + string.Join(",", symbol.Parameters.Select(p => p.Type)))
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
