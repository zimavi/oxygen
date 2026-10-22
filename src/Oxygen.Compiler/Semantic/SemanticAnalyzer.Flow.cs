using System.Runtime.CompilerServices;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

// Definite assignment and reachability: the flow state that branches, loops and try statements join.
public sealed partial class SemanticAnalyzer
{
    // One part of a local that may be unassigned: the local itself (an empty path), or one leaf field of a struct local (`inner.x`).
    // Declarations are compared by identity, so two locals with one name are two different parts.
    private sealed class UnassignedSlot
    {
        public UnassignedSlot(VariableDeclarationStatement declaration, string path)
        {
            Declaration = declaration;
            Path = path;
        }

        public VariableDeclarationStatement Declaration { get; }

        public string Path { get; }

        public override bool Equals(object? obj) =>
            obj is UnassignedSlot other && ReferenceEquals(Declaration, other.Declaration) && Path == other.Path;

        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Declaration), Path);
    }

    // The assignment state at one point: the parts of locals that may be unassigned on some path to it, and whether it is reached at all.
    // A path that returns, throws, breaks or continues does not reach the code after it, so its state is not joined there.
    // Pointers are the pointer locals whose nullness is known at that point. See PointerNullness.
    private sealed record FlowState(HashSet<UnassignedSlot> Unassigned, bool Reachable, Dictionary<VariableSymbol, PointerNullness> Pointers);

    private FlowState SnapshotFlow() =>
        new(new HashSet<UnassignedSlot>(_unassignedParts), _reachable, new Dictionary<VariableSymbol, PointerNullness>(_pointerStates));

    // The flow state of a body being analyzed, kept while another body is analyzed inside it.
    private sealed record SavedFlowContext(
        HashSet<UnassignedSlot> Unassigned,
        bool Reachable,
        Dictionary<VariableSymbol, PointerNullness> Pointers,
        Stack<List<FlowState>> BreakStates,
        Stack<(FlowState Start, List<(int Index, FlowState State)> Ends)> SwitchFlows);

    private SavedFlowContext SaveAndResetFlowContext()
    {
        var saved = new SavedFlowContext(
            new HashSet<UnassignedSlot>(_unassignedParts),
            _reachable, new Dictionary<VariableSymbol, PointerNullness>(_pointerStates), _breakStates, _switchFlows);
        _unassignedParts.Clear();
        _pointerStates.Clear();
        _reachable = true;
        _breakStates = new();
        _switchFlows = new();
        return saved;
    }

    private void RestoreFlowContext(SavedFlowContext saved)
    {
        _unassignedParts.Clear();
        _unassignedParts.UnionWith(saved.Unassigned);
        _pointerStates.Clear();
        foreach (var (variable, nullness) in saved.Pointers)
            _pointerStates[variable] = nullness;
        _reachable = saved.Reachable;
        _breakStates = saved.BreakStates;
        _switchFlows = saved.SwitchFlows;
    }

    private void RestoreFlow(FlowState state)
    {
        _unassignedParts.Clear();
        _unassignedParts.UnionWith(state.Unassigned);
        _pointerStates.Clear();
        foreach (var (variable, nullness) in state.Pointers)
            _pointerStates[variable] = nullness;
        _reachable = state.Reachable;
    }

    private static FlowState UnreachableFlow() =>
        new(new HashSet<UnassignedSlot>(), false, new Dictionary<VariableSymbol, PointerNullness>());

    // Where two paths meet, a part is unassigned if it is unassigned on either path, and a pointer keeps its nullness only if both paths
    // agree on it. An unreachable path adds nothing.
    private static FlowState JoinFlows(FlowState first, FlowState second)
    {
        if (!first.Reachable)
            return second;
        if (!second.Reachable)
            return first;
        return new FlowState(new HashSet<UnassignedSlot>(first.Unassigned.Concat(second.Unassigned)), true,
            AgreedPointers(first.Pointers, second.Pointers));
    }

    // The pointers whose nullness both maps give the same value.
    private static Dictionary<VariableSymbol, PointerNullness> AgreedPointers(
        Dictionary<VariableSymbol, PointerNullness> first, Dictionary<VariableSymbol, PointerNullness> second)
    {
        var agreed = new Dictionary<VariableSymbol, PointerNullness>();
        foreach (var (variable, nullness) in first)
        {
            if (second.TryGetValue(variable, out var other) && other == nullness)
                agreed[variable] = nullness;
        }
        return agreed;
    }

    // A loop or try statement may run its body again, or enter a catch or finally from anywhere in its body. A pointer that the
    // statement changes is therefore not known when the statement starts, so it is forgotten there. Changes are assignments and
    // increments, anywhere in the statement.
    private void ForgetPointerStatesModifiedIn(Statement statement, Scope scope)
    {
        foreach (var name in ModifiedNames(statement))
            if (TryLookupVariable(scope, name, statement.Location) is { } variable)
                _pointerStates.Remove(variable);
    }

    // The names of the variables that a statement changes: those it assigns, and those that an increment or decrement changes inside it.
    private static IEnumerable<string> ModifiedNames(Statement statement) =>
        AssignedNames(statement).Concat(
            EnumerateExpressions(statement)
                .OfType<UnaryExpression>()
                .Where(item => item.Operator is TokenKind.PlusPlus or TokenKind.MinusMinus && item.Operand is IdentifierExpression)
                .Select(item => ((IdentifierExpression)item.Operand).Name));

    // The names of the variables that a statement assigns, including the statements inside its blocks.
    private static IEnumerable<string> AssignedNames(Statement? statement) => statement switch
    {
        AssignmentStatement { Target: IdentifierExpression target } => [target.Name],
        BlockStatement block => block.Statements.SelectMany(AssignedNames),
        IfStatement conditional => AssignedNames(conditional.Then).Concat(AssignedNames(conditional.Else)),
        WhileStatement loop => AssignedNames(loop.Body),
        ForStatement loop => AssignedNames(loop.Initializer).Concat(AssignedNames(loop.Increment)).Concat(AssignedNames(loop.Body)),
        ForInStatement loop => AssignedNames(loop.Body),
        TryStatement tryStatement => AssignedNames(tryStatement.Body)
            .Concat(tryStatement.Catches.SelectMany(item => AssignedNames(item.Body)))
            .Concat(AssignedNames(tryStatement.Finally)),
        SwitchStatement switchStatement => switchStatement.Cases.SelectMany(item => AssignedNames(item.Body)),
        _ => []
    };

    // A read of a whole local needs all of it to be assigned.
    private void CheckAssigned(VariableDeclarationStatement declaration, string name, SourceLocation location) =>
        CheckPartsAssigned(declaration, name, "", location, name.Length);

    // A read of the part at `path` (empty for the whole local) needs every part it covers to be assigned: the parts at or below
    // the path, and an unassigned ancestor of it.
    private void CheckPartsAssigned(VariableDeclarationStatement declaration, string name, string path, SourceLocation location, int length)
    {
        var unassigned = _unassignedParts
            .Where(slot => ReferenceEquals(slot.Declaration, declaration) && (IsWithinPath(slot.Path, path) || IsWithinPath(path, slot.Path)))
            .OrderBy(slot => slot.Path, StringComparer.Ordinal)
            .FirstOrDefault();
        if (unassigned is not null)
            throw Error($"Variable '{PartName(name, unassigned.Path)}' may be read before it is assigned", location, length);
    }

    // A write to the part at `path` assigns every part under it.
    private void ClearAssignedParts(VariableDeclarationStatement declaration, string path) =>
        _unassignedParts.RemoveWhere(slot => ReferenceEquals(slot.Declaration, declaration) && IsWithinPath(slot.Path, path));

    // A local declared without an initializer starts with every one of its leaf parts unassigned.
    private void AddUnassignedParts(VariableDeclarationStatement declaration, SemanticType type)
    {
        foreach (var path in LeafPaths(type, ""))
            _unassignedParts.Add(new UnassignedSlot(declaration, path));
    }

    // The leaf paths of a value: a struct contributes its fields, a nested struct its own leaves, and anything else is one leaf.
    private IEnumerable<string> LeafPaths(SemanticType type, string prefix)
    {
        if (!type.IsStruct)
        {
            yield return prefix;
            yield break;
        }
        foreach (var field in _structsByName[type.StructName!].Fields)
        {
            foreach (var path in LeafPaths(field.Type, JoinPath(prefix, field.Name)))
                yield return path;
        }
    }

    // Whether `path` is `ancestor` or lies under it. The empty path is the whole local.
    private static bool IsWithinPath(string path, string ancestor) =>
        ancestor.Length == 0 || path == ancestor || path.StartsWith(ancestor + ".", StringComparison.Ordinal);

    // The path of a field under a path, where the empty path is the whole local.
    private static string JoinPath(string path, string field) => path.Length == 0 ? field : $"{path}.{field}";

    // The name of a part for a diagnostic: the local, or the local and its field path.
    private static string PartName(string name, string path) => path.Length == 0 ? name : $"{name}.{path}";

    // The place that a member chain names inside a struct local: the local, then fields through struct values. Walked are the
    // expressions below the place whose values are structs. They are part of the place's path, so they are not read on their own.
    private sealed record StructPlace(VariableDeclarationStatement Declaration, string Name, string Path, SemanticType Type,
        IReadOnlyList<Expression> Walked);

    // The struct place of an expression, or null when it is not a field path of a struct local.
    private StructPlace? ResolveStructPlace(Expression expression, Scope scope)
    {
        switch (expression)
        {
            case IdentifierExpression identifier
                when TryLookupVariable(scope, identifier.Name, identifier.Location) is { Declaration: VariableDeclarationStatement declaration, Type.IsStruct: true } symbol:
                return new StructPlace(declaration, identifier.Name, "", symbol.Type, []);
            case MemberAccessExpression member
                when ResolveStructPlace(member.Target, scope) is { Type.IsStruct: true } owner
                    && _structsByName[owner.Type.StructName!].Fields.FirstOrDefault(field => field.Name == member.MemberName) is { } field:
                return owner with
                {
                    Path = JoinPath(owner.Path, field.Name),
                    Type = field.Type,
                    Walked = [.. owner.Walked, member.Target],
                };
            default:
                return null;
        }
    }

    // The nodes of a struct place's walk that are not checked on their own, added for the walk and returned so they can be removed.
    private List<Expression> StartStructWalk(Expression target, Scope scope)
    {
        if (ResolveStructPlace(target, scope) is not { Type.IsStruct: true } place)
            return [];
        return place.Walked.Append(target).Where(_skippedPartReads.Add).ToList();
    }

    private void EndStructWalk(List<Expression> nodes)
    {
        foreach (var node in nodes)
            _skippedPartReads.Remove(node);
    }

    // After a loop: the path where the condition ends it, which does not exist when the condition is always true, joined with
    // every break. The body's own end state does not count, because the body may run zero times.
    private static FlowState AfterLoopFlow(FlowState beforeLoop, List<FlowState> breaks, bool alwaysTrue)
    {
        var after = alwaysTrue ? UnreachableFlow() : beforeLoop;
        foreach (var state in breaks)
            after = JoinFlows(after, state);
        return after;
    }

}
