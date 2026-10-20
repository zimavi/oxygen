using System.Globalization;
using System.Text.Json;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    private LLVMValueRef EmitCall(LLVMContextRef context, LLVMBuilderRef builder, CallExpression expression, IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage, SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var parts = expression.Name.Split('.');
        if (parts.Length == 2 && model.Unions.TryGetValue(parts[0], out var union))
        {
            var index = union.Cases.Select((item, i) => (item, i)).First(pair => pair.item.Name == parts[1]).i;
            var unionType = NormalizeAggregateType(model.ExpressionTypes[expression], model);
            if (unionType.EnumName is not null && model.Unions.TryGetValue(unionType.EnumName, out var specializedUnion))
                union = specializedUnion;
            var result = LLVMValueRef.CreateConstNull(ToLlvmType(unionType, model));
            result = builder.BuildInsertValue(result, LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)index, false), 0, "union.tag");
            if (expression.Arguments.Count == 1)
                result = builder.BuildInsertValue(result,
                    CoerceValue(builder,
                        EmitExpression(context, builder, expression.Arguments[0], values, storage, model, llvmFunctions),
                        model.ExpressionTypes[expression.Arguments[0]], UnionPayloadType(unionType, model), model),
                    1, "union.payload");
            return result;
        }
        var function = model.ResolvedCalls[expression];
        var argumentsWithDefaults = ArgumentsInParameterOrder(model, expression, function.Parameters, expression.Arguments);
        if (model.ReceiverArgumentCalls.Contains(expression))
            return EmitReceiverArgumentCall(context, builder, expression, function, values, storage, model, llvmFunctions);
        var method = IsClassMethod(function, model);
        var arguments = new List<LLVMValueRef>();
        if (method)
        {
            // A receiver expression is evaluated. A name-form receiver is `this`, `base` (the same instance as `this`, seen as
            // its base class), or a variable; it is null for a receiver expression.
            var receiver = expression.Receiver is null ? expression.Name[..expression.Name.LastIndexOf('.')] : null;
            if (receiver == "base")
                receiver = "this";
            var receiverType = NormalizeAggregateType(model.CallReceiverTypes[expression], model);
            LLVMValueRef receiverValue;
            if (receiver is null)
                receiverValue = EmitExpression(context, builder, expression.Receiver!, values, storage, model, llvmFunctions);
            else if (!values.TryGetValue(receiver, out receiverValue))
                receiverValue = builder.BuildLoad2(ToLlvmType(receiverType, model), storage[receiver], receiver);
            else if (receiver == "this"
                && model.Classes[function.ModuleName[(function.ModuleName.LastIndexOf('.') + 1)..]].IsValue)
            {
                var receiverClassName = function.ModuleName[(function.ModuleName.LastIndexOf('.') + 1)..];
                receiverValue = builder.BuildLoad2(
                    _classTypes[receiverClassName], receiverValue, "this.value");
            }
            if (receiverType.IsInterface)
            {
                var table = builder.BuildExtractValue(receiverValue, 1, "interface.table");
                receiverValue = builder.BuildExtractValue(receiverValue, 0, "interface.data");
                EmitNullReferenceCheck(builder, receiverValue, function.Name, "called", model, llvmFunctions);
                var iface = model.Interfaces[receiverType.StructName!];
                var contract = function;
                var slot = _interfaceSlots[iface.Name].ToList().IndexOf(contract);
                if (slot < 0)
                    slot = _interfaceSlots[iface.Name].ToList().FindIndex(candidate =>
                        candidate.Name == function.Name && candidate.Parameters.Select(p => p.Type)
                            .SequenceEqual(function.Parameters.Select(p => p.Type)));
                var tableEntry = builder.BuildGEP2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                    table, [LLVMValueRef.CreateConstInt(_context.Int64Type, (ulong)slot, false)],
                    (ReadOnlySpan<char>)"interface.slot");
                var rawTarget = builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                    tableEntry, "interface.target");
                // Opaque pointers: the loaded pointer is called with the function type directly.
                var interfaceTarget = rawTarget;
                arguments.Add(receiverValue);
                for (var index = 0; index < argumentsWithDefaults.Count; index++)
                {
                    var argument = argumentsWithDefaults[index];
                    var value = EmitExpression(context, builder, argument, values, storage, model, llvmFunctions);
                    arguments.Add(CoerceValue(builder, value, model.ExpressionTypes[argument],
                        function.Parameters[index].Type, model));
                }
                return EmitCallOrInvoke(builder, llvmFunctions[function].FunctionType, interfaceTarget,
                    arguments.ToArray(), function.ReturnType.IsVoid ? string.Empty : "interface.call");
            }
            // A method call through a null class reference raises NullReferenceException, even when the method body
            // never touches the receiver. `this` inside a method is never null.
            var callee = model.Classes[receiverType.StructName!];
            if (receiver != "this" && !callee.IsValue)
                EmitNullReferenceCheck(builder, receiverValue, function.Name, "called", model, llvmFunctions);
            arguments.Add(receiverValue);
        }
        for (var index = 0; index < argumentsWithDefaults.Count; index++)
        {
            var argument = argumentsWithDefaults[index];
            var value = EmitExpression(context, builder, argument, values, storage, model, llvmFunctions);
            value = CoerceValue(builder, value, model.ExpressionTypes[argument], function.Parameters[index].Type, model);
            if (UsesPointerStringAbi(function) && function.Parameters[index].Type.Kind == SemanticTypeKind.String)
            {
                var storagePointer = BuildEntryAlloca(builder, ToLlvmStringType(), "cabi.string");
                builder.BuildStore(value, storagePointer);
                value = storagePointer;
            }

            if (argument is IdentifierExpression identifier && function.Parameters[index].Type.IsClass)
                RemoveOwned(identifier.Name);
            arguments.Add(value);
        }

        if (ReturnsStringThroughPointer(function))
        {
            var result = BuildEntryAlloca(builder, ToLlvmStringType(), "cabi.result");
            arguments.Add(result);
            EmitCallOrInvoke(builder, llvmFunctions[function].FunctionType, llvmFunctions[function],
                arguments.ToArray(), string.Empty);
            return builder.BuildLoad2(ToLlvmStringType(), result, "string.result");
        }

        var resultName = function.ReturnType.IsVoid ? string.Empty : "call";
        // A base call is direct, even when the method is virtual: the base class's own implementation runs.
        if (method && (function.Declaration.IsVirtual || function.Declaration.IsOverride) && !model.BaseCalls.Contains(expression))
        {
            var receiverType = NormalizeAggregateType(model.CallReceiverTypes[expression], model);
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
                    LLVMTypeRef.CreatePointer(_context.Int8Type, 0), tableSlot, "vtable.target");
                return EmitCallOrInvoke(builder, llvmFunctions[function].FunctionType, rawTarget,
                    arguments.ToArray(), resultName);
            }
        }
        return EmitCallOrInvoke(builder, llvmFunctions[function].FunctionType, llvmFunctions[function],
            arguments.ToArray(), resultName);
    }

    // `receiver.name(args)` on a primitive, string, or struct receiver: the receiver is the first argument.
    private LLVMValueRef EmitReceiverArgumentCall(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        CallExpression expression,
        FunctionSymbol function,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var argumentsWithDefaults = ArgumentsInParameterOrder(model, expression, function.Parameters.Skip(1).ToList(), expression.Arguments);
        var receiverType = NormalizeAggregateType(model.CallReceiverTypes[expression], model);
        LLVMValueRef receiverValue;
        if (expression.Receiver is not null)
        {
            receiverValue = EmitExpression(context, builder, expression.Receiver, values, storage, model, llvmFunctions);
        }
        else
        {
            var receiverName = expression.Name[..expression.Name.LastIndexOf('.')];
            receiverValue = values.TryGetValue(receiverName, out var direct)
                ? direct
                : builder.BuildLoad2(ToLlvmType(receiverType, model), storage[receiverName], receiverName);
        }
        var arguments = new List<LLVMValueRef> { receiverValue };
        for (var index = 0; index < argumentsWithDefaults.Count; index++)
        {
            var argument = argumentsWithDefaults[index];
            var value = EmitExpression(context, builder, argument, values, storage, model, llvmFunctions);
            arguments.Add(CoerceValue(builder, value, model.ExpressionTypes[argument],
                function.Parameters[index + 1].Type, model));
        }
        return EmitCallOrInvoke(builder, llvmFunctions[function].FunctionType, llvmFunctions[function],
            arguments.ToArray(), function.ReturnType.IsVoid ? string.Empty : "call");
    }

    // The arguments of a call in the order of the parameters they fill. The binding is the analyzer's: a named argument is
    // emitted where its parameter is, and an omitted parameter takes its default. A call without a binding is in position.
    private static IReadOnlyList<Expression> ArgumentsInParameterOrder(
        SemanticModel model, SyntaxNode call, IReadOnlyList<ParameterSymbol> parameters, IReadOnlyList<Expression> arguments)
    {
        if (!model.ArgumentOrders.TryGetValue(call, out var order))
            return ArgumentsWithDefaults(parameters, arguments);
        return order.Select((index, position) => index < 0
            ? parameters[position].Declaration.DefaultValue!
            : arguments[index] is NamedArgumentExpression named ? named.Value : arguments[index]).ToList();
    }

    // The arguments of a call followed by the default of each parameter it omits, so that every parameter has an argument.
    private static IReadOnlyList<Expression> ArgumentsWithDefaults(
        IReadOnlyList<ParameterSymbol> parameters, IReadOnlyList<Expression> arguments) =>
        arguments.Concat(parameters.Skip(arguments.Count).Select(parameter => parameter.Declaration.DefaultValue!)).ToList();

    // Calls a runtime helper that does not throw. Inside a Windows funclet the call must carry the funclet bundle;
    // without it the callee does not get the funclet's frame, and the code faults at run time.
    private LLVMValueRef BuildNoThrowCall(LLVMBuilderRef builder, LLVMValueRef function,
        LLVMValueRef[] arguments, string resultName = "")
    {
        if (_usesWindowsSeh && _exceptionContexts.Count > 0 && _exceptionContexts.Peek().Pad.Handle != IntPtr.Zero)
        {
            using var funclet = LLVMOperandBundleRef.Create("funclet",
                new LLVMValueRef[] { _exceptionContexts.Peek().Pad });
            return builder.BuildCallWithOperandBundles(function.FunctionType, function, arguments,
                new LLVMOperandBundleRef[] { funclet }, resultName);
        }
        return builder.BuildCall2(function.FunctionType, function, arguments, resultName);
    }

    private LLVMValueRef EmitCallOrInvoke(
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
        // A context without an unwind target only runs code that continues unwinding by itself, so the call is plain.
        if (exceptionContext.Unwind.Handle == IntPtr.Zero)
            return builder.BuildCall2(functionType, target, arguments, resultName);
        var normal = _currentFunction.AppendBasicBlock("invoke.continue");
        result = builder.BuildInvoke2(functionType, target, arguments, normal,
            exceptionContext.Unwind, resultName);
        builder.PositionAtEnd(normal);
        return result;
    }

}
