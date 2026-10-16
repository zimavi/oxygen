using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

public sealed class SemanticAnalyzer
{
    private readonly ILogger _logger;

    public SemanticAnalyzer(ILogger? logger = null)
    {
        _logger = logger ?? new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();
    }

    public SemanticModel Analyze(IReadOnlyList<CompilationUnit> compilationUnits)
        => AnalyzeCore(compilationUnits, null);

    private SemanticModel AnalyzeCore(
        IReadOnlyList<CompilationUnit> compilationUnits,
        DiagnosticBag? diagnostics,
        IReadOnlyList<string?>? sourceNames = null)
    {
        ArgumentNullException.ThrowIfNull(compilationUnits);

        var functions = new Dictionary<FunctionDeclaration, FunctionSymbol>();
        var functionsByName = new Dictionary<string, List<FunctionSymbol>>(StringComparer.Ordinal);
        var functionSources = new Dictionary<FunctionSymbol, string?>();

        for (var unitIndex = 0; unitIndex < compilationUnits.Count; unitIndex++)
        {
            var unit = compilationUnits[unitIndex];
            var sourceName = diagnostics is null || sourceNames is null || unitIndex >= sourceNames.Count
                ? null
                : sourceNames[unitIndex];
            foreach (var declaration in unit.Functions)
            {
                var symbol = CreateFunctionSymbol(declaration);
                if (!functionsByName.TryGetValue(symbol.Name, out var overloads))
                {
                    overloads = [];
                    functionsByName.Add(symbol.Name, overloads);
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
            }
        }

        FunctionSymbol? mainFunction = null;
        if (!functionsByName.TryGetValue("main", out var mainOverloads))
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
            try
            {
                AnalyzeFunction(symbol, functionsByName, expressionTypes);
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

        _logger.Information("Analyzed {FunctionCount} function(s).", functions.Count);
        return new SemanticModel(functions, expressionTypes, mainFunction);
    }

    public SemanticResult AnalyzeWithDiagnostics(
        IReadOnlyList<CompilationUnit> compilationUnits,
        IReadOnlyList<string?>? sourceNames = null)
    {
        var diagnostics = new DiagnosticBag();
        return new SemanticResult(AnalyzeCore(compilationUnits, diagnostics, sourceNames), diagnostics.Diagnostics);
    }

    private static FunctionSymbol CreateFunctionSymbol(FunctionDeclaration declaration)
    {
        var returnType = GetType(declaration.ReturnType);
        var parameters = new List<ParameterSymbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var parameter in declaration.Parameters)
        {
            if (!names.Add(parameter.Name))
            {
                throw Error($"Parameter '{parameter.Name}' is declared more than once", parameter.Location, parameter.Name.Length);
            }

            parameters.Add(new ParameterSymbol(parameter.Name, GetType(parameter.Type), parameter));
        }

        return new FunctionSymbol(declaration.Name, returnType, parameters, declaration);
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

    private static SemanticType GetType(TypeSyntax syntax)
    {
        if (syntax.IsInferred || syntax.Primitive is null)
        {
            throw Error("Type inference is not valid in this context", syntax.Location);
        }

        return SemanticType.FromPrimitive(syntax.Primitive.Value);
    }

    private void AnalyzeFunction(
        FunctionSymbol function,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        IDictionary<Expression, SemanticType> expressionTypes)
    {
        var scope = new Scope();
        foreach (var parameter in function.Parameters)
        {
            scope.Declare(parameter.Name, new VariableSymbol(parameter.Type, false, parameter.Declaration));
        }

        var hasReturn = AnalyzeBlock(function.Declaration.Body, scope, function, functions, expressionTypes);
        if (!function.ReturnType.IsVoid && !hasReturn)
        {
            throw Error($"Function '{function.Name}' must return a value", function.Declaration.Location);
        }
    }

    private bool AnalyzeBlock(
        BlockStatement block,
        Scope parent,
        FunctionSymbol function,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        IDictionary<Expression, SemanticType> expressionTypes)
    {
        var scope = new Scope(parent);
        var hasReturn = false;

        foreach (var statement in block.Statements)
        {
            hasReturn |= AnalyzeStatement(statement, scope, function, functions, expressionTypes);
        }

        return hasReturn;
    }

    private bool AnalyzeStatement(
        Statement statement,
        Scope scope,
        FunctionSymbol function,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        IDictionary<Expression, SemanticType> expressionTypes)
    {
        switch (statement)
        {
            case BlockStatement block:
                return AnalyzeBlock(block, scope, function, functions, expressionTypes);
            case VariableDeclarationStatement declaration:
                AnalyzeVariableDeclaration(declaration, scope, functions, expressionTypes);
                return false;
            case AssignmentStatement assignment:
                AnalyzeAssignment(assignment, scope, functions, expressionTypes);
                return false;
            case ReturnStatement returnStatement:
                AnalyzeReturn(returnStatement, function, scope, functions, expressionTypes);
                return true;
            case ExpressionStatement expressionStatement:
                AnalyzeExpression(expressionStatement.Expression, scope, functions, expressionTypes);
                return false;
            default:
                throw new InvalidOperationException($"Unsupported statement type {statement.GetType().Name}.");
        }
    }

    private void AnalyzeVariableDeclaration(
        VariableDeclarationStatement declaration,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        IDictionary<Expression, SemanticType> expressionTypes)
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
            : AnalyzeExpression(declaration.Initializer, scope, functions, expressionTypes);
        var type = declaration.Type.IsInferred
            ? initializerType!.Value
            : GetType(declaration.Type);

        if (type.IsVoid)
        {
            throw Error($"Variable '{declaration.Name}' cannot have type void", declaration.Location, declaration.Name.Length);
        }

        if (initializerType is not null && initializerType.Value != type)
        {
            throw Error(
                $"Cannot initialize '{declaration.Name}' with {initializerType.Value}; expected {type}",
                declaration.Initializer!.Location);
        }

        scope.Declare(declaration.Name, new VariableSymbol(type, declaration.IsConst, declaration));
    }

    private void AnalyzeAssignment(
        AssignmentStatement assignment,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        IDictionary<Expression, SemanticType> expressionTypes)
    {
        var variable = scope.Lookup(assignment.Name, assignment.Location);
        if (variable.IsConst)
        {
            throw Error($"Cannot assign to const variable '{assignment.Name}'", assignment.Location, assignment.Name.Length);
        }

        var valueType = AnalyzeExpression(assignment.Value, scope, functions, expressionTypes);
        if (valueType != variable.Type)
        {
            throw Error($"Cannot assign {valueType} to '{assignment.Name}' of type {variable.Type}", assignment.Value.Location);
        }
    }

    private void AnalyzeReturn(
        ReturnStatement returnStatement,
        FunctionSymbol function,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        IDictionary<Expression, SemanticType> expressionTypes)
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

        var valueType = AnalyzeExpression(returnStatement.Value, scope, functions, expressionTypes);
        if (valueType != function.ReturnType)
        {
            throw Error(
                $"Cannot return {valueType} from function '{function.Name}'; expected {function.ReturnType}",
                returnStatement.Value.Location);
        }
    }

    private SemanticType AnalyzeExpression(
        Expression expression,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        IDictionary<Expression, SemanticType> expressionTypes)
    {
        var type = expression switch
        {
            IdentifierExpression identifier => scope.Lookup(identifier.Name, identifier.Location).Type,
            IntegerLiteralExpression => SemanticType.FromPrimitive(PrimitiveType.I32),
            FloatingPointLiteralExpression => SemanticType.FromPrimitive(PrimitiveType.F64),
            StringLiteralExpression => SemanticType.String,
            BooleanLiteralExpression => SemanticType.FromPrimitive(PrimitiveType.Bool),
            UnaryExpression unary => AnalyzeUnary(unary, scope, functions, expressionTypes),
            BinaryExpression binary => AnalyzeBinary(binary, scope, functions, expressionTypes),
            CallExpression call => AnalyzeCall(call, scope, functions, expressionTypes),
            _ => throw new InvalidOperationException($"Unsupported expression type {expression.GetType().Name}.")
        };

        expressionTypes[expression] = type;
        return type;
    }

    private SemanticType AnalyzeUnary(
        UnaryExpression expression,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        IDictionary<Expression, SemanticType> expressionTypes)
    {
        var operandType = AnalyzeExpression(expression.Operand, scope, functions, expressionTypes);
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
        IDictionary<Expression, SemanticType> expressionTypes)
    {
        var left = AnalyzeExpression(expression.Left, scope, functions, expressionTypes);
        var right = AnalyzeExpression(expression.Right, scope, functions, expressionTypes);
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
        IDictionary<Expression, SemanticType> expressionTypes)
    {
        if (!functions.TryGetValue(call.Name, out var overloads))
        {
            throw Error($"Function '{call.Name}' is not declared", call.Location, call.Name.Length);
        }

        var argumentTypes = call.Arguments
            .Select(argument => AnalyzeExpression(argument, scope, functions, expressionTypes))
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
                .Select(parameter => parameter.Type)
                .SequenceEqual(argumentTypes))
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
        if (function.ReturnType.IsVoid)
        {
            throw Error($"Void function '{call.Name}' cannot be used as an expression", call.Location);
        }

        return function.ReturnType;
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

    private sealed record VariableSymbol(SemanticType Type, bool IsConst, SyntaxNode Declaration);

    private sealed class Scope
    {
        private readonly Dictionary<string, VariableSymbol> _variables = new(StringComparer.Ordinal);
        private readonly Scope? _parent;

        public Scope(Scope? parent = null)
        {
            _parent = parent;
        }

        public void Declare(string name, VariableSymbol symbol) => _variables.Add(name, symbol);

        public bool ContainsInCurrentScope(string name) => _variables.ContainsKey(name);

        public VariableSymbol Lookup(string name, SourceLocation location) =>
            _variables.TryGetValue(name, out var symbol)
                ? symbol
                : _parent?.Lookup(name, location)
                  ?? throw Error($"Variable '{name}' is not declared", location, name.Length);
    }
}
