using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    // An owned class local whose object an exception leaving its scope must destroy. Flag is true while the local holds
    // an object the cleanup owns: it is set when the local is declared and cleared when its scope ends or it is deleted.
    private sealed record UnwindLocal(
        VariableDeclarationStatement Declaration, string Name, SemanticType Type, LLVMValueRef Slot, LLVMValueRef Flag);

    // Starts the unwind cleanup of the current function, when one of its statements declares an owned class local. The
    // cleanup is the function's outermost exception target, so calls and try statements unwind to it, and it continues
    // unwinding to the caller once the locals are destroyed.
    private void BeginUnwindCleanup(BlockStatement body, SemanticModel model)
    {
        _unwindLocals.Clear();
        _unwindBlock = default;
        _unwindPad = default;
        _unwindLandingPad = default;
        if (!_usesExceptions || !DeclaresOwnedLocal(body, model))
            return;

        _unwindBlock = _currentFunction.AppendBasicBlock("unwind.cleanup");
        using var builder = _context.CreateBuilder();
        builder.PositionAtEnd(_unwindBlock);
        if (_usesWindowsSeh)
        {
            _unwindPad = builder.BuildCleanupPad(default, Array.Empty<LLVMValueRef>(), "unwind.pad");
        }
        else
        {
            var landingType = _context.GetStructType(
                [LLVMTypeRef.CreatePointer(_context.Int8Type, 0), _context.Int32Type], false);
            _unwindLandingPad = builder.BuildLandingPad(landingType, _personality, 0, "unwind.landingpad");
            _unwindLandingPad.IsCleanup = true;
        }
        _exceptionContexts.Push(new ExceptionContext(_unwindBlock, default));
    }

    // Emits the cleanup body after the function's own code: destroy each local whose flag is set, newest first, and then
    // continue unwinding to the caller.
    private void EndUnwindCleanup(SemanticModel model)
    {
        if (_unwindBlock.Handle == IntPtr.Zero)
            return;

        // Drop the function-level target, so the destructor calls below do not unwind into this same cleanup.
        _exceptionContexts.Pop();
        using var builder = _context.CreateBuilder();
        builder.PositionAtEnd(_unwindBlock);
        _exceptionContexts.Push(new ExceptionContext(default, _unwindPad));
        for (var index = _unwindLocals.Count - 1; index >= 0; index--)
        {
            var local = _unwindLocals[index];
            var destroy = _currentFunction.AppendBasicBlock("unwind.destroy");
            var next = _currentFunction.AppendBasicBlock("unwind.next");
            builder.BuildCondBr(builder.BuildLoad2(_context.Int1Type, local.Flag, "unwind.alive"), destroy, next);
            builder.PositionAtEnd(destroy);
            EmitUnwindDestroy(builder, local, model);
            builder.BuildStore(LLVMValueRef.CreateConstInt(_context.Int1Type, 0, false), local.Flag);
            builder.BuildBr(next);
            builder.PositionAtEnd(next);
        }
        _exceptionContexts.Pop();

        if (_usesWindowsSeh)
            builder.BuildCleanupRet(_unwindPad, default);
        else
            builder.BuildResume(_unwindLandingPad);
        _unwindBlock = default;
    }

    // Destroys one owned local the way its scope end does. A local that the function frees releases its memory too.
    private void EmitUnwindDestroy(LLVMBuilderRef builder, UnwindLocal local, SemanticModel model)
    {
        if (model.AutomaticallyFreedLocals.Contains(local.Declaration))
        {
            var allocated = builder.BuildLoad2(ToLlvmType(local.Type, model), local.Slot, local.Name);
            EmitDestroyAndFree(builder, allocated);
            return;
        }
        if (!local.Type.IsClass || !_destructors.TryGetValue(local.Type.StructName!, out var destructor))
            return;
        if (IsValueClass(local.Type, model))
        {
            EmitCallOrInvoke(builder, destructor.FunctionType, destructor, [local.Slot], string.Empty);
            return;
        }
        var reference = builder.BuildLoad2(ToLlvmType(local.Type, model), local.Slot, local.Name);
        EmitCallOrInvoke(builder, destructor.FunctionType, destructor, [reference], string.Empty);
    }

    // Registers an owned class local that has just been declared and initialized, so an exception from now on destroys it.
    private void RegisterUnwindLocal(LLVMBuilderRef builder, VariableDeclarationStatement declaration,
        SemanticType type, LLVMValueRef slot)
    {
        if (_unwindBlock.Handle == IntPtr.Zero)
            return;

        var flag = BuildEntryFlag(declaration.Name + ".unwind");
        builder.BuildStore(LLVMValueRef.CreateConstInt(_context.Int1Type, 1, false), flag);
        _unwindLocals.Add(new UnwindLocal(declaration, declaration.Name, type, slot, flag));
    }

    // Clears the flag of a local whose scope has ended, before its destructor runs on that path.
    private void ClearUnwindFlag(LLVMBuilderRef builder, VariableDeclarationStatement declaration)
    {
        foreach (var local in _unwindLocals)
            if (ReferenceEquals(local.Declaration, declaration))
                builder.BuildStore(LLVMValueRef.CreateConstInt(_context.Int1Type, 0, false), local.Flag);
    }

    // Clears the flag of the local stored in `slot`, when a delete destroys it.
    private void ClearUnwindFlagOfSlot(LLVMBuilderRef builder, LLVMValueRef slot)
    {
        foreach (var local in _unwindLocals)
            if (local.Slot.Handle == slot.Handle)
                builder.BuildStore(LLVMValueRef.CreateConstInt(_context.Int1Type, 0, false), local.Flag);
    }

    // A flag in the entry block, false from the start of the function. Entry allocas keep it out of loops.
    private LLVMValueRef BuildEntryFlag(string name)
    {
        var entry = _currentFunction.EntryBasicBlock;
        using var entryBuilder = _context.CreateBuilder();
        var first = entry.FirstInstruction;
        if (first.Handle == IntPtr.Zero)
            entryBuilder.PositionAtEnd(entry);
        else
            entryBuilder.PositionBefore(first);
        var flag = entryBuilder.BuildAlloca(_context.Int1Type, name);
        entryBuilder.BuildStore(LLVMValueRef.CreateConstInt(_context.Int1Type, 0, false), flag);
        return flag;
    }

    // Whether the body declares an owned class local anywhere, including in nested blocks and try statements.
    private bool DeclaresOwnedLocal(Statement statement, SemanticModel model) => statement switch
    {
        VariableDeclarationStatement declaration => IsOwnedDeclaration(declaration, model),
        BlockStatement block => block.Statements.Any(item => DeclaresOwnedLocal(item, model)),
        IfStatement conditional => DeclaresOwnedLocal(conditional.Then, model)
            || (conditional.Else is not null && DeclaresOwnedLocal(conditional.Else, model)),
        WhileStatement loop => DeclaresOwnedLocal(loop.Body, model),
        ForStatement loop => (loop.Initializer is not null && DeclaresOwnedLocal(loop.Initializer, model))
            || DeclaresOwnedLocal(loop.Body, model),
        ForInStatement loop => DeclaresOwnedLocal(loop.Body, model),
        TryStatement tryStatement => DeclaresOwnedLocal(tryStatement.Body, model)
            || tryStatement.Catches.Any(clause => DeclaresOwnedLocal(clause.Body, model))
            || (tryStatement.Finally is not null && DeclaresOwnedLocal(tryStatement.Finally, model)),
        SwitchStatement switchStatement => switchStatement.Cases.Any(item => DeclaresOwnedLocal(item.Body, model)),
        _ => false
    };

    // The same rule that registers an owned local for scope-end destruction in EmitStatement.
    private bool IsOwnedDeclaration(VariableDeclarationStatement declaration, SemanticModel model)
    {
        var declaredType = declaration.Type.IsInferred && declaration.Initializer is not null
            ? model.ExpressionTypes[declaration.Initializer]
            : FromTypeSyntax(declaration.Type, model);
        return declaredType.IsClass && (IsValueClass(declaredType, model)
            || declaration.Initializer is ClassConstructionExpression stackObject && !model.ConstructionCalls.ContainsKey(stackObject)
            || model.AutomaticallyFreedLocals.Contains(declaration));
    }
}
