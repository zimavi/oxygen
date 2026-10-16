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

    private CompilationUnit Parse(string source) =>
        new Parser(_tokenizer.Tokenize(source), Log.Logger).Parse();
}
