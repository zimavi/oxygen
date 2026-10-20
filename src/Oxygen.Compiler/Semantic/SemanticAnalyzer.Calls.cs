using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

// Calls: overload resolution and the call expression.
public sealed partial class SemanticAnalyzer
{
    // The calls that a `Name(...)` expression stands for when Name is a function rather than a class.
    private readonly Dictionary<ClassConstructionExpression, CallExpression> _constructionCalls =
        new(ReferenceEqualityComparer.Instance);

    // The calls written as base.name(...). They are called directly, without virtual dispatch.
    private readonly HashSet<CallExpression> _baseCalls = new(ReferenceEqualityComparer.Instance);

    // The base class of the class whose member is being analysed. A base call is made on the same instance, as that class.
    private SemanticType BaseReceiverType(SourceLocation location)
    {
        var owner = _currentOwnerClass ?? throw Error("'base' is only valid inside a class member", location, 4);
        if (owner.BaseTypeName is null || !_classesByName.ContainsKey(owner.BaseTypeName))
            throw Error($"Class '{owner.Name}' has no base class, so 'base' cannot be used", location, 4);
        return SemanticType.Class(owner.BaseTypeName, null);
    }

    // The type of a variable named by a receiver, or null when no variable has that name.
    private static SemanticType? LookupVariableType(Scope scope, string name, SourceLocation location)
    {
        try
        {
            return scope.Lookup(name, location).Type;
        }
        catch (SemanticException)
        {
            return null;
        }
    }

    // A manual iter() call is rejected. Its result is an object that no variable frees: a local takes it without setting
    // its drop flag, which is set only for `new`, so the object would leak. For-in calls iter() itself and is not affected.
    // This is a temporary guard, removed once ownership covers interfaces and call results.
    private SemanticType AnalyzeCallExpression(
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
        var type = AnalyzeCall(call, scope, functions, moduleName, imports, expressionTypes, resolvedCalls, expectedType, allowVoid);
        if (resolvedCalls.TryGetValue(call, out var callee) && callee.Name == "iter" && callee.Parameters.Count == 0
            && (callee.ReturnType.IsClass || callee.ReturnType.IsInterface))
            throw Error("Calling iter() by hand is not supported yet, because its result would never be freed; iterate with for-in instead",
                call.Location);
        return type;
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
            // Explicit type arguments bind the parameters in order. Otherwise the payload binds them, and then the expected type
            // binds the parameters that the payload left open, such as `Option.None` where a return type is expected.
            var typeParameters = union.DeclaredTypeParameters;
            var bindings = new Dictionary<string, SemanticType>(StringComparer.Ordinal);
            for (var index = 0; index < call.ExplicitTypeArguments.Count && index < typeParameters.Count; index++)
                bindings[typeParameters[index]] = ResolveType(call.ExplicitTypeArguments[index]);
            var actual = item.PayloadType is null
                ? null
                : AnalyzeExpression(call.Arguments[0], scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
            if (call.ExplicitTypeArguments.Count == 0)
            {
                if (actual is not null && !InferFromArgument(item.PayloadType!, actual, typeParameters, bindings))
                    throw Error($"Union case '{item.Name}' expects {item.PayloadType}, got {actual}", call.Location);
                if (expectedType is not null)
                    BindFromExpectedType(
                        SemanticType.Union(union.Name, typeParameters.Select(SemanticType.GenericParameter).ToArray()),
                        expectedType, typeParameters, bindings);
            }

            var missing = typeParameters.FirstOrDefault(name => !bindings.ContainsKey(name));
            if (missing is not null)
                throw ErrorWithCode(DiagnosticCode.CannotInfer,
                    $"Cannot infer type parameter '{missing}' of union case '{item.Name}'; write its type arguments explicitly, such as {union.Name}<T>.{item.Name}(...)",
                    call.Location, item.Name.Length);
            if (actual is not null)
            {
                var payload = SubstituteGenericType(item.PayloadType!, bindings);
                if (!CanAssign(payload, call.Arguments[0], actual))
                    throw Error($"Union case '{item.Name}' expects {payload}, got {actual}", call.Location);
            }

            var unionArguments = typeParameters.Count == 0 ? null : typeParameters.Select(name => bindings[name]).ToArray();
            return SemanticType.Union(union.Name, unionArguments);
        }
        // `functionName` is the name the overloads are looked up by. It is the call name, except for a receiver chain that
        // names a module, where it is the whole dotted name, as in `std.io.print(...)`.
        var functionName = call.Name;
        if (call.Receiver is not null)
        {
            var qualifier = QualifiedName(call.Receiver);
            var head = qualifier?.Split('.')[0];
            // A receiver whose first name is a variable, or any other expression, is a value that the method is called on.
            if (head is null || LookupVariableType(scope, head, call.Location) is not null)
            {
                var receiverType = AnalyzeExpression(call.Receiver, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                return AnalyzeMethodCall(call, receiverType, call.Name, scope, functions, moduleName, imports, expressionTypes, resolvedCalls)
                    ?? throw ErrorWithCode(DiagnosticCode.NotDeclared,
                        $"Method '{call.Name}' is not declared for a receiver of type {receiverType}", call.Location, call.Name.Length);
            }
            functionName = $"{qualifier}.{call.Name}";
        }
        else if (call.Name.Contains('.', StringComparison.Ordinal))
        {
            var separator = call.Name.LastIndexOf('.');
            var receiverName = call.Name[..separator];
            var methodName = call.Name[(separator + 1)..];
            // `this` names the current instance and `base` the same instance seen as its base class. Neither is a variable.
            SemanticType? receiverType = receiverName switch
            {
                "this" => scope.LookupThis(call.Location),
                "base" => BaseReceiverType(call.Location),
                _ => LookupVariableType(scope, receiverName, call.Location)
            };
            if (receiverName == "base")
                _baseCalls.Add(call);
            if (receiverType is not null)
            {
                var methodResult = AnalyzeMethodCall(call, receiverType, methodName, scope, functions, moduleName, imports,
                    expressionTypes, resolvedCalls);
                if (methodResult is not null)
                    return methodResult;
            }
        }
        var overloads = ResolveFunctions(functionName, moduleName, imports, functions);
        if (overloads.Count == 0)
        {
            throw ErrorWithCode(DiagnosticCode.NotDeclared, $"Function '{functionName}' is not declared", call.Location, functionName.Length);
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
        string? inferenceProblem = null;
        if (call.ExplicitTypeArguments.Count != 0)
        {
            var generic = overloads.Where(candidate => candidate.IsGeneric).ToList();
            if (generic.Count == 0)
                throw Error($"Function '{functionName}' does not accept explicit type arguments", call.Location);
            if (generic.Count > 1)
                throw Error($"Function '{functionName}' has ambiguous generic overloads", call.Location);
            var genericFunction = generic[0];
            if (call.ExplicitTypeArguments.Count != genericFunction.DeclaredGenericParameters.Count)
                throw ErrorWithCode(DiagnosticCode.ArgumentCount, 
                    $"Function '{functionName}' expects {genericFunction.DeclaredGenericParameters.Count} type argument(s), got {call.ExplicitTypeArguments.Count}",
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
            var attempts = overloads
                .Where(candidate => candidate.IsGeneric && candidate.AcceptsArgumentCount(argumentTypes.Count))
                .Select(candidate => (candidate, substitutions: InferGenericSubstitutions(candidate, argumentTypes, expectedType,
                    out var problem), problem))
                .ToList();
            var inferred = attempts.Where(item => item.substitutions is not null).ToList();
            // When no generic overload is inferred, the reason from the first attempt is the one to report, if there is one.
            inferenceProblem = inferred.Count == 0 ? attempts.Select(item => item.problem).FirstOrDefault(text => text is not null) : null;
            // Specializations made by other calls are not candidates: the type arguments inferred here select the
            // specializations this call considers. Each generic overload the arguments fit is a candidate, and the ranking
            // below chooses among them. The expected return type does not choose, so overloads tied on their arguments
            // are ambiguous.
            overloads = overloads.Where(overload => !overload.IsSpecialized).ToList();
            foreach (var match in inferred)
            {
                var specialized = SpecializeFunction(match.candidate, match.substitutions!, functions, call.Location);
                AnalyzeSpecialization(specialized, match.candidate, functions, imports, expressionTypes, resolvedCalls);
                overloads = overloads.Append(specialized).ToList();
            }
        }
        var candidates = overloads
            .Where(function => function.AcceptsArgumentCount(argumentTypes.Count))
            .Where(function => !function.IsGeneric)
            .ToList();

        if (candidates.Count == 0)
        {
            if (inferenceProblem is not null)
                throw ErrorWithCode(DiagnosticCode.CannotInfer, inferenceProblem, call.Location, functionName.Length);
            if (overloads.Count == 1)
            {
                throw ErrorWithCode(DiagnosticCode.ArgumentCount, 
                    $"Function '{functionName}' expects {overloads[0].ArgumentCountDescription} argument(s), got {call.Arguments.Count}",
                    call.Location);
            }

            throw ErrorWithCode(DiagnosticCode.ArgumentCount, 
                $"Function '{functionName}' has no overload accepting {call.Arguments.Count} argument(s)",
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
            throw Error($"Function '{functionName}' is ambiguous; use a module prefix", call.Location, functionName.Length);
        if (matches.Count > 1)
            throw Error($"Function '{functionName}' is ambiguous: no overload is closer than the others", call.Location, functionName.Length);
        if (matches.Count == 0)
        {
            if (candidates.Count == 1)
            {
                var mismatchIndex = candidates[0].Parameters
                    .Select((parameter, index) => (parameter, index))
                    .First(pair => pair.parameter.Type != argumentTypes[pair.index]);
                throw Error(
                    $"Argument {mismatchIndex.index + 1} of '{functionName}' has type {argumentTypes[mismatchIndex.index]}; expected {mismatchIndex.parameter.Type}",
                    call.Arguments[mismatchIndex.index].Location);
            }

            throw Error($"No overload of '{functionName}' matches the argument types", call.Location);
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
            throw Error($"Void function '{functionName}' cannot be used as an expression", call.Location);
        }

        return function.ReturnType;
    }

    // Resolves a method call on a receiver of the given type. Returns null when the type has no method of that name,
    // so the caller can report the name or try another meaning of it.
    private SemanticType? AnalyzeMethodCall(
        CallExpression call,
        SemanticType receiverType,
        string methodName,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
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
            var methodRanked = methodCandidates.Where(method => method.AcceptsArgumentCount(methodArgumentTypes.Length))
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
            var methodMatches = methodCandidates.Where(method => method.AcceptsArgumentCount(methodArgumentTypes.Length))
                .Where(method => method.Parameters.Take(methodArgumentTypes.Length).Select((parameter, index) =>
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
                .Where(function => function.AcceptsArgumentCount(receiverArguments.Length + 1)
                    && function.Parameters[0].Type == receiverType)
                .Where(function => function.Parameters.Skip(1).Take(receiverArguments.Length).Select((parameter, index) =>
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
        return null;
    }

    // The dotted name of a receiver that is a chain of names, such as `a.b`, or null for any other expression.
    private static string? QualifiedName(Expression expression) => expression switch
    {
        IdentifierExpression identifier => identifier.Name,
        MemberAccessExpression member when QualifiedName(member.Target) is { } target => $"{target}.{member.MemberName}",
        _ => null
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

}
