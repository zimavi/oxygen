using System.Text;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    // Throws an object the way a throw statement does: through the runtime, and to the innermost landing pad when the
    // current code is inside a try.
    private void EmitThrowObject(LLVMBuilderRef builder, LLVMValueRef thrown) => EmitRaiseObject(builder, thrown, _exceptionThrow);

    // Raises an exception object through `raise`, which captures its trace (`_exceptionThrow`) or keeps it (`_exceptionContinueThrow`).
    private void EmitRaiseObject(LLVMBuilderRef builder, LLVMValueRef thrown, LLVMValueRef raise)
    {
        var raw = builder.BuildBitCast(thrown, LLVMTypeRef.CreatePointer(_context.Int8Type, 0), "exception.object");
        if (_exceptionContexts.Count == 0)
        {
            builder.BuildCall2(raise.FunctionType, raise,
                new[] { raw }, (ReadOnlySpan<char>)string.Empty);
            builder.BuildUnreachable();
            return;
        }

        var normal = _currentFunction.AppendBasicBlock("throw.returned");
        var exceptionContext = _exceptionContexts.Peek();
        if (_usesWindowsSeh && exceptionContext.Pad.Handle != IntPtr.Zero)
        {
            using var funclet = LLVMOperandBundleRef.Create("funclet", new LLVMValueRef[] { exceptionContext.Pad });
            builder.BuildInvokeWithOperandBundles(raise.FunctionType, raise,
                new[] { raw }, normal, exceptionContext.Unwind, new LLVMOperandBundleRef[] { funclet },
                (ReadOnlySpan<char>)string.Empty);
        }
        else
        {
            builder.BuildInvoke2(raise.FunctionType, raise,
                new[] { raw }, normal, exceptionContext.Unwind, (ReadOnlySpan<char>)string.Empty);
        }
        builder.PositionAtEnd(normal);
        builder.BuildUnreachable();
    }

    // Reading or writing a member through a class reference first checks that the reference is not null. A null
    // reference raises a NullReferenceException, which can be caught like any other exception; otherwise the access
    // continues in the valid block. `access` is "read" or "written".
    private void EmitNullReferenceCheck(
        LLVMBuilderRef builder, LLVMValueRef reference, string memberName, string access, SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var isNull = builder.BuildIsNull(reference, "null.reference");
        EmitNullBranch(builder, isNull, "null.reference", model,
            message: $"The member '{memberName}' was {access} through a null class reference.",
            stop: _nullReferenceFailure, llvmFunctions);
    }

    // A pointer that is dereferenced is checked the same way: a null pointer raises a NullReferenceException, and a build without
    // std.exception stops the program. The access continues in the valid block.
    private void EmitNullPointerCheck(
        LLVMBuilderRef builder, LLVMValueRef pointer, SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var isNull = builder.BuildIsNull(pointer, "null.pointer");
        EmitNullBranch(builder, isNull, "null.pointer", model,
            message: "A null pointer was dereferenced.",
            stop: _nullPointerFailure, llvmFunctions);
    }

    // The value of a pointer expression that is about to be dereferenced. Every dereference uses this, so no site can miss the check. The
    // analysis decides which operands are proven non-null (SemanticModel.NonNullDereferences), and only those are not checked.
    private LLVMValueRef EmitCheckedPointer(
        LLVMContextRef context, LLVMBuilderRef builder, Expression pointer,
        IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var value = EmitExpression(context, builder, pointer, values, storage, model, llvmFunctions);
        if (!model.NonNullDereferences.Contains(pointer))
            EmitNullPointerCheck(builder, value, model, llvmFunctions);
        return value;
    }

    // Branches to a failure block when `isNull` holds, and continues in a valid block otherwise. The failure block raises a
    // NullReferenceException with `message` when std.exception is loaded. Without it (a build with --no-std) there is no exception to
    // throw, so the block calls the `stop` runtime failure, which ends the program.
    private void EmitNullBranch(
        LLVMBuilderRef builder, LLVMValueRef isNull, string name, SemanticModel model, string message, LLVMValueRef stop,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var failure = _currentFunction.AppendBasicBlock($"{name}.failure");
        var valid = _currentFunction.AppendBasicBlock($"{name}.valid");
        builder.BuildCondBr(isNull, failure, valid);

        builder.PositionAtEnd(failure);
        if (!model.Classes.ContainsKey(NullReferenceClassName))
        {
            builder.BuildCall2(stop.FunctionType, stop, Array.Empty<LLVMValueRef>(), (ReadOnlySpan<char>)string.Empty);
            builder.BuildUnreachable();
        }
        else
        {
            EmitThrowObject(builder, EmitNullReferenceException(builder, message, model, llvmFunctions));
        }
        builder.PositionAtEnd(valid);
    }

    private const string NullReferenceClassName = "std.exception.NullReferenceException";

    // Builds a NullReferenceException with its message. When the class is loaded, its module (std.exception) is in the build, so the
    // class and its (string) constructor exist.
    private LLVMValueRef EmitNullReferenceException(
        LLVMBuilderRef builder, string message, SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions) =>
        EmitExceptionInstance(builder, NullReferenceClassName, message, model, llvmFunctions);

    // Builds an instance of a class of std.exception with its message, through its (string) constructor. The class must be loaded.
    private LLVMValueRef EmitExceptionInstance(
        LLVMBuilderRef builder, string className, string message, SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        if (!model.Classes.TryGetValue(className, out var cls))
            throw new InvalidOperationException(
                $"Internal compiler error: '{className}' is not loaded, so the exception cannot be built.");
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
