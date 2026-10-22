using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

// Calls through a function pointer. The analysis chooses the callee and checks its nullness (SemanticModel.IndirectCallees and
// NonNullDereferences), so the backend only evaluates the callee, converts the arguments, and calls the pointer.
public sealed partial class LlvmModuleGenerator
{
    private LLVMValueRef EmitIndirectCall(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        Expression call,
        Expression callee,
        IReadOnlyList<Expression> arguments,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var calleeType = model.ExpressionTypes[callee];
        var pointer = EmitCheckedPointer(context, builder, callee, values, storage, model, llvmFunctions);
        var parameterTypes = calleeType.FunctionParameterTypes;
        var argumentValues = new LLVMValueRef[arguments.Count];
        for (var index = 0; index < arguments.Count; index++)
        {
            var value = EmitExpression(context, builder, arguments[index], values, storage, model, llvmFunctions);
            argumentValues[index] = CoerceValue(builder, value, model.ExpressionTypes[arguments[index]], parameterTypes[index], model);
        }
        var resultName = calleeType.FunctionReturnType.IsVoid ? string.Empty : "indirect.call";
        return EmitCallOrInvoke(builder, FunctionPointerSignatureType(calleeType, model), pointer, argumentValues, resultName);
    }

    // The LLVM function type that a function pointer type calls. It is the type of a function with the same return and parameter types.
    private LLVMTypeRef FunctionPointerSignatureType(SemanticType type, SemanticModel model) =>
        LLVMTypeRef.CreateFunction(ToLlvmType(type.FunctionReturnType, model),
            type.FunctionParameterTypes.Select(parameter => ToLlvmType(parameter, model)).ToArray(), false);
}
