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
        foreach (var body in _analyzedBodies)
            FlowBody(body.Function.Declaration.Body);
        ComputeReturnsOwned();
        foreach (var body in _analyzedBodies)
            ClassifyBody(body);
    }

    // ----- Flow -----

    private void FlowBody(BlockStatement body) => FlowStatement(body);

    private void FlowStatement(Statement statement)
    {
        switch (statement)
        {
            case BlockStatement block:
                foreach (var item in block.Statements)
                    FlowStatement(item);
                break;
            case VariableDeclarationStatement declaration when declaration.Initializer is not null:
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
                // The loop's iterator refers to the collection while the loop runs. A local collection is only read, unless the iterator
                // keeps it: its iter() then stores the collection, so the collection escapes and is released by count. A temporary
                // collection is kept, so it is never released while the iterator refers to it.
                var iteratorKeepsCollection = !_iteratorProtocols.TryGetValue(loop, out var loopProtocol)
                    || ReceiverKeepsThis(loopProtocol.IterCall, loopProtocol.Iter);
                FlowExpression(loop.Collection, loop.Collection is IdentifierExpression && !iteratorKeepsCollection ? Flow.Borrow : Flow.Escape);
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
        if (!_keepsParameterInProgress.Add(declaration))
            return true;

        FlowBody(callee.Declaration.Body);
        var keeps = ExpressionsWithReceivers(StatementsWithin(callee.Declaration.Body))
            .OfType<IdentifierExpression>()
            .Where(identifier => identifier.Name == parameter.Name)
            .Any(identifier => _parameterReferences.TryGetValue(identifier, out var referenced)
                // A specialized copy of the body refers to the parameter declarations of the body it was copied from, so a parameter is
                // matched by its name: the names of a function's parameters are unique, and the body sees no other parameter of that name.
                ? referenced.Name == parameter.Name && _flows.GetValueOrDefault(identifier, Flow.Escape) != Flow.Borrow
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
    // A method that keeps its instance may store it, so the receiver escapes and its owner releases it by count. A result that is only
    // read by its statement is a temporary released there, before the receiver's owner can release the receiver.
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
        // The result may hold a count of the receiver, so the receiver is released by count and not freed directly.
        return Flow.Escape;
    }

    // Whether an expression is a union value, which a case constructs and keeps its payload in.
    private bool IsUnionValue(Expression expression) =>
        _ownershipTypes.TryGetValue(expression, out var type) && type.IsUnion;

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

        FlowBody(declaration.Body);
        var expressions = ExpressionsWithReceivers(StatementsWithin(declaration.Body)).ToList();
        var keeps = expressions.Any(expression => expression is ThisExpression && _flows.GetValueOrDefault(expression, Flow.Escape) != Flow.Borrow)
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
        // A call that returns a class or an interface gives its caller one count. Every such function returns counted references
        // (docs/reference-counting.md), so the result is owned whether the call is dispatched or not.
        CallExpression call => _ownershipCalls.TryGetValue(call, out var callee)
            && callee.CInterop is null && !callee.IsExternal
            && _ownershipTypes.TryGetValue(call, out var returned) && IsOwnedType(returned),
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
    // Every function that returns a class or an interface returns a counted reference: a returned owned value moves its count, and any
    // other returned reference is retained first (docs/reference-counting.md, step 2a).
    private void ComputeReturnsOwned()
    {
        foreach (var body in _analyzedBodies)
        {
            var function = body.Function;
            if (!function.ReturnType.IsVoid && IsOwnedType(function.ReturnType))
                _returnsOwned.Add(function.Declaration);
        }
    }

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
    // The escape facts of a body's locals: which escape, which own their object, which are returned (moved to the caller), and which
    // have a name the analysis did not resolve.
    private sealed record BodyEscapes(
        HashSet<VariableDeclarationStatement> Escaping,
        Dictionary<VariableDeclarationStatement, bool> Owns,
        HashSet<VariableDeclarationStatement> Returned,
        HashSet<VariableDeclarationStatement> Uncertain);

    private BodyEscapes ComputeBodyEscapes(FunctionDeclaration function)
    {
        var statements = StatementsWithin(function.Body).ToList();
        var references = References(statements).ToList();
        var unresolved = references.Where(reference => reference.Declaration is null).Select(reference => reference.Name)
            .ToHashSet(StringComparer.Ordinal);
        var declarations = statements.OfType<VariableDeclarationStatement>().ToList();
        var escaping = new HashSet<VariableDeclarationStatement>(ReferenceEqualityComparer.Instance);
        var owns = new Dictionary<VariableDeclarationStatement, bool>(ReferenceEqualityComparer.Instance);
        var returned = new HashSet<VariableDeclarationStatement>(ReferenceEqualityComparer.Instance);
        var uncertain = new HashSet<VariableDeclarationStatement>(ReferenceEqualityComparer.Instance);
        foreach (var declaration in declarations)
        {
            var flows = references.Where(reference => reference.Declaration == declaration).Select(reference => reference.Flow).ToList();
            var assigned = flows.Contains(Flow.Assign);
            if (assigned || flows.Any(flow => flow is Flow.Escape or Flow.Return) || unresolved.Contains(declaration.Name))
                escaping.Add(declaration);
            if (flows.Contains(Flow.Return))
                returned.Add(declaration);
            if (unresolved.Contains(declaration.Name))
                uncertain.Add(declaration);
            owns[declaration] = OwnsItsInitializer(declaration, assigned);
        }
        return new BodyEscapes(escaping, owns, returned, uncertain);
    }

    private void ClassifyBody(AnalyzedBody body)
    {
        var function = body.Function;
        var automatic = function.Memory == MemoryMode.Automatic;
        var statements = StatementsWithin(function.Declaration.Body).ToList();
        var escapes = ComputeBodyEscapes(function.Declaration);
        var deletes = References(statements).Where(reference => reference.Declaration is not null && reference.Flow == Flow.Delete)
            .Select(reference => reference.Declaration!).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var declaration in statements.OfType<VariableDeclarationStatement>())
        {
            var owned = escapes.Owns[declaration];
            var isEscaping = escapes.Escaping.Contains(declaration);
            if (owned)
                _ownedDeclarations.Add(declaration);
            if (isEscaping)
                _escapingClassLocals.Add(declaration);
            // A delete frees the local's object. For a local that does not own it, that delete is the only free, so the object escapes to it.
            if (deletes.Contains(declaration) && !owned)
                _escapingClassLocals.Add(declaration);
            // An object from `new` that nothing else refers to is freed directly. Any other owned object may have been retained by a
            // holder, so its owner releases its count instead. A returned local is moved to the caller, so its scope does not release it.
            if (automatic && owned && declaration.Initializer is NewExpression && !isEscaping)
                _autoFreedLocals.Add(declaration);
            else if (automatic && owned && !escapes.Returned.Contains(declaration) && !escapes.Uncertain.Contains(declaration))
                _releasedLocals.Add(declaration);
            // A manual function frees nothing itself, so a `new` object it keeps for no one is leaked.
            if (!automatic && owned && !isEscaping && declaration.Initializer is NewExpression)
                ReportWarning($"Memory allocated for '{declaration.Name}' is never freed", declaration.Location, declaration.Name.Length,
                    body.SourceName);
        }

        // A returned owned local moves its count to the caller, so its return does not retain it (docs/reference-counting.md).
        foreach (var returned in statements.OfType<ReturnStatement>())
        {
            if (returned.Value is IdentifierExpression returnedIdentifier && _localReferences.TryGetValue(returnedIdentifier, out var returnedLocal)
                && _ownedDeclarations.Contains(returnedLocal))
                _movedReturnValues.Add(returnedIdentifier);
        }

        if (!automatic)
            return;
        // A call or a constructor only borrows the temporaries it is given: the callee retains what it keeps. So an owned temporary that is
        // given to a call is released when its statement ends, whether or not the callee keeps it. A `new` object that a callee may keep is
        // released by count, since it may have been retained.
        foreach (var expression in ExpressionsWithReceivers(statements))
        {
            IEnumerable<Expression> borrowed = expression switch
            {
                // A union case built as `Option.Some(value)` is a call that names no function: its payload is moved into the union, not borrowed.
                CallExpression call when _ownershipCalls.ContainsKey(call) && !IsUnionValue(call) =>
                    call.Receiver is null ? call.Arguments : [call.Receiver, .. call.Arguments],
                CallExpression => [],
                NewExpression construction => construction.Arguments,
                // A union case is built as a class construction, and its payload is moved into the union, not borrowed.
                ClassConstructionExpression construction when !IsUnionValue(construction) => construction.Arguments,
                _ => []
            };
            foreach (var argument in borrowed)
            {
                var value = ValueOf(argument);
                if (value is not (NewExpression or CallExpression) || !OwnedExpression(value))
                    continue;
                _temporaryReleases.Add(value);
                if (value is NewExpression && _flows.GetValueOrDefault(value, Flow.Escape) == Flow.Escape)
                    _countedTemporaries.Add(value);
            }
        }
        foreach (var expression in ExpressionsWithReceivers(statements))
        {
            if (expression is NewExpression or CallExpression && _flows.GetValueOrDefault(expression, Flow.Escape) == Flow.Borrow
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
