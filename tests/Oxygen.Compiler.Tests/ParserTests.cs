using System.Linq;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Parsing;
using Serilog;
using Xunit;

namespace Oxygen.Compiler.Tests;

public sealed class ParserTests
{
    private readonly Tokenizer _tokenizer = new(Log.Logger);

    [Fact]
    public void ParsesMainFunctionAndReturnExpression()
    {
        var unit = Parse("i32 main() { return 1 + 2 * 3; }");

        var function = Assert.Single(unit.Functions);
        Assert.Equal("main", function.Name);
        Assert.Equal(PrimitiveType.I32, function.ReturnType.Primitive);
        var statement = Assert.IsType<ReturnStatement>(Assert.Single(function.Body.Statements));
        var addition = Assert.IsType<BinaryExpression>(statement.Value);
        Assert.Equal(TokenKind.Plus, addition.Operator);
        Assert.IsType<BinaryExpression>(addition.Right);
    }

    [Fact]
    public void ParsesEnumsAndExplicitCasts()
    {
        var unit = Parse("enum Color : i32 { Red, Green = 4, Blue } i32 main() { return (i32) Color.Blue; }");

        var enumeration = Assert.Single(unit.Enums);
        Assert.Equal("Color", enumeration.Name);
        Assert.Equal(PrimitiveType.I32, enumeration.UnderlyingType);
        Assert.Equal(["Red", "Green", "Blue"], enumeration.Members.Select(member => member.Name));
        var cast = Assert.IsType<CastExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(Assert.Single(unit.Functions).Body.Statements)).Value);
        Assert.Equal(PrimitiveType.I32, cast.TargetType.Primitive);
    }

    [Fact]
    public void ParsesTaggedUnionsAndSwitchCases()
    {
        var unit = Parse(
            "union Result { Ok(i32); Error(i32); Empty; } i32 main() { Result value = Result.Ok(42); switch (value) { case Ok(number): return number; case Error(code): return code; case Empty: return 0; } }");

        var union = Assert.Single(unit.Unions);
        Assert.Equal(["Ok", "Error", "Empty"], union.Cases.Select(item => item.Name));
        var switchStatement = Assert.IsType<SwitchStatement>(unit.Functions[0].Body.Statements[1]);
        Assert.Equal("number", switchStatement.Cases[0].BindingName);
        Assert.Null(switchStatement.Cases[2].BindingName);
    }

    [Fact]
    public void ParsesParametersDeclarationsAssignmentsAndCalls()
    {
        var unit = Parse("i32 add(i32 left, i32 right) { var result = left + right; result = print(result); return result; }");

        var function = Assert.Single(unit.Functions);
        Assert.Equal(2, function.Parameters.Count);
        Assert.IsType<VariableDeclarationStatement>(function.Body.Statements[0]);
        var assignment = Assert.IsType<AssignmentStatement>(function.Body.Statements[1]);
        var call = Assert.IsType<CallExpression>(assignment.Value);
        Assert.Equal("print", call.Name);
    }

    [Fact]
    public void ParsesThisMemberAccess()
    {
        var unit = Parse("class Box { i32 value; i32 get() { return this.value; } } i32 main() { return 0; }");

        var member = Assert.IsType<MemberAccessExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(unit.Classes[0].Methods[0].Body.Statements)).Value);
        Assert.IsType<ThisExpression>(member.Target);
        Assert.Equal("value", member.MemberName);
    }

    [Fact]
    public void ReportsMalformedSyntax()
    {
        var exception = Assert.Throws<ParseException>(() => Parse("i32 main() { return 1 }"));

        Assert.Equal(TokenKind.CloseBrace, exception.Token.Kind);
    }

    [Fact]
    public void ParsesNestedModulesImportsVisibilityAndQualifiedCalls()
    {
        var unit = Parse("public module foo.bar; import foo.bar.baz; public i32 main() { return bar.func(); }");

        Assert.Equal("foo.bar", unit.ModuleName);
        Assert.Equal(Visibility.Public, unit.ModuleVisibility);
        Assert.Equal("foo.bar.baz", Assert.Single(unit.Imports).Name);
        Assert.Equal(Visibility.Public, Assert.Single(unit.Functions).Visibility);
        var call = Assert.IsType<CallExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(Assert.Single(unit.Functions).Body.Statements)).Value);
        Assert.Equal("bar.func", call.Name);
    }

    [Fact]
    public void ParsesFunctionAnnotations()
    {
        var unit = Parse("@extern(\"puts\") @calling_convention(c) i32 main() { return 0; }");

        var annotations = Assert.Single(unit.Functions).DeclaredAnnotations;
        Assert.Equal(["extern", "calling_convention"], annotations.Select(annotation => annotation.Name));
        Assert.Equal(["\"puts\""], annotations[0].Arguments);
        Assert.Equal(["c"], annotations[1].Arguments);
    }

    [Fact]
    public void ParsesMultipleExternDeclarationsWithAnnotations()
    {
        var unit = Parse(
            """
            module std.io;
            @extern("oxygen_write_stdout")
            public extern void write_stdout(string value);
            @extern("oxygen_write_stderr")
            public extern void write_stderr(string value);
            public void print(string value) {
                write_stdout(value);
            }
            """);

        Assert.Equal(3, unit.Functions.Count);
        Assert.True(unit.Functions[0].IsExternalDeclaration);
        Assert.True(unit.Functions[1].IsExternalDeclaration);
    }

    [Fact]
    public void ParsesMultipleExternDeclarationsWithDiagnostics()
    {
        var result = new Parser(
                _tokenizer.Tokenize(
                    """
                    module std.io;
                    @extern("oxygen_write_stdout")
                    public extern void write_stdout(string value);
                    @extern("oxygen_write_stderr")
                    public extern void write_stderr(string value);
                    public void print(string value) {
                        write_stdout(value);
                    }
                    """),
                Log.Logger)
            .ParseWithDiagnostics("std/io.oxy");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(3, result.Unit.Functions.Count);
    }

    [Fact]
    public void ParsesExternFunctionDeclaration()
    {
        var unit = Parse("@extern(\"puts\") extern i32 puts(i32 value);");

        var function = Assert.Single(unit.Functions);
        Assert.True(function.IsExternalDeclaration);
        Assert.Empty(function.Body.Statements);
    }

    [Fact]
    public void ParsesExplicitStringType()
    {
        var unit = Parse("string main() { string value = \"hello\"; return value; }");

        var function = Assert.Single(unit.Functions);
        var declaration = Assert.IsType<VariableDeclarationStatement>(function.Body.Statements[0]);
        Assert.True(declaration.Type.IsString);
    }

    [Fact]
    public void ParsesPointerTypesAndDereferenceAssignments()
    {
        var unit = Parse("i32 main() { i32 value = 41; i32* pointer = &value; *pointer = 42; return *pointer; }");

        var declaration = Assert.IsType<VariableDeclarationStatement>(unit.Functions[0].Body.Statements[1]);
        Assert.True(declaration.Type.IsPointer);
        Assert.Equal(PrimitiveType.I32, declaration.Type.Pointee!.Primitive);
        var assignment = Assert.IsType<AssignmentStatement>(unit.Functions[0].Body.Statements[2]);
        Assert.IsType<UnaryExpression>(assignment.Target);
    }

    [Fact]
    public void ParsesGenericFunctionsAndExplicitTypeArguments()
    {
        var unit = Parse("i32 identity<T>(T value) { return 1; } i32 main() { return identity<i32>(2); }");

        var generic = unit.Functions[0];
        var parameter = Assert.Single(generic.DeclaredTypeParameters);
        Assert.Equal("T", parameter.Name);
        var call = Assert.IsType<CallExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(unit.Functions[1].Body.Statements)).Value);
        Assert.Equal(PrimitiveType.I32, Assert.Single(call.ExplicitTypeArguments).Primitive);
    }

    [Fact]
    public void ParsesGenericCastSizeOfAndVoidPointerTypes()
    {
        var unit = Parse(
            "void* pointer<T>(T value) { return pointer; } i32 main() { i32 value = cast<i32>(sizeof<void*>()); return sizeof(value); }");

        Assert.True(unit.Functions[0].ReturnType.IsPointer);
        Assert.Equal(PrimitiveType.Void, unit.Functions[0].ReturnType.Pointee!.Primitive);
        var declaration = Assert.IsType<VariableDeclarationStatement>(unit.Functions[1].Body.Statements[0]);
        var cast = Assert.IsType<CastExpression>(declaration.Initializer);
        Assert.IsType<SizeOfExpression>(cast.Operand);
        var sizeOfValue = Assert.IsType<SizeOfExpression>(
            Assert.IsType<ReturnStatement>(unit.Functions[1].Body.Statements[1]).Value);
        Assert.NotNull(sizeOfValue.Operand);
    }

    [Fact]
    public void ParsesStructDeclarationsLiteralsAndMembers()
    {
        var unit = Parse(
            "struct Point { i32 x; i32 y; } i32 main() { Point point = Point { x: 1, y: 2 }; point.x = 3; return point.y; }");

        var structure = Assert.Single(unit.Structs);
        Assert.Equal("Point", structure.Name);
        Assert.Equal(["x", "y"], structure.Fields.Select(field => field.Name));
        var declaration = Assert.IsType<VariableDeclarationStatement>(unit.Functions[0].Body.Statements[0]);
        Assert.Equal("Point", declaration.Type.NamedType);
        Assert.IsType<StructLiteralExpression>(declaration.Initializer);
        Assert.IsType<MemberAccessExpression>(
            Assert.IsType<AssignmentStatement>(unit.Functions[0].Body.Statements[1]).Target);
    }

    [Fact]
    public void ParsesClassesInterfacesMembersConstructorsDestructorsAndNew()
    {
        var unit = Parse(
            "interface Printable { void print(i32 value); } " +
            "class Child : Base, Printable { i32 value; public Child(i32 initial) { value = initial; } " +
            "void print(i32 value) { } ~Child() { } } " +
            "i32 main() { var child = new Child(42); return 0; }");

        var @interface = Assert.Single(unit.Interfaces);
        Assert.Equal("Printable", @interface.Name);
        Assert.Equal("print", Assert.Single(@interface.Methods).Name);

        var @class = Assert.Single(unit.Classes);
        Assert.Equal("Base", @class.BaseType);
        Assert.Equal(["Printable"], @class.Interfaces);
        Assert.Equal("value", Assert.Single(@class.Fields).Name);
        Assert.Equal("Child", Assert.Single(@class.Constructors).ClassName);
        Assert.Equal("Child", @class.Destructor!.ClassName);
        Assert.Equal("print", Assert.Single(@class.Methods).Name);

        var declaration = Assert.IsType<VariableDeclarationStatement>(
            unit.Functions[0].Body.Statements[0]);
        var construction = Assert.IsType<NewExpression>(declaration.Initializer);
        Assert.Equal("Child", construction.Type.NamedType);
        Assert.Single(construction.Arguments);
    }

    [Fact]
    public void ParsesFixedArrayTypesLiteralsAndIndexing()
    {
        var unit = Parse("i32 main() { i32[3] values = { 1, 2, 3 }; values[1] = 4; return values[1]; }");

        var declaration = Assert.IsType<VariableDeclarationStatement>(unit.Functions[0].Body.Statements[0]);
        Assert.True(declaration.Type.IsArray);
        Assert.Equal(3, declaration.Type.ArrayLength);
        Assert.IsType<ArrayLiteralExpression>(declaration.Initializer);
        var assignment = Assert.IsType<AssignmentStatement>(unit.Functions[0].Body.Statements[1]);
        Assert.IsType<IndexExpression>(assignment.Target);
    }

    [Fact]
    public void ParsesControlFlowStatements()
    {
        var unit = Parse(
            "i32 main() { i32 value = 0; while (value < 10) { if (value == 5) { continue; } value = value + 1; } for (i32 i = 0; i < 3; i = i + 1) { if (i == 2) break; } return value; }");

        var body = unit.Functions[0].Body.Statements;
        Assert.IsType<WhileStatement>(body[1]);
        var loop = Assert.IsType<ForStatement>(body[2]);
        Assert.IsType<VariableDeclarationStatement>(loop.Initializer);
        Assert.IsType<AssignmentStatement>(loop.Increment);
        var whileBody = Assert.IsType<BlockStatement>(Assert.IsType<WhileStatement>(body[1]).Body);
        Assert.IsType<IfStatement>(whileBody.Statements[0]);
    }

    [Fact]
    public void ParsesPostfixIncrementAndVarAfterForLoop()
    {
        var unit = Parse(
            "i32 main() { i32[2] arr = { 34, 35 }; for (i32 i = 0; i < length(arr); i++) { } var ret = arr[1]; return ret; }");

        var body = unit.Functions[0].Body.Statements;
        var loop = Assert.IsType<ForStatement>(body[1]);
        var increment = Assert.IsType<AssignmentStatement>(loop.Increment);
        Assert.Equal("i", Assert.IsType<IdentifierExpression>(increment.Target).Name);
        Assert.IsType<BinaryExpression>(increment.Value);
        var inferred = Assert.IsType<VariableDeclarationStatement>(body[2]);
        Assert.True(inferred.Type.IsInferred);
    }

    private CompilationUnit Parse(string source) =>
        new Parser(_tokenizer.Tokenize(source), Log.Logger).Parse();
}
