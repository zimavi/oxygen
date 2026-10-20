using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

// Function bodies: the per-function analysis, field initializers, constructor chains and blocks.
public sealed partial class SemanticAnalyzer
{
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
        // The loop, break and catch depths belong to the body being analysed too. A call to a generic function inside a loop or a
        // catch clause analyses that function's body in the middle of this one, so the depths are saved and restored with it.
        var outerFlow = SaveAndResetFlowContext();
        var outerLoopDepth = _loopDepth;
        var outerBreakableDepth = _breakableDepth;
        var outerCatchDepth = _catchDepth;
        try
        {
            AnalyzeFunctionBody(function, functions, moduleName, imports, expressionTypes, resolvedCalls, constructor);
        }
        finally
        {
            _loopDepth = outerLoopDepth;
            _breakableDepth = outerBreakableDepth;
            _catchDepth = outerCatchDepth;
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
        var globals = GlobalScopeOf(moduleName);
        var scope = new Scope(globals, MarkVariableUsed);
        var className = function.ModuleName[(function.ModuleName.LastIndexOf('.') + 1)..];
        if (_classesByName.TryGetValue(className, out var ownerClass))
        {
            var ownerTypeArguments = ownerClass.DeclaredTypeParameters
                .Select(parameter => _genericTypes.TryGetValue(parameter, out var type)
                    ? type
                    : SemanticType.GenericParameter(parameter))
                .ToArray();
            scope = new Scope(globals, MarkVariableUsed,
                SemanticType.Class(ownerClass.Name, ownerTypeArguments.Length == 0 ? null : ownerTypeArguments));
            foreach (var field in ownerClass.Fields)
            {
                // A private field belongs to the class that declares it, so a subclass cannot name it.
                if (field.Visibility == Visibility.Private && !IsDeclaredBy(field, ownerClass))
                    continue;
                scope.Declare(field.Name, new VariableSymbol(SubstituteGenericType(field.Type, _genericTypes),
                    field.IsConst, field.Declaration, false));
            }
        }
        _currentOwnerClass = _classesByName.GetValueOrDefault(className);
        _currentIsConstructor = constructor is not null;
        var parameterScope = new Scope(scope, MarkVariableUsed);
        // A constructor's initializer runs before the object's fields are initialized, so its arguments see the
        // parameters but neither the fields nor `this`.
        var initializerScope = new Scope(globals, MarkVariableUsed,
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
        _catchDepth = 0;
        if (constructor is not null && _currentOwnerClass is not null)
        {
            AnalyzeFieldInitializers(_currentOwnerClass, scope, functions, moduleName, imports,
                expressionTypes, resolvedCalls);
            AnalyzeConstructorInitializer(constructor, _currentOwnerClass, initializerScope, functions, moduleName,
                imports, expressionTypes, resolvedCalls);
        }
        AnalyzeBlock(function.Declaration.Body, parameterScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
        // A non-void function must not reach its end. Reachability follows every path, including switch fall-through,
        // breaks and loops that never end, so it decides this rather than whether the last statement returns.
        if (!function.ReturnType.IsVoid && _reachable)
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
        // A statement after one that returns, throws, breaks or continues, or after a loop that never ends, is never run.
        // It is reported once, and not again inside a block that the enclosing code already found unreachable.
        var entryReachable = _reachable;
        var reportedUnreachable = false;

        foreach (var statement in block.Statements)
        {
            if (!_reachable && entryReachable && !reportedUnreachable)
            {
                ReportWarning("Unreachable statement", statement.Location);
                reportedUnreachable = true;
            }

            hasReturn |= AnalyzeStatement(statement, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
        }

        return hasReturn;
    }

}
