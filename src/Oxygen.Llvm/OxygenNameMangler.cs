using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

// The native symbol names of Oxygen functions. The scheme is documented in docs/mangling.md, and every symbol is decoded in only one
// way: each name is preceded by its length, each list by its count, and each type code by a tag that no other encoding starts with.
public static class OxygenNameMangler
{
    // The version of the scheme. It is part of every symbol, and it changes whenever an encoding changes, so symbols from
    // different schemes never match.
    public const int Version = 1;
    private const string ManglingPrefix = "_Ox_v1_";

    // The class or interface that declares a method. TypeName is its simple name, and ModuleName is the module it is declared in.
    public readonly record struct MethodOwner(string ModuleName, string TypeName, bool IsInterface);

    // The symbol of a function. A method passes its owner, so that it cannot have the same symbol as a function of a module whose
    // name is the same as the class's qualified name.
    public static string Mangle(string moduleName, FunctionSymbol function, MethodOwner? owner = null)
    {
        if (function.IsExternal && !string.IsNullOrWhiteSpace(function.NativeName))
            return function.NativeName;
        var ownerSegment = owner is { } methodOwner
            ? $"_{(methodOwner.IsInterface ? "i" : "c")}{Name(methodOwner.TypeName)}"
            : string.Empty;
        var specialization = function.IsSpecialized
            ? $"_g{function.SpecializationArguments!.Count}_{string.Concat(function.SpecializationArguments.Select(EncodeType))}"
            : string.Empty;
        return $"{ManglingPrefix}m{Name(moduleName)}{ownerSegment}_f{Name(function.Name)}{Signature(function.Parameters.Select(parameter => parameter.Type))}{specialization}";
    }

    public static string EncodeType(SemanticType type) => type.Kind == SemanticTypeKind.String
        ? "str"
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
            _ when type.Kind == SemanticTypeKind.GenericParameter => "g" + Name(type.GenericParameterName!),
            _ when type.Kind == SemanticTypeKind.Enum => "E" + Name(type.EnumName!),
            _ when type.Kind == SemanticTypeKind.Union => EncodeNamedType("U", type.EnumName!, type.TypeArguments),
            _ when type.Kind == SemanticTypeKind.Struct => EncodeNamedType("S", type.StructName!, type.TypeArguments),
            _ when type.Kind == SemanticTypeKind.Class => EncodeNamedType("C", type.StructName!, type.TypeArguments),
            _ when type.Kind == SemanticTypeKind.Interface => EncodeNamedType("I", type.StructName!, type.TypeArguments),
            _ => throw new InvalidOperationException($"Unsupported semantic type '{type}'.")
        };

    public static string MangleConstructor(string moduleName, ClassSymbol cls, ConstructorSymbol constructor) =>
        $"{ManglingPrefix}m{Name(moduleName)}_c{Name(cls.Name)}_k{Signature(constructor.Parameters.Select(parameter => parameter.Type))}";

    public static string MangleDestructor(string moduleName, ClassSymbol cls) =>
        $"{ManglingPrefix}m{Name(moduleName)}_c{Name(cls.Name)}_d";

    // An identifier with its length in front, so the identifier ends where its length says.
    private static string Name(string identifier) => identifier.Length + identifier;

    // The parameter count, then the parameter types. A function without parameters has _p0_.
    private static string Signature(IEnumerable<SemanticType> parameterTypes)
    {
        var types = parameterTypes.ToList();
        return $"_p{types.Count}_{string.Concat(types.Select(EncodeType))}";
    }

    // A class, interface, struct or union name, then its type arguments when it has any.
    private static string EncodeNamedType(string tag, string name, IReadOnlyList<SemanticType>? arguments) =>
        tag + Name(name) +
        (arguments is { Count: > 0 }
            ? $"_a{arguments.Count}_{string.Concat(arguments.Select(EncodeType))}"
            : string.Empty);
}
