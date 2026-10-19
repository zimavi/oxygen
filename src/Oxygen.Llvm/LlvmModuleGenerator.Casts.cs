using System.Globalization;
using System.Text.Json;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    private static LLVMTypeRef getterType(FunctionSymbol getter, SemanticModel model) =>
        ToLlvmFunctionType(getter, model);

    private static LLVMValueRef EmitCast(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        CastExpression cast,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var operand = EmitExpression(context, builder, cast.Operand, values, storage, model, llvmFunctions);
        var targetType = model.ExpressionTypes[cast];
        var operandType = model.ExpressionTypes[cast.Operand];
        if (IsReferenceType(targetType, model) && IsReferenceType(operandType, model))
            return EmitReferenceCast(builder, operand, operandType, targetType, model);
        if (targetType.IsPointer && operandType.IsPointer)
        {
            return builder.BuildBitCast(
                operand,
                ToLlvmType(targetType, model),
                "ptr.cast");
        }
        if (targetType.IsPointer && operandType.Primitive is PrimitiveType.USize or PrimitiveType.ISize)
            return builder.BuildIntToPtr(operand, ToLlvmType(targetType, model), "int.to.ptr");
        if (operandType.IsPointer && targetType.Primitive is PrimitiveType.USize or PrimitiveType.ISize)
            return builder.BuildPtrToInt(operand, ToLlvmType(targetType, model), "ptr.to.int");
        if (targetType.IsNumeric && operandType.IsNumeric)
            return EmitNumericConversion(builder, operand, operandType, targetType, model);
        if (!targetType.IsEnum)
            return RequireSameRepresentation(operand, operandType, targetType, model);

        if (cast.Operand is IntegerLiteralExpression)
            return RequireSameRepresentation(operand, operandType, targetType, model);

        var enumSymbol = model.Enums[targetType.EnumName!];
        var enumLlvmType = ToLlvmType(targetType, model);
        LLVMValueRef valid = LLVMValueRef.CreateConstInt(_context.Int1Type, 0, false);
        foreach (var member in enumSymbol.Members)
        {
            var candidate = LLVMValueRef.CreateConstInt(enumLlvmType, unchecked((ulong)member.Value), false);
            var match = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, operand, candidate, "enum.member");
            valid = builder.BuildOr(valid, match, "enum.valid");
        }
        var invalid = builder.BuildNot(valid, "enum.invalid");
        var failureBlock = _currentFunction.AppendBasicBlock("enum.cast.failure");
        var continueBlock = _currentFunction.AppendBasicBlock("enum.cast.continue");
        builder.BuildCondBr(invalid, failureBlock, continueBlock);
        builder.PositionAtEnd(failureBlock);
        var failureValue = enumSymbol.UnderlyingType is PrimitiveType.I64 or PrimitiveType.U64
            ? operand
            : builder.BuildZExt(operand, _context.Int64Type, "enum.value");
        BuildNoThrowCall(builder, _enumCastFailure, [failureValue]);
        builder.BuildUnreachable();
        builder.PositionAtEnd(continueBlock);
        return RequireSameRepresentation(operand, operandType, targetType, model);
    }

    // Semantic analysis admits only the cast pairs lowered above. The one case left to pass through is a value that
    // already has the target's LLVM type, such as an enum and its underlying type. Any other pair is a compiler bug,
    // so it is reported rather than turned into IR of the wrong type.
    private static LLVMValueRef RequireSameRepresentation(
        LLVMValueRef operand,
        SemanticType operandType,
        SemanticType targetType,
        SemanticModel model)
    {
        if (operand.TypeOf != ToLlvmType(targetType, model))
            throw new InvalidOperationException(
                $"Internal compiler error: no lowering for cast from '{operandType}' to '{targetType}'.");
        return operand;
    }

    private static bool IsReferenceType(SemanticType type, SemanticModel model) =>
        (type.IsClass && !IsValueClass(type, model)) || type.IsInterface;

    private static void InitializeRuntimeTypeInfo(
        LLVMBuilderRef builder,
        string typeName,
        string? parentType,
        uint kind,
        SemanticModel model)
    {
        var info = _runtimeTypeInfos[typeName];
        var name = builder.BuildGlobalStringPtr(typeName, $"oxygen.type.name.{typeName}");
        builder.BuildStore(name, builder.BuildStructGEP2(_runtimeTypeInfoType, info, 0, "type.name"));
        builder.BuildStore(
            LLVMValueRef.CreateConstInt(_context.Int32Type, kind, false),
            builder.BuildStructGEP2(_runtimeTypeInfoType, info, 1, "type.kind"));
        builder.BuildStore(
            LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false),
            builder.BuildStructGEP2(_runtimeTypeInfoType, info, 2, "type.flags"));
        var parent = parentType is null
            ? LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(_context.Int8Type, 0))
            : GetTypeName(builder, parentType);
        builder.BuildStore(parent, builder.BuildStructGEP2(_runtimeTypeInfoType, info, 3, "type.parent"));
        // Only advertise a message field when the generated class layout
        // actually contains one.  Name-based inference made an unrelated
        // class such as MyException unsafe for the native unhandled reporter.
        var messageField = model.Classes.TryGetValue(typeName, out var cls)
            ? cls.Fields.Select((field, index) => (field, index))
                .FirstOrDefault(item => item.field.Name == "message"
                    && item.field.Type.Kind == SemanticTypeKind.String)
            : default;
        // The offset is the field's address in the object's LLVM layout, so padding and the pointer size come from the
        // module's data layout rather than a second copy of those rules.
        var messageOffset = messageField.field is null
            ? LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false)
            : builder.BuildPtrToInt(
                builder.BuildStructGEP2(_classTypes[typeName],
                    LLVMValueRef.CreateConstNull(ClassPointerType(typeName)),
                    (uint)FieldOffset(cls!, model, messageField.index), "message.address"),
                _context.Int32Type, "message.offset");
        builder.BuildStore(messageOffset, builder.BuildStructGEP2(_runtimeTypeInfoType, info, 4, "type.message.offset"));
        // Releasing an object of this type calls the nearest destructor in its class chain, or nothing when there is none.
        builder.BuildStore(FindDestructor(typeName, model) ?? LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(_context.Int8Type, 0)),
            builder.BuildStructGEP2(_runtimeTypeInfoType, info, 5, "type.destructor"));
    }

    // The destructor that releases an object of `typeName`: the nearest one in its class chain, or null when there is none.
    private static LLVMValueRef? FindDestructor(string typeName, SemanticModel model)
    {
        for (var cls = model.Classes.GetValueOrDefault(typeName); cls is not null;
             cls = cls.BaseTypeName is null ? null : model.Classes.GetValueOrDefault(cls.BaseTypeName))
            if (_destructors.TryGetValue(cls.Name, out var destructor))
                return destructor;
        return null;
    }

    // Releases the object `reference` points to through its runtime type. The destructor stored in its type information
    // runs first, so a reference typed as a base class still runs the derived destructor; then the memory is freed.
    private static void EmitDestroyAndFree(LLVMBuilderRef builder, LLVMValueRef reference)
    {
        var pointerType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
        var function = builder.InsertBlock.Parent;
        var typeInfo = builder.BuildLoad2(pointerType, reference, "dispatch.type.info");
        var destructorSlot = builder.BuildStructGEP2(_runtimeTypeInfoType, typeInfo, 5, "dispatch.destructor.slot");
        var destructor = builder.BuildLoad2(pointerType, destructorSlot, "dispatch.destructor");
        var hasDestructor = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, destructor,
            LLVMValueRef.CreateConstNull(pointerType), "dispatch.has.destructor");
        var call = function.AppendBasicBlock("destroy.call");
        var release = function.AppendBasicBlock("destroy.release");
        builder.BuildCondBr(hasDestructor, call, release);
        builder.PositionAtEnd(call);
        builder.BuildCall2(LLVMTypeRef.CreateFunction(_context.VoidType, [pointerType], false), destructor, [reference]);
        builder.BuildBr(release);
        builder.PositionAtEnd(release);
        builder.BuildCall2(_oxygenFree.FunctionType, _oxygenFree,
            [builder.BuildBitCast(reference, pointerType, "free.memory")]);
    }

    private static LLVMValueRef AddRuntimeTypeInfoGlobal(LLVMModuleRef module, string typeName)
    {
        var info = module.AddGlobal(_runtimeTypeInfoType, $"oxygen.type.info.{typeName}");
        info.Linkage = LLVMLinkage.LLVMInternalLinkage;
        info.Initializer = LLVMValueRef.CreateConstNull(_runtimeTypeInfoType);
        return info;
    }

    private static LLVMValueRef GetTypeName(LLVMBuilderRef builder, string typeName)
    {
        return _runtimeTypeInfos[typeName];
    }

    private static LLVMValueRef EmitReferenceCast(
        LLVMBuilderRef builder,
        LLVMValueRef operand,
        SemanticType source,
        SemanticType target,
        SemanticModel model)
    {
        if (source.IsClass && target.IsInterface)
        {
            var result = LLVMValueRef.CreateConstNull(ToLlvmType(target, model));
            result = builder.BuildInsertValue(
                result,
                builder.BuildBitCast(operand, LLVMTypeRef.CreatePointer(_context.Int8Type, 0), "interface.data"),
                0,
                "interface.data");
            var table = _interfaceTables[(source.StructName!, target.StructName!)];
            return builder.BuildInsertValue(result,
                builder.BuildBitCast(table, LLVMTypeRef.CreatePointer(_context.Int8Type, 0), "interface.table"),
                1, "interface.table");
        }

        var objectValue = source.IsInterface
            ? builder.BuildExtractValue(operand, 0, "cast.data")
            : operand;
        if (source.IsInterface)
            objectValue = builder.BuildBitCast(
                objectValue,
                LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                "cast.object");

        if (target.IsClass && (!source.IsClass || IsClassDerivedFrom(target.StructName!, source.StructName!, model)))
        {
            var possibleTypes = model.Classes.Keys
                .Where(candidate => !model.Classes[candidate].IsGeneric)
                .Where(candidate => IsClassDerivedFrom(candidate, target.StructName!, model))
                .Where(candidate => !source.IsInterface
                    || ClassImplementsInterface(candidate, source.StructName!, model))
                .ToArray();
            var valid = LLVMValueRef.CreateConstInt(_context.Int8Type, 0, false);
            foreach (var possibleType in possibleTypes)
            {
                var candidate = BuildNoThrowCall(builder, _isInstance, [objectValue, GetTypeName(builder, possibleType)], "cast.valid");
                valid = builder.BuildOr(valid, candidate, "cast.valid");
            }
            var invalid = builder.BuildICmp(
                LLVMIntPredicate.LLVMIntEQ,
                valid,
                LLVMValueRef.CreateConstInt(_context.Int8Type, 0, false),
                "cast.invalid");
            var failureBlock = _currentFunction.AppendBasicBlock("cast.failure");
            var continueBlock = _currentFunction.AppendBasicBlock("cast.continue");
            builder.BuildCondBr(invalid, failureBlock, continueBlock);
            builder.PositionAtEnd(failureBlock);
            var sourceName = source.IsClass
                ? GetTypeName(builder, source.StructName!)
                : builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                    objectValue, "interface.type");
            BuildNoThrowCall(builder, _invalidCast, [sourceName, GetTypeName(builder, target.StructName!)]);
            builder.BuildUnreachable();
            builder.PositionAtEnd(continueBlock);
        }

        return target.IsClass
            ? builder.BuildBitCast(objectValue, ToLlvmType(target, model), "class.cast")
            : objectValue;
    }

    private static LLVMValueRef EmitIsExpression(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        IsExpression expression,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var operand = EmitExpression(context, builder, expression.Operand, values, storage, model, llvmFunctions);
        return EmitRuntimeTypeCheck(builder, operand, model.ExpressionTypes[expression.Operand],
            FromTypeSyntax(expression.TargetType, model), model);
    }

    private static LLVMValueRef EmitRuntimeTypeCheck(
        LLVMBuilderRef builder,
        LLVMValueRef operand,
        SemanticType source,
        SemanticType target,
        SemanticModel model)
    {
        var objectValue = source.IsInterface
            ? builder.BuildExtractValue(operand, 0, "pattern.data")
            : builder.BuildBitCast(operand, LLVMTypeRef.CreatePointer(_context.Int8Type, 0), "pattern.object");
        var possibleTypes = model.Classes.Keys
            .Where(candidate => !model.Classes[candidate].IsGeneric)
            .Where(candidate => target.IsClass
                ? IsClassDerivedFrom(candidate, target.StructName!, model)
                : ClassImplementsInterface(candidate, target.StructName!, model))
            .ToArray();
        var match = LLVMValueRef.CreateConstInt(_context.Int8Type, 0, false);
        foreach (var possibleType in possibleTypes)
        {
            var candidate = BuildNoThrowCall(builder, _isInstance, [objectValue, GetTypeName(builder, possibleType)], "type.match");
            match = builder.BuildOr(match, candidate, "type.match");
        }
        return match;
    }

    private static LLVMValueRef EmitPatternBindingValue(
        LLVMBuilderRef builder,
        LLVMValueRef operand,
        SemanticType source,
        SemanticType target,
        SemanticModel model)
    {
        if (target.IsInterface)
        {
            if (source.IsInterface)
                return operand;
            var result = LLVMValueRef.CreateConstNull(ToLlvmType(target, model));
            result = builder.BuildInsertValue(result,
                builder.BuildBitCast(operand, LLVMTypeRef.CreatePointer(_context.Int8Type, 0), "pattern.data"),
                0, "pattern.data");
            var table = _interfaceTables[(source.StructName!, target.StructName!)];
            return builder.BuildInsertValue(result,
                builder.BuildBitCast(table, LLVMTypeRef.CreatePointer(_context.Int8Type, 0), "pattern.table"),
                1, "pattern.table");
        }
        var objectValue = source.IsInterface
            ? builder.BuildExtractValue(operand, 0, "pattern.object")
            : operand;
        return builder.BuildBitCast(objectValue, ToLlvmType(target, model), "pattern.cast");
    }

    private static bool IsClassDerivedFrom(string sourceName, string targetName, SemanticModel model)
    {
        if (string.Equals(sourceName, targetName, StringComparison.Ordinal))
            return true;
        return model.Classes.TryGetValue(sourceName, out var source)
            && source.BaseTypeName is not null
            && model.Classes.ContainsKey(source.BaseTypeName)
            && IsClassDerivedFrom(source.BaseTypeName, targetName, model);
    }

    private static bool ClassImplementsInterface(string className, string interfaceName, SemanticModel model)
    {
        if (!model.Classes.TryGetValue(className, out var cls))
            return false;
        if (cls.InterfaceNames.Contains(interfaceName, StringComparer.Ordinal))
            return true;
        return cls.BaseTypeName is not null
            && model.Classes.ContainsKey(cls.BaseTypeName)
            && ClassImplementsInterface(cls.BaseTypeName, interfaceName, model);
    }

    // The size is LLVM's own answer for the module's data layout, which is also what the struct GEPs use.
    private static LLVMValueRef EmitSizeOf(SizeOfExpression expression, SemanticModel model)
    {
        var type = expression.TargetType is not null
            ? FromTypeSyntax(expression.TargetType, model)
            : model.ExpressionTypes[expression.Operand!];
        return ToLlvmType(type, model).SizeOf;
    }

    private static int IntegerBitWidth(PrimitiveType type) =>
        type switch
        {
            PrimitiveType.I8 or PrimitiveType.U8 or PrimitiveType.Bool => 8,
            PrimitiveType.I16 or PrimitiveType.U16 => 16,
            PrimitiveType.I32 or PrimitiveType.U32 => 32,
            PrimitiveType.I64 or PrimitiveType.U64 or PrimitiveType.USize or PrimitiveType.ISize => 64,
            _ => throw new InvalidOperationException($"'{type}' is not an integer type.")
        };

}
