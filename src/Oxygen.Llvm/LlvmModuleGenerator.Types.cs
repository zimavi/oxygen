using System.Globalization;
using System.Text.Json;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    private static bool HasGenericAggregateOwner(FunctionSymbol symbol, SemanticModel model)
    {
        var separator = symbol.ModuleName.LastIndexOf('.');
        if (separator < 0)
            return false;
        var owner = symbol.ModuleName[(separator + 1)..];
        return model.Classes.TryGetValue(owner, out var cls) && cls.IsGeneric
            || model.Interfaces.TryGetValue(owner, out var iface) && iface.IsGeneric;
    }

    private static bool HasGenericType(SemanticType type) =>
        type.IsGenericParameter
        || (type.Pointee is not null && HasGenericType(type.Pointee))
        || (type.ElementType is not null && HasGenericType(type.ElementType))
        || (type.TypeArguments?.Any(HasGenericType) ?? false);

    private static LLVMTypeRef ToLlvmFunctionType(FunctionSymbol symbol, SemanticModel model)
    {
        var parameterTypes = new List<LLVMTypeRef>();
        foreach (var parameter in symbol.Parameters)
        {
            try
            {
                parameterTypes.Add(UsesPointerStringAbi(symbol) && parameter.Type.Kind == SemanticTypeKind.String
                    ? LLVMTypeRef.CreatePointer(ToLlvmStringType(), 0)
                    : ToLlvmType(parameter.Type, model));
            }
            catch (KeyNotFoundException exception)
            {
                throw new InvalidOperationException($"Cannot lower parameter '{parameter.Name}' of '{symbol.QualifiedName}' with type '{parameter.Type}'.", exception);
            }
        }
        if (IsClassMethod(symbol, model))
        {
            var owner = model.Interfaces.Values.FirstOrDefault(iface => iface.Methods.Contains(symbol))?.Name
                ?? model.Classes.Values.FirstOrDefault(cls => cls.Methods.Contains(symbol))?.Name
                ?? symbol.ModuleName[(symbol.ModuleName.LastIndexOf('.') + 1)..];
            if (!model.Classes.ContainsKey(owner) && !model.Interfaces.ContainsKey(owner))
            {
                var candidates = model.Classes.Keys
                    .Where(name => name.StartsWith(owner + "__ox_", StringComparison.Ordinal))
                    .ToArray();
                if (candidates.Length == 1)
                    owner = candidates[0];
            }
            if (!model.Classes.ContainsKey(owner) && !model.Interfaces.ContainsKey(owner))
                throw new InvalidOperationException($"Cannot lower owner '{owner}' for '{symbol.QualifiedName}'. Classes: {string.Join(", ", model.Classes.Keys)}");
            try
            {
                parameterTypes.Insert(0, model.Interfaces.ContainsKey(owner)
                    ? LLVMTypeRef.CreatePointer(_context.Int8Type, 0)
                    : ToLlvmType(SemanticType.Class(owner), model));
            }
            catch (KeyNotFoundException exception)
            {
                throw new InvalidOperationException(
                    $"Cannot lower receiver '{owner}' for '{symbol.QualifiedName}'. Classes: {string.Join(", ", model.Classes.Keys)}",
                    exception);
            }
        }
        LLVMTypeRef returnType;
        try
        {
            returnType = ToLlvmType(symbol.ReturnType, model);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidOperationException($"Cannot lower return type of '{symbol.QualifiedName}': '{symbol.ReturnType}'.", exception);
        }
        if (ReturnsStringThroughPointer(symbol))
        {
            // An external string result is written through a trailing out-pointer, not returned by value.
            parameterTypes.Add(LLVMTypeRef.CreatePointer(ToLlvmStringType(), 0));
            returnType = _context.VoidType;
        }
        return LLVMTypeRef.CreateFunction(returnType, parameterTypes.ToArray(), false);
    }

    // External functions return string through a trailing out-pointer, because a 16-byte struct return
    // is classified differently by LLVM and by the C ABI of each target.
    private static bool ReturnsStringThroughPointer(FunctionSymbol symbol) =>
        symbol.IsExternal && symbol.ReturnType.Kind == SemanticTypeKind.String;

    private static bool IsClassMethod(FunctionSymbol symbol, SemanticModel model) =>
        _accessorSymbols.Contains(symbol)
        || model.Classes.Values.Any(cls => cls.Methods.Contains(symbol))
        || model.Interfaces.Values.Any(iface => iface.Methods.Contains(symbol));

    private static bool UsesPointerStringAbi(FunctionSymbol symbol) =>
        symbol.IsExternal &&
        symbol.NativeName is "oxygen_write_stdout" or "oxygen_write_stderr";

    private static LLVMTypeRef ToLlvmType(SemanticType type, SemanticModel model)
    {
        type = NormalizeAggregateType(type, model);
        return type.Primitive switch
    {
        PrimitiveType.I8 or PrimitiveType.U8 => _context.Int8Type,
        PrimitiveType.I16 or PrimitiveType.U16 => _context.Int16Type,
        PrimitiveType.I32 or PrimitiveType.U32 => _context.Int32Type,
        PrimitiveType.I64 or PrimitiveType.U64 or PrimitiveType.USize or PrimitiveType.ISize => _context.Int64Type,
        PrimitiveType.Bool => _context.Int1Type,
        PrimitiveType.F32 => _context.FloatType,
        PrimitiveType.F64 => _context.DoubleType,
        PrimitiveType.Void => _context.VoidType,
        _ when type.Kind == SemanticTypeKind.Pointer && type.IsOpaquePointer => LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
        _ when type.Kind == SemanticTypeKind.Pointer => LLVMTypeRef.CreatePointer(ToLlvmType(type.Pointee!, model), 0),
        _ when type.Kind == SemanticTypeKind.Class && !IsValueClass(type, model) =>
            ClassPointerType(type.StructName!),
        _ when type.Kind == SemanticTypeKind.Class => _classTypes[type.StructName!],
        _ when type.Kind == SemanticTypeKind.Interface => _context.GetStructType(
            [LLVMTypeRef.CreatePointer(_context.Int8Type, 0), LLVMTypeRef.CreatePointer(_context.Int8Type, 0)], false),
        _ when type.Kind == SemanticTypeKind.Struct => _context.GetStructType(
            model.Structs[type.StructName!].Fields.Select(field => ToLlvmType(field.Type, model)).ToArray(),
            model.Structs[type.StructName!].IsPacked),
        _ when type.Kind == SemanticTypeKind.String => ToLlvmStringType(),
        _ when type.Kind == SemanticTypeKind.Array => LLVMTypeRef.CreateArray(ToLlvmType(type.ElementType!, model), (uint)type.ArrayLength),
        _ when type.Kind == SemanticTypeKind.Enum => ToLlvmType(
            SemanticType.FromPrimitive(model.Enums[type.EnumName!].UnderlyingType), model),
        _ when type.Kind == SemanticTypeKind.Union => _context.GetStructType(
            [_context.Int32Type, ToLlvmType(UnionPayloadType(type, model), model)], false),
        _ => throw new InvalidOperationException($"Unsupported semantic type '{type}'.")
    };
    }

    private static SemanticType NormalizeAggregateType(SemanticType type, SemanticModel model)
    {
        if (type.IsPointer)
            return SemanticType.PointerTo(NormalizeAggregateType(type.Pointee!, model));
        if (type.IsArray)
            return SemanticType.ArrayOf(NormalizeAggregateType(type.ElementType!, model), type.ArrayLength);
        if (type.TypeArguments is not { Count: > 0 })
        {
            var baseName = type.StructName ?? type.EnumName;
            if (baseName is not null)
            {
                var candidates = model.AggregateSpecializations
                    .Where(pair => pair.Value.StartsWith(baseName + "__ox_", StringComparison.Ordinal))
                    .Select(pair => pair.Value)
                    .Concat(model.Classes.Keys.Where(name =>
                        name.StartsWith(baseName + "__ox_", StringComparison.Ordinal)))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (candidates.Length == 1)
                    return type.Kind == SemanticTypeKind.Union
                        ? type with { EnumName = candidates[0] }
                        : type with { StructName = candidates[0] };
            }
            return type;
        }
        var directKey = $"{type.Kind}:{type.StructName ?? type.EnumName}<{string.Join(",", type.TypeArguments)}>";
        if (model.AggregateSpecializations.TryGetValue(directKey, out var directSpecialized))
            return type.Kind == SemanticTypeKind.Union
                ? type with { EnumName = directSpecialized, TypeArguments = null }
                : type with { StructName = directSpecialized, TypeArguments = null };
        var normalized = type with
        {
            TypeArguments = type.TypeArguments.Select(argument => NormalizeAggregateType(argument, model)).ToArray()
        };
        var key = $"{normalized.Kind}:{normalized.StructName ?? normalized.EnumName}<{string.Join(",", normalized.TypeArguments)}>";
        if (!model.AggregateSpecializations.TryGetValue(key, out var specialized))
            return normalized;
        return normalized.Kind == SemanticTypeKind.Union
            ? normalized with { EnumName = specialized, TypeArguments = null }
            : normalized with { StructName = specialized, TypeArguments = null };
    }

    private static SemanticType UnionPayloadType(SemanticType type, SemanticModel model)
    {
        var union = model.Unions[type.EnumName!];
        var payload = union.Cases.FirstOrDefault(item => item.PayloadType is not null)?.PayloadType;
        if (payload is null)
            return SemanticType.FromPrimitive(PrimitiveType.I8);
        return SubstituteGeneric(payload, union.DeclaredTypeParameters, type.TypeArguments);
    }

    private static SemanticType SubstituteGeneric(
        SemanticType type, IReadOnlyList<string> parameters, IReadOnlyList<SemanticType>? arguments)
    {
        if (type.IsGenericParameter && arguments is not null)
        {
            var index = parameters.ToList().IndexOf(type.GenericParameterName!);
            if (index >= 0 && index < arguments.Count)
                return arguments[index];
        }
        if (type.IsPointer)
            return SemanticType.PointerTo(SubstituteGeneric(type.Pointee!, parameters, arguments));
        if (type.TypeArguments is { Count: > 0 })
            return type with
            {
                TypeArguments = type.TypeArguments.Select(item =>
                SubstituteGeneric(item, parameters, arguments)).ToArray()
            };
        return type;
    }

    private static string NumericValue(string lexeme)
    {
        foreach (var suffix in new[] { "usize", "isize", "f32", "f64", "i16", "i32", "i64", "u16", "u32", "u64", "i8", "u8" })
            if (lexeme.EndsWith(suffix, StringComparison.Ordinal))
                return lexeme[..^suffix.Length];
        return lexeme;
    }

    private static LLVMValueRef CoerceValue(
        LLVMBuilderRef builder, LLVMValueRef value, SemanticType source, SemanticType target, SemanticModel model)
    {
        if (source == target)
            return value;
        if (source.IsClass && target.IsClass)
            return builder.BuildBitCast(value, ToLlvmType(target, model), "class.upcast");
        if (source.IsClass && target.IsInterface)
        {
            var result = LLVMValueRef.CreateConstNull(ToLlvmType(target, model));
            result = builder.BuildInsertValue(
                result,
                builder.BuildBitCast(value, LLVMTypeRef.CreatePointer(_context.Int8Type, 0), "interface.data"),
                0,
                "interface.data");
            var table = _interfaceTables[(source.StructName!, target.StructName!)];
            return builder.BuildInsertValue(result,
                builder.BuildBitCast(table, LLVMTypeRef.CreatePointer(_context.Int8Type, 0), "interface.table"),
                1, "interface.table");
        }
        if (source.IsPointer && target.IsNumeric)
            return builder.BuildPtrToInt(value, ToLlvmType(target, model), "ptr.to.int");
        if (target.IsPointer && source.IsNumeric)
            return builder.BuildIntToPtr(value, ToLlvmType(target, model), "int.to.ptr");
        if (source.IsNumeric && target.IsNumeric)
            return EmitNumericConversion(builder, value, source, target, model);
        return value;
    }

    // The single place where numeric conversions are chosen, shared by implicit coercion and explicit casts. The
    // operand's signedness picks the extension and the int/float instructions, so an unsigned value is never read as
    // negative.
    private static LLVMValueRef EmitNumericConversion(LLVMBuilderRef builder, LLVMValueRef value, SemanticType source,
        SemanticType target, SemanticModel model)
    {
        var targetType = ToLlvmType(target, model);
        var sourceIsFloat = source.Primitive is PrimitiveType.F32 or PrimitiveType.F64;
        if (target.Primitive is PrimitiveType.F32 or PrimitiveType.F64)
        {
            if (sourceIsFloat)
                return builder.BuildFPCast(value, targetType, "float.cast");
            return IsSignedInteger(source)
                ? builder.BuildSIToFP(value, targetType, "int.to.float")
                : builder.BuildUIToFP(value, targetType, "int.to.float");
        }
        if (sourceIsFloat)
            return IsSignedInteger(target)
                ? builder.BuildFPToSI(value, targetType, "float.to.int")
                : builder.BuildFPToUI(value, targetType, "float.to.int");
        var sourceBits = IntegerBitWidth(source.Primitive!.Value);
        var targetBits = IntegerBitWidth(target.Primitive!.Value);
        if (targetBits > sourceBits)
            return IsSignedInteger(source)
                ? builder.BuildSExt(value, targetType, "int.cast")
                : builder.BuildZExt(value, targetType, "int.cast");
        if (targetBits < sourceBits)
            return builder.BuildTrunc(value, targetType, "int.cast");
        return value;
    }

    private static bool IsSignedInteger(SemanticType type) =>
        type.Primitive is PrimitiveType.I8 or PrimitiveType.I16 or PrimitiveType.I32 or PrimitiveType.I64
            or PrimitiveType.ISize;

    // Pointer and array indexes are widened to i64. An unsigned index is zero-extended, so u32 4294967295 stays positive.
    private static LLVMValueRef ExtendIndexToInt64(LLVMBuilderRef builder, LLVMValueRef index, SemanticType type,
        string name) =>
        type.Primitive is PrimitiveType.I64 or PrimitiveType.U64 or PrimitiveType.USize
            ? index
            : IsSignedInteger(type)
                ? builder.BuildSExt(index, _context.Int64Type, name)
                : builder.BuildZExt(index, _context.Int64Type, name);

    // Specializations are keyed by structural types (`Box<Cell<i32>>`), so the type is built
    // structurally and normalized to its specialized name once, at the top.
    private static SemanticType FromTypeSyntax(TypeSyntax syntax, SemanticModel model) =>
        NormalizeAggregateType(StructuralTypeFromSyntax(syntax, model), model);

    private static SemanticType StructuralTypeFromSyntax(TypeSyntax syntax, SemanticModel model)
    {
        if (syntax.IsArray)
            return SemanticType.ArrayOf(StructuralTypeFromSyntax(syntax.ElementType!, model), syntax.ArrayLength!.Value);
        if (syntax.IsPointer)
            return SemanticType.PointerTo(StructuralTypeFromSyntax(syntax.Pointee!, model));
        if (syntax.IsString)
            return SemanticType.String;
        if (syntax.NamedType is not null)
        {
            var arguments = syntax.DeclaredTypeArguments.Count == 0
                ? null
                : syntax.DeclaredTypeArguments.Select(argument => StructuralTypeFromSyntax(argument, model)).ToArray();
            if (model.Classes.ContainsKey(syntax.NamedType))
                return SemanticType.Class(syntax.NamedType, arguments);
            if (model.Interfaces.ContainsKey(syntax.NamedType))
                return SemanticType.Interface(syntax.NamedType, arguments);
            if (model.Structs.ContainsKey(syntax.NamedType))
                return SemanticType.Struct(syntax.NamedType, arguments);
            if (model.Unions.ContainsKey(syntax.NamedType))
                return SemanticType.Union(syntax.NamedType, arguments);
            return SemanticType.Enum(syntax.NamedType);
        }
        if (syntax.Primitive is null)
            throw new InvalidOperationException("Inferred variable type is missing.");
        var type = SemanticType.FromPrimitive(syntax.Primitive.Value);
        return syntax.IsPointer ? SemanticType.PointerTo(type) : type;
    }
}
