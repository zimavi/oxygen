using Oxygen.Compiler.Ast;

namespace Oxygen.Compiler.Semantic;

public enum SemanticTypeKind
{
    Primitive,
    String,
    Pointer,
    Struct,
    Array,
    Enum,
    Union
}

public sealed record SemanticType(
    SemanticTypeKind Kind,
    PrimitiveType? Primitive,
    SemanticType? Pointee = null,
    string? StructName = null,
    string? EnumName = null)
{
    public int ArrayLength { get; init; }
    public SemanticType? ElementType { get; init; }
    public static SemanticType String => new(SemanticTypeKind.String, null);

    public static SemanticType FromPrimitive(PrimitiveType primitive) => new(SemanticTypeKind.Primitive, primitive);

    public static SemanticType PointerTo(SemanticType pointee) => new(SemanticTypeKind.Pointer, null, pointee);

    public static SemanticType Struct(string name) => new(SemanticTypeKind.Struct, null, null, name);
    public static SemanticType Enum(string name) => new(SemanticTypeKind.Enum, null, null, null, name);
    public static SemanticType Union(string name) => new(SemanticTypeKind.Union, null, null, null, name);
    public static SemanticType ArrayOf(SemanticType element, int length) => new(SemanticTypeKind.Array, null) { ElementType = element, ArrayLength = length };

    public bool IsVoid => Kind == SemanticTypeKind.Primitive && Primitive == PrimitiveType.Void;

    public bool IsPointer => Kind == SemanticTypeKind.Pointer;

    public bool IsStruct => Kind == SemanticTypeKind.Struct;
    public bool IsArray => Kind == SemanticTypeKind.Array;
    public bool IsEnum => Kind == SemanticTypeKind.Enum;
    public bool IsUnion => Kind == SemanticTypeKind.Union;

    public bool IsNumeric =>
        Kind == SemanticTypeKind.Primitive &&
        Primitive is not PrimitiveType.Bool and not PrimitiveType.Void;

    public override string ToString() =>
        Kind switch
        {
            SemanticTypeKind.String => "string",
            SemanticTypeKind.Pointer => $"{Pointee}*",
            SemanticTypeKind.Struct => StructName ?? "struct",
            SemanticTypeKind.Array => $"{ElementType}[{ArrayLength}]",
            SemanticTypeKind.Enum => EnumName ?? "enum",
            SemanticTypeKind.Union => EnumName ?? "union",
            _ => Primitive?.ToString().ToLowerInvariant() ?? "unknown"
        };
}
