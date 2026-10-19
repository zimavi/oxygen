using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

// Definite assignment and reachability: the flow state that branches, loops and try statements join.
public sealed partial class SemanticAnalyzer
{
    // The assignment state at one point: the locals that may be unassigned on some path to it, and whether it is reached at all.
    // A path that returns, throws, breaks or continues does not reach the code after it, so its state is not joined there.
    private sealed record FlowState(HashSet<VariableDeclarationStatement> Unassigned, bool Reachable);

    private FlowState SnapshotFlow() =>
        new(new HashSet<VariableDeclarationStatement>(_unassignedLocals, ReferenceEqualityComparer.Instance), _reachable);

    // The flow state of a body being analyzed, kept while another body is analyzed inside it.
    private sealed record SavedFlowContext(
        HashSet<VariableDeclarationStatement> Unassigned,
        bool Reachable,
        Stack<List<FlowState>> BreakStates,
        Stack<(FlowState Start, List<FlowState> Ends)> SwitchFlows);

    private SavedFlowContext SaveAndResetFlowContext()
    {
        var saved = new SavedFlowContext(
            new HashSet<VariableDeclarationStatement>(_unassignedLocals, ReferenceEqualityComparer.Instance),
            _reachable, _breakStates, _switchFlows);
        _unassignedLocals.Clear();
        _reachable = true;
        _breakStates = new();
        _switchFlows = new();
        return saved;
    }

    private void RestoreFlowContext(SavedFlowContext saved)
    {
        _unassignedLocals.Clear();
        _unassignedLocals.UnionWith(saved.Unassigned);
        _reachable = saved.Reachable;
        _breakStates = saved.BreakStates;
        _switchFlows = saved.SwitchFlows;
    }

    private void RestoreFlow(FlowState state)
    {
        _unassignedLocals.Clear();
        _unassignedLocals.UnionWith(state.Unassigned);
        _reachable = state.Reachable;
    }

    private static FlowState UnreachableFlow() =>
        new(new HashSet<VariableDeclarationStatement>(ReferenceEqualityComparer.Instance), false);

    // Where two paths meet, a local is unassigned if it is unassigned on either path. An unreachable path adds nothing.
    private static FlowState JoinFlows(FlowState first, FlowState second)
    {
        if (!first.Reachable)
            return second;
        if (!second.Reachable)
            return first;
        return new FlowState(new HashSet<VariableDeclarationStatement>(first.Unassigned.Concat(second.Unassigned),
            ReferenceEqualityComparer.Instance), true);
    }

    private void CheckAssigned(VariableDeclarationStatement declaration, string name, SourceLocation location)
    {
        if (_unassignedLocals.Contains(declaration))
            throw Error($"Variable '{name}' may be read before it is assigned", location, name.Length);
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
