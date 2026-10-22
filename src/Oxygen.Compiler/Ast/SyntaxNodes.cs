using System.Numerics;
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
    Public,
    Protected,
    Internal
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
    public IReadOnlyList<ClassDeclaration> Classes { get; init; } = [];
    public IReadOnlyList<InterfaceDeclaration> Interfaces { get; init; } = [];
    public IReadOnlyList<GlobalVariableDeclaration> Globals { get; init; } = [];
    public IReadOnlyList<TypeAliasDeclaration> Aliases { get; init; } = [];
}

// A type alias, `type Name = Target;`: another name for the type Target, not a new type. Its target may be any type. An alias is
// declared at module scope, and `public` exports it like the other type declarations.
public sealed record TypeAliasDeclaration(
    string Name,
    TypeSyntax Target,
    Visibility Visibility,
    SourceLocation Location) : SyntaxNode(Location);

// A module-level variable. Without an initializer it starts as zero. Its initializer must be a constant.
public sealed record GlobalVariableDeclaration(
    TypeSyntax Type,
    string Name,
    bool IsConst,
    Expression? Initializer,
    SourceLocation Location) : SyntaxNode(Location);

public sealed record UnionCaseDeclaration(
    string Name,
    TypeSyntax? PayloadType,
    SourceLocation Location) : SyntaxNode(Location);

public sealed record UnionDeclaration(
    string Name,
    IReadOnlyList<UnionCaseDeclaration> Cases,
    Visibility Visibility,
    SourceLocation Location,
    IReadOnlyList<AnnotationSyntax>? Annotations = null,
    IReadOnlyList<TypeParameterSyntax>? TypeParameters = null) : SyntaxNode(Location)
{
    public IReadOnlyList<AnnotationSyntax> DeclaredAnnotations => Annotations ?? [];
    public IReadOnlyList<TypeParameterSyntax> DeclaredTypeParameters => TypeParameters ?? [];
}

public sealed record EnumMemberDeclaration(
    string Name,
    BigInteger? ExplicitValue,
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
    IReadOnlyList<AnnotationSyntax>? Annotations = null,
    IReadOnlyList<TypeParameterSyntax>? TypeParameters = null) : SyntaxNode(Location)
{
    public IReadOnlyList<AnnotationSyntax> DeclaredAnnotations => Annotations ?? [];
    public IReadOnlyList<TypeParameterSyntax> DeclaredTypeParameters => TypeParameters ?? [];
}

public sealed record ClassFieldDeclaration(
    TypeSyntax Type,
    string Name,
    Visibility Visibility,
    SourceLocation Location,
    Expression? Initializer = null,
    bool IsConst = false,
    bool IsStatic = false) : SyntaxNode(Location);

public enum PropertyAccessorKind { Get, Set, Init }

public sealed record PropertyAccessorDeclaration(
    PropertyAccessorKind Kind,
    Visibility Visibility,
    BlockStatement? Body,
    bool IsAuto,
    SourceLocation Location) : SyntaxNode(Location);

public sealed record PropertyDeclaration(
    TypeSyntax Type,
    string Name,
    Visibility Visibility,
    IReadOnlyList<PropertyAccessorDeclaration> Accessors,
    SourceLocation Location,
    bool IsVirtual = false,
    bool IsOverride = false) : SyntaxNode(Location)
{
    public bool HasGetter => Accessors.Any(a => a.Kind == PropertyAccessorKind.Get);
    public bool HasSetter => Accessors.Any(a => a.Kind == PropertyAccessorKind.Set);
    public bool HasInitializer => Accessors.Any(a => a.Kind == PropertyAccessorKind.Init);
}

public enum ConstructorInitializerKind { This, Base }

// The `: this(...)` or `: base(...)` call that starts a constructor body.
public sealed record ConstructorInitializer(
    ConstructorInitializerKind Kind,
    IReadOnlyList<Expression> Arguments,
    SourceLocation Location) : SyntaxNode(Location);

public sealed record ConstructorDeclaration(
    string ClassName,
    IReadOnlyList<ParameterDeclaration> Parameters,
    BlockStatement Body,
    Visibility Visibility,
    SourceLocation Location) : SyntaxNode(Location)
{
    public ConstructorInitializer? Initializer { get; init; }
}

public sealed record DestructorDeclaration(
    string ClassName,
    BlockStatement Body,
    Visibility Visibility,
    SourceLocation Location) : SyntaxNode(Location);

public sealed record ClassDeclaration(
    string Name,
    string? BaseType,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<ClassFieldDeclaration> Fields,
    IReadOnlyList<PropertyDeclaration>? Properties,
    IReadOnlyList<FunctionDeclaration> Methods,
    IReadOnlyList<ConstructorDeclaration> Constructors,
    DestructorDeclaration? Destructor,
    Visibility Visibility,
    SourceLocation Location,
    IReadOnlyList<AnnotationSyntax>? Annotations = null,
    IReadOnlyList<TypeParameterSyntax>? TypeParameters = null) : SyntaxNode(Location)
{
    public IReadOnlyList<TypeSyntax>? BaseTypeSyntax { get; init; }
    public IReadOnlyList<TypeSyntax>? InterfaceTypeSyntaxes { get; init; }
    public IReadOnlyList<PropertyDeclaration> DeclaredProperties => Properties ?? [];
    public IReadOnlyList<AnnotationSyntax> DeclaredAnnotations => Annotations ?? [];
    public IReadOnlyList<TypeParameterSyntax> DeclaredTypeParameters => TypeParameters ?? [];
    public bool IsValue => DeclaredAnnotations.Any(annotation => annotation.Name == "value");
}

public sealed record InterfaceDeclaration(
    string Name,
    IReadOnlyList<FunctionDeclaration> Methods,
    IReadOnlyList<PropertyDeclaration>? Properties,
    Visibility Visibility,
    SourceLocation Location,
    IReadOnlyList<AnnotationSyntax>? Annotations = null,
    IReadOnlyList<TypeParameterSyntax>? TypeParameters = null) : SyntaxNode(Location)
{
    public IReadOnlyList<AnnotationSyntax> DeclaredAnnotations => Annotations ?? [];
    public IReadOnlyList<TypeParameterSyntax> DeclaredTypeParameters => TypeParameters ?? [];
    public IReadOnlyList<PropertyDeclaration> DeclaredProperties => Properties ?? [];
}

public sealed record AnnotationSyntax(
    string Name,
    IReadOnlyList<string> Arguments,
    SourceLocation Location) : SyntaxNode(Location);

// One name in a symbol list, such as `free as release` in `import std.memory{alloc, free as release};`. LocalName is the name
// the importing module uses, which is Name when there is no alias.
public sealed record ImportSymbol(string Name, string LocalName, SourceLocation Location) : SyntaxNode(Location);

// An import of one module. A symbol list has the module as Name and its listed names in Symbols, which brings only those
// declarations into scope unqualified.
public sealed record ImportDeclaration(
    string Name,
    SourceLocation Location,
    string? Alias = null,
    IReadOnlyList<ImportSymbol>? Symbols = null) : SyntaxNode(Location);

public sealed record FunctionDeclaration(
    TypeSyntax ReturnType,
    string Name,
    IReadOnlyList<ParameterDeclaration> Parameters,
    BlockStatement Body,
    Visibility Visibility,
    SourceLocation Location,
    IReadOnlyList<AnnotationSyntax>? Annotations = null,
    bool IsExternalDeclaration = false,
    IReadOnlyList<TypeParameterSyntax>? TypeParameters = null,
    bool IsVirtual = false,
    bool IsOverride = false,
    bool IsStatic = false) : SyntaxNode(Location)
{
    public IReadOnlyList<AnnotationSyntax> DeclaredAnnotations => Annotations ?? [];
    public IReadOnlyList<TypeParameterSyntax> DeclaredTypeParameters => TypeParameters ?? [];
}

public sealed record TypeParameterSyntax(
    string Name,
    SourceLocation Location) : SyntaxNode(Location)
{
    // The interfaces a type argument must implement, written `<T : A & B>`. Empty when unconstrained.
    public IReadOnlyList<string> Constraints { get; init; } = [];
}

// A parameter. DefaultValue is the constant a call uses when it omits the argument; null when the parameter is required.
public sealed record ParameterDeclaration(
    TypeSyntax Type,
    string Name,
    SourceLocation Location,
    Expression? DefaultValue = null) : SyntaxNode(Location);

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
    USize,
    ISize,
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
    TypeSyntax? ElementType = null,
    IReadOnlyList<TypeSyntax>? TypeArguments = null) : SyntaxNode(Location)
{
    public bool IsPointer => Pointee is not null;
    public bool IsArray => ArrayLength is not null;
    public IReadOnlyList<TypeSyntax> DeclaredTypeArguments => TypeArguments ?? [];

    // A function pointer type, `fn <return> (<types>)`. Its parameters are types only.
    public bool IsFunction => FunctionReturn is not null;
    public TypeSyntax? FunctionReturn { get; init; }
    public IReadOnlyList<TypeSyntax> FunctionParameters { get; init; } = [];
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
    SourceLocation Location,
    TokenKind Operator = TokenKind.Equals) : Statement(Location);

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

public sealed record ForInStatement(
    string VariableName,
    TypeSyntax VariableType,
    Expression Collection,
    Statement Body,
    SourceLocation Location) : Statement(Location);

public sealed record BreakStatement(SourceLocation Location) : Statement(Location);

public sealed record ContinueStatement(SourceLocation Location) : Statement(Location);

// `throw expression;` throws a value. `throw;` has no value and rethrows the exception of the catch clause it is in.
public sealed record ThrowStatement(
    Expression? Value,
    SourceLocation Location) : Statement(Location);

public sealed record DeleteStatement(
    Expression Target,
    SourceLocation Location) : Statement(Location);

public sealed record CatchClause(
    TypeSyntax? ExceptionType,
    string? BindingName,
    BlockStatement Body,
    SourceLocation Location) : SyntaxNode(Location);

public sealed record TryStatement(
    BlockStatement Body,
    IReadOnlyList<CatchClause> Catches,
    BlockStatement? Finally,
    SourceLocation Location) : Statement(Location);

public abstract record Expression(SourceLocation Location) : SyntaxNode(Location);

public sealed record IdentifierExpression(
    string Name,
    SourceLocation Location) : Expression(Location);

public sealed record ThisExpression(SourceLocation Location) : Expression(Location);

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

public sealed record NullLiteralExpression(SourceLocation Location) : Expression(Location);

public sealed record IsExpression(
    Expression Operand,
    TypeSyntax TargetType,
    string? BindingName,
    SourceLocation Location) : Expression(Location);

public sealed record UnaryExpression(
    TokenKind Operator,
    Expression Operand,
    SourceLocation Location,
    bool IsPostfix = false) : Expression(Location);

public sealed record BinaryExpression(
    Expression Left,
    TokenKind Operator,
    Expression Right,
    SourceLocation Location) : Expression(Location);

public sealed record ConditionalExpression(
    Expression Condition,
    Expression WhenTrue,
    Expression WhenFalse,
    SourceLocation Location) : Expression(Location);

// A call. Without a receiver, Name is the function's name, possibly qualified by a module (`io.print`), or `this.m` or
// `base.m` for a method of the current instance. With a receiver, `receiver.Name(...)` calls the method Name on the value
// of the receiver expression, and Name is the method alone.
public sealed record CallExpression(
    string Name,
    IReadOnlyList<Expression> Arguments,
    SourceLocation Location,
    IReadOnlyList<TypeSyntax>? TypeArguments = null,
    Expression? Receiver = null) : Expression(Location)
{
    public IReadOnlyList<TypeSyntax> ExplicitTypeArguments => TypeArguments ?? [];

    // A name with type arguments and no parentheses, such as `Box<i32>.count` or `Option<T>.None`. It is a static field or a union case.
    public bool IsBareName { get; init; }
}

public sealed record StructLiteralExpression(
    string TypeName,
    IReadOnlyList<StructLiteralField> Fields,
    SourceLocation Location) : Expression(Location);

public sealed record StructLiteralField(
    string Name,
    Expression Value,
    SourceLocation Location);

// `name: value` in a call's arguments. It is only valid as an argument, where the call binds it to the parameter called `name`.
public sealed record NamedArgumentExpression(
    string Name,
    Expression Value,
    SourceLocation Location) : Expression(Location);

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

public sealed record SizeOfExpression(
    TypeSyntax? TargetType,
    Expression? Operand,
    SourceLocation Location) : Expression(Location);

public sealed record UnionConstructionExpression(
    string UnionName,
    string CaseName,
    IReadOnlyList<Expression> Arguments,
    SourceLocation Location) : Expression(Location);

public sealed record NewExpression(
    TypeSyntax Type,
    IReadOnlyList<Expression> Arguments,
    SourceLocation Location) : Expression(Location);

public sealed record ClassConstructionExpression(
    string TypeName,
    IReadOnlyList<Expression> Arguments,
    SourceLocation Location,
    IReadOnlyList<TypeSyntax>? TypeArguments = null) : Expression(Location)
{
    public IReadOnlyList<TypeSyntax> ExplicitTypeArguments => TypeArguments ?? [];
}

public sealed record SwitchCase(
    string? CaseName,
    Expression? Value,
    bool IsDefault,
    string? BindingName,
    Statement Body,
    SourceLocation Location,
    TypeSyntax? PatternType = null) : SyntaxNode(Location);

public sealed record SwitchStatement(
    Expression Value,
    IReadOnlyList<SwitchCase> Cases,
    SourceLocation Location) : Statement(Location);

// A lambda without captures, `fn <return> (<type> <name>, ...) { <body> }`. Its body cannot name a local of the function it is written in.
// Its type is the function pointer type of its signature.
public sealed record LambdaExpression(
    TypeSyntax ReturnType,
    IReadOnlyList<ParameterDeclaration> Parameters,
    BlockStatement Body,
    SourceLocation Location) : Expression(Location);

// A call of a function pointer that is not a name: an element of an array, the result of a call, or any other expression of a function
// pointer type. A call through a name is a CallExpression, which the analysis resolves to a function or a function pointer.
public sealed record IndirectCallExpression(
    Expression Callee,
    IReadOnlyList<Expression> Arguments,
    SourceLocation Location) : Expression(Location);
