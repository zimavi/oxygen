using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

// Releasing the objects that the ownership pass decided the code owns (docs/memory.md). A temporary is kept in a slot of its statement
// until the end of its full expression, and then released; a condition is released right after it is evaluated.
public sealed partial class LlvmModuleGenerator
{
    // The temporaries of each statement being emitted, innermost last. Each holds the slot of an object and the object's type.
    private readonly Stack<List<(LLVMValueRef Slot, SemanticType Type)>> _temporaryScopes = new();

    // Emits a statement that may hold temporaries: they are released when it ends, unless it has already left the block.
    private bool EmitStatementWithTemporaries(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMValueRef function,
        Statement statement,
        Dictionary<string, LLVMValueRef> values,
        Dictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions,
        SemanticType emittedReturnType,
        Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)> loops)
    {
        _temporaryScopes.Push([]);
        var terminated = EmitStatementBody(context, builder, function, statement, values, storage, model, llvmFunctions,
            emittedReturnType, loops);
        if (!terminated)
            ReleaseTemporaries(builder, model);
        _temporaryScopes.Pop();
        return terminated;
    }

    // Evaluates a condition, and releases the temporaries it created before the condition is used.
    private LLVMValueRef EmitScopedCondition(LLVMBuilderRef builder, SemanticModel model, Func<LLVMValueRef> evaluate)
    {
        _temporaryScopes.Push([]);
        var value = evaluate();
        ReleaseTemporaries(builder, model);
        _temporaryScopes.Pop();
        return value;
    }

    // Keeps an object that a full expression created until the expression ends, when nothing else keeps it.
    private void KeepTemporary(LLVMBuilderRef builder, LLVMValueRef value, SemanticType type, SemanticModel model)
    {
        var slot = BuildEntryTemporary(ToLlvmType(type, model), CreateZeroValue(type, model));
        builder.BuildStore(value, slot);
        _temporaryScopes.Peek().Add((slot, type));
    }

    // Releases the temporaries of the current scope, newest first, and leaves the scope empty.
    private void ReleaseTemporaries(LLVMBuilderRef builder, SemanticModel model)
    {
        var scope = _temporaryScopes.Peek();
        for (var index = scope.Count - 1; index >= 0; index--)
        {
            var (slot, type) = scope[index];
            var value = builder.BuildLoad2(ToLlvmType(type, model), slot, "temp.owned");
            EmitReleaseOwned(builder, OwnedObjectPointer(builder, value, type));
            builder.BuildStore(CreateZeroValue(type, model), slot);
        }
        scope.Clear();
    }

    // The object pointer of an owned value. An interface value holds its object in its first word.
    private LLVMValueRef OwnedObjectPointer(LLVMBuilderRef builder, LLVMValueRef value, SemanticType type) =>
        type.IsInterface ? builder.BuildExtractValue(value, 0, "owned.object") : value;

    // Destroys and frees an owned object, unless the pointer is null: a call may return null, and a null reference owns nothing.
    private void EmitReleaseOwned(LLVMBuilderRef builder, LLVMValueRef pointer)
    {
        var function = builder.InsertBlock.Parent;
        var release = function.AppendBasicBlock("owned.release");
        var done = function.AppendBasicBlock("owned.done");
        builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, pointer,
            LLVMValueRef.CreateConstNull(pointer.TypeOf), "owned.present"), release, done);
        builder.PositionAtEnd(release);
        EmitDestroyAndFree(builder, pointer);
        builder.BuildBr(done);
        builder.PositionAtEnd(done);
    }

    // A slot in the entry block that holds a temporary. It starts with the zero value and is cleared again after each release, so a
    // conditional path that did not create the temporary finds it empty.
    private LLVMValueRef BuildEntryTemporary(LLVMTypeRef type, LLVMValueRef zero)
    {
        var entry = _currentFunction.EntryBasicBlock;
        using var entryBuilder = _context.CreateBuilder();
        var first = entry.FirstInstruction;
        if (first.Handle == IntPtr.Zero)
            entryBuilder.PositionAtEnd(entry);
        else
            entryBuilder.PositionBefore(first);
        var slot = entryBuilder.BuildAlloca(type, "temp.slot");
        entryBuilder.BuildStore(zero, slot);
        return slot;
    }
}
