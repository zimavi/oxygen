using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public static class OxygenNameMangler
{
    private const string ManglingPrefix = "_Ox_v0_";

    public static string Mangle(string moduleName, FunctionSymbol function)
    {
        if (function.IsExternal && !string.IsNullOrWhiteSpace(function.NativeName))
            return function.NativeName;
        var parameterTypes = string.Concat(function.Parameters.Select(parameter => EncodeType(parameter.Type)));
        var signature = function.Parameters.Count == 0
            ? string.Empty
            : $"_p{function.Parameters.Count}_{parameterTypes}";
        var specialization = function.IsSpecialized
            ? $"_g{string.Concat(function.SpecializationArguments!.Select(EncodeType))}"
            : string.Empty;
        return $"{ManglingPrefix}m{moduleName.Length}{moduleName}_f{function.Name.Length}{function.Name}{signature}{specialization}";
    }

    public static string EncodeType(SemanticType type) => type.Kind == SemanticTypeKind.String
        ? "s"
        : type.IsArray ? $"a{type.ArrayLength}_{EncodeType(type.ElementType!)}"
        : type.Primitive switch
        {
            PrimitiveType.I8 => "i8",
            PrimitiveType.I16 => "i16",
            PrimitiveType.I32 => "i32",
            PrimitiveType.I64 => "i64",
            PrimitiveType.U8 => "u8",
            PrimitiveType.U16 => "u16",
            PrimitiveType.U32 => "u32",
            PrimitiveType.U64 => "u64",
            PrimitiveType.USize => "us",
            PrimitiveType.ISize => "is",
            PrimitiveType.F32 => "f32",
            PrimitiveType.F64 => "f64",
            PrimitiveType.Bool => "b",
            PrimitiveType.Void => "v",
            _ when type.Kind == SemanticTypeKind.Pointer => "p" + EncodeType(type.Pointee!),
            _ when type.Kind == SemanticTypeKind.GenericParameter => "g" + type.GenericParameterName!.Length + type.GenericParameterName,
            _ when type.Kind == SemanticTypeKind.Enum => "e" + type.EnumName!.Length + type.EnumName,
            _ when type.Kind == SemanticTypeKind.Union => EncodeNamedType("u", type.EnumName!, type.TypeArguments),
            _ when type.Kind == SemanticTypeKind.Struct => EncodeNamedType("s", type.StructName!, type.TypeArguments),
            _ when type.Kind == SemanticTypeKind.Class => EncodeNamedType("c", type.StructName!, type.TypeArguments),
            _ when type.Kind == SemanticTypeKind.Interface => EncodeNamedType("i", type.StructName!, type.TypeArguments),
            _ => throw new InvalidOperationException($"Unsupported semantic type '{type}'.")
        };

    private static string EncodeNamedType(string prefix, string name, IReadOnlyList<SemanticType>? arguments) =>
        prefix + name.Length + name +
        (arguments is { Count: > 0 }
            ? $"_a{arguments.Count}_{string.Concat(arguments.Select(EncodeType))}"
            : string.Empty);

    public static string MangleConstructor(string moduleName, ClassSymbol cls, ConstructorSymbol constructor) =>
        $"{ManglingPrefix}m{moduleName.Length}{moduleName}_c{cls.Name.Length}{cls.Name}_p{constructor.Parameters.Count}_{string.Concat(constructor.Parameters.Select(p => EncodeType(p.Type)))}";

    public static string MangleDestructor(string moduleName, ClassSymbol cls) =>
        $"{ManglingPrefix}m{moduleName.Length}{moduleName}_d{cls.Name.Length}{cls.Name}";
}
