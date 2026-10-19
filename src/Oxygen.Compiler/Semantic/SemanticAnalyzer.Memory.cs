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
            if (symbol.Declaration is VariableDeclarationStatement declaration && IsOwnedClass(targetType))
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
        else
        {
            targetType = AnalyzeExpression(statement.Target, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        }

        if (!IsOwnedClass(targetType))
            throw Error($"'delete' requires a reference to a class object, got {targetType}", statement.Target.Location);
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
