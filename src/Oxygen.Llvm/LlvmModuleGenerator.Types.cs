using System.Globalization;
using System.Text.Json;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    private static LLVMTypeRef ToLlvmFunctionType(FunctionSymbol symbol, SemanticModel model)
    {
        var parameterTypes = symbol.Parameters
            .Select(parameter => UsesPointerStringAbi(symbol) && parameter.Type.Kind == SemanticTypeKind.String
                ? LLVMTypeRef.CreatePointer(ToLlvmStringType(), 0)
                : ToLlvmType(parameter.Type, model))
            .ToList();
        if (IsClassMethod(symbol, model))
        {
            var owner = symbol.ModuleName[(symbol.ModuleName.LastIndexOf('.') + 1)..];
            parameterTypes.Insert(0, model.Interfaces.ContainsKey(owner)
                ? LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)
                : ToLlvmType(SemanticType.Class(owner), model));
        }
        return LLVMTypeRef.CreateFunction(ToLlvmType(symbol.ReturnType, model), parameterTypes.ToArray(), false);
    }

    private static bool IsClassMethod(FunctionSymbol symbol, SemanticModel model) =>
        _accessorSymbols.Contains(symbol)
        || model.Classes.Values.Any(cls => cls.Methods.Contains(symbol))
        || model.Interfaces.Values.Any(iface => iface.Methods.Contains(symbol));

    private static bool UsesPointerStringAbi(FunctionSymbol symbol) =>
        symbol.IsExternal &&
        symbol.NativeName is "oxygen_write_stdout" or "oxygen_write_stderr";

    private static LLVMTypeRef ToLlvmType(SemanticType type, SemanticModel model) => type.Primitive switch
    {
        PrimitiveType.I8 or PrimitiveType.U8 => LLVMTypeRef.Int8,
        PrimitiveType.I16 or PrimitiveType.U16 => LLVMTypeRef.Int16,
        PrimitiveType.I32 or PrimitiveType.U32 => LLVMTypeRef.Int32,
        PrimitiveType.I64 or PrimitiveType.U64 or PrimitiveType.USize or PrimitiveType.ISize => LLVMTypeRef.Int64,
        PrimitiveType.Bool => LLVMTypeRef.Int1,
        PrimitiveType.F32 => LLVMTypeRef.Float,
        PrimitiveType.F64 => LLVMTypeRef.Double,
        PrimitiveType.Void => LLVMTypeRef.Void,
        _ when type.Kind == SemanticTypeKind.Pointer && type.IsOpaquePointer => LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
        _ when type.Kind == SemanticTypeKind.Pointer => LLVMTypeRef.CreatePointer(ToLlvmType(type.Pointee!, model), 0),
        _ when type.Kind == SemanticTypeKind.Class && !IsValueClass(type, model) =>
            ClassPointerType(type.StructName!),
        _ when type.Kind == SemanticTypeKind.Class => _classTypes[type.StructName!],
        _ when type.Kind == SemanticTypeKind.Interface => LLVMTypeRef.CreateStruct(
            [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)], false),
        _ when type.Kind == SemanticTypeKind.Struct => LLVMTypeRef.CreateStruct(
            model.Structs[type.StructName!].Fields.Select(field => ToLlvmType(field.Type, model)).ToArray(),
            model.Structs[type.StructName!].IsPacked),
        _ when type.Kind == SemanticTypeKind.String => ToLlvmStringType(),
        _ when type.Kind == SemanticTypeKind.Array => LLVMTypeRef.CreateArray(ToLlvmType(type.ElementType!, model), (uint)type.ArrayLength),
        _ when type.Kind == SemanticTypeKind.Enum => ToLlvmType(
            SemanticType.FromPrimitive(model.Enums[type.EnumName!].UnderlyingType), model),
        _ when type.Kind == SemanticTypeKind.Union => LLVMTypeRef.CreateStruct(
            [LLVMTypeRef.Int32, ToLlvmType(UnionPayloadType(type, model), model)], false),
        _ => throw new InvalidOperationException($"Unsupported semantic type '{type}'.")
    };

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
                builder.BuildBitCast(value, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "interface.data"),
                0,
                "interface.data");
            var table = _interfaceTables[(source.StructName!, target.StructName!)];
            return builder.BuildInsertValue(result,
                builder.BuildBitCast(table, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "interface.table"),
                1, "interface.table");
        }
        if (source.IsPointer && target.IsNumeric)
            return builder.BuildPtrToInt(value, ToLlvmType(target, model), "ptr.to.int");
        if (target.IsPointer && source.IsNumeric)
            return builder.BuildIntToPtr(value, ToLlvmType(target, model), "int.to.ptr");
        if (source.IsNumeric && target.IsNumeric)
        {
            var targetType = ToLlvmType(target, model);
            if (target.Primitive is PrimitiveType.F32 or PrimitiveType.F64)
                return source.Primitive is PrimitiveType.F32 or PrimitiveType.F64
                    ? builder.BuildFPCast(value, targetType, "float.cast")
                    : builder.BuildSIToFP(value, targetType, "int.to.float");
            if (source.Primitive is PrimitiveType.F32 or PrimitiveType.F64)
                return builder.BuildFPToSI(value, targetType, "float.to.int");
            var sourceBits = IntegerBitWidth(source.Primitive!.Value);
            var targetBits = IntegerBitWidth(target.Primitive!.Value);
            if (targetBits > sourceBits)
                return source.Primitive is PrimitiveType.I8 or PrimitiveType.I16 or PrimitiveType.I32 or PrimitiveType.I64 or PrimitiveType.ISize
                    ? builder.BuildSExt(value, targetType, "int.cast")
                    : builder.BuildZExt(value, targetType, "int.cast");
            if (targetBits < sourceBits)
                return builder.BuildTrunc(value, targetType, "int.cast");
        }
        return value;
    }

    private static SemanticType FromTypeSyntax(TypeSyntax syntax, SemanticModel model)
    {
        if (syntax.IsArray)
            return SemanticType.ArrayOf(FromTypeSyntax(syntax.ElementType!, model), syntax.ArrayLength!.Value);
        if (syntax.IsPointer)
            return SemanticType.PointerTo(FromTypeSyntax(syntax.Pointee!, model));
        if (syntax.IsString)
            return SemanticType.String;
        if (syntax.NamedType is not null)
        {
            if (model.Classes.ContainsKey(syntax.NamedType))
                return SemanticType.Class(syntax.NamedType);
            if (model.Interfaces.ContainsKey(syntax.NamedType))
                return SemanticType.Interface(syntax.NamedType);
            if (model.Structs.ContainsKey(syntax.NamedType))
                return SemanticType.Struct(syntax.NamedType);
            if (model.Unions.ContainsKey(syntax.NamedType))
                return SemanticType.Union(syntax.NamedType);
            return SemanticType.Enum(syntax.NamedType);
        }
        if (syntax.Primitive is null)
            throw new InvalidOperationException("Inferred variable type is missing.");
        var type = SemanticType.FromPrimitive(syntax.Primitive.Value);
        return syntax.IsPointer ? SemanticType.PointerTo(type) : type;
    }
}
