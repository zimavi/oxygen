using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

// Ownership of class objects (docs/memory.md). An object is owned by the local that holds it, by the temporary that an expression
// evaluates, or by the caller of a function that returns it. Every use of a reference is classified by its flow: a use that only reads
// the object (Borrow) leaves the owner in charge of it, and any use that may keep the reference elsewhere (Escape, Return, Assign) does
// not. The rules err towards a leak: a reference that may be kept is never freed by its owner, so an object is never freed while
// another reference to it is still used.
public sealed partial class SemanticAnalyzer
{
    private enum Flow
    {
        // The reference is read and dropped before its owner ends it.
        Borrow,
        // The reference is kept somewhere that outlives the expression: a variable, a field, an element, a copy, or a callee that may keep it.
        Escape,
        // The reference is returned, which moves it to the caller.
        Return,
        // The reference is assigned to a variable, which replaces the variable's previous reference.
        Assign,
        // The local is deleted, which frees its object. An owned local keeps its scope-end free behind a drop flag, so a delete does not
        // escape it; any other local is freed by the delete alone.
        Delete,
    }

    // A body that was analysed, with the source it came from (for the warnings of a manual function).
    private sealed record AnalyzedBody(FunctionSymbol Function, string? SourceName);

    private readonly List<AnalyzedBody> _analyzedBodies = [];

    // The local that each identifier refers to, recorded when the analysis resolves the identifier.
    private readonly Dictionary<IdentifierExpression, VariableDeclarationStatement> _localReferences =
        new(ReferenceEqualityComparer.Instance);

    // The flow of each expression of the bodies. An expression with no recorded flow escapes, which is the safe answer.
    private readonly Dictionary<Expression, Flow> _flows = new(ReferenceEqualityComparer.Instance);

    // The parameter that each identifier refers to, recorded when the analysis resolves it.
    // The variable that a method call names as its receiver, as in `node.remember(holder)`. The parser keeps that name in the call's name,
    // so the call has no receiver expression, and this identifier is what the flow pass reads as the receiver's use.
    private readonly Dictionary<CallExpression, Expression> _qualifiedReceivers = new(ReferenceEqualityComparer.Instance);

    // The local whose initializer is a call. The local holds the call's result, which may refer to the receiver of that call.
    private readonly Dictionary<CallExpression, VariableDeclarationStatement> _resultHolders = new(ReferenceEqualityComparer.Instance);

    // The holders whose result refers to each receiver: a local, a parameter, or `this` (the function declaration). The receiver must
    // outlive each holder, so it escapes when a holder escapes, or when the holder is not freed before a local receiver. Recomputed by
    // each flow round.
    private readonly Dictionary<object, HashSet<VariableDeclarationStatement>> _lifetimes = new(ReferenceEqualityComparer.Instance);

    // The parameters and `this` that a holder of their result makes escape. A caller's argument, or the receiver of a call, is kept.
    private readonly HashSet<object> _keptReceivers = new(ReferenceEqualityComparer.Instance);

    // The function whose body is being flowed. `this` in the body names this function as its receiver's owner.
    private FunctionDeclaration? _flowOwner;

    private readonly Dictionary<IdentifierExpression, ParameterDeclaration> _parameterReferences =
        new(ReferenceEqualityComparer.Instance);

    // Whether a function keeps a parameter, by the parameter's declaration. A parameter that is only read, or passed to a call that does not
    // keep it, is not kept.
    private readonly Dictionary<ParameterDeclaration, bool> _keepsParameter = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ParameterDeclaration> _keepsParameterInProgress = new(ReferenceEqualityComparer.Instance);

    // The functions whose every returned value is a new object, a call that returns one, or a local that holds one.
    private readonly HashSet<FunctionDeclaration> _returnsOwned = new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<FunctionDeclaration, bool> _keepsThis = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<FunctionDeclaration> _keepsThisInProgress = new(ReferenceEqualityComparer.Instance);

    // The new objects and calls that a function releases at the end of their full expression, because nothing keeps them.
    private readonly HashSet<Expression> _temporaryReleases = new(ReferenceEqualityComparer.Instance);

    // The for-in loops whose iterator is owned by the loop, which releases it when the loop ends.
    private readonly HashSet<ForInStatement> _ownedIterators = new(ReferenceEqualityComparer.Instance);

    private IDictionary<Expression, SemanticType> _ownershipTypes = new Dictionary<Expression, SemanticType>();
    private IDictionary<CallExpression, FunctionSymbol> _ownershipCalls = new Dictionary<CallExpression, FunctionSymbol>();

    // The bodies that the ownership pass reads: the final functions, after the aggregates are specialized, and the constructors and
    // destructors of the classes. A specialized class's members are copies with nodes of their own, which the backend emits and the calls
    // refer to, so the pass reads them here rather than the generic instance that the analysis visited.
    private void CollectFinalBodies(IEnumerable<FunctionSymbol> functions, IDictionary<FunctionSymbol, string?> sources)
    {
        _analyzedBodies.Clear();
        var seen = new HashSet<FunctionDeclaration>(ReferenceEqualityComparer.Instance);
        void Add(FunctionSymbol function)
        {
            if (!function.IsExternal && !function.IsGeneric && function.Declaration.Body is not null && seen.Add(function.Declaration))
            {
                sources.TryGetValue(function, out var source);
                _analyzedBodies.Add(new AnalyzedBody(function, source));
            }
        }

        foreach (var function in functions)
            Add(function);
        foreach (var cls in _classesByName.Values.Where(cls => !cls.IsGeneric))
        {
            foreach (var constructor in cls.Constructors.Where(item => !item.IsSynthesized))
                Add(CreateConstructorFunctionSymbol(cls.ModuleName, constructor));
            if (cls.Destructor is not null)
                Add(CreateDestructorFunctionSymbol(cls.ModuleName, cls.Destructor));
        }
    }

    // Classifies the flow of every reference in every collected body, decides which objects are owned, and reports the leaks of manual
    // functions. It runs after every body is collected, since a call's ownership depends on the body of the callee.
    private void CheckOwnership(
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        _ownershipTypes = expressionTypes;
        _ownershipCalls = resolvedCalls;
        // The flow of a receiver depends on which methods return owned objects, and those depend on the flows. Each round recomputes the
        // flows from the methods found so far. The set only grows, so the rounds end once a round finds no new method, and the flows of
        // that last round are computed with the final set.
        // The kept receivers grow in the same way, and a holder's escape decides them, so the rounds also repeat until they stop growing.
        int ownedMethods;
        int keptReceivers;
        do
        {
            ownedMethods = _returnsOwned.Count;
            keptReceivers = _keptReceivers.Count;
            _keepsThis.Clear();
            _keepsParameter.Clear();
            _lifetimes.Clear();
            foreach (var body in _analyzedBodies)
                FlowBody(body.Function.Declaration);
            ComputeReturnsOwned();
            foreach (var body in _analyzedBodies)
                ComputeBodyEscapes(body.Function.Declaration);
        } while (_returnsOwned.Count != ownedMethods || _keptReceivers.Count != keptReceivers);
        foreach (var body in _analyzedBodies)
            ClassifyBody(body);
    }

    // ----- Flow -----

    private void FlowBody(FunctionDeclaration function)
    {
        var previous = _flowOwner;
        _flowOwner = function;
        FlowStatement(function.Body);
        _flowOwner = previous;
    }

    private void FlowStatement(Statement statement)
    {
        switch (statement)
        {
            case BlockStatement block:
                foreach (var item in block.Statements)
                    FlowStatement(item);
                break;
            case VariableDeclarationStatement declaration when declaration.Initializer is not null:
                if (declaration.Initializer is CallExpression initializerCall)
                    _resultHolders[initializerCall] = declaration;
                FlowExpression(declaration.Initializer, Flow.Escape);
                break;
            case AssignmentStatement assignment:
                FlowAssignmentTarget(assignment.Target);
                FlowExpression(assignment.Value, Flow.Escape);
                break;
            case ReturnStatement returned when returned.Value is not null:
                FlowExpression(returned.Value, Flow.Return);
                break;
            case ExpressionStatement expression:
                FlowExpression(expression.Expression, Flow.Borrow);
                break;
            case IfStatement conditional:
                FlowExpression(conditional.Condition, Flow.Borrow);
                FlowStatement(conditional.Then);
                if (conditional.Else is not null)
                    FlowStatement(conditional.Else);
                break;
            case WhileStatement loop:
                FlowExpression(loop.Condition, Flow.Borrow);
                FlowStatement(loop.Body);
                break;
            case ForStatement loop:
                if (loop.Initializer is not null)
                    FlowStatement(loop.Initializer);
                if (loop.Condition is not null)
                    FlowExpression(loop.Condition, Flow.Borrow);
                if (loop.Increment is not null)
                    FlowStatement(loop.Increment);
                FlowStatement(loop.Body);
                break;
            case ForInStatement loop:
                // The loop's iterator refers to the collection while the loop runs. A local collection outlives the loop, so it is only
                // read. A temporary collection is kept, so it is never released while the iterator refers to it.
                FlowExpression(loop.Collection, loop.Collection is IdentifierExpression ? Flow.Borrow : Flow.Escape);
                FlowStatement(loop.Body);
                break;
            case SwitchStatement switchStatement:
                FlowExpression(switchStatement.Value, Flow.Escape);
                foreach (var switchCase in switchStatement.Cases)
                {
                    if (switchCase.Value is not null)
                        FlowExpression(switchCase.Value, Flow.Borrow);
                    FlowStatement(switchCase.Body);
                }
                break;
            case TryStatement tryStatement:
                FlowStatement(tryStatement.Body);
                foreach (var clause in tryStatement.Catches)
                    FlowStatement(clause.Body);
                if (tryStatement.Finally is not null)
                    FlowStatement(tryStatement.Finally);
                break;
            case ThrowStatement throwStatement when throwStatement.Value is not null:
                FlowExpression(throwStatement.Value, Flow.Escape);
                break;
            case DeleteStatement deleteStatement:
                FlowExpression(deleteStatement.Target, deleteStatement.Target is IdentifierExpression ? Flow.Delete : Flow.Escape);
                break;
        }
    }

    private void FlowAssignmentTarget(Expression target)
    {
        switch (target)
        {
            case IdentifierExpression identifier:
                _flows[identifier] = Flow.Assign;
                break;
            case MemberAccessExpression member:
                // Writing a field reads the object it belongs to. A property setter receives the object, so it may keep it.
                FlowExpression(member.Target, IsPropertyAccess(member) ? Flow.Escape : Flow.Borrow);
                _flows[member] = Flow.Borrow;
                break;
            case IndexExpression element:
                FlowExpression(element.Target, Flow.Borrow);
                FlowExpression(element.Index, Flow.Borrow);
                _flows[element] = Flow.Borrow;
                break;
            default:
                FlowExpression(target, Flow.Escape);
                break;
        }
    }

    private void FlowExpression(Expression expression, Flow flow)
    {
        _flows[expression] = flow;
        switch (expression)
        {
            case CallExpression call:
                FlowCall(call, flow);
                break;
            case NewExpression construction:
                foreach (var argument in construction.Arguments)
                    FlowExpression(ValueOf(argument), Flow.Escape);
                break;
            case ClassConstructionExpression construction:
                foreach (var argument in construction.Arguments)
                    FlowExpression(ValueOf(argument), Flow.Escape);
                break;
            case MemberAccessExpression member:
                // A field that holds a class value is an alias of an object the target owns, so the target has the flow of that alias.
                FlowExpression(member.Target, IsPropertyAccess(member) ? Flow.Escape : IsPlainResult(member) ? Flow.Borrow : flow);
                break;
            case BinaryExpression binary:
                FlowExpression(binary.Left, Flow.Borrow);
                FlowExpression(binary.Right, Flow.Borrow);
                break;
            case UnaryExpression unary:
                FlowExpression(unary.Operand, unary.Operator is TokenKind.Ampersand or TokenKind.Star ? Flow.Escape : Flow.Borrow);
                break;
            case IsExpression typeTest:
                // A binding of the test names the object, so the test keeps it.
                FlowExpression(typeTest.Operand, typeTest.BindingName is null ? Flow.Borrow : Flow.Escape);
                break;
            case CastExpression cast:
                FlowExpression(cast.Operand, Flow.Escape);
                break;
            case ConditionalExpression conditional:
                FlowExpression(conditional.Condition, Flow.Borrow);
                FlowExpression(conditional.WhenTrue, Flow.Escape);
                FlowExpression(conditional.WhenFalse, Flow.Escape);
                break;
            case NamedArgumentExpression named:
                FlowExpression(named.Value, Flow.Escape);
                break;
            case LengthExpression length:
                FlowExpression(length.Target, Flow.Borrow);
                break;
            case SizeOfExpression sizeOf when sizeOf.Operand is not null:
                FlowExpression(sizeOf.Operand, Flow.Borrow);
                break;
            case IndexExpression element:
                FlowExpression(element.Target, Flow.Borrow);
                FlowExpression(element.Index, Flow.Borrow);
                break;
            case ArrayLiteralExpression literal:
                foreach (var element in literal.Elements)
                    FlowExpression(element, Flow.Escape);
                break;
            case StructLiteralExpression literal:
                foreach (var field in literal.Fields)
                    FlowExpression(field.Value, Flow.Escape);
                break;
            case UnionConstructionExpression union:
                foreach (var argument in union.Arguments)
                    FlowExpression(argument, Flow.Escape);
                break;
            case IndirectCallExpression indirect:
                FlowExpression(indirect.Callee, Flow.Escape);
                foreach (var argument in indirect.Arguments)
                    FlowExpression(argument, Flow.Escape);
                break;
        }
    }

    private void FlowCall(CallExpression call, Flow flow)
    {
        FunctionSymbol? callee = _ownershipCalls.TryGetValue(call, out var resolved) ? resolved : null;
        Expression? receiver = call.Receiver ?? (_qualifiedReceivers.TryGetValue(call, out var named) ? named : null);
        if (receiver is not null)
            FlowExpression(receiver, ReceiverFlow(call, callee, flow, receiver));
        var positions = ParameterPositions(call, callee);
        for (var index = 0; index < call.Arguments.Count; index++)
        {
            var position = positions[index];
            var argumentFlow = callee is not null && position >= 0 && !KeepsParameter(callee, position) ? Flow.Borrow : Flow.Escape;
            FlowExpression(ValueOf(call.Arguments[index]), argumentFlow);
        }
    }

    // Whether the callee keeps the reference that it receives in a parameter, after the flows of its body are known. A use of the
    // parameter that is not a read or a use by a call that does not keep it may keep the reference. A use that the flows cannot name
    // (a copy of the body) keeps it too.
    private bool KeepsParameter(FunctionSymbol callee, int position)
    {
        if (callee.IsExternal || callee.Declaration.IsVirtual || callee.Declaration.IsOverride || IsInterfaceMethod(callee))
            return true;
        if (!_analyzedBodies.Any(body => ReferenceEquals(body.Function.Declaration, callee.Declaration)))
            return true;
        var parameter = callee.Parameters[position];
        var declaration = parameter.Declaration;
        if (_keepsParameter.TryGetValue(declaration, out var known))
            return known;
        if (_keptReceivers.Contains(declaration))
            return true;
        if (!_keepsParameterInProgress.Add(declaration))
            return true;

        FlowBody(callee.Declaration);
        var keeps = ExpressionsWithReceivers(StatementsWithin(callee.Declaration.Body))
            .OfType<IdentifierExpression>()
            .Where(identifier => identifier.Name == parameter.Name)
            .Any(identifier => _parameterReferences.TryGetValue(identifier, out var referenced)
                ? referenced == declaration && _flows.GetValueOrDefault(identifier, Flow.Escape) != Flow.Borrow
                : !_localReferences.ContainsKey(identifier));
        _keepsParameterInProgress.Remove(declaration);
        _keepsParameter[declaration] = keeps;
        return keeps;
    }


    // The parameter that each argument fills, or -1 when it fills none (a defaulted parameter, or a call the analysis could not bind).
    private int[] ParameterPositions(CallExpression call, FunctionSymbol? callee)
    {
        var positions = Enumerable.Repeat(-1, call.Arguments.Count).ToArray();
        if (callee is null)
            return positions;
        if (_argumentOrders.TryGetValue(call, out var order))
        {
            for (var position = 0; position < order.Length; position++)
                if (order[position] != DefaultArgument && order[position] < positions.Length)
                    positions[order[position]] = position;
            return positions;
        }
        for (var index = 0; index < positions.Length && index < callee.Parameters.Count; index++)
            positions[index] = index;
        return positions;
    }

    // A receiver is read when the method does not keep its instance. A plain result, or an owned result (a new object that the caller
    // frees), cannot refer into the instance, so the receiver is only read. Any other result may be an alias of the instance, so the
    // receiver has the flow of that result.
    // A method that keeps its instance makes an owned result refer to it. The receiver then outlives the result when the result is only
    // read by its statement, or when a local holds it and the receiver is a local (see ClassifyBody). Anything else keeps the receiver.
    private Flow ReceiverFlow(CallExpression call, FunctionSymbol? callee, Flow flow, Expression receiver)
    {
        if (callee is null)
            return Flow.Escape;
        if (!ReceiverKeepsThis(call, callee))
            return IsPlainResult(call) || OwnedExpression(call) ? Flow.Borrow : flow;
        if (!OwnedExpression(call))
            return Flow.Escape;
        if (flow == Flow.Borrow)
            return Flow.Borrow;
        if (_resultHolders.TryGetValue(call, out var holder) && ReceiverOwner(receiver) is { } owner)
        {
            AddLifetime(owner, holder);
            return Flow.Borrow;
        }
        return Flow.Escape;
    }

    // The declaration that a receiver names, which must outlive the holder of the result: a local, a parameter, or `this` of the function
    // being flowed. Any other receiver names no declaration, so its result cannot be given a holder.
    private object? ReceiverOwner(Expression receiver) => receiver switch
    {
        IdentifierExpression identifier when _localReferences.TryGetValue(identifier, out var local) => local,
        IdentifierExpression identifier when _parameterReferences.TryGetValue(identifier, out var parameter) => parameter,
        ThisExpression => _flowOwner,
        _ => null
    };

    private void AddLifetime(object owner, VariableDeclarationStatement holder)
    {
        if (!_lifetimes.TryGetValue(owner, out var holders))
            _lifetimes[owner] = holders = new HashSet<VariableDeclarationStatement>(ReferenceEqualityComparer.Instance);
        holders.Add(holder);
    }

    // A plain result cannot refer into the object it came from.
    private bool IsPlainResult(Expression expression) =>
        _ownershipTypes.TryGetValue(expression, out var type) && IsPlainType(type);

    // A type whose values cannot refer to an object: a primitive, a string, an enum, or a union whose payloads are all such types. Depth
    // bounds the payloads that are followed, so a union that names itself is not plain.
    private bool IsPlainType(SemanticType type, int depth = 0)
    {
        if (type.Kind is SemanticTypeKind.Primitive or SemanticTypeKind.String or SemanticTypeKind.Enum)
            return true;
        // A union's name is kept in the enum-name slot of its type.
        if (!type.IsUnion || depth >= MaxPlainUnionDepth || type.EnumName is null || !_unionsByName.TryGetValue(type.EnumName, out var union))
            return false;
        var arguments = type.TypeArguments ?? [];
        var bindings = new Dictionary<string, SemanticType>(StringComparer.Ordinal);
        for (var index = 0; index < union.DeclaredTypeParameters.Count && index < arguments.Count; index++)
            bindings[union.DeclaredTypeParameters[index]] = arguments[index];
        return union.Cases.All(item => item.PayloadType is null
            || IsPlainType(SubstituteGenericType(item.PayloadType, bindings), depth + 1));
    }

    private const int MaxPlainUnionDepth = 8;

    private bool IsPropertyAccess(MemberAccessExpression member) =>
        _ownershipTypes.TryGetValue(member.Target, out var target) && IsPropertyOf(target, member.MemberName);

    // ----- Calls on an instance -----

    // Whether a method keeps its instance, when the call is on a receiver. A dispatched call may run any override, so every method of
    // that name that a class declares must not keep its instance.
    private bool ReceiverKeepsThis(CallExpression call, FunctionSymbol callee)
    {
        if (callee.CInterop is not null)
            return true;
        // An interface method has no body, so it is external, but its implementations are the bodies that run. No implementation found
        // means one may be outside this compilation, so the instance is kept.
        if (IsDispatchedCall(call, callee))
        {
            // A generic class is not run; its instances are specialized copies, which are the bodies that the program runs.
            var implementations = _classesByName.Values.Where(cls => !cls.IsGeneric).SelectMany(cls => cls.Methods).Where(method =>
                method.Name == callee.Name && method.Parameters.Count == callee.Parameters.Count).ToList();
            return implementations.Count == 0 || implementations.Any(method => KeepsThisBody(method.Declaration));
        }
        if (callee.IsExternal)
            return true;
        return KeepsThisBody(callee.Declaration);
    }

    // Whether a method body keeps its instance: it uses `this` in a place that outlives the call, or it calls a method on `this` that
    // keeps it. A body that was not analysed here, such as a library's method, may keep it.
    private bool KeepsThisBody(FunctionDeclaration declaration)
    {
        if (_keepsThis.TryGetValue(declaration, out var known))
            return known;
        if (!_analyzedBodies.Any(body => ReferenceEquals(body.Function.Declaration, declaration)))
            return true;
        if (!_keepsThisInProgress.Add(declaration))
            return true;

        FlowBody(declaration);
        var expressions = ExpressionsWithReceivers(StatementsWithin(declaration.Body)).ToList();
        var keeps = _keptReceivers.Contains(declaration)
            || expressions.Any(expression => expression is ThisExpression && _flows.GetValueOrDefault(expression, Flow.Escape) != Flow.Borrow)
            // A call on `this` without a recorded receiver is not flowed, so it is assumed to keep the instance.
            || expressions.OfType<CallExpression>().Any(call => call.Receiver is null && IsImplicitThisCall(call)
                && !_qualifiedReceivers.ContainsKey(call) && _ownershipCalls.TryGetValue(call, out var callee)
                && ReceiverKeepsThis(call, callee));
        _keepsThisInProgress.Remove(declaration);
        _keepsThis[declaration] = keeps;
        return keeps;
    }

    // `this.m()` and `base.m()` name their instance in the call's name, not in a receiver expression.
    private static bool IsImplicitThisCall(CallExpression call) =>
        call.Name.StartsWith("this.", StringComparison.Ordinal) || call.Name.StartsWith("base.", StringComparison.Ordinal);

    private bool IsDispatchedCall(CallExpression call, FunctionSymbol callee) =>
        !_baseCalls.Contains(call)
        && (callee.Declaration.IsVirtual || callee.Declaration.IsOverride || IsInterfaceMethod(callee));

    // ----- Owned values -----

    // Whether an expression produces a new object that its evaluation owns: a `new` of a reference class, or a call that returns one.
    private bool OwnedExpression(Expression expression) => expression switch
    {
        NewExpression => _ownershipTypes.TryGetValue(expression, out var created) && IsOwnedClass(created),
        CallExpression call => _ownershipCalls.TryGetValue(call, out var callee)
            && callee.CInterop is null && !callee.IsExternal
            && !IsDispatchedCall(call, callee)
            && _returnsOwned.Contains(callee.Declaration),
        _ => false
    };

    // A type whose values are references to objects that the owner frees: a class that is a reference, or an interface.
    private bool IsOwnedType(SemanticType type) => type.IsInterface || type.IsClass && IsOwnedClass(type);

    // The declared type of a local: the type of its initializer when it is inferred.
    private SemanticType? DeclaredTypeOf(VariableDeclarationStatement declaration)
    {
        if (declaration.Type.IsInferred)
            return declaration.Initializer is not null && _ownershipTypes.TryGetValue(declaration.Initializer, out var inferred)
                ? inferred
                : null;
        return _resolvedTypeSyntax.TryGetValue(declaration.Type, out var declared) ? declared : null;
    }

    // Whether a local is initialized with an owned object and never assigned, so that the local owns that object.
    private bool OwnsItsInitializer(VariableDeclarationStatement declaration, bool assigned)
    {
        if (assigned || declaration.Initializer is null || !OwnedExpression(declaration.Initializer))
            return false;
        return DeclaredTypeOf(declaration) is { } type && IsOwnedType(type);
    }

    // The functions that return an owned object: every returned value is null, a new object, a call that returns one, or a local that
    // owns its object and is used only in a way that does not keep it elsewhere. Starting from none, the set only grows, so a recursive
    // function returns owned values only when some path proves it.
    private void ComputeReturnsOwned()
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var body in _analyzedBodies)
            {
                var function = body.Function;
                if (_returnsOwned.Contains(function.Declaration) || function.ReturnType.IsVoid || !IsOwnedType(function.ReturnType))
                    continue;
                if (ReturnsOwnedValues(function.Declaration.Body))
                {
                    _returnsOwned.Add(function.Declaration);
                    changed = true;
                }
            }
        }
    }

    private bool ReturnsOwnedValues(BlockStatement body)
    {
        var statements = StatementsWithin(body).ToList();
        var returns = statements.OfType<ReturnStatement>().Where(item => item.Value is not null).ToList();
        if (returns.Count == 0)
            return false;
        foreach (var returned in returns)
        {
            var value = returned.Value!;
            if (value is NullLiteralExpression)
                continue;
            if (value is IdentifierExpression identifier)
            {
                if (!_localReferences.TryGetValue(identifier, out var declaration)
                    || !OwnsItsInitializer(declaration, AssignedIn(declaration, statements)) || !ReferencesOnlyForReturn(declaration, statements))
                    return false;
                continue;
            }
            if (!OwnedExpression(value))
                return false;
        }
        return true;
    }

    // Whether a local is assigned anywhere in the statements that use it.
    private bool AssignedIn(VariableDeclarationStatement declaration, IEnumerable<Statement> statements) =>
        References(statements).Any(reference => reference.Declaration == declaration && reference.Flow == Flow.Assign);

    // Whether every use of the local is a read or a return: a use that keeps the object elsewhere makes the return a second owner.
    private bool ReferencesOnlyForReturn(VariableDeclarationStatement declaration, IEnumerable<Statement> statements) =>
        References(statements).Where(reference => reference.Declaration == declaration)
            .All(reference => reference.Flow is Flow.Borrow or Flow.Return);

    // The expressions of the statements, each followed by the receiver variable that a qualified call names.
    private IEnumerable<Expression> ExpressionsWithReceivers(IEnumerable<Statement> statements)
    {
        foreach (var expression in statements.SelectMany(OwnExpressions).SelectMany(EnumerateExpressions))
        {
            yield return expression;
            if (expression is CallExpression call && _qualifiedReceivers.TryGetValue(call, out var receiver))
                yield return receiver;
        }
    }

    // A method call on a variable names the variable in the call's name, not in an expression. Recording it as an identifier use lets the
    // flow pass classify the variable the same way as a receiver expression.
    private void RecordQualifiedReceiver(CallExpression call, string receiverName, Scope scope)
    {
        // `this` and `base` name the instance of the function being analysed, which the receiver of the call is.
        if (receiverName is "this" or "base")
        {
            _qualifiedReceivers[call] = new ThisExpression(call.Location);
            return;
        }
        var identifier = new IdentifierExpression(receiverName, call.Location);
        _qualifiedReceivers[call] = identifier;
        var symbol = scope.Lookup(receiverName, call.Location);
        if (symbol.Declaration is VariableDeclarationStatement declaration)
            _localReferences[identifier] = declaration;
        else if (symbol.Declaration is ParameterDeclaration parameter)
            _parameterReferences[identifier] = parameter;
    }

    // Whether the receiver is declared in a block and the holder is declared later in that block, or in a statement nested in it. Locals
    // are freed in reverse declaration order, so the holder is freed first. A receiver declared in a for loop's initializer is not in a
    // block, so it is never proven to outlive a holder.
    private static bool Outlives(VariableDeclarationStatement receiver, VariableDeclarationStatement holder, IEnumerable<Statement> statements)
    {
        foreach (var block in statements.OfType<BlockStatement>())
        {
            for (var index = 0; index < block.Statements.Count; index++)
            {
                if (!ReferenceEquals(block.Statements[index], receiver))
                    continue;
                for (var later = index + 1; later < block.Statements.Count; later++)
                    if (StatementsWithin(block.Statements[later]).Any(item => ReferenceEquals(item, holder)))
                        return true;
            }
        }
        return false;
    }

    // The identifiers of the statements that refer to a local, with their flows. An identifier that the analysis did not resolve is
    // reported with no declaration, so the caller can match it by name.
    private IEnumerable<(VariableDeclarationStatement? Declaration, string Name, Flow Flow)> References(IEnumerable<Statement> statements)
    {
        foreach (var expression in ExpressionsWithReceivers(statements))
        {
            if (expression is not IdentifierExpression identifier)
                continue;
            var flow = _flows.GetValueOrDefault(identifier, Flow.Escape);
            yield return (_localReferences.GetValueOrDefault(identifier), identifier.Name, flow);
        }
    }

    // ----- Classification -----

    // Decides which locals of a body own their objects, which temporaries it releases, and which for-in loops own their iterators.
    // The locals of a function's body that escape, and whether each owns its object. A local's receiver escapes when a holder of the
    // receiver's call result escapes, is not owned, or is not freed before the receiver. Escaping locals can be holders of other
    // receivers, so this repeats until no receiver is added. A parameter or `this` with such a holder is kept (see KeepsParameter).
    private (HashSet<VariableDeclarationStatement> Escaping, Dictionary<VariableDeclarationStatement, bool> Owns) ComputeBodyEscapes(
        FunctionDeclaration function)
    {
        var statements = StatementsWithin(function.Body).ToList();
        var references = References(statements).ToList();
        var unresolved = references.Where(reference => reference.Declaration is null).Select(reference => reference.Name)
            .ToHashSet(StringComparer.Ordinal);
        var declarations = statements.OfType<VariableDeclarationStatement>().ToList();
        var declared = new HashSet<VariableDeclarationStatement>(declarations, ReferenceEqualityComparer.Instance);
        var escaping = new HashSet<VariableDeclarationStatement>(ReferenceEqualityComparer.Instance);
        var owns = new Dictionary<VariableDeclarationStatement, bool>(ReferenceEqualityComparer.Instance);
        foreach (var declaration in declarations)
        {
            var flows = references.Where(reference => reference.Declaration == declaration).Select(reference => reference.Flow).ToList();
            var assigned = flows.Contains(Flow.Assign);
            if (assigned || flows.Any(flow => flow is Flow.Escape or Flow.Return) || unresolved.Contains(declaration.Name))
                escaping.Add(declaration);
            owns[declaration] = OwnsItsInitializer(declaration, assigned);
        }
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (owner, holders) in _lifetimes)
            {
                if (owner is not VariableDeclarationStatement receiver || !declared.Contains(receiver) || escaping.Contains(receiver))
                    continue;
                if (holders.Any(holder => !declared.Contains(holder) || escaping.Contains(holder) || !owns[holder]
                    || !Outlives(receiver, holder, statements)))
                {
                    escaping.Add(receiver);
                    changed = true;
                }
            }
        }
        foreach (var (owner, holders) in _lifetimes)
        {
            if (owner is not VariableDeclarationStatement && holders.Any(holder => declared.Contains(holder)
                && (escaping.Contains(holder) || !owns[holder])))
                _keptReceivers.Add(owner);
        }
        return (escaping, owns);
    }

    private void ClassifyBody(AnalyzedBody body)
    {
        var function = body.Function;
        var automatic = function.Memory == MemoryMode.Automatic;
        var statements = StatementsWithin(function.Declaration.Body).ToList();
        var (escaping, owns) = ComputeBodyEscapes(function.Declaration);
        var deletes = References(statements).Where(reference => reference.Declaration is not null && reference.Flow == Flow.Delete)
            .Select(reference => reference.Declaration!).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var declaration in statements.OfType<VariableDeclarationStatement>())
        {
            var owned = owns[declaration];
            var isEscaping = escaping.Contains(declaration);
            if (isEscaping)
                _escapingClassLocals.Add(declaration);
            // A delete frees the local's object. For a local that does not own it, that delete is the only free, so the object escapes to it.
            if (deletes.Contains(declaration) && !owned)
                _escapingClassLocals.Add(declaration);
            if (automatic && owned && !isEscaping)
                _autoFreedLocals.Add(declaration);
            // A manual function frees nothing itself, so a `new` object it keeps for no one is leaked.
            if (!automatic && owned && !isEscaping && declaration.Initializer is NewExpression)
                ReportWarning($"Memory allocated for '{declaration.Name}' is never freed", declaration.Location, declaration.Name.Length,
                    body.SourceName);
        }

        if (!automatic)
            return;
        foreach (var expression in ExpressionsWithReceivers(statements))
        {
            if (expression is NewExpression or CallExpression && _flows.GetValueOrDefault(expression) == Flow.Borrow
                && OwnedExpression(expression))
                _temporaryReleases.Add(expression);
        }
        foreach (var loop in statements.OfType<ForInStatement>())
        {
            if (_iteratorProtocols.TryGetValue(loop, out var protocol) && !IsDispatchedCall(protocol.IterCall, protocol.Iter)
                && _returnsOwned.Contains(protocol.Iter.Declaration) && IsOwnedType(protocol.IteratorType))
                _ownedIterators.Add(loop);
        }
    }
}
