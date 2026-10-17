using Oxygen.Compiler.Ast;

namespace Oxygen.Compiler.Semantic;

public enum SemanticTypeKind
{
    Primitive,
    String,
    Pointer,
    Struct,
    Class,
    Interface,
    Array,
    Enum,
    Union,
    GenericParameter
}

public sealed record SemanticType(
    SemanticTypeKind Kind,
    PrimitiveType? Primitive,
    SemanticType? Pointee = null,
    string? StructName = null,
    string? EnumName = null,
    string? GenericParameterName = null)
{
    public int ArrayLength { get; init; }
    public SemanticType? ElementType { get; init; }
    public IReadOnlyList<SemanticType>? TypeArguments { get; init; }
    public static SemanticType String => new(SemanticTypeKind.String, null);

    public static SemanticType FromPrimitive(PrimitiveType primitive) => new(SemanticTypeKind.Primitive, primitive);

    public static SemanticType PointerTo(SemanticType pointee) => new(SemanticTypeKind.Pointer, null, pointee);

    public static SemanticType Struct(string name, IReadOnlyList<SemanticType>? typeArguments = null) =>
        new(SemanticTypeKind.Struct, null, null, name) { TypeArguments = typeArguments };
    public static SemanticType Class(string name, IReadOnlyList<SemanticType>? typeArguments = null) =>
        new(SemanticTypeKind.Class, null, null, name) { TypeArguments = typeArguments };
    public static SemanticType Interface(string name, IReadOnlyList<SemanticType>? typeArguments = null) =>
        new(SemanticTypeKind.Interface, null, null, name) { TypeArguments = typeArguments };
    public static SemanticType Enum(string name) => new(SemanticTypeKind.Enum, null, null, null, name);
    public static SemanticType Union(string name, IReadOnlyList<SemanticType>? typeArguments = null) =>
        new(SemanticTypeKind.Union, null, null, null, name) { TypeArguments = typeArguments };
    public static SemanticType GenericParameter(string name) =>
        new(SemanticTypeKind.GenericParameter, null, null, null, null, name);
    public static SemanticType ArrayOf(SemanticType element, int length) => new(SemanticTypeKind.Array, null) { ElementType = element, ArrayLength = length };

    public bool IsVoid => Kind == SemanticTypeKind.Primitive && Primitive == PrimitiveType.Void;

    public bool IsPointer => Kind == SemanticTypeKind.Pointer;

    public bool IsStruct => Kind == SemanticTypeKind.Struct;
    public bool IsClass => Kind == SemanticTypeKind.Class;
    public bool IsInterface => Kind == SemanticTypeKind.Interface;
    public bool IsArray => Kind == SemanticTypeKind.Array;
    public bool IsEnum => Kind == SemanticTypeKind.Enum;
    public bool IsUnion => Kind == SemanticTypeKind.Union;
    public bool IsGenericParameter => Kind == SemanticTypeKind.GenericParameter;
    public bool IsOpaquePointer => IsPointer && Pointee?.IsVoid == true;

    public bool IsNumeric =>
        Kind == SemanticTypeKind.Primitive &&
        Primitive is not PrimitiveType.Bool and not PrimitiveType.Void;

    public bool IsInteger =>
        Kind == SemanticTypeKind.Primitive &&
        Primitive is not (PrimitiveType.F32 or PrimitiveType.F64 or PrimitiveType.Bool or PrimitiveType.Void);

    public override string ToString() =>
        Kind switch
        {
            SemanticTypeKind.String => "string",
            SemanticTypeKind.Pointer => $"{Pointee}*",
            SemanticTypeKind.Struct => FormatNamed(StructName ?? "struct"),
            SemanticTypeKind.Class => FormatNamed(StructName ?? "class"),
            SemanticTypeKind.Interface => FormatNamed(StructName ?? "interface"),
            SemanticTypeKind.Array => $"{ElementType}[{ArrayLength}]",
            SemanticTypeKind.Enum => EnumName ?? "enum",
            SemanticTypeKind.Union => FormatNamed(EnumName ?? "union"),
            SemanticTypeKind.GenericParameter => GenericParameterName ?? "generic",
            _ => Primitive?.ToString().ToLowerInvariant() ?? "unknown"
        };

    private string FormatNamed(string name) =>
        TypeArguments is { Count: > 0 }
            ? $"{name}<{string.Join(", ", TypeArguments)}>"
            : name;
}
