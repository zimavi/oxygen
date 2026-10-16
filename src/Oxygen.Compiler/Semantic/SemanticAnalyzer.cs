using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

public sealed class SemanticAnalyzer
{
    private readonly ILogger _logger;
    private DiagnosticBag? _diagnostics;
    private readonly HashSet<VariableSymbol> _usedVariables = [];
    private readonly HashSet<VariableSymbol> _definitelyNullVariables = [];
    private readonly HashSet<FunctionSymbol> _usedFunctions = [];
    private readonly List<VariableSymbol> _declaredVariables = [];
    private string? _sourceName;
    private readonly Dictionary<string, StructSymbol> _structsByName = new(StringComparer.Ordinal);

    public SemanticAnalyzer(ILogger? logger = null)
    {
        _logger = logger ?? new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();
    }

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
        _structsByName.Clear();

        var functions = new Dictionary<FunctionDeclaration, FunctionSymbol>();
        foreach (var unit in compilationUnits)
        {
            var moduleName = unit.ModuleName ?? "program";
            foreach (var declaration in unit.Structs)
            {
                if (_structsByName.ContainsKey(declaration.Name))
                    throw Error($"A struct named '{declaration.Name}' is already declared", declaration.Location, declaration.Name.Length);
                var annotations = ParseStructLayout(declaration);
                _structsByName.Add(declaration.Name,
                    new StructSymbol(declaration.Name, moduleName, [], declaration, declaration.Visibility,
                        annotations.IsPacked, annotations.Alignment));
            }
        }
        foreach (var unit in compilationUnits)
        {
            foreach (var declaration in unit.Structs)
            {
                var fields = declaration.Fields
                    .Select(field => new StructFieldSymbol(field.Name, ResolveType(field.Type), field))
                    .ToArray();
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
                var symbol = CreateFunctionSymbol(declaration, moduleName);
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
        foreach (var symbol in functionsByName.Values.SelectMany(overloads => overloads))
        {
            if (symbol.IsExternal)
                continue;
            try
            {
                _sourceName = functionSources.GetValueOrDefault(symbol);
                var module = functionModules[symbol];
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
        var modules = functionsByName
            .GroupBy(pair => pair.Key[..pair.Key.LastIndexOf('.')], StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<FunctionSymbol>)group.SelectMany(pair => pair.Value).ToList(),
                StringComparer.Ordinal);
        return new SemanticModel(functions, expressionTypes, mainFunction, modules, resolvedCalls, _structsByName);
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
            CInterop: ParseCInterop(declaration));
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

        if (syntax.IsString)
            return syntax.IsPointer
                ? SemanticType.PointerTo(ResolveType(syntax.Pointee!))
                : SemanticType.String;

        if (syntax.NamedType is not null)
        {
            if (!_structsByName.ContainsKey(syntax.NamedType))
                throw Error($"Struct '{syntax.NamedType}' is not declared", syntax.Location, syntax.NamedType.Length);
            var named = SemanticType.Struct(syntax.NamedType);
            return syntax.IsPointer ? SemanticType.PointerTo(named) : named;
        }

        if (syntax.Primitive is null)
            return syntax.IsPointer
                ? SemanticType.PointerTo(ResolveType(syntax.Pointee!))
                : SemanticType.String;

        var type = SemanticType.FromPrimitive(syntax.Primitive.Value);
        return syntax.IsPointer ? SemanticType.PointerTo(type) : type;
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
        foreach (var parameter in function.Parameters)
        {
            var variable = new VariableSymbol(parameter.Type, false, parameter.Declaration, true);
            scope.Declare(parameter.Name, variable);
            _declaredVariables.Add(variable);
        }

        var hasReturn = AnalyzeBlock(function.Declaration.Body, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
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
            case VariableDeclarationStatement declaration:
                AnalyzeVariableDeclaration(declaration, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                return false;
            case AssignmentStatement assignment:
                AnalyzeAssignment(assignment, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                return false;
            case ReturnStatement returnStatement:
                AnalyzeReturn(returnStatement, function, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                return true;
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
        if (!CanAssign(targetType, assignment.Value, valueType))
        {
            throw Error($"Cannot assign {valueType} to target of type {targetType}", assignment.Value.Location);
        }

        if (assignment.Target is IdentifierExpression identifier)
        {
            var variable = scope.Lookup(identifier.Name, identifier.Location);
            if (variable.IsConst)
                throw Error($"Cannot assign to const variable '{identifier.Name}'", assignment.Location, identifier.Name.Length);
        }
        else if (assignment.Target is MemberAccessExpression member)
        {
            if (member.Target is IdentifierExpression memberIdentifier
                && scope.Lookup(memberIdentifier.Name, memberIdentifier.Location).IsConst)
                throw Error($"Cannot assign to a field of const variable '{memberIdentifier.Name}'", assignment.Location,
                    memberIdentifier.Name.Length);
        }
        else if (assignment.Target is not UnaryExpression { Operator: TokenKind.Star })
        {
            throw Error("Assignment target is not addressable", assignment.Target.Location);
        }

        if (assignment.Target is IdentifierExpression assignedIdentifier)
        {
            var variable = scope.Lookup(assignedIdentifier.Name, assignedIdentifier.Location);
            if (variable.Type.IsPointer)
            {
                if (assignment.Value is IntegerLiteralExpression { Lexeme: "0" })
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
            IntegerLiteralExpression => SemanticType.FromPrimitive(PrimitiveType.I32),
            FloatingPointLiteralExpression => SemanticType.FromPrimitive(PrimitiveType.F64),
            StringLiteralExpression => SemanticType.String,
            BooleanLiteralExpression => SemanticType.FromPrimitive(PrimitiveType.Bool),
            StructLiteralExpression literal => AnalyzeStructLiteral(
                literal, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            MemberAccessExpression member => AnalyzeMemberAccess(
                member, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            UnaryExpression unary => AnalyzeUnary(unary, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            BinaryExpression binary => AnalyzeBinary(binary, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            CallExpression call => AnalyzeCall(call, scope, functions, moduleName, imports, expressionTypes, resolvedCalls, allowVoid),
            _ => throw new InvalidOperationException($"Unsupported expression type {expression.GetType().Name}.")
        };

        expressionTypes[expression] = type;
        return type;
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
        var targetType = AnalyzeExpression(member.Target, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        if (!targetType.IsStruct)
            throw Error($"Member access requires a struct value, got {targetType}", member.Location);
        var structure = _structsByName[targetType.StructName!];
        return structure.Fields.FirstOrDefault(field => field.Name == member.MemberName)?.Type
            ?? throw Error($"Struct '{targetType.StructName}' has no field named '{member.MemberName}'", member.Location, member.MemberName.Length);
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
            if (left.IsPointer && right.IsNumeric
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

        if (left != right)
        {
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
        var overloads = ResolveFunctions(call.Name, moduleName, imports, functions);
        if (overloads.Count == 0)
        {
            throw Error($"Function '{call.Name}' is not declared", call.Location, call.Name.Length);
        }

        var argumentTypes = call.Arguments
            .Select(argument => AnalyzeExpression(argument, scope, functions, moduleName, imports, expressionTypes, resolvedCalls))
            .ToList();
        var candidates = overloads
            .Where(function => function.Parameters.Count == argumentTypes.Count)
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

    private static bool CanAssign(SemanticType target, Expression value, SemanticType actual) =>
        target == actual || (target.IsPointer && value is IntegerLiteralExpression { Lexeme: "0" } && actual.IsNumeric);

    private static bool IsAddressable(Expression expression, Scope scope) =>
        expression switch
        {
            IdentifierExpression identifier => scope.Lookup(identifier.Name, identifier.Location) is not null,
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
            var leaf = import.Name[(import.Name.LastIndexOf('.') + 1)..];
            if (name.StartsWith(leaf + ".", StringComparison.Ordinal))
            {
                // import math; float.pow(...) -> math.float.pow(...)
                // import math.float; float.pow(...) -> math.float.pow(...)
                keys.Add($"{import.Name}.{name[(leaf.Length + 1)..]}");
            }
            else if (name.Contains('.', StringComparison.Ordinal))
            {
                // import math; float.pow(...) -> math.float.pow(...)
                keys.Add($"{import.Name}.{name}");
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

    private sealed record VariableSymbol(
        SemanticType Type,
        bool IsConst,
        SyntaxNode Declaration,
        bool IsParameter);

    private sealed class Scope
    {
        private readonly Dictionary<string, VariableSymbol> _variables = new(StringComparer.Ordinal);
        private readonly Scope? _parent;
        private readonly Action<VariableSymbol> _markUsed;

        public Scope(Scope? parent = null, Action<VariableSymbol>? markUsed = null)
        {
            _parent = parent;
            _markUsed = markUsed ?? (_ => { });
        }

        public void Declare(string name, VariableSymbol symbol) => _variables.Add(name, symbol);

        public bool ContainsInCurrentScope(string name) => _variables.ContainsKey(name);

        public VariableSymbol Lookup(string name, SourceLocation location) =>
            _variables.TryGetValue(name, out var symbol)
                ? MarkUsed(symbol)
                : _parent?.Lookup(name, location)
                  ?? throw Error($"Variable '{name}' is not declared", location, name.Length);

        private VariableSymbol MarkUsed(VariableSymbol symbol)
        {
            _markUsed(symbol);
            return symbol;
        }
    }
}
