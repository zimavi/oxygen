using System.Text;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    // Throws an object the way a throw statement does: through the runtime, and to the innermost landing pad when the
    // current code is inside a try.
    private void EmitThrowObject(LLVMBuilderRef builder, LLVMValueRef thrown)
    {
        var raw = builder.BuildBitCast(thrown, LLVMTypeRef.CreatePointer(_context.Int8Type, 0), "exception.object");
        if (_exceptionContexts.Count == 0)
        {
            builder.BuildCall2(_exceptionThrow.FunctionType, _exceptionThrow,
                new[] { raw }, (ReadOnlySpan<char>)string.Empty);
            builder.BuildUnreachable();
            return;
        }

        var normal = _currentFunction.AppendBasicBlock("throw.returned");
        var exceptionContext = _exceptionContexts.Peek();
        if (_usesWindowsSeh && exceptionContext.Pad.Handle != IntPtr.Zero)
        {
            using var funclet = LLVMOperandBundleRef.Create("funclet", new LLVMValueRef[] { exceptionContext.Pad });
            builder.BuildInvokeWithOperandBundles(_exceptionThrow.FunctionType, _exceptionThrow,
                new[] { raw }, normal, exceptionContext.Unwind, new LLVMOperandBundleRef[] { funclet },
                (ReadOnlySpan<char>)string.Empty);
        }
        else
        {
            builder.BuildInvoke2(_exceptionThrow.FunctionType, _exceptionThrow,
                new[] { raw }, normal, exceptionContext.Unwind, (ReadOnlySpan<char>)string.Empty);
        }
        builder.PositionAtEnd(normal);
        builder.BuildUnreachable();
    }

    // Reading a member through a class reference first checks that the reference is not null. A null reference raises
    // a NullReferenceException, which can be caught like any other exception; otherwise the read continues in the
    // valid block.
    private void EmitNullReferenceCheck(
        LLVMBuilderRef builder, LLVMValueRef reference, string memberName, SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var isNull = builder.BuildIsNull(reference, "null.reference");
        var failure = _currentFunction.AppendBasicBlock("null.reference.failure");
        var valid = _currentFunction.AppendBasicBlock("null.reference.valid");
        builder.BuildCondBr(isNull, failure, valid);

        builder.PositionAtEnd(failure);
        var message = $"The member '{memberName}' was read through a null class reference.";
        EmitThrowObject(builder, EmitNullReferenceException(builder, message, model, llvmFunctions));
        builder.PositionAtEnd(valid);
    }

    // Builds a NullReferenceException with its message. std.exception is always loaded (the source resolver requires
    // it), so the class and its (string) constructor exist in every module.
    private LLVMValueRef EmitNullReferenceException(
        LLVMBuilderRef builder, string message, SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        const string className = "NullReferenceException";
        if (!model.Classes.TryGetValue(className, out var cls))
            throw new InvalidOperationException(
                $"Internal compiler error: '{className}' is not loaded, so a null reference cannot be reported. " +
                "The source resolver must load std.exception.");
        var ctor = cls.Constructors.First(candidate => candidate.Parameters.Count == 1
            && candidate.Parameters[0].Type.Kind == SemanticTypeKind.String);
        var instance = AllocateClassInstance(builder, className, cls, true, model, llvmFunctions);
        var constructor = _constructors[ConstructorKey(cls, ctor)];
        var text = EmitStringBytes(builder, Encoding.UTF8.GetBytes(message));
        builder.BuildCall2(constructor.FunctionType, constructor, new[] { instance, text },
            (ReadOnlySpan<char>)string.Empty);
        return instance;
    }
}
