using System.Globalization;
using System.Text;
using System.Text.Json;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    private LLVMTypeRef getterType(FunctionSymbol getter, SemanticModel model) =>
        ToLlvmFunctionType(getter, model);

    private LLVMValueRef EmitCast(
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
            var candidate = LLVMValueRef.CreateConstInt(enumLlvmType, EnumBits(member.Value), false);
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
    private LLVMValueRef RequireSameRepresentation(
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

    private bool IsReferenceType(SemanticType type, SemanticModel model) =>
        (type.IsClass && !IsValueClass(type, model)) || type.IsInterface;

    // The class that a class extends, as the runtime names it, or null. The parser cannot tell a base class from an interface
    // written after ':', so BaseTypeName can name an interface, and a generic base is named by its instance.
    private string? RuntimeParentName(ClassSymbol cls, SemanticModel model)
    {
        if (cls.BaseTypeName is null)
            return null;
        var parent = NormalizeAggregateType(SemanticType.Class(cls.BaseTypeName), model).StructName;
        return parent is not null && model.Classes.ContainsKey(parent) ? parent : null;
    }

    // The interfaces that a class implements, as the runtime names them: the instance's name for a generic interface.
    private IEnumerable<string> RuntimeInterfaceNames(ClassSymbol cls, SemanticModel model) =>
        cls.DeclaredInterfaceTypes.Select(type => NormalizeAggregateType(type, model).StructName!);

    // Every base class and interface of `cls`, nearest first, without `cls` itself. An interface that a base class and a
    // derived class both name is listed once. The runtime type check reads this list, so an instance is found by a derived class's
    // interface or by any base class's interface.
    private List<string> RuntimeSupertypeNames(ClassSymbol cls, SemanticModel model)
    {
        var names = new List<string>();
        void Add(string name)
        {
            if (name != cls.QualifiedName && !names.Contains(name))
                names.Add(name);
        }

        for (var current = cls; ;)
        {
            foreach (var interfaceName in RuntimeInterfaceNames(current, model))
                Add(interfaceName);
            var parent = RuntimeParentName(current, model);
            if (parent is null)
                return names;
            Add(parent);
            current = model.Classes[parent];
        }
    }

    // The constant table of supertype addresses that `typeName` points to. A type with no supertypes gets a null table.
    private (LLVMValueRef Table, uint Count) AddRuntimeSupertypeTable(
        LLVMModuleRef module,
        string typeName,
        IReadOnlyList<string> supertypeNames)
    {
        var pointerType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
        var count = (uint)supertypeNames.Count;
        if (count == 0)
            return (LLVMValueRef.CreateConstNull(pointerType), 0);

        var table = module.AddGlobal(LLVMTypeRef.CreateArray(pointerType, count), $"oxygen.type.supertypes.{typeName}");
        table.Linkage = LLVMLinkage.LLVMInternalLinkage;
        table.IsGlobalConstant = true;
        table.Initializer = LLVMValueRef.CreateConstArray(pointerType,
            supertypeNames.Select(name => _runtimeTypeInfos[name]).ToArray());
        return (table, count);
    }

    // Gives the type information of `typeName` its value. Every field is known at compile time, so the value is a constant: it needs no
    // startup code, and every object that defines the type gives the same value.
    private void DefineRuntimeTypeInfo(
        LLVMModuleRef module,
        string typeName,
        string? parentType,
        uint kind,
        SemanticModel model)
    {
        var pointerType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
        var int32 = _context.Int32Type;
        var parent = parentType is null
            ? LLVMValueRef.CreateConstNull(pointerType)
            : _runtimeTypeInfos[parentType];
        // Only advertise a message field when the generated class layout
        // actually contains one.  Name-based inference made an unrelated
        // class such as MyException unsafe for the native unhandled reporter.
        var messageField = model.Classes.TryGetValue(typeName, out var cls)
            ? cls.Fields.Select((field, index) => (field, index))
                .FirstOrDefault(item => item.field.Name == "message"
                    && item.field.Type.Kind == SemanticTypeKind.String)
            : default;
        // The offset is the field's byte offset in the object's LLVM layout, so padding and the pointer size come from the
        // module's data layout rather than a second copy of those rules.
        var messageOffset = messageField.field is null
            ? 0
            : MessageFieldOffset(typeName, FieldOffset(cls!, model, messageField.index));
        // Releasing an object of this type calls the nearest destructor in its class chain, or nothing when there is none.
        var destructor = FindDestructor(typeName, model) ?? LLVMValueRef.CreateConstNull(pointerType);
        var supertypes = _runtimeSupertypes[typeName];
        var info = _runtimeTypeInfos[typeName];
        info.Initializer = LLVMValueRef.CreateConstNamedStruct(_runtimeTypeInfoType,
        [
            AddRuntimeTypeNameString(module, typeName),
            LLVMValueRef.CreateConstInt(int32, kind, false),
            LLVMValueRef.CreateConstInt(int32, 0, false),
            parent,
            LLVMValueRef.CreateConstInt(int32, (ulong)messageOffset, false),
            destructor,
            supertypes.Table,
            LLVMValueRef.CreateConstInt(int32, supertypes.Count, false)
        ]);
    }

    // The byte offset of a class's field in its LLVM layout. `structIndex` is the field's index in the struct, as FieldOffset gives it.
    private unsafe int MessageFieldOffset(string typeName, int structIndex)
    {
        var data = LLVMTargetDataRef.FromStringRepresentation(_target.DataLayout);
        try
        {
            return checked((int)data.OffsetOfElement(_classTypes[typeName], (uint)structIndex));
        }
        finally
        {
            LLVM.DisposeTargetData(data);
        }
    }

    // The destructor that releases an object of `typeName`: the nearest one in its class chain, or null when there is none.
    private LLVMValueRef? FindDestructor(string typeName, SemanticModel model)
    {
        for (var cls = model.Classes.GetValueOrDefault(typeName); cls is not null;
             cls = cls.BaseTypeName is null ? null : model.Classes.GetValueOrDefault(cls.BaseTypeName))
            if (_destructors.TryGetValue(cls.QualifiedName, out var destructor))
                return destructor;
        return null;
    }

    // Releases the object `reference` points to through its runtime type. The destructor stored in its type information
    // runs first, so a reference typed as a base class still runs the derived destructor; then the memory is freed.
    private void EmitDestroyAndFree(LLVMBuilderRef builder, LLVMValueRef reference)
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
        EmitCallOrInvoke(builder, LLVMTypeRef.CreateFunction(_context.VoidType, [pointerType], false), destructor,
            [reference], string.Empty);
        builder.BuildBr(release);
        builder.PositionAtEnd(release);
        EmitCallOrInvoke(builder, _oxygenFree.FunctionType, _oxygenFree,
            [builder.BuildBitCast(reference, pointerType, "free.memory")], string.Empty);
    }

    // The type information of a type. A type's owner module defines it with external linkage, and every other object that has the
    // type (an instance of a generic class, or a class of another module) defines it as linkonce_odr, so the linker keeps one copy.
    // The runtime compares these addresses for `is` and catch, so a type must have one address in the whole program.
    private LLVMValueRef AddRuntimeTypeInfoGlobal(LLVMModuleRef module, string typeName, bool ownedByThisObject)
    {
        var info = module.AddGlobal(_runtimeTypeInfoType, $"oxygen.type.info.{typeName}");
        info.Linkage = ownedByThisObject ? LLVMLinkage.LLVMExternalLinkage : LLVMLinkage.LLVMLinkOnceODRLinkage;
        return info;
    }

    // The name of a type as a constant string, with its terminating zero.
    private LLVMValueRef AddRuntimeTypeNameString(LLVMModuleRef module, string typeName)
    {
        var int8 = _context.Int8Type;
        var bytes = Encoding.UTF8.GetBytes(typeName).Append((byte)0)
            .Select(value => LLVMValueRef.CreateConstInt(int8, value, false)).ToArray();
        var text = module.AddGlobal(LLVMTypeRef.CreateArray(int8, (uint)bytes.Length), $"oxygen.type.name.{typeName}");
        text.Linkage = LLVMLinkage.LLVMInternalLinkage;
        text.IsGlobalConstant = true;
        text.Initializer = LLVMValueRef.CreateConstArray(int8, bytes);
        return text;
    }

    private LLVMValueRef GetTypeName(LLVMBuilderRef builder, string typeName)
    {
        return _runtimeTypeInfos[typeName];
    }

    private LLVMValueRef EmitReferenceCast(
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
            var table = _interfaceTables[(NormalizeAggregateType(source, model).StructName!, NormalizeAggregateType(target, model).StructName!)];
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

    private LLVMValueRef EmitIsExpression(
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

    private LLVMValueRef EmitRuntimeTypeCheck(
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
        // The runtime reports a match as an i8. A test is a boolean (i1) wherever it is used: as a value, a condition or an operand.
        return builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, match,
            LLVMValueRef.CreateConstInt(_context.Int8Type, 0, false), "type.is");
    }

    private LLVMValueRef EmitPatternBindingValue(
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
            var table = _interfaceTables[(NormalizeAggregateType(source, model).StructName!, NormalizeAggregateType(target, model).StructName!)];
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

    private bool ClassImplementsInterface(string className, string interfaceName, SemanticModel model)
    {
        if (!model.Classes.TryGetValue(className, out var cls))
            return false;
        // InterfaceNames has the base name of each interface, so a generic interface is matched by its instance's name here:
        // the class declares Iterable<i32>, whose instance is Iterable__ox_i32.
        if (cls.InterfaceNames.Contains(interfaceName, StringComparer.Ordinal)
            || cls.DeclaredInterfaceTypes.Any(type => NormalizeAggregateType(type, model).StructName == interfaceName))
            return true;
        return cls.BaseTypeName is not null
            && model.Classes.ContainsKey(cls.BaseTypeName)
            && ClassImplementsInterface(cls.BaseTypeName, interfaceName, model);
    }

    // The size is LLVM's own answer for the module's data layout, which is also what the struct GEPs use.
    private LLVMValueRef EmitSizeOf(SizeOfExpression expression, SemanticModel model)
    {
        var type = expression.TargetType is not null
            ? FromTypeSyntax(expression.TargetType, model)
            : model.ExpressionTypes[expression.Operand!];
        return ToLlvmType(type, model).SizeOf;
    }

    // The low 64 bits of an enum value in two's complement, which is how the value is stored in its integer type.
    private static ulong EnumBits(System.Numerics.BigInteger value) => (ulong)(value & ulong.MaxValue);

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
