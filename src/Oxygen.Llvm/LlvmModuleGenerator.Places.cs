using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    // A location that a compound assignment reads and then writes. The parts that find the location, such as an object or an
    // index, are evaluated once, when the place is built, so `x op= y` evaluates x once.
    private sealed record Place(Func<LLVMValueRef> Load, Action<LLVMValueRef> Store);

    private Place EmitPlace(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        Expression target,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var type = ToLlvmType(model.ExpressionTypes[target], model);
        switch (target)
        {
            case IdentifierExpression identifier:
                return MemoryPlace(builder, type, VariableAddress(identifier, storage, model), identifier.Name);
            case IndexExpression index:
                return MemoryPlace(builder, type,
                    EmitArrayAddress(context, builder, index, values, storage, model, llvmFunctions), "element");
            case UnaryExpression { Operator: TokenKind.Star } dereference:
                return MemoryPlace(builder, type,
                    EmitExpression(context, builder, dereference.Operand, values, storage, model, llvmFunctions), "deref");
            case MemberAccessExpression member:
                return EmitMemberPlace(context, builder, member, values, storage, model, llvmFunctions);
            default:
                throw new InvalidOperationException("Internal compiler error: a compound assignment target is not a location.");
        }
    }

    private static Place MemoryPlace(LLVMBuilderRef builder, LLVMTypeRef type, LLVMValueRef address, string name) =>
        new(() => builder.BuildLoad2(type, address, name), value => builder.BuildStore(value, address));

    private Place EmitMemberPlace(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        MemberAccessExpression member,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var memberType = ToLlvmType(model.ExpressionTypes[member], model);
        if (member.Target is ThisExpression)
        {
            var owner = ThisPropertyOwner(member, model);
            if (owner is not null)
            {
                var ownerProperty = owner.DeclaredProperties.First(candidate => candidate.Name == member.MemberName);
                return PropertyPlace(builder, owner, ownerProperty, values["this"], model, llvmFunctions);
            }
            return MemoryPlace(builder, memberType, storage["this." + member.MemberName], member.MemberName);
        }

        var objectType = model.ExpressionTypes[member.Target];
        if (objectType.IsClass && !model.Classes[objectType.StructName!].IsValue)
        {
            var cls = model.Classes[objectType.StructName!];
            var receiver = EmitExpression(context, builder, member.Target, values, storage, model, llvmFunctions);
            EmitNullReferenceCheck(builder, receiver, member.MemberName, "read", model, llvmFunctions);
            var property = cls.DeclaredProperties.FirstOrDefault(candidate => candidate.Name == member.MemberName);
            if (property is not null)
                return PropertyPlace(builder, cls, property, receiver, model, llvmFunctions);
            var fieldIndex = cls.Fields.Select((field, index) => (field, index))
                .First(pair => pair.field.Name == member.MemberName).index;
            var address = builder.BuildStructGEP2(_classTypes[cls.Name], receiver,
                (uint)FieldOffset(cls, model, fieldIndex), $"field.{member.MemberName}");
            return MemoryPlace(builder, memberType, address, member.MemberName);
        }

        // A field of a value class or a struct is part of the value of its variable. The whole value is read and written back
        // with the field replaced, through the place of the object.
        var part = EmitPlace(context, builder, member.Target, values, storage, model, llvmFunctions);
        var partIndex = objectType.IsClass
            ? model.Classes[objectType.StructName!].Fields.Select((field, index) => (field, index))
                .First(pair => pair.field.Name == member.MemberName).index
            : model.Structs[objectType.StructName!].Fields.Select((field, index) => (field, index))
                .First(pair => pair.field.Name == member.MemberName).index;
        return new Place(
            () => builder.BuildExtractValue(part.Load(), (uint)partIndex, member.MemberName),
            value => part.Store(builder.BuildInsertValue(part.Load(), value, (uint)partIndex, "struct.update")));
    }

    // A property of a class object, read through its getter and written through its setter, with the receiver evaluated once.
    private Place PropertyPlace(
        LLVMBuilderRef builder,
        ClassSymbol cls,
        PropertySymbol property,
        LLVMValueRef receiver,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var getter = _propertyAccessors[(cls.Name, property.Name, PropertyAccessorKind.Get)];
        var setter = _propertyAccessors[(cls.Name, property.Name, PropertyAccessorKind.Set)];
        return new Place(
            () => EmitCallOrInvoke(builder, ToLlvmFunctionType(getter, model),
                AccessorTarget(builder, cls, property, getter, receiver, model, llvmFunctions), [receiver], "property.get"),
            value => EmitCallOrInvoke(builder, ToLlvmFunctionType(setter, model),
                AccessorTarget(builder, cls, property, setter, receiver, model, llvmFunctions), [receiver, value],
                string.Empty));
    }

    // The function to call for a property accessor. A virtual or overriding property is called through the receiver's vtable.
    private LLVMValueRef AccessorTarget(
        LLVMBuilderRef builder,
        ClassSymbol cls,
        PropertySymbol property,
        FunctionSymbol accessor,
        LLVMValueRef receiver,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        if (!property.Declaration.IsVirtual && !property.Declaration.IsOverride)
            return llvmFunctions[accessor];
        var slot = GetVirtualMethods(cls, model).Select((candidate, index) => (candidate, index))
            .First(pair => pair.candidate == accessor).index;
        var slotAddress = builder.BuildStructGEP2(_classTypes[cls.Name], receiver, (uint)slot + 1, "property.vtable.slot");
        return builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), slotAddress, "property.vtable.target");
    }

    // x op= y is x = x op y, with x located and evaluated once: the place is located first, then the value, in the order C uses.
    private void EmitCompoundAssignment(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        AssignmentStatement assignment,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var place = EmitPlace(context, builder, assignment.Target, values, storage, model, llvmFunctions);
        var current = place.Load();
        var operand = EmitExpression(context, builder, assignment.Value, values, storage, model, llvmFunctions);
        var targetType = model.ExpressionTypes[assignment.Target];
        var valueType = model.ExpressionTypes[assignment.Value];
        var resultType = !targetType.IsPointer && targetType.IsNumeric && valueType.IsNumeric
            ? CommonNumericType(targetType, valueType)
            : targetType;
        var result = EmitBinaryOperation(builder, CompoundAssignment.BinaryOperator(assignment.Operator), current, targetType,
            operand, valueType, resultType, model);
        place.Store(CoerceValue(builder, result, resultType, targetType, model));
    }
}
