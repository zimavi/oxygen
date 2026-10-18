using System.Globalization;
using System.Text.Json;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    private static LLVMValueRef EmitExpression(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        Expression expression,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions) =>
        expression switch
        {
            IdentifierExpression identifier when values.TryGetValue(identifier.Name, out var value) => value,
            IdentifierExpression identifier => builder.BuildLoad2(
                ToLlvmType(model.ExpressionTypes[identifier], model),
                storage[identifier.Name],
                identifier.Name),
            ThisExpression thisExpression => values["this"],
            IntegerLiteralExpression integer when model.ExpressionTypes[integer].IsPointer =>
                LLVMValueRef.CreateConstNull(ToLlvmType(model.ExpressionTypes[integer], model)),
            IntegerLiteralExpression integer => LLVMValueRef.CreateConstInt(
                ToLlvmType(model.ExpressionTypes[integer], model), ulong.Parse(NumericValue(integer.Lexeme), CultureInfo.InvariantCulture), false),
            FloatingPointLiteralExpression floating => LLVMValueRef.CreateConstRealOfString(
                ToLlvmType(model.ExpressionTypes[floating], model), NumericValue(floating.Lexeme)),
            StringLiteralExpression literal => EmitStringLiteral(builder, literal),
            BooleanLiteralExpression boolean => LLVMValueRef.CreateConstInt(
                LLVMTypeRef.Int1, boolean.Value ? 1UL : 0UL, false),
            StructLiteralExpression literal => EmitStructLiteral(
                context, builder, literal, values, storage, model, llvmFunctions),
            ArrayLiteralExpression literal => EmitArrayLiteral(
                context, builder, literal, values, storage, model, llvmFunctions),
            UnionConstructionExpression construction => EmitUnionConstruction(
                context, builder, construction, values, storage, model, llvmFunctions),
            ClassConstructionExpression construction => EmitClassConstructionExpression(
                context, builder, construction, values, storage, model, llvmFunctions),
            NewExpression creation => EmitNewExpression(
                context, builder, creation, values, storage, model, llvmFunctions),
            IndexExpression index => builder.BuildLoad2(ToLlvmType(model.ExpressionTypes[index], model),
                EmitArrayAddress(context, builder, index, values, storage, model, llvmFunctions), "array.element"),
            CallExpression { Name: "length", Arguments: [var target] } => LLVMValueRef.CreateConstInt(
                LLVMTypeRef.Int32, (ulong)model.ExpressionTypes[target].ArrayLength, false),
            MemberAccessExpression member => EmitMemberAccess(
                context, builder, member, values, storage, model, llvmFunctions),
            CastExpression cast => EmitCast(
                context, builder, cast, values, storage, model, llvmFunctions),
            IsExpression pattern => EmitIsExpression(
                context, builder, pattern, values, storage, model, llvmFunctions),
            SizeOfExpression sizeOf => EmitSizeOf(sizeOf, model),
            BinaryExpression binary => EmitBinary(
                context, builder, binary, values, storage, model, llvmFunctions),
            ConditionalExpression conditional => EmitConditional(
                context, builder, conditional, values, storage, model, llvmFunctions),
            UnaryExpression unary => EmitUnary(context, builder, unary, values, storage, model, llvmFunctions),
            CallExpression call => EmitCall(context, builder, call, values, storage, model, llvmFunctions),
            _ => throw new InvalidOperationException($"Unsupported expression '{expression.GetType().Name}'.")
        };

    private static bool IsNativeSwitchType(SemanticType type) =>
        type.IsInteger || type.IsEnum || type.Primitive == PrimitiveType.Bool;

    private static LLVMValueRef EmitSwitchConstant(
        Expression expression,
        SemanticType selectorType,
        LLVMContextRef context,
        LLVMBuilderRef builder,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var label = EmitExpression(
            context, builder, expression, values, storage, model, llvmFunctions);
        return CoerceValue(builder, label, model.ExpressionTypes[expression], selectorType, model);
    }

    private static LLVMValueRef EmitSwitchEquality(
        LLVMBuilderRef builder,
        LLVMValueRef selector,
        LLVMValueRef label,
        SemanticType selectorType,
        SemanticModel model)
    {
        if (selectorType.Kind == SemanticTypeKind.String)
        {
            var selectorPointer = builder.BuildExtractValue(selector, 0, "switch.string.pointer");
            var labelPointer = builder.BuildExtractValue(label, 0, "switch.label.pointer");
            var selectorLength = builder.BuildExtractValue(selector, 1, "switch.string.length");
            var labelLength = builder.BuildExtractValue(label, 1, "switch.label.length");
            var sameLength = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, selectorLength, labelLength,
                "switch.string.length.match");
            var contents = builder.BuildCall2(_memcmp.FunctionType, _memcmp,
                new LLVMValueRef[] { selectorPointer, labelPointer, selectorLength }, "switch.string.compare");
            var sameContents = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, contents,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false), "switch.string.match");
            return builder.BuildAnd(sameLength, sameContents, "switch.string.equal");
        }

        if (selectorType.IsPointer)
            return builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, selector, label, "switch.pointer.equal");
        return builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, selector, label, "switch.equal");
    }

    // The class declaring the property accessed as `this.<member>`: the class of `this` or its
    // nearest base class that declares it. Null when the member is a field.
    private static ClassSymbol? ThisPropertyOwner(MemberAccessExpression member, SemanticModel model)
    {
        var thisType = NormalizeAggregateType(model.ExpressionTypes[member.Target], model);
        for (var cls = model.Classes.GetValueOrDefault(thisType.StructName!); cls is not null;
             cls = cls.BaseTypeName is null ? null : model.Classes.GetValueOrDefault(cls.BaseTypeName))
        {
            if (cls.DeclaredProperties.Any(property => property.Name == member.MemberName))
                return cls;
        }
        return null;
    }

    private static LLVMValueRef EmitNewExpression(
        LLVMContextRef context, LLVMBuilderRef builder, NewExpression expression,
        IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var className = NormalizeAggregateType(model.ExpressionTypes[expression], model).StructName
            ?? expression.Type.NamedType
            ?? throw new InvalidOperationException("Class construction requires a named type.");
        return EmitClassConstruction(context, builder, className, expression.Arguments, true,
            model.ConstructorSelections[expression], values, storage, model, llvmFunctions);
    }

    private static LLVMValueRef EmitClassConstructionExpression(
        LLVMContextRef context, LLVMBuilderRef builder, ClassConstructionExpression expression,
        IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions) =>
        EmitClassConstruction(context, builder,
            NormalizeAggregateType(model.ExpressionTypes[expression], model).StructName ?? expression.TypeName,
            expression.Arguments, false, model.ConstructorSelections[expression],
            values, storage, model, llvmFunctions);

    private static LLVMValueRef EmitClassConstruction(
        LLVMContextRef context, LLVMBuilderRef builder, string className,
        IReadOnlyList<Expression> arguments, bool heapAllocated, int constructorIndex,
        IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        className = NormalizeAggregateType(SemanticType.Class(className), model).StructName ?? className;
        var cls = model.Classes[className];
        var classType = ToLlvmType(SemanticType.Class(className), model);
        LLVMValueRef instance;
        if (heapAllocated)
        {
            // The object's own layout (header and fields), not SizeOf(class), which is the size of
            // a class reference.
            var raw = builder.BuildCall2(_oxygenAlloc.FunctionType, _oxygenAlloc,
                new[] { _classTypes[className].SizeOf }, "object");
            instance = builder.BuildBitCast(raw, ClassPointerType(className), "object.cast");
        }
        else
        {
            instance = builder.BuildAlloca(_classTypes[className], "object.addr");
        }
        if (!cls.IsValue)
        {
            var vtable = builder.BuildStructGEP2(_classTypes[className], instance, 0, "object.vtable");
            builder.BuildStore(GetTypeName(builder, className), vtable);
            var virtualMethods = GetVirtualMethods(cls, model);
            for (var slot = 0; slot < _virtualSlotCount; slot++)
            {
                var slotAddress = builder.BuildStructGEP2(_classTypes[className], instance, (uint)slot + 1,
                    "object.vtable.slot");
                builder.BuildStore(LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)),
                    slotAddress);
            }
            for (var slot = 0; slot < virtualMethods.Count; slot++)
            {
                var slotAddress = builder.BuildStructGEP2(_classTypes[className], instance, (uint)slot + 1,
                    "object.vtable.slot");
                var target = llvmFunctions[virtualMethods[slot]];
                builder.BuildStore(builder.BuildBitCast(target,
                    LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "vtable.method"), slotAddress);
            }
        }
        var ctor = cls.Constructors[constructorIndex];
        var args = new List<LLVMValueRef> { instance };
        for (var i = 0; i < arguments.Count; i++)
            args.Add(CoerceValue(builder, EmitExpression(context, builder, arguments[i], values, storage, model, llvmFunctions),
                model.ExpressionTypes[arguments[i]], ctor.Parameters[i].Type, model));
        builder.BuildCall2(_constructors[ConstructorKey(cls, ctor)].FunctionType,
            _constructors[ConstructorKey(cls, ctor)], args.ToArray(), (ReadOnlySpan<char>)string.Empty);
        return cls.IsValue ? builder.BuildLoad2(classType, instance, "value") : instance;
    }

    private static LLVMValueRef EmitUnionConstruction(
        LLVMContextRef context, LLVMBuilderRef builder, UnionConstructionExpression construction,
        IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var unionType = NormalizeAggregateType(model.ExpressionTypes[construction], model);
        var union = model.Unions[unionType.EnumName ?? construction.UnionName];
        var index = union.Cases.Select((item, i) => (item, i)).First(pair => pair.item.Name == construction.CaseName).i;
        var result = LLVMValueRef.CreateConstNull(ToLlvmType(unionType, model));
        result = builder.BuildInsertValue(result,
            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)index, false), 0, "union.tag");
        if (construction.Arguments.Count == 1)
            result = builder.BuildInsertValue(result,
                CoerceValue(builder,
                    EmitExpression(context, builder, construction.Arguments[0], values, storage, model, llvmFunctions),
                    model.ExpressionTypes[construction.Arguments[0]], UnionPayloadType(unionType, model), model),
                1, "union.payload");
        return result;
    }

    private static LLVMValueRef EmitArrayLiteral(LLVMContextRef context, LLVMBuilderRef builder, ArrayLiteralExpression literal,
        IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var result = LLVMValueRef.CreateConstNull(ToLlvmType(model.ExpressionTypes[literal], model));
        for (var i = 0; i < literal.Elements.Count; i++)
            result = builder.BuildInsertValue(result, EmitExpression(context, builder, literal.Elements[i], values, storage, model, llvmFunctions), (uint)i, "array.element");
        return result;
    }

    private static LLVMValueRef EmitArrayAddress(LLVMContextRef context, LLVMBuilderRef builder, IndexExpression index,
        IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var targetType = model.ExpressionTypes[index.Target];
        if (targetType.IsPointer)
        {
            var pointerValue = EmitExpression(context, builder, index.Target, values, storage, model, llvmFunctions);
            var pointerIndex = EmitExpression(context, builder, index.Index, values, storage, model, llvmFunctions);
            if (model.ExpressionTypes[index.Index].Primitive != PrimitiveType.I64)
                pointerIndex = builder.BuildSExt(pointerIndex, LLVMTypeRef.Int64, "pointer.index");
            return builder.BuildGEP2(ToLlvmType(targetType.Pointee!, model), pointerValue,
                [pointerIndex], (ReadOnlySpan<char>)"pointer.address");
        }

        var arrayType = targetType;
        LLVMValueRef pointer;
        if (index.Target is IdentifierExpression identifier && storage.TryGetValue(identifier.Name, out var identifierStorage))
            pointer = identifierStorage;
        else if (index.Target is IndexExpression nested)
            pointer = EmitArrayAddress(context, builder, nested, values, storage, model, llvmFunctions);
        else
            throw new InvalidOperationException("Array indexing requires addressable storage.");
        var llvmIndex = EmitExpression(context, builder, index.Index, values, storage, model, llvmFunctions);
        if (model.ExpressionTypes[index.Index].Primitive != PrimitiveType.I64)
            llvmIndex = builder.BuildSExt(llvmIndex, LLVMTypeRef.Int64, "array.index");
        var length = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)arrayType.ArrayLength, false);
        var negative = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, llvmIndex,
            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false), "array.negative");
        var tooLarge = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, llvmIndex, length, "array.too.large");
        var invalid = builder.BuildOr(negative, tooLarge, "array.invalid");
        var failureBlock = _currentFunction.AppendBasicBlock("array.bounds.failure");
        var continueBlock = _currentFunction.AppendBasicBlock("array.bounds.continue");
        builder.BuildCondBr(invalid, failureBlock, continueBlock);
        builder.PositionAtEnd(failureBlock);
        builder.BuildCall2(_arrayBoundsFailure.FunctionType, _arrayBoundsFailure, [llvmIndex, length]);
        builder.BuildUnreachable();
        builder.PositionAtEnd(continueBlock);
        return builder.BuildGEP2(ToLlvmType(arrayType, model), pointer,
            new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false), llvmIndex }, (ReadOnlySpan<char>)"array.address");
    }

    private static LLVMValueRef EmitStructLiteral(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        StructLiteralExpression literal,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var structureType = NormalizeAggregateType(model.ExpressionTypes[literal], model);
        var result = LLVMValueRef.CreateConstNull(ToLlvmType(structureType, model));
        var structure = model.Structs[structureType.StructName ?? literal.TypeName];
        foreach (var field in literal.Fields)
        {
            var index = structure.Fields.Select((candidate, index) => (candidate, index))
                .First(pair => pair.candidate.Name == field.Name).index;
            result = builder.BuildInsertValue(
                result,
                EmitExpression(context, builder, field.Value, values, storage, model, llvmFunctions),
                (uint)index,
                $"struct.{field.Name}");
        }
        return result;
    }

    private static LLVMValueRef EmitMemberAccess(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        MemberAccessExpression member,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        if (member.Target is IdentifierExpression enumIdentifier
            && model.Enums.TryGetValue(enumIdentifier.Name, out var enumSymbol))
        {
            var value = enumSymbol.Members.First(candidate => candidate.Name == member.MemberName).Value;
            return LLVMValueRef.CreateConstInt(
                ToLlvmType(SemanticType.Enum(enumSymbol.Name), model),
                unchecked((ulong)value),
                false);
        }
        if (member.Target is ThisExpression)
        {
            var owner = ThisPropertyOwner(member, model);
            if (owner is not null)
            {
                var property = owner.DeclaredProperties.First(property => property.Name == member.MemberName);
                if (_propertyAccessors.TryGetValue((owner.Name, property.Name, PropertyAccessorKind.Get), out var getter))
                    return EmitCallOrInvoke(builder, ToLlvmFunctionType(getter, model), llvmFunctions[getter],
                        [values["this"]], "property.get");
            }
            var address = storage["this." + member.MemberName];
            return builder.BuildLoad2(ToLlvmType(model.ExpressionTypes[member], model), address,
                $"field.{member.MemberName}");
        }
        var target = EmitExpression(context, builder, member.Target, values, storage, model, llvmFunctions);
        var targetType = model.ExpressionTypes[member.Target];
        if (targetType.IsInterface)
        {
            var iface = model.Interfaces[targetType.StructName!];
            var getter = _propertyAccessors[(iface.Name, member.MemberName, PropertyAccessorKind.Get)];
            var slot = _interfaceSlots[iface.Name].ToList().IndexOf(getter);
            var table = builder.BuildExtractValue(target, 1, "interface.table");
            var objectValue = builder.BuildExtractValue(target, 0, "interface.data");
            var tableEntry = builder.BuildGEP2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), table,
                [LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)slot, false)],
                (ReadOnlySpan<char>)"interface.property.slot");
            var rawTarget = builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                tableEntry, "interface.property.call");
            return EmitCallOrInvoke(builder, ToLlvmFunctionType(getter, model), rawTarget,
                [objectValue], "interface.property");
        }
        if (targetType.IsClass)
        {
            var cls = model.Classes[targetType.StructName!];
            var property = cls.DeclaredProperties.FirstOrDefault(candidate => candidate.Name == member.MemberName);
            if (property is not null)
            {
                var getter = _propertyAccessors[(cls.Name, property.Name, PropertyAccessorKind.Get)];
                var targetFunction = llvmFunctions[getter];
                var receiver = target;
                if (property.Declaration.IsVirtual || property.Declaration.IsOverride)
                {
                    var slot = GetVirtualMethods(cls, model).Select((candidate, index) => (candidate, index))
                        .First(pair => pair.candidate == getter).index;
                    var slotAddress = builder.BuildStructGEP2(_classTypes[cls.Name], receiver,
                        (uint)slot + 1, "property.vtable.slot");
                    targetFunction = builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), slotAddress,
                        "property.vtable.target");
                }
                return EmitCallOrInvoke(builder, getterType(getter, model), targetFunction, [receiver], "property.get");
            }
            var fieldIndex = cls.Fields.Select((field, index) => (field, index))
                .First(pair => pair.field.Name == member.MemberName).index + 1;
            if (cls.IsValue)
                return builder.BuildExtractValue(target, (uint)(fieldIndex - 1), $"field.{member.MemberName}");
            fieldIndex = FieldOffset(cls, model, fieldIndex - 1);
            var address = builder.BuildStructGEP2(_classTypes[cls.Name], target, (uint)fieldIndex,
                $"field.{member.MemberName}");
            return builder.BuildLoad2(ToLlvmType(model.ExpressionTypes[member], model), address,
                $"field.{member.MemberName}");
        }
        var index = model.Structs[targetType.StructName!].Fields
            .Select((field, index) => (field, index))
            .First(pair => pair.field.Name == member.MemberName).index;
        return builder.BuildExtractValue(target, (uint)index, $"struct.{member.MemberName}");
    }

}
