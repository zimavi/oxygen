using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

// Exception handling for try statements on Windows (SEH funclets) and the finally clause, which every way out of a
// try statement must run. Returns, breaks and continues run the enclosing finally bodies inline before they leave.
public sealed partial class LlvmModuleGenerator
{
    // A try statement with a finally clause that is still being emitted. ContextDepth is the number of exception
    // contexts in effect outside the try statement, so a copy of its finally body can be routed past its own handlers.
    // LoopDepth is the number of enclosing loops and switches, which decides whether a break or continue crosses it.
    private sealed record FinallyScope(Statement Finally, int ContextDepth, int LoopDepth);

    private Stack<FinallyScope> _finallyScopes = new();

    private bool PushExceptionTarget(LLVMBasicBlockRef unwind)
    {
        if (unwind.Handle == IntPtr.Zero)
            return false;
        _exceptionContexts.Push(new ExceptionContext(unwind, default));
        return true;
    }

    // Emits one copy of a finally body as control leaves the try statement that owns `scope`. Exceptions in the copy
    // go to the handlers that enclose that try statement. Returns true when the copy itself returns or branches away.
    private bool EmitFinallyCopy(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMValueRef function,
        FinallyScope scope,
        Dictionary<string, LLVMValueRef> values,
        Dictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions,
        SemanticType emittedReturnType,
        Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)> loops)
    {
        var savedScopes = _finallyScopes.ToArray();
        var savedContexts = _exceptionContexts.ToArray();
        while (_finallyScopes.Count > 0)
        {
            if (ReferenceEquals(_finallyScopes.Pop(), scope))
                break;
        }
        while (_exceptionContexts.Count > scope.ContextDepth)
            _exceptionContexts.Pop();
        try
        {
            return EmitStatement(context, builder, function, scope.Finally, values, storage, model, llvmFunctions,
                emittedReturnType, loops);
        }
        finally
        {
            _exceptionContexts.Clear();
            foreach (var context_ in savedContexts.Reverse())
                _exceptionContexts.Push(context_);
            _finallyScopes.Clear();
            foreach (var scope_ in savedScopes.Reverse())
                _finallyScopes.Push(scope_);
        }
    }

    // Runs the finally bodies that a return, break or continue leaves, innermost first. Only scopes whose LoopDepth is
    // at least minLoopDepth are crossed: a break or continue stays inside its target loop. Returns true if a finally
    // body returned, in which case the caller must not emit its own exit.
    private bool EmitExitFinallies(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMValueRef function,
        Dictionary<string, LLVMValueRef> values,
        Dictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions,
        SemanticType emittedReturnType,
        Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)> loops,
        int minLoopDepth)
    {
        // The stack changes while each copy is emitted, so iterate over a snapshot (innermost first).
        foreach (var scope in _finallyScopes.ToArray())
        {
            if (scope.LoopDepth < minLoopDepth)
                break;
            if (EmitFinallyCopy(context, builder, function, scope, values, storage, model, llvmFunctions,
                    emittedReturnType, loops))
                return true;
        }

        return false;
    }

    // The loop depth a break or continue leaves to: its target loop is the innermost entry for break and the innermost
    // loop for continue. Scopes deeper than that are crossed.
    private int TargetLoopDepth(
        Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)> loops,
        bool isContinue)
    {
        var index = 0;
        foreach (var entry in loops)
        {
            if (!isContinue || entry.IsLoop)
                return loops.Count - index;
            index++;
        }

        return 0;
    }

    // Windows SEH lowering. The catch funclet only captures the exception object into a stack slot and returns to an
    // ordinary block. The catch bodies then run as normal code, so a return, break or continue inside them needs no
    // funclet-aware control flow, and their finally copy is emitted inline like any other exit. Only the unwinding path
    // runs finally inside a cleanup funclet.
    private bool EmitWindowsSehTryStatement(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMValueRef function,
        TryStatement statement,
        Dictionary<string, LLVMValueRef> values,
        Dictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions,
        SemanticType emittedReturnType,
        Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)> loops)
    {
        var hasFinally = statement.Finally is not null;
        var hasCatches = statement.Catches.Count > 0;
        var outerUnwind = _exceptionContexts.Count == 0 ? default : _exceptionContexts.Peek().Unwind;
        var contextDepth = _exceptionContexts.Count;
        var cleanup = hasFinally ? function.AppendBasicBlock("try.cleanuppad") : default;
        // Where an exception that leaves the try body or a catch body goes.
        var escape = hasFinally ? cleanup : outerUnwind;
        var end = function.AppendBasicBlock("try.end");
        var catchSwitchBlock = hasCatches ? function.AppendBasicBlock("try.catchswitch") : default;
        var bodyUnwind = hasCatches ? catchSwitchBlock : escape;
        var scope = hasFinally ? new FinallyScope(statement.Finally!, contextDepth, loops.Count) : null;
        var exceptionSlot = hasCatches
            ? BuildEntryAlloca(builder, LLVMTypeRef.CreatePointer(_context.Int8Type, 0), "exception.addr")
            : default;

        if (scope is not null)
            _finallyScopes.Push(scope);

        var body = function.AppendBasicBlock("try.body");
        builder.BuildBr(body);
        builder.PositionAtEnd(body);
        var pushedBody = PushExceptionTarget(bodyUnwind);
        var bodyReturned = EmitStatement(context, builder, function, statement.Body, values, storage,
            model, llvmFunctions, emittedReturnType, loops);
        if (pushedBody)
            _exceptionContexts.Pop();
        if (!bodyReturned && !(scope is not null && EmitFinallyCopy(context, builder, function, scope, values,
                storage, model, llvmFunctions, emittedReturnType, loops)))
            builder.BuildBr(end);

        if (hasCatches)
        {
            var catchPadBlock = function.AppendBasicBlock("try.catchpad");
            var catchBodyBlock = function.AppendBasicBlock("try.catch");
            builder.PositionAtEnd(catchSwitchBlock);
            var catchSwitch = builder.BuildCatchSwitch(default, escape, 1, "try.catchswitch.token");
            catchSwitch.AddHandler(catchPadBlock);

            builder.PositionAtEnd(catchPadBlock);
            var catchPad = builder.BuildCatchPad(catchSwitch,
                new LLVMValueRef[]
                {
                    LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(_context.Int8Type, 0)),
                    LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(_context.Int8Type, 0))
                },
                "try.catchpad");
            var exceptionPointer = GetWindowsExceptionPointer(catchPad);
            var nativeException = builder.BuildCall2(exceptionPointer.FunctionType, exceptionPointer,
                new LLVMValueRef[] { catchPad }, "native.exception");
            using (var funclet = LLVMOperandBundleRef.Create("funclet", new LLVMValueRef[] { catchPad }))
            {
                var objectValue = builder.BuildCallWithOperandBundles(_exceptionObject.FunctionType, _exceptionObject,
                    new LLVMValueRef[] { nativeException }, new LLVMOperandBundleRef[] { funclet }, "exception.object");
                builder.BuildStore(objectValue, exceptionSlot);
            }
            builder.BuildCatchRet(catchPad, catchBodyBlock);

            // Dispatch on the object, in order. A clause that matches everything ends the chain.
            builder.PositionAtEnd(catchBodyBlock);
            var caught = builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), exceptionSlot,
                "caught.object");
            var catchBlocks = statement.Catches.Select((_, index) =>
                function.AppendBasicBlock($"catch.{index}")).ToArray();
            var dispatchBlocks = statement.Catches.Select((_, index) =>
                index == 0 ? catchBodyBlock : function.AppendBasicBlock($"catch.next{index}")).ToArray();
            var unmatched = function.AppendBasicBlock("try.unmatched");
            var pushedDispatch = PushExceptionTarget(escape);
            for (var index = 0; index < statement.Catches.Count; index++)
            {
                builder.PositionAtEnd(dispatchBlocks[index]);
                var clause = statement.Catches[index];
                if (clause.ExceptionType is null)
                {
                    builder.BuildBr(catchBlocks[index]);
                    for (var rest = index + 1; rest < dispatchBlocks.Length; rest++)
                    {
                        builder.PositionAtEnd(dispatchBlocks[rest]);
                        builder.BuildUnreachable();
                    }
                    break;
                }

                var target = FromTypeSyntax(clause.ExceptionType, model);
                var match = EmitExceptionTypeCheck(builder, caught, target, model);
                builder.BuildCondBr(match, catchBlocks[index],
                    index + 1 < dispatchBlocks.Length ? dispatchBlocks[index + 1] : unmatched);
            }

            builder.PositionAtEnd(unmatched);
            EmitCallOrInvoke(builder, _exceptionRethrow.FunctionType, _exceptionRethrow, [], string.Empty);
            builder.BuildUnreachable();
            if (pushedDispatch)
                _exceptionContexts.Pop();

            for (var index = 0; index < statement.Catches.Count; index++)
            {
                builder.PositionAtEnd(catchBlocks[index]);
                var clause = statement.Catches[index];
                if (clause.BindingName is not null && clause.ExceptionType is not null)
                    values[clause.BindingName] = builder.BuildBitCast(caught,
                        ToLlvmType(FromTypeSyntax(clause.ExceptionType, model), model), "caught.exception");
                BuildNoThrowCall(builder, _exceptionEndCatch, []);
                var pushedCatch = PushExceptionTarget(escape);
                _caughtObjects.Push(caught);
                var returned = EmitStatement(context, builder, function, clause.Body, values, storage,
                    model, llvmFunctions, emittedReturnType, loops);
                _caughtObjects.Pop();
                if (pushedCatch)
                    _exceptionContexts.Pop();
                if (!returned && !(scope is not null && EmitFinallyCopy(context, builder, function, scope, values,
                        storage, model, llvmFunctions, emittedReturnType, loops)))
                    builder.BuildBr(end);
            }
        }

        if (scope is not null)
            _finallyScopes.Pop();

        if (hasFinally)
        {
            builder.PositionAtEnd(cleanup);
            var cleanupPad = builder.BuildCleanupPad(default, Array.Empty<LLVMValueRef>(), "try.cleanup");
            _exceptionContexts.Push(new ExceptionContext(outerUnwind, cleanupPad));
            var cleanupReturned = EmitStatement(context, builder, function, statement.Finally!, values, storage,
                model, llvmFunctions, emittedReturnType, loops);
            _exceptionContexts.Pop();
            if (!cleanupReturned)
                builder.BuildCleanupRet(cleanupPad, outerUnwind);
        }

        builder.PositionAtEnd(end);
        // Nothing reaches the end when every path returns or rethrows; the block still needs a terminator then.
        if (!end.AsValue().Uses.Any())
        {
            builder.BuildUnreachable();
            return true;
        }

        return false;
    }
}
