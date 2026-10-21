using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

// The Itanium lowering of try statements, for the ELF targets. It follows the SEH lowering (LlvmModuleGenerator.Finally.cs): the body
// and each catch body end with a copy of the finally body, and so does every return, break or continue that leaves them. A landing
// pad catches the exceptions that leave the body, and its catch clauses are tried in order. An exception that no clause matches, and
// an exception that a catch body throws, run a copy of the finally body and then keep unwinding. A cleanup landing pad does that
// for the exceptions that leave the body or a catch body when there is no matching clause.
public sealed partial class LlvmModuleGenerator
{
    private bool EmitItaniumTryStatement(
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
        var cleanup = hasFinally ? function.AppendBasicBlock("try.cleanup") : default;
        // Where an exception that leaves the try body or a catch body goes.
        var escape = hasFinally ? cleanup : outerUnwind;
        var end = function.AppendBasicBlock("try.end");
        var landing = hasCatches ? function.AppendBasicBlock("try.landingpad") : default;
        var bodyUnwind = hasCatches ? landing : escape;
        var scope = hasFinally ? new FinallyScope(statement.Finally!, contextDepth, loops.Count) : null;
        var savedTraceDepth = hasCatches ? EmitSaveTraceDepth(builder) : default;
        var landingType = _context.GetStructType(
            [LLVMTypeRef.CreatePointer(_context.Int8Type, 0), _context.Int32Type], false);
        var nullPointer = LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(_context.Int8Type, 0));

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
            builder.PositionAtEnd(landing);
            var landingPad = builder.BuildLandingPad(landingType, _personality, 1, "exception.landingpad");
            landingPad.AddClause(nullPointer);
            var exception = builder.BuildExtractValue(landingPad, 0, "exception.handle");
            var objectValue = builder.BuildCall2(_exceptionObject.FunctionType, _exceptionObject,
                new[] { exception }, "exception.object");

            // The first dispatch block is the landing pad itself, which the checks below continue from.
            var catchBlocks = statement.Catches.Select((_, index) =>
                function.AppendBasicBlock($"catch.{index}")).ToArray();
            var dispatchBlocks = statement.Catches.Select((_, index) =>
                index == 0 ? landing : function.AppendBasicBlock($"catch.next{index}")).ToArray();
            var unmatched = function.AppendBasicBlock("try.unmatched");
            for (var index = 0; index < statement.Catches.Count; index++)
            {
                builder.PositionAtEnd(dispatchBlocks[index]);
                var clause = statement.Catches[index];
                if (clause.ExceptionType is null)
                {
                    builder.BuildBr(catchBlocks[index]);
                    // A clause that matches everything ends the chain; the blocks after it are never reached.
                    for (var rest = index + 1; rest < dispatchBlocks.Length; rest++)
                    {
                        builder.PositionAtEnd(dispatchBlocks[rest]);
                        builder.BuildUnreachable();
                    }
                    break;
                }

                var target = FromTypeSyntax(clause.ExceptionType, model);
                var match = EmitExceptionTypeCheck(builder, objectValue, target, model);
                builder.BuildCondBr(match, catchBlocks[index], index + 1 < dispatchBlocks.Length
                    ? dispatchBlocks[index + 1] : unmatched);
            }

            // No clause matched. Without a finally body the exception is rethrown. With one, the finally body runs first, and the
            // exception keeps unwinding from the outer handlers, so the finally body does not run twice.
            builder.PositionAtEnd(unmatched);
            var pushedOuter = PushExceptionTarget(outerUnwind);
            if (scope is null)
            {
                EmitCallOrInvoke(builder, _exceptionRethrow.FunctionType, _exceptionRethrow, [], string.Empty);
                builder.BuildUnreachable();
            }
            else
            {
                var finallyReturned = EmitFinallyCopy(context, builder, function, scope, values, storage, model,
                    llvmFunctions, emittedReturnType, loops);
                if (!finallyReturned)
                    EmitRaiseObject(builder, objectValue, _exceptionContinueThrow);
            }
            if (pushedOuter)
                _exceptionContexts.Pop();

            for (var index = 0; index < statement.Catches.Count; index++)
            {
                builder.PositionAtEnd(catchBlocks[index]);
                var clause = statement.Catches[index];
                if (clause.BindingName is not null && clause.ExceptionType is not null)
                    values[clause.BindingName] = builder.BuildBitCast(objectValue,
                        ToLlvmType(FromTypeSyntax(clause.ExceptionType, model), model), "caught.exception");
                EmitRestoreTraceDepth(builder, savedTraceDepth);
                builder.BuildCall2(_exceptionEndCatch.FunctionType, _exceptionEndCatch,
                    Array.Empty<LLVMValueRef>(), (ReadOnlySpan<char>)string.Empty);
                var pushedCatch = PushExceptionTarget(escape);
                _caughtObjects.Push(objectValue);
                var returned = EmitStatement(context, builder, function, clause.Body, values, storage,
                    model, llvmFunctions, emittedReturnType, loops);
                _caughtObjects.Pop();
                if (pushedCatch)
                    _exceptionContexts.Pop();
                if (!returned)
                    EmitReleaseCaughtTrace(builder, objectValue);
                if (!returned && !(scope is not null && EmitFinallyCopy(context, builder, function, scope, values,
                        storage, model, llvmFunctions, emittedReturnType, loops)))
                    builder.BuildBr(end);
            }
        }

        if (scope is not null)
            _finallyScopes.Pop();

        if (hasFinally)
        {
            // An exception that reaches here is not handled by this try statement. The finally body runs, and then the exception
            // keeps unwinding, unless the finally body returns and discards it.
            builder.PositionAtEnd(cleanup);
            var cleanupPad = builder.BuildLandingPad(landingType, _personality, 1, "try.cleanup.landingpad");
            cleanupPad.AddClause(nullPointer);
            var thrown = builder.BuildCall2(_exceptionObject.FunctionType, _exceptionObject,
                new[] { builder.BuildExtractValue(cleanupPad, 0, "exception.handle") }, "exception.object");
            var pushedCleanup = PushExceptionTarget(outerUnwind);
            var cleanupReturned = EmitStatement(context, builder, function, statement.Finally!, values, storage,
                model, llvmFunctions, emittedReturnType, loops);
            if (pushedCleanup)
                _exceptionContexts.Pop();
            if (!cleanupReturned)
                EmitRaiseObject(builder, thrown, _exceptionContinueThrow);
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
