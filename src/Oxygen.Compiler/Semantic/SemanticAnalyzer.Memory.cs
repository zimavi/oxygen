using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

// Memory model: delete tracking, automatic frees and leak warnings.
public sealed partial class SemanticAnalyzer
{
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
            if (symbol.Declaration is VariableDeclarationStatement declaration)
            {
                // Delete reads the variable, so a local that may still be unassigned is rejected, as a read of it is.
                CheckAssigned(declaration, identifier.Name, identifier.Location);
                if (IsOwnedClass(targetType))
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
        }
        else
        {
            targetType = AnalyzeExpression(statement.Target, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        }

        if (!IsOwnedClass(targetType))
            throw ErrorWithCode(DiagnosticCode.OperandRequirement, $"'delete' requires a reference to a class object, got {targetType}", statement.Target.Location);
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

}

// Stack objects. `T(...)` of a reference class builds its object in the current function, which destroys it when the function
// returns. Such an object must not outlive that function, so it cannot be returned, stored in a field or assigned to a variable
// of an outer scope. Use `new T(...)` for an object that must outlive its function.
public sealed partial class SemanticAnalyzer
{
    // The locals whose initializer is a stack object.
    private readonly HashSet<VariableDeclarationStatement> _stackObjectLocals = new(ReferenceEqualityComparer.Instance);

    // Whether an expression is a stack object: `T(...)` of a reference class, or a local that holds one.
    private bool IsStackObject(Expression expression, Scope scope) => expression switch
    {
        ClassConstructionExpression construction =>
            FindTypeName(construction.TypeName, construction.Location) is { } className
            && _classesByName.TryGetValue(className, out var cls) && !cls.IsValue,
        IdentifierExpression identifier =>
            scope.Lookup(identifier.Name, identifier.Location).Declaration is VariableDeclarationStatement declaration
            && _stackObjectLocals.Contains(declaration),
        _ => false
    };

    private void RequireStackObjectNotReturned(Expression value, Scope scope)
    {
        if (IsStackObject(value, scope))
            throw Error("A stack object cannot be returned, because it is destroyed when its function returns. " +
                "Create it with new to return it", value.Location);
    }

    private void RequireStackObjectStaysInFunction(AssignmentStatement assignment, Scope scope)
    {
        if (!IsStackObject(assignment.Value, scope))
            return;
        // A static field, named as `Class.field` or `Class<T>.field`, is a field too.
        if (assignment.Target is MemberAccessExpression or CallExpression)
            throw Error("A stack object cannot be stored in a field, because the field outlives its function. " +
                "Create it with new to store it", assignment.Value.Location);
        if (assignment.Target is IdentifierExpression target)
        {
            // A variable of an outer scope outlives the block that holds the stack object.
            var sourceDepth = assignment.Value is IdentifierExpression source
                ? scope.LookupDepth(source.Name, source.Location)
                : 0;
            if (scope.LookupDepth(target.Name, target.Location) > sourceDepth)
                throw Error("A stack object cannot be assigned to a variable of an outer scope, because it outlives its block. " +
                    "Create it with new to keep it", assignment.Value.Location);
        }
    }

    // A stack object passed to a parameter that may keep it would outlive its function. Each argument is checked against the
    // callee the call resolved to. The check is run after the call's own analysis, so the callee and binding are known.
    private void RequireStackArgumentsNotKept(CallExpression call, Scope scope, IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        if (!resolvedCalls.TryGetValue(call, out var callee))
            return;
        int[]? order = _argumentOrders.TryGetValue(call, out var bound)
            ? bound
            : HasNamedArguments(call.Arguments) ? null : Enumerable.Range(0, call.Arguments.Count).ToArray();
        if (order is null)
            return;
        for (var position = 0; position < order.Length && position < callee.Parameters.Count; position++)
        {
            if (order[position] == DefaultArgument)
                continue;
            var argument = ValueOf(call.Arguments[order[position]]);
            if (IsStackObject(argument, scope) && MayKeepParameter(callee, position))
                throw Error($"A stack object cannot be passed to '{callee.Name}', whose parameter '{callee.Parameters[position].Name}' " +
                    "may keep it after the call returns. Create it with new to pass it on", argument.Location);
        }
    }

    // Whether a function may keep a reference that it receives in parameter `position`. The answer is conservative: it is false
    // only when the body uses the parameter as the base of a field access (`p.field`, where the field is not a property) and
    // nowhere else. A virtual or interface method, an external function, and a call on the parameter are all treated as keeping it.
    private bool MayKeepParameter(FunctionSymbol callee, int position)
    {
        if (callee.IsExternal || callee.Declaration.IsVirtual || callee.Declaration.IsOverride
            || _interfacesByName.Values.Any(contract => contract.Methods.Any(method => ReferenceEquals(method.Declaration, callee.Declaration))))
            return true;
        var parameter = callee.Parameters[position];
        var fieldClass = parameter.Type.IsClass && _classesByName.TryGetValue(parameter.Type.StructName!, out var cls) ? cls : null;
        if (fieldClass is null)
            return true;
        var uses = EnumerateExpressions(callee.Declaration.Body).ToList();
        var fieldBases = uses.OfType<MemberAccessExpression>()
            .Where(member => member.Target is IdentifierExpression target && target.Name == parameter.Name
                && FindProperty(fieldClass, member.MemberName) is null)
            .Select(member => (Expression)member.Target)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        return uses.Any(use => use is IdentifierExpression identifier && identifier.Name == parameter.Name && !fieldBases.Contains(use))
            || uses.Any(use => use is CallExpression dotted && dotted.Name.StartsWith(parameter.Name + ".", StringComparison.Ordinal));
    }
}
