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

    private CompilationUnit Parse(string source) =>
        new Parser(_tokenizer.Tokenize(source), Log.Logger).Parse();
}
