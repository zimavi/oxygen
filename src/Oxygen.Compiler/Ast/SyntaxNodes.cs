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
    SourceLocation Location) : SyntaxNode(Location);

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
    TypeSyntax? Pointee = null) : SyntaxNode(Location)
{
    public bool IsPointer => Pointee is not null;
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
