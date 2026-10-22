using LLVMSharp.Interop;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

// Runtime failures that a catch can handle (docs/exceptions.md). The compiled code detects each failure and calls the failure's helper, an
// internal function of the object that builds an instance of the failure's class and throws it. The helper does not push a trace frame, so
// the trace of the exception ends at the function that failed. Without std.exception there is no class, and the runtime stops the program.
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

    // The class that a failure throws, its message, and the name of its helper. The messages are fixed: the index, length or value are not
    // formatted yet.
    private static (string ClassName, string Message, string HelperName) DescribeFailure(CatchableFailure failure) => failure switch
    {
        CatchableFailure.ArrayIndex => (IndexOutOfRangeClassName, "An array index is outside the bounds of the array.", "oxygen.fail.array_index"),
        CatchableFailure.StringIndex => (IndexOutOfRangeClassName, "A string index is outside the bounds of the string.", "oxygen.fail.string_index"),
        CatchableFailure.DivideByZero => (DivideByZeroClassName, "Integer division by zero.", "oxygen.fail.divide_by_zero"),
        CatchableFailure.SignedOverflow => (RuntimeExceptionClassName,
            "Signed integer division overflowed: the minimum value was divided by -1.", "oxygen.fail.signed_overflow"),
        CatchableFailure.ShiftOutOfRange => (RuntimeExceptionClassName,
            "The shift count is out of range for the shifted integer type.", "oxygen.fail.shift_out_of_range"),
        CatchableFailure.InvalidEnumValue => (RuntimeExceptionClassName,
            "The value is not a case of the enum.", "oxygen.fail.invalid_enum_value"),
        _ => throw new InvalidOperationException($"Internal compiler error: unknown failure '{failure}'.")
    };

    // The helper of each failure that the object uses. A helper is declared when a failure first needs it, and its body is written at the end.
    private readonly Dictionary<CatchableFailure, LLVMValueRef> _failureHelpers = new();

    // Reports a failure from the failure block the builder is in. The block must end after this call, since the failure does not return. When
    // the failure's class is loaded, the failure throws an instance of it; otherwise the runtime stops the program with the given hook.
    private void EmitFailureCall(
        LLVMBuilderRef builder, CatchableFailure failure, SemanticModel model, LLVMValueRef runtimeHook, LLVMValueRef[] runtimeArguments)
    {
        if (!model.Classes.ContainsKey(DescribeFailure(failure).ClassName))
        {
            BuildNoThrowCall(builder, runtimeHook, runtimeArguments);
            return;
        }
        var helper = FailureHelper(failure);
        EmitCallOrInvoke(builder, helper.FunctionType, helper, [], string.Empty);
    }

    private LLVMValueRef FailureHelper(CatchableFailure failure)
    {
        if (_failureHelpers.TryGetValue(failure, out var helper))
            return helper;
        var (_, _, name) = DescribeFailure(failure);
        helper = _module.AddFunction(name, LLVMTypeRef.CreateFunction(_context.VoidType, [], false));
        helper.Linkage = LLVMLinkage.LLVMInternalLinkage;
        _failureHelpers[failure] = helper;
        return helper;
    }

    // Writes the body of each helper the object uses. It runs after the functions, so no function is being emitted.
    private void EmitFailureHelpers(SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        foreach (var (failure, helper) in _failureHelpers.ToList())
        {
            var (className, message, _) = DescribeFailure(failure);
            var previousFunction = _currentFunction;
            _currentFunction = helper;
            using var builder = _context.CreateBuilder();
            builder.PositionAtEnd(helper.AppendBasicBlock("entry"));
            var exception = EmitExceptionInstance(builder, className, message, model, llvmFunctions);
            EmitThrowObject(builder, exception);
            _currentFunction = previousFunction;
        }
    }
}
