using Oxygen.Compiler.Ast;

namespace Oxygen.Compiler.Semantic;

public enum SemanticTypeKind
{
    Primitive,
    String,
    Pointer
}

public sealed record SemanticType(
    SemanticTypeKind Kind,
    PrimitiveType? Primitive,
    SemanticType? Pointee = null)
{
    public static SemanticType String => new(SemanticTypeKind.String, null);

    public static SemanticType FromPrimitive(PrimitiveType primitive) => new(SemanticTypeKind.Primitive, primitive);

    public static SemanticType PointerTo(SemanticType pointee) => new(SemanticTypeKind.Pointer, null, pointee);

    public bool IsVoid => Kind == SemanticTypeKind.Primitive && Primitive == PrimitiveType.Void;

    public bool IsPointer => Kind == SemanticTypeKind.Pointer;

    public bool IsNumeric =>
        Kind == SemanticTypeKind.Primitive &&
        Primitive is not PrimitiveType.Bool and not PrimitiveType.Void;

    public override string ToString() =>
        Kind switch
        {
            SemanticTypeKind.String => "string",
            SemanticTypeKind.Pointer => $"{Pointee}*",
            _ => Primitive?.ToString().ToLowerInvariant() ?? "unknown"
        };
}
