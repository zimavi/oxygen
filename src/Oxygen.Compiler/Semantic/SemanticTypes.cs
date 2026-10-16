using Oxygen.Compiler.Ast;

namespace Oxygen.Compiler.Semantic;

public enum SemanticTypeKind
{
    Primitive,
    String
}

public readonly record struct SemanticType(SemanticTypeKind Kind, PrimitiveType? Primitive)
{
    public static SemanticType String => new(SemanticTypeKind.String, null);

    public static SemanticType FromPrimitive(PrimitiveType primitive) => new(SemanticTypeKind.Primitive, primitive);

    public bool IsVoid => Kind == SemanticTypeKind.Primitive && Primitive == PrimitiveType.Void;

    public bool IsNumeric =>
        Kind == SemanticTypeKind.Primitive &&
        Primitive is not PrimitiveType.Bool and not PrimitiveType.Void;

    public override string ToString() =>
        Kind == SemanticTypeKind.String ? "string" : Primitive?.ToString().ToLowerInvariant() ?? "unknown";
}
