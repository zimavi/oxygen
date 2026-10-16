using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Ast;

public interface ISyntaxNode
{
    SourceLocation Location { get; }
}

public abstract record SyntaxNode(SourceLocation Location) : ISyntaxNode;

public enum Visibility
{
    Private,
    Public
}

public sealed record CompilationUnit(
    string? ModuleName,
    Visibility ModuleVisibility,
    IReadOnlyList<ImportDeclaration> Imports,
    IReadOnlyList<FunctionDeclaration> Functions,
    SourceLocation Location) : SyntaxNode(Location)
{
    public IReadOnlyList<StructDeclaration> Structs { get; init; } = [];
    public IReadOnlyList<EnumDeclaration> Enums { get; init; } = [];
    public IReadOnlyList<UnionDeclaration> Unions { get; init; } = [];
}

public sealed record UnionCaseDeclaration(
    string Name,
    TypeSyntax? PayloadType,
    SourceLocation Location) : SyntaxNode(Location);

public sealed record UnionDeclaration(
    string Name,
    IReadOnlyList<UnionCaseDeclaration> Cases,
    Visibility Visibility,
    SourceLocation Location,
    IReadOnlyList<AnnotationSyntax>? Annotations = null) : SyntaxNode(Location)
{
    public IReadOnlyList<AnnotationSyntax> DeclaredAnnotations => Annotations ?? [];
}

public sealed record EnumMemberDeclaration(
    string Name,
    int? ExplicitValue,
    SourceLocation Location) : SyntaxNode(Location);

public sealed record EnumDeclaration(
    string Name,
    PrimitiveType UnderlyingType,
    IReadOnlyList<EnumMemberDeclaration> Members,
    Visibility Visibility,
    SourceLocation Location,
    IReadOnlyList<AnnotationSyntax>? Annotations = null) : SyntaxNode(Location)
{
    public IReadOnlyList<AnnotationSyntax> DeclaredAnnotations => Annotations ?? [];
}

public sealed record StructFieldDeclaration(
    TypeSyntax Type,
    string Name,
    SourceLocation Location) : SyntaxNode(Location);

public sealed record StructDeclaration(
    string Name,
    IReadOnlyList<StructFieldDeclaration> Fields,
    Visibility Visibility,
    SourceLocation Location,
    IReadOnlyList<AnnotationSyntax>? Annotations = null) : SyntaxNode(Location)
{
    public IReadOnlyList<AnnotationSyntax> DeclaredAnnotations => Annotations ?? [];
}

public sealed record AnnotationSyntax(
    string Name,
    IReadOnlyList<string> Arguments,
    SourceLocation Location) : SyntaxNode(Location);

public sealed record ImportDeclaration(
    string Name,
    SourceLocation Location) : SyntaxNode(Location);

public sealed record FunctionDeclaration(
    TypeSyntax ReturnType,
    string Name,
    IReadOnlyList<ParameterDeclaration> Parameters,
    BlockStatement Body,
    Visibility Visibility,
    SourceLocation Location,
    IReadOnlyList<AnnotationSyntax>? Annotations = null,
    bool IsExternalDeclaration = false) : SyntaxNode(Location)
{
    public IReadOnlyList<AnnotationSyntax> DeclaredAnnotations => Annotations ?? [];
}

public sealed record ParameterDeclaration(
    TypeSyntax Type,
    string Name,
    SourceLocation Location) : SyntaxNode(Location);

public enum PrimitiveType
{
    I8,
    I16,
    I32,
    I64,
    U8,
    U16,
    U32,
    U64,
    F32,
    F64,
    Bool,
    Void
}

public sealed record TypeSyntax(
    PrimitiveType? Primitive,
    bool IsInferred,
    SourceLocation Location,
    bool IsString = false,
    TypeSyntax? Pointee = null,
    string? NamedType = null,
    int? ArrayLength = null,
    TypeSyntax? ElementType = null) : SyntaxNode(Location)
{
    public bool IsPointer => Pointee is not null;
    public bool IsArray => ArrayLength is not null;
}

public abstract record Statement(SourceLocation Location) : SyntaxNode(Location);

public sealed record BlockStatement(
    IReadOnlyList<Statement> Statements,
    SourceLocation Location) : Statement(Location);

public sealed record VariableDeclarationStatement(
    TypeSyntax Type,
    string Name,
    bool IsConst,
    Expression? Initializer,
    SourceLocation Location) : Statement(Location);

public sealed record AssignmentStatement(
    Expression Target,
    Expression Value,
    SourceLocation Location) : Statement(Location);

public sealed record ReturnStatement(
    Expression? Value,
    SourceLocation Location) : Statement(Location);

public sealed record ExpressionStatement(
    Expression Expression,
    SourceLocation Location) : Statement(Location);

public sealed record IfStatement(
    Expression Condition,
    Statement Then,
    Statement? Else,
    SourceLocation Location) : Statement(Location);

public sealed record WhileStatement(
    Expression Condition,
    Statement Body,
    SourceLocation Location) : Statement(Location);

public sealed record ForStatement(
    Statement? Initializer,
    Expression? Condition,
    Statement? Increment,
    Statement Body,
    SourceLocation Location) : Statement(Location);

public sealed record BreakStatement(SourceLocation Location) : Statement(Location);

public sealed record ContinueStatement(SourceLocation Location) : Statement(Location);

public abstract record Expression(SourceLocation Location) : SyntaxNode(Location);

public sealed record IdentifierExpression(
    string Name,
    SourceLocation Location) : Expression(Location);

public sealed record IntegerLiteralExpression(
    string Lexeme,
    SourceLocation Location) : Expression(Location);

public sealed record FloatingPointLiteralExpression(
    string Lexeme,
    SourceLocation Location) : Expression(Location);

public sealed record StringLiteralExpression(
    string Lexeme,
    SourceLocation Location) : Expression(Location);

public sealed record BooleanLiteralExpression(
    bool Value,
    SourceLocation Location) : Expression(Location);

public sealed record UnaryExpression(
    TokenKind Operator,
    Expression Operand,
    SourceLocation Location) : Expression(Location);

public sealed record BinaryExpression(
    Expression Left,
    TokenKind Operator,
    Expression Right,
    SourceLocation Location) : Expression(Location);

public sealed record CallExpression(
    string Name,
    IReadOnlyList<Expression> Arguments,
    SourceLocation Location) : Expression(Location);

public sealed record StructLiteralExpression(
    string TypeName,
    IReadOnlyList<StructLiteralField> Fields,
    SourceLocation Location) : Expression(Location);

public sealed record StructLiteralField(
    string Name,
    Expression Value,
    SourceLocation Location);

public sealed record MemberAccessExpression(
    Expression Target,
    string MemberName,
    SourceLocation Location) : Expression(Location);

public sealed record ArrayLiteralExpression(
    IReadOnlyList<Expression> Elements,
    SourceLocation Location) : Expression(Location);

public sealed record IndexExpression(
    Expression Target,
    Expression Index,
    SourceLocation Location) : Expression(Location);

public sealed record CastExpression(
    TypeSyntax TargetType,
    Expression Operand,
    SourceLocation Location) : Expression(Location);

public sealed record LengthExpression(
    Expression Target,
    SourceLocation Location) : Expression(Location);

public sealed record UnionConstructionExpression(
    string UnionName,
    string CaseName,
    IReadOnlyList<Expression> Arguments,
    SourceLocation Location) : Expression(Location);

public sealed record SwitchCase(
    string CaseName,
    string? BindingName,
    Statement Body,
    SourceLocation Location) : SyntaxNode(Location);

public sealed record SwitchStatement(
    Expression Value,
    IReadOnlyList<SwitchCase> Cases,
    SourceLocation Location) : Statement(Location);
