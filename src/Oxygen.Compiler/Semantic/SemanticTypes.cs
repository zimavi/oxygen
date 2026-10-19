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

    // Equality is structural, so `==` on types works wherever they were built. The default record equality would compare the
    // type-argument lists by reference.
    public bool Equals(SemanticType? other) => SemanticTypeComparer.Instance.Equals(this, other);

    public override int GetHashCode() => SemanticTypeComparer.Instance.GetHashCode(this);

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

// Structural equality of semantic types. Type arguments are lists, so they are compared by content, not by reference.
internal sealed class SemanticTypeComparer : IEqualityComparer<SemanticType>
{
    public static SemanticTypeComparer Instance { get; } = new();

    public bool Equals(SemanticType? left, SemanticType? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null
            || left.Kind != right.Kind
            || left.Primitive != right.Primitive
            || left.StructName != right.StructName
            || left.EnumName != right.EnumName
            || left.GenericParameterName != right.GenericParameterName
            || left.ArrayLength != right.ArrayLength)
            return false;
        if (!Equals(left.Pointee, right.Pointee) || !Equals(left.ElementType, right.ElementType))
            return false;
        return (left.TypeArguments ?? []).SequenceEqual(right.TypeArguments ?? [], this);
    }

    public int GetHashCode(SemanticType type)
    {
        var hash = new HashCode();
        hash.Add(type.Kind);
        hash.Add(type.Primitive);
        hash.Add(type.StructName);
        hash.Add(type.EnumName);
        hash.Add(type.GenericParameterName);
        hash.Add(type.ArrayLength);
        if (type.Pointee is not null)
            hash.Add(GetHashCode(type.Pointee));
        if (type.ElementType is not null)
            hash.Add(GetHashCode(type.ElementType));
        foreach (var argument in type.TypeArguments ?? [])
            hash.Add(GetHashCode(argument));
        return hash.ToHashCode();
    }
}
