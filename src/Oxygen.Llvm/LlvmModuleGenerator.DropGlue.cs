using LLVMSharp.Interop;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

// The release glue of a class: what an object releases when it is freed (docs/reference-counting.md, step 2b). The glue is the
// destructor of the class's runtime type, so every path that frees an object runs it: the release of its last count, and the direct free
// of a `new` object that nothing retained.
public sealed partial class LlvmModuleGenerator
{
    // The release glue of each class of the program, by its qualified name.
    private readonly Dictionary<string, LLVMValueRef> _releaseGlue = new(StringComparer.Ordinal);

    private const string ReleaseElementsModule = "std.memory";
    private const string ReleaseElementsName = "release_elements";

    // Runs the class's own destructor, then the glue of its base class, then releases the counted references in the class's own fields. So
    // a derived object runs its destructor first, then its base class's destructor, and its counted fields are released last.
    private void EmitReleaseGlue(LLVMBuilderRef builder, LLVMValueRef glue, ClassSymbol cls, SemanticModel model)
    {
        builder.PositionAtEnd(glue.AppendBasicBlock("entry"));
        var self = glue.GetParam(0);
        if (cls.Destructor is not null && _destructors.TryGetValue(cls.QualifiedName, out var destructor))
            builder.BuildCall2(destructor.FunctionType, destructor, [self], (ReadOnlySpan<char>)string.Empty);
        if (cls.BaseTypeName is not null && _releaseGlue.TryGetValue(cls.BaseTypeName, out var baseGlue))
            builder.BuildCall2(baseGlue.FunctionType, baseGlue, [self], (ReadOnlySpan<char>)string.Empty);
        // The class's fields include its base class's, which the base class's glue releases, so only the class's own fields are released here.
        for (var index = InheritedFieldCount(cls, model); index < cls.Fields.Count; index++)
        {
            var field = cls.Fields[index];
            var address = builder.BuildStructGEP2(_classTypes[cls.QualifiedName], self, (uint)FieldOffset(cls, model, index),
                $"drop.{field.Name}");
            ReleaseField(builder, address, field.Type, model);
        }
        builder.BuildRetVoid();
    }

    // Releases the counted references that a field holds. A counted field is released by count. A union releases the payload of the case
    // that it holds. Structs, arrays and value classes in a field are not released, which is a limit of step 2b.
    private void ReleaseField(LLVMBuilderRef builder, LLVMValueRef address, SemanticType type, SemanticModel model)
    {
        if (IsCountedType(type, model))
        {
            var value = builder.BuildLoad2(ToLlvmType(type, model), address, "drop.value");
            CallRelease(builder, OwnedObjectPointer(builder, value, type));
            return;
        }
        if (!type.IsUnion)
            return;
        var unionType = NormalizeAggregateType(type, model);
        var union = model.Unions[unionType.EnumName!];
        var unionValue = builder.BuildLoad2(ToLlvmType(unionType, model), address, "drop.union");
        var tag = builder.BuildExtractValue(unionValue, 0, "drop.tag");
        var function = builder.InsertBlock.Parent;
        for (var index = 0; index < union.Cases.Count; index++)
        {
            var item = union.Cases[index];
            if (item.PayloadType is null)
                continue;
            var slot = UnionPayloadSlot(unionType, model, item.Name);
            if (!IsCountedType(slot.Type, model))
                continue;
            var release = function.AppendBasicBlock("drop.union.payload");
            var next = function.AppendBasicBlock("drop.union.next");
            builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, tag,
                LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)index, false), "drop.is.case"), release, next);
            builder.PositionAtEnd(release);
            var payload = builder.BuildExtractValue(unionValue, slot.Field, "drop.payload");
            CallRelease(builder, OwnedObjectPointer(builder, payload, slot.Type));
            builder.BuildBr(next);
            builder.PositionAtEnd(next);
        }
    }

    // A plain call to the runtime's release. The glue has no unwind context of its own, and the runtime releases a null pointer as nothing.
    private void CallRelease(LLVMBuilderRef builder, LLVMValueRef pointer) =>
        builder.BuildCall2(_oxygenRelease.FunctionType, _oxygenRelease, [pointer], (ReadOnlySpan<char>)string.Empty);

    // Whether a call is std.memory's release_elements, which the compiler implements rather than running its body.
    private static bool IsReleaseElements(FunctionSymbol function) =>
        function.Name == ReleaseElementsName && function.ModuleName == ReleaseElementsModule
        && function.Declaration.Annotations?.Any(annotation => annotation.Name == "intrinsic") == true;

    // release_elements<T>(pointer, count): releases each of the first count elements when T is counted. Any other T has nothing to release.
    private void EmitReleaseElements(LLVMBuilderRef builder, FunctionSymbol function, LLVMValueRef pointer, LLVMValueRef count,
        SemanticModel model)
    {
        var elementType = NormalizeAggregateType(function.Parameters[0].Type.Pointee!, model);
        if (!IsCountedType(elementType, model))
            return;
        var owner = builder.InsertBlock.Parent;
        var index = BuildEntryAlloca(builder, _context.Int64Type, "release.index");
        builder.BuildStore(LLVMValueRef.CreateConstInt(_context.Int64Type, 0, false), index);
        var check = owner.AppendBasicBlock("release.check");
        var body = owner.AppendBasicBlock("release.body");
        var done = owner.AppendBasicBlock("release.done");
        builder.BuildBr(check);
        builder.PositionAtEnd(check);
        var current = builder.BuildLoad2(_context.Int64Type, index, "release.current");
        builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, current, count, "release.more"), body, done);
        builder.PositionAtEnd(body);
        var address = builder.BuildGEP2(ToLlvmType(elementType, model), pointer, [current], (ReadOnlySpan<char>)"release.element");
        var element = builder.BuildLoad2(ToLlvmType(elementType, model), address, "release.value");
        CallRelease(builder, OwnedObjectPointer(builder, element, elementType));
        builder.BuildStore(builder.BuildAdd(current, LLVMValueRef.CreateConstInt(_context.Int64Type, 1, false), "release.next"), index);
        builder.BuildBr(check);
        builder.PositionAtEnd(done);
    }
}
