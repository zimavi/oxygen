using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    // Stores into a member of a class object: a property setter, or a field of a heap object. `receiver` is the object
    // reference, or the value of a value class when the member is a property. A null object raises
    // NullReferenceException before the store. Fields of a value class are stored by the caller, into the variable.
    private void EmitClassMemberStore(
        LLVMBuilderRef builder, ClassSymbol cls, LLVMValueRef receiver, MemberAccessExpression member,
        LLVMValueRef assigned, SemanticType assignedType, SemanticType targetType, bool moved, SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var property = cls.DeclaredProperties.FirstOrDefault(candidate => candidate.Name == member.MemberName);
        if (property is not null)
        {
            var setter = _propertyAccessors[(cls.QualifiedName, property.Name, PropertyAccessorKind.Set)];
            var setterType = ToLlvmFunctionType(setter, model);
            if (!cls.IsValue)
                EmitNullReferenceCheck(builder, receiver, member.MemberName, "written", model, llvmFunctions);
            var targetFunction = llvmFunctions[setter];
            if (property.Declaration.IsVirtual || property.Declaration.IsOverride)
            {
                var slot = GetVirtualMethods(cls, model).Select((candidate, index) => (candidate, index))
                    .First(pair => pair.candidate == setter).index;
                var slotAddress = builder.BuildStructGEP2(_classTypes[cls.QualifiedName], receiver,
                    (uint)SlotIndex(cls, slot, model), "property.vtable.slot");
                targetFunction = builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                    slotAddress, "property.vtable.target");
            }
            EmitCallOrInvoke(builder, setterType, targetFunction,
                [receiver, CoerceValue(builder, assigned, assignedType, property.Type, model)], string.Empty);
            return;
        }

        if (cls.IsValue)
            throw new InvalidOperationException(
                $"Internal compiler error: field '{member.MemberName}' of value class '{cls.QualifiedName}' must be stored through its variable.");
        var fieldIndex = cls.Fields
            .Select((field, index) => (field, index))
            .First(pair => pair.field.Name == member.MemberName).index;
        var coerced = CoerceValue(builder, assigned, assignedType, targetType, model);
        EmitNullReferenceCheck(builder, receiver, member.MemberName, "written", model, llvmFunctions);
        var address = builder.BuildStructGEP2(_classTypes[cls.QualifiedName], receiver,
            (uint)FieldOffset(cls, model, fieldIndex), $"field.{member.MemberName}");
        StoreCounted(builder, address, coerced, targetType, moved, true, model);
    }
}
