using Oxygen.Compiler.Diagnostics;
using System.Globalization;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

// Module-level variables. A module's globals form a scope that each of its functions sees below its own locals, parameters
// and class fields. An initializer is a constant, so it names no variable and calls no function.
public sealed partial class SemanticAnalyzer
{
    private readonly Dictionary<string, Scope> _moduleScopes = new(StringComparer.Ordinal);
    private readonly Dictionary<GlobalVariableDeclaration, GlobalVariableSymbol> _globalSymbols =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Expression, GlobalVariableSymbol> _globalReferences =
        new(ReferenceEqualityComparer.Instance);

    // The getter that each read of a static property names, and the setter that each write names. See SemanticModel.
    private readonly Dictionary<Expression, FunctionSymbol> _staticPropertyGetters = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Expression, FunctionSymbol> _staticPropertySetters = new(ReferenceEqualityComparer.Instance);
    private readonly List<GlobalVariableSymbol> _globals = [];

    // Declares every module's globals. This runs after types and functions are registered and before any body is analysed.
    private void DeclareGlobalVariables(
        IReadOnlyList<CompilationUnit> compilationUnits,
        IReadOnlyList<string?>? sourceNames,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        for (var unitIndex = 0; unitIndex < compilationUnits.Count; unitIndex++)
        {
            var unit = compilationUnits[unitIndex];
            _sourceName = sourceNames is not null && unitIndex < sourceNames.Count ? sourceNames[unitIndex] : null;
            var moduleName = unit.ModuleName ?? "program";
            _typeScopeModule = moduleName;
            _typeScopeImports = unit.Imports;
            foreach (var declaration in unit.Globals)
                DeclareGlobalVariable(declaration, moduleName, functions, unit.Imports, expressionTypes, resolvedCalls);
        }

        _sourceName = null;
    }

    private void DeclareGlobalVariable(
        GlobalVariableDeclaration declaration,
        string moduleName,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var name = declaration.Name;
        var scope = ModuleScope(moduleName);
        if (scope.ContainsInCurrentScope(name))
            throw ErrorWithCode(DiagnosticCode.AlreadyDeclared, $"A global variable named '{name}' is already declared", declaration.Location, name.Length);

        var type = ResolveType(declaration.Type);
        if (type.IsVoid)
            throw Error($"Global variable '{name}' cannot have type void", declaration.Location, name.Length);
        if (type.Primitive is null)
            throw Error($"Global variable '{name}' has type {type}; only numeric and bool globals are supported",
                declaration.Location, name.Length);

        object? value = null;
        if (declaration.Initializer is { } initializer)
        {
            if (!IsConstantInitializer(initializer))
                throw Error($"Global variable '{name}' must be initialized with a literal or an integer constant expression",
                    initializer.Location);
            var actual = AnalyzeExpression(initializer, scope, functions, moduleName, imports, expressionTypes, resolvedCalls,
                expectedType: type);
            if (!CanAssign(type, initializer, actual))
                throw ErrorWithCode(DiagnosticCode.ConversionMismatch, $"Cannot initialize '{name}' with {actual}; expected {type}", initializer.Location);
            value = ConstantValueOf(initializer);
        }
        else if (declaration.IsConst)
        {
            throw Error($"Const global variable '{name}' needs an initializer", declaration.Location, name.Length);
        }

        var symbol = new GlobalVariableSymbol(name, moduleName, type, declaration.IsConst, value, declaration);
        scope.Declare(name, new VariableSymbol(type, declaration.IsConst, declaration, false));
        _globalSymbols[declaration] = symbol;
        _globals.Add(symbol);
    }

    // The scope of a module's globals. A module that is not yet seen gets an empty scope, so its functions see no globals.
    private Scope ModuleScope(string moduleName)
    {
        if (!_moduleScopes.TryGetValue(moduleName, out var scope))
            _moduleScopes[moduleName] = scope = new Scope(null, MarkVariableUsed);
        return scope;
    }

    // The globals a function sees, given its module name. A class member's module name is "module.Class", so it maps to
    // the class's module.
    private Scope GlobalScopeOf(string functionModuleName)
    {
        var owner = _classesByName.Values.FirstOrDefault(cls => cls.QualifiedName == functionModuleName);
        return ModuleScope(owner?.ModuleName ?? functionModuleName);
    }

    // A reference to a module-level variable is recorded for the backend, which cannot tell it from a local by name.
    private void RecordGlobalReference(IdentifierExpression identifier, VariableSymbol symbol)
    {
        if (symbol.Declaration is GlobalVariableDeclaration declaration)
            _globalReferences[identifier] = _globalSymbols[declaration];
    }

    // An initializer may be a bool literal, a floating-point literal with an optional sign, or an integer constant
    // expression. Anything else, including a name, is not constant.
    private static bool IsConstantInitializer(Expression expression) =>
        expression is BooleanLiteralExpression or FloatingPointLiteralExpression
        || expression is UnaryExpression { Operator: TokenKind.Minus or TokenKind.Plus, Operand: FloatingPointLiteralExpression }
        || TryGetConstantInteger(expression, out _);

    // The starting value of a variable with a constant initializer: a bool, a double, or a BigInteger.
    private static object? ConstantValueOf(Expression expression) => expression switch
    {
        BooleanLiteralExpression boolean => boolean.Value,
        FloatingPointLiteralExpression floating => ParseFloatingPointLiteral(floating.Lexeme),
        UnaryExpression { Operator: TokenKind.Minus, Operand: FloatingPointLiteralExpression negated } =>
            -ParseFloatingPointLiteral(negated.Lexeme),
        UnaryExpression { Operator: TokenKind.Plus, Operand: FloatingPointLiteralExpression positive } =>
            ParseFloatingPointLiteral(positive.Lexeme),
        _ => TryGetConstantInteger(expression, out var integer) ? integer : null
    };

    // The value of a floating-point literal, without its f32 or f64 suffix.
    private static double ParseFloatingPointLiteral(string lexeme)
    {
        var digits = lexeme.EndsWith("f32", StringComparison.Ordinal) || lexeme.EndsWith("f64", StringComparison.Ordinal)
            ? lexeme[..^3]
            : lexeme;
        return double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
