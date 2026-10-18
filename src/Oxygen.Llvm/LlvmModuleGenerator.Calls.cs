using System.Globalization;
using System.Text.Json;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    private static LLVMValueRef EmitCall(LLVMContextRef context, LLVMBuilderRef builder, CallExpression expression, IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage, SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var parts = expression.Name.Split('.');
        if (parts.Length == 2 && model.Unions.TryGetValue(parts[0], out var union))
        {
            var index = union.Cases.Select((item, i) => (item, i)).First(pair => pair.item.Name == parts[1]).i;
            var unionType = model.ExpressionTypes[expression];
            var result = LLVMValueRef.CreateConstNull(ToLlvmType(unionType, model));
            result = builder.BuildInsertValue(result, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)index, false), 0, "union.tag");
            if (expression.Arguments.Count == 1)
                result = builder.BuildInsertValue(result,
                    CoerceValue(builder,
                        EmitExpression(context, builder, expression.Arguments[0], values, storage, model, llvmFunctions),
                        model.ExpressionTypes[expression.Arguments[0]], UnionPayloadType(unionType, model), model),
                    1, "union.payload");
            return result;
        }
        var function = model.ResolvedCalls[expression];
        var method = IsClassMethod(function, model);
        var arguments = new List<LLVMValueRef>();
        if (method)
        {
            var separator = expression.Name.LastIndexOf('.');
            var receiver = expression.Name[..separator];
            if (!values.TryGetValue(receiver, out var receiverValue))
                receiverValue = builder.BuildLoad2(ToLlvmType(model.ExpressionTypes.First(pair => pair.Key is IdentifierExpression id && id.Name == receiver).Value, model),
                    storage[receiver], receiver);
            else if (receiver == "this"
                && model.Classes[function.ModuleName[(function.ModuleName.LastIndexOf('.') + 1)..]].IsValue)
            {
                var receiverClassName = function.ModuleName[(function.ModuleName.LastIndexOf('.') + 1)..];
                receiverValue = builder.BuildLoad2(
                    _classTypes[receiverClassName], receiverValue, "this.value");
            }
            var receiverType = model.ExpressionTypes.First(pair =>
                pair.Key is IdentifierExpression id && id.Name == receiver).Value;
            if (receiverType.IsInterface)
            {
                var table = builder.BuildExtractValue(receiverValue, 1, "interface.table");
                receiverValue = builder.BuildExtractValue(receiverValue, 0, "interface.data");
                var iface = model.Interfaces[receiverType.StructName!];
                var contract = function;
                var slot = _interfaceSlots[iface.Name].ToList().IndexOf(contract);
                if (slot < 0)
                    slot = _interfaceSlots[iface.Name].ToList().FindIndex(candidate =>
                        candidate.Name == function.Name && candidate.Parameters.Select(p => p.Type)
                            .SequenceEqual(function.Parameters.Select(p => p.Type)));
                var tableEntry = builder.BuildGEP2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                    table, [LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)slot, false)],
                    (ReadOnlySpan<char>)"interface.slot");
                var rawTarget = builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                    tableEntry, "interface.target");
                var interfaceTarget = builder.BuildBitCast(rawTarget, llvmFunctions[function].FunctionType,
                    "interface.call");
                arguments.Add(receiverValue);
                for (var index = 0; index < expression.Arguments.Count; index++)
                {
                    var argument = expression.Arguments[index];
                    var value = EmitExpression(context, builder, argument, values, storage, model, llvmFunctions);
                    arguments.Add(CoerceValue(builder, value, model.ExpressionTypes[argument],
                        function.Parameters[index].Type, model));
                }
                return EmitCallOrInvoke(builder, llvmFunctions[function].FunctionType, interfaceTarget,
                    arguments.ToArray(), function.ReturnType.IsVoid ? string.Empty : "interface.call");
            }
            arguments.Add(receiverValue);
        }
        for (var index = 0; index < expression.Arguments.Count; index++)
        {
            var argument = expression.Arguments[index];
            var value = EmitExpression(context, builder, argument, values, storage, model, llvmFunctions);
            value = CoerceValue(builder, value, model.ExpressionTypes[argument], function.Parameters[index].Type, model);
            if (UsesPointerStringAbi(function) && function.Parameters[index].Type.Kind == SemanticTypeKind.String)
            {
                var storagePointer = builder.BuildAlloca(ToLlvmStringType(), "cabi.string");
                builder.BuildStore(value, storagePointer);
                value = storagePointer;
            }

            if (argument is IdentifierExpression identifier && function.Parameters[index].Type.IsClass)
                RemoveOwned(identifier.Name);
            arguments.Add(value);
        }

        var resultName = function.ReturnType.IsVoid ? string.Empty : "call";
        if (method && (function.Declaration.IsVirtual || function.Declaration.IsOverride))
        {
            var receiverName = expression.Name[..expression.Name.LastIndexOf('.')];
            var receiverType = model.ExpressionTypes.First(pair =>
                pair.Key is IdentifierExpression id && id.Name == receiverName).Value;
            var receiverClass = model.Classes[receiverType.StructName!];
            var slotPair = GetVirtualMethods(receiverClass, model).Select((candidate, index) => (candidate, index))
                .FirstOrDefault(pair => pair.candidate.Name == function.Name
                    && pair.candidate.Parameters.Select(parameter => parameter.Type)
                        .SequenceEqual(function.Parameters.Select(parameter => parameter.Type)));
            if (slotPair.candidate is not null)
            {
                var slot = slotPair.index;
                var tableSlot = builder.BuildStructGEP2(_classTypes[receiverClass.Name], arguments[0],
                    (uint)slot + 1, "vtable.slot");
                var rawTarget = builder.BuildLoad2(
                    LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), tableSlot, "vtable.target");
                var target = builder.BuildBitCast(rawTarget, llvmFunctions[function].FunctionType, "vtable.call");
                return EmitCallOrInvoke(builder, llvmFunctions[function].FunctionType, target,
                    arguments.ToArray(), resultName);
            }
        }
        return EmitCallOrInvoke(builder, llvmFunctions[function].FunctionType, llvmFunctions[function],
            arguments.ToArray(), resultName);
    }

    private static LLVMValueRef EmitCallOrInvoke(
        LLVMBuilderRef builder,
        LLVMTypeRef functionType,
        LLVMValueRef target,
        LLVMValueRef[] arguments,
        string resultName)
    {
        if (_exceptionContexts.Count == 0)
            return builder.BuildCall2(functionType, target, arguments, resultName);

        var exceptionContext = _exceptionContexts.Peek();
        LLVMValueRef result;
        if (_usesWindowsSeh && exceptionContext.Pad.Handle != IntPtr.Zero)
        {
            using var funclet = LLVMOperandBundleRef.Create("funclet", new LLVMValueRef[] { exceptionContext.Pad });
            return builder.BuildCallWithOperandBundles(functionType, target, arguments,
                new LLVMOperandBundleRef[] { funclet }, resultName);
        }
        var normal = _currentFunction.AppendBasicBlock("invoke.continue");
        result = builder.BuildInvoke2(functionType, target, arguments, normal,
            exceptionContext.Unwind, resultName);
        builder.PositionAtEnd(normal);
        return result;
    }

}
