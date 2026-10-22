using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

// Function pointers: `&name` of a function, lambdas, and calls through a value of function pointer type. A call is checked for null like a
// dereference of a pointer (see SemanticAnalyzer.Pointers.cs), so the callee is classified the same way.
public sealed partial class SemanticAnalyzer
{
    // The callee of each call through a function pointer, keyed by the call node (a CallExpression or an IndirectCallExpression).
    private readonly Dictionary<Expression, Expression> _indirectCallees = new(ReferenceEqualityComparer.Instance);

    // The function that each `&name` names, keyed by the reference's unary node.
    private readonly Dictionary<Expression, FunctionSymbol> _functionReferences = new(ReferenceEqualityComparer.Instance);

    // The function that each lambda is compiled to, keyed by the lambda node.
    private readonly Dictionary<LambdaExpression, FunctionSymbol> _lambdaFunctions = new(ReferenceEqualityComparer.Instance);

    // Lambdas are numbered over the whole analysis, so each one has its own native name even when a generic body that holds one is instantiated
    // more than once.
    private int _lambdaCount;

    // The functions that a name denotes, which can be called or have their address taken: no generic function (which needs type arguments), and
    // no method of a class (which needs an object).
    private List<FunctionSymbol> FunctionValueCandidates(
        string name, string moduleName, IReadOnlyList<ImportDeclaration> imports, IReadOnlyDictionary<string, List<FunctionSymbol>> functions) =>
        ResolveFunctions(name, moduleName, imports, functions).Where(function => !IsClassMethodSymbol(function)).ToList();

    // Whether a name in an expression is a function and not a variable, so `&name` takes the address of the function.
    private bool IsFunctionName(
        IdentifierExpression name, Scope scope, string moduleName, IReadOnlyList<ImportDeclaration> imports,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions) =>
        LookupVariableType(scope, name.Name, name.Location) is null
        && FunctionValueCandidates(name.Name, moduleName, imports, functions).Count > 0;

    // Whether an argument is the address of a name with more than one usable overload, which the callee's parameter must choose (see AnalyzeCall).
    private bool IsOverloadedFunctionReference(
        Expression argument, Scope scope, string moduleName, IReadOnlyList<ImportDeclaration> imports,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions) =>
        argument is UnaryExpression { Operator: TokenKind.Ampersand, Operand: IdentifierExpression name }
        && IsFunctionName(name, scope, moduleName, imports, functions)
        && FunctionValueCandidates(name.Name, moduleName, imports, functions)
            .Count(candidate => !candidate.IsGeneric && !HasDefaultParameter(candidate)) > 1;

    // `&name` of a function. The overload is chosen by the function pointer type that the value is given: the function whose signature is that
    // type. Without such a type, an overloaded name is an error.
    private SemanticType AnalyzeFunctionReference(
        UnaryExpression reference,
        IdentifierExpression name,
        SemanticType? expectedType,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions)
    {
        var candidates = FunctionValueCandidates(name.Name, moduleName, imports, functions);
        if (candidates.Any(candidate => candidate.IsGeneric) && candidates.All(candidate => candidate.IsGeneric))
            throw Error($"'{name.Name}' is a generic function; take its address with its type arguments, such as &{name.Name}<i32>", name.Location,
                name.Name.Length);
        return ChooseFunctionAddress(reference, name.Name, name.Location,
            candidates.Where(candidate => !candidate.IsGeneric).ToList(), expectedType);
    }

    // `&name<T>` of a generic function. The type arguments specialize the one generic overload of the name, which is then chosen like any other
    // address. Non-generic overloads of the name are not candidates, as for a call with explicit type arguments.
    private SemanticType AnalyzeFunctionAddress(
        FunctionAddressExpression address,
        Scope scope,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        SemanticType? expectedType)
    {
        if (LookupVariableType(scope, address.Name, address.Location) is not null)
            throw Error($"'{address.Name}' is a variable, not a function", address.Location, address.Name.Length);
        var candidates = FunctionValueCandidates(address.Name, moduleName, imports, functions);
        var generics = candidates.Where(candidate => candidate.IsGeneric).ToList();
        if (generics.Count == 0)
            throw Error($"'{address.Name}' is not a generic function, so it takes no type arguments", address.Location, address.Name.Length);
        if (generics.Count > 1)
            throw Error($"Function '{address.Name}' has ambiguous generic overloads", address.Location, address.Name.Length);

        var generic = generics[0];
        if (address.TypeArguments.Count != generic.DeclaredGenericParameters.Count)
            throw Error($"Function '{address.Name}' expects {generic.DeclaredGenericParameters.Count} type argument(s), got {address.TypeArguments.Count}",
                address.Location, address.Name.Length);
        var substitutions = generic.DeclaredGenericParameters
            .Select((typeName, index) => (typeName, type: ResolveType(address.TypeArguments[index])))
            .ToDictionary(item => item.typeName, item => item.type, StringComparer.Ordinal);
        var specialized = SpecializeFunction(generic, substitutions, functions, address.Location);
        AnalyzeSpecialization(specialized, generic, functions, imports, expressionTypes, resolvedCalls);
        return ChooseFunctionAddress(address, address.Name, address.Location, [specialized], expectedType);
    }

    // Chooses the function whose address a reference takes, among its usable candidates, and records it for the backend. The overload is the one
    // whose signature is the function pointer type that the value is given. Without such a type, an overloaded name is an error. A function with
    // default parameters has no address, since a function pointer type carries no defaults.
    private SemanticType ChooseFunctionAddress(
        Expression reference,
        string name,
        SourceLocation nameLocation,
        List<FunctionSymbol> usable,
        SemanticType? expectedType)
    {
        if (usable.Count > 0 && usable.All(HasDefaultParameter))
            throw Error($"'{name}' has default parameters, which a function pointer type cannot carry; pass every argument", nameLocation,
                name.Length);
        usable = usable.Where(candidate => !HasDefaultParameter(candidate)).ToList();
        if (expectedType is { IsFunctionPointer: true })
            usable = usable.Where(candidate => FunctionTypeOf(candidate) == expectedType).ToList();
        if (usable.Count == 0)
        {
            var wanted = expectedType is { IsFunctionPointer: true } ? $" with the signature {expectedType}" : string.Empty;
            throw Error($"No function '{name}'{wanted} can have its address taken here", reference.Location, name.Length);
        }
        if (usable.Count > 1)
            throw Error($"'{name}' names {usable.Count} functions; the function pointer type that the value is given must select one",
                reference.Location, name.Length);

        var function = usable[0];
        _usedFunctions.Add(function);
        _functionReferences[reference] = function;
        return FunctionTypeOf(function);
    }

    private static bool HasDefaultParameter(FunctionSymbol function) =>
        function.Parameters.Any(parameter => parameter.Declaration.DefaultValue is not null);

    // The function pointer type of a function's signature.
    private static SemanticType FunctionTypeOf(FunctionSymbol function) =>
        SemanticType.FunctionPointer(function.ReturnType, function.Parameters.Select(parameter => parameter.Type).ToArray());

    // A lambda without captures. Its body is analysed as a function of the module it is written in, so it sees the module's functions and globals
    // and none of the enclosing function's locals, parameters or fields. Its type is the function pointer type of its signature.
    private SemanticType AnalyzeLambda(
        LambdaExpression lambda,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var returnType = ResolveType(lambda.ReturnType);
        var parameters = new List<ParameterSymbol>();
        foreach (var parameter in lambda.Parameters)
        {
            if (parameters.Any(item => item.Name == parameter.Name))
                throw Error($"Lambda declares parameter '{parameter.Name}' more than once", parameter.Location, parameter.Name.Length);
            parameters.Add(new ParameterSymbol(parameter.Name,
                ResolveValueType(parameter.Type, "A lambda parameter", parameter.Location), parameter));
        }

        var module = TypeScopeModuleOf(moduleName);
        _lambdaCount++;
        var name = $"lambda#{_lambdaCount}";
        var declaration = new FunctionDeclaration(lambda.ReturnType, name, lambda.Parameters, lambda.Body, Visibility.Private, lambda.Location);
        var symbol = new FunctionSymbol(name, returnType, parameters, declaration, module, Visibility.Private);
        _functionSymbols?.Add(declaration, symbol);
        _usedFunctions.Add(symbol);
        _lambdaFunctions[lambda] = symbol;
        AnalyzeFunction(symbol, functions, module, imports, expressionTypes, resolvedCalls);
        return FunctionTypeOf(symbol);
    }

    // A call through a function pointer that is not a name: `ops[0](1, 2)`, `make()(1, 2)`.
    private SemanticType AnalyzeIndirectCallExpression(
        IndirectCallExpression call,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        return AnalyzeFunctionPointerCall(call, call.Callee, call.Arguments, call.Location, scope, functions, moduleName, imports,
            expressionTypes, resolvedCalls);
    }

    // The call of a function pointer value. The callee is analysed first, and it must have a function pointer type. Each argument converts to
    // its parameter type, with no named arguments. The callee is checked for null, like a dereference (see ClassifyDereference).
    private SemanticType AnalyzeFunctionPointerCall(
        Expression node,
        Expression callee,
        IReadOnlyList<Expression> arguments,
        SourceLocation location,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var calleeType = AnalyzeExpression(callee, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        if (!calleeType.IsFunctionPointer)
            throw ErrorWithCode(DiagnosticCode.OperandRequirement, $"Only a function pointer can be called, and this is {calleeType}", callee.Location);
        var parameterTypes = calleeType.FunctionParameterTypes;
        if (arguments.Count != parameterTypes.Count)
            throw ErrorWithCode(DiagnosticCode.ArgumentCount,
                $"A function pointer of type {calleeType} takes {parameterTypes.Count} argument(s), but received {arguments.Count}", location);
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] is NamedArgumentExpression)
                throw Error("A call through a function pointer takes its arguments by position, not by name", arguments[index].Location);
            var argumentType = AnalyzeExpression(arguments[index], scope, functions, moduleName, imports, expressionTypes, resolvedCalls,
                expectedType: parameterTypes[index]);
            if (!CanAssign(parameterTypes[index], arguments[index], argumentType))
                throw ErrorWithCode(DiagnosticCode.ConversionMismatch,
                    $"Argument {index + 1} of the function pointer call expects {parameterTypes[index]}, got {argumentType}",
                    arguments[index].Location);
            // A class object passed to a call that the callee may keep escapes, as it does for a direct call.
            if (ValueOf(arguments[index]) is IdentifierExpression identifier
                && IsOwnedClass(scope.Lookup(identifier.Name, identifier.Location).Type)
                && scope.Lookup(identifier.Name, identifier.Location).Declaration is VariableDeclarationStatement declaration)
                _escapingClassLocals.Add(declaration);
        }
        ClassifyDereference(callee, scope, callee.Location);
        _indirectCallees[node] = callee;
        return calleeType.FunctionReturnType;
    }

    // A field of function pointer type that a call names: `calc.op(1, 2)` calls the field when the receiver's class or struct has a field of that
    // name and no method of that name. The field is analysed through a member access of the receiver, which is kept for the backend.
    private SemanticType? TryFunctionPointerFieldCall(
        CallExpression call,
        Expression receiver,
        string memberName,
        SemanticType receiverType,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var fieldTypes = receiverType.IsClass && _classesByName.TryGetValue(receiverType.StructName!, out var cls)
            ? cls.Fields.Select(field => (field.Name, field.Type))
            : receiverType.IsStruct && _structsByName.TryGetValue(receiverType.StructName!, out var structure)
                ? structure.Fields.Select(field => (field.Name, field.Type))
                : [];
        if (!fieldTypes.Any(field => field.Name == memberName && field.Type.IsFunctionPointer))
            return null;
        var member = new MemberAccessExpression(receiver, memberName, call.Location);
        return AnalyzeFunctionPointerCall(call, member, call.Arguments, call.Location, scope, functions, moduleName, imports,
            expressionTypes, resolvedCalls);
    }

    // A call of a name that is a variable of function pointer type, when no function has the name. The callee is the variable.
    private SemanticType? TryFunctionPointerVariableCall(
        CallExpression call,
        bool hasFunction,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        if (LookupVariableType(scope, call.Name, call.Location) is not { IsFunctionPointer: true })
            return null;
        if (hasFunction)
            throw Error($"'{call.Name}' is both a function and a variable of function pointer type; rename one of them", call.Location,
                call.Name.Length);
        return AnalyzeFunctionPointerCall(call, new IdentifierExpression(call.Name, call.Location), call.Arguments, call.Location, scope,
            functions, moduleName, imports, expressionTypes, resolvedCalls);
    }
}
