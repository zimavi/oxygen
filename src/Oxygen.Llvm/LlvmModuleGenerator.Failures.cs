using LLVMSharp.Interop;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

// Runtime failures that a catch can handle (docs/exceptions.md). The compiled code detects each failure and calls the failure's helper, an
// internal function of the object that builds an instance of the failure's class and throws it. The helper takes the two values that the
// message shows. The runtime formats the message from them (__oxygen_runtime_format_failure). The helper does not push a trace frame, so the
// trace of the exception ends at the function that failed. Without std.exception there is no class, and the runtime stops the program.
public sealed partial class LlvmModuleGenerator
{
    private enum CatchableFailure
    {
        ArrayIndex,
        StringIndex,
        DivideByZero,
        SignedOverflow,
        ShiftOutOfRange,
        InvalidEnumValue
    }

    // The failure of each arithmetic check.
    private static CatchableFailure FailureOf(ArithmeticFailureKind kind) => kind switch
    {
        ArithmeticFailureKind.DivideByZero => CatchableFailure.DivideByZero,
        ArithmeticFailureKind.SignedOverflow => CatchableFailure.SignedOverflow,
        ArithmeticFailureKind.ShiftOutOfRange => CatchableFailure.ShiftOutOfRange,
        _ => throw new InvalidOperationException($"Internal compiler error: unknown arithmetic failure '{kind}'.")
    };

    private const string IndexOutOfRangeClassName = "std.exception.IndexOutOfRangeException";
    private const string DivideByZeroClassName = "std.exception.DivideByZeroException";
    private const string RuntimeExceptionClassName = "std.exception.RuntimeException";

    // The class that a failure throws, the failure's number in the runtime (OXYGEN_FAILURE_* in oxygen_runtime.h), and the name of its helper.
    private static (string ClassName, int Kind, string HelperName) DescribeFailure(CatchableFailure failure) => failure switch
    {
        CatchableFailure.ArrayIndex => (IndexOutOfRangeClassName, 1, "oxygen.fail.array_index"),
        CatchableFailure.StringIndex => (IndexOutOfRangeClassName, 2, "oxygen.fail.string_index"),
        CatchableFailure.DivideByZero => (DivideByZeroClassName, 3, "oxygen.fail.divide_by_zero"),
        CatchableFailure.SignedOverflow => (RuntimeExceptionClassName, 4, "oxygen.fail.signed_overflow"),
        CatchableFailure.ShiftOutOfRange => (RuntimeExceptionClassName, 5, "oxygen.fail.shift_out_of_range"),
        CatchableFailure.InvalidEnumValue => (RuntimeExceptionClassName, 6, "oxygen.fail.invalid_enum_value"),
        _ => throw new InvalidOperationException($"Internal compiler error: unknown failure '{failure}'.")
    };

    // The helper of each failure that the object uses. A helper is declared when a failure first needs it, and its body is written at the end.
    private readonly Dictionary<CatchableFailure, LLVMValueRef> _failureHelpers = new();

    // Reports a failure from the failure block the builder is in. The block must end after this call, since the failure does not return.
    // `first` and `second` are the values that the message shows (see OXYGEN_FAILURE_* in oxygen_runtime.h), and they are 0 when unused.
    // When the failure's class is loaded, the failure throws an instance of it; otherwise the runtime stops the program with the hook.
    private void EmitFailureCall(
        LLVMBuilderRef builder, CatchableFailure failure, SemanticModel model, LLVMValueRef runtimeHook, LLVMValueRef[] runtimeArguments,
        LLVMValueRef first, LLVMValueRef second)
    {
        // Without exceptions, or in a @nothrow function, the failure stops the program: nothing may throw from there.
        if (!_usesExceptions || _noThrowFunction || !model.Classes.ContainsKey(DescribeFailure(failure).ClassName))
        {
            BuildNoThrowCall(builder, runtimeHook, runtimeArguments);
            return;
        }
        var helper = FailureHelper(failure);
        EmitCallOrInvoke(builder, helper.FunctionType, helper, [first, second], string.Empty);
    }

    private LLVMValueRef FailureHelper(CatchableFailure failure)
    {
        if (_failureHelpers.TryGetValue(failure, out var helper))
            return helper;
        var (_, _, name) = DescribeFailure(failure);
        helper = _module.AddFunction(name,
            LLVMTypeRef.CreateFunction(_context.VoidType, [_context.Int64Type, _context.Int64Type], false));
        helper.Linkage = LLVMLinkage.LLVMInternalLinkage;
        _failureHelpers[failure] = helper;
        return helper;
    }

    // Writes the body of each helper the object uses. It runs after the functions, so no function is being emitted. The helper formats the
    // message in the runtime, then throws an instance of the failure's class that holds it.
    private void EmitFailureHelpers(SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        // A helper throws, so it is never a @nothrow body, whatever function was emitted last.
        _noThrowFunction = false;
        var stringType = ToLlvmType(SemanticType.String, model);
        var messageType = LLVMTypeRef.CreateFunction(_context.VoidType,
            [_context.Int32Type, _context.Int64Type, _context.Int64Type, LLVMTypeRef.CreatePointer(stringType, 0)], false);
        var format = DeclareNativeFunction(_module, "__oxygen_runtime_format_failure", messageType);
        foreach (var (failure, helper) in _failureHelpers.ToList())
        {
            var (className, kind, _) = DescribeFailure(failure);
            var previousFunction = _currentFunction;
            _currentFunction = helper;
            using var builder = _context.CreateBuilder();
            builder.PositionAtEnd(helper.AppendBasicBlock("entry"));
            var message = builder.BuildAlloca(stringType, "failure.message");
            builder.BuildCall2(format.FunctionType, format,
                [LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)kind, false), helper.GetParam(0), helper.GetParam(1), message],
                (ReadOnlySpan<char>)string.Empty);
            var text = builder.BuildLoad2(stringType, message, "failure.text");
            var exception = EmitExceptionInstance(builder, className, text, model, llvmFunctions);
            // The exception holds the text, so releasing the exception frees it (see __oxygen_runtime_release_caught_exception).
            var data = builder.BuildExtractValue(text, 0, "failure.data");
            builder.BuildCall2(_adoptFailureText.FunctionType, _adoptFailureText, [exception, data],
                (ReadOnlySpan<char>)string.Empty);
            EmitThrowObject(builder, exception);
            _currentFunction = previousFunction;
        }
    }
}
