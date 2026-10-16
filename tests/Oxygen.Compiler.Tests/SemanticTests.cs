using System.Linq;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Parsing;
using Oxygen.Compiler.Semantic;
using Serilog;
using Xunit;

namespace Oxygen.Compiler.Tests;

public sealed class SemanticTests
{
    private readonly Tokenizer _tokenizer = new(Log.Logger);

    [Fact]
    public void AnalyzesDeclarationsScopesOperatorsAndCalls()
    {
        var model = Analyze(
            """
            i32 add(i32 left, i32 right) {
                var result = left + right;
                {
                    const i32 doubled = result * 2;
                    result = doubled;
                }
                return result;
            }

            i32 main() {
                return add(20, 22);
            }
            """);

        Assert.Equal(2, model.Functions.Count);
        Assert.Equal(
            SemanticType.FromPrimitive(PrimitiveType.I32),
            model.ExpressionTypes.Values.Last());
        Assert.Equal("main", model.MainFunction!.Name);
    }

    [Fact]
    public void FindsVoidMainFunction()
    {
        var model = Analyze("void main() { return; }");

        Assert.NotNull(model.MainFunction);
        Assert.True(model.MainFunction!.ReturnType.IsVoid);
    }

    [Fact]
    public void ResolvesOverloadsByParameterTypes()
    {
        var model = Analyze(
            """
            i32 value(i32 input) { return input + 1; }
            f64 value(f64 input) { return input; }
            i32 main() { return value(41); }
            """);

        Assert.Equal(3, model.Functions.Count);
        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.I32), model.ExpressionTypes.Values.Last());
    }

    [Fact]
    public void RejectsOverloadsWithIdenticalParameterTypes()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "i32 value(i32 input) { return input; } f64 value(i32 input) { return input; } i32 main() { return 0; }"));

        Assert.Contains("with the same parameter types is already declared", exception.Message);
    }

    [Fact]
    public void RejectsMissingMainFunction()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze("i32 helper() { return 1; }"));

        Assert.Contains("A parameterless 'main' function returning i32 or void is required", exception.Message);
    }

    [Fact]
    public void RejectsParameterizedMainFunction()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze("i32 main(i32 value) { return value; }"));

        Assert.Contains("The 'main' function cannot have parameters", exception.Message);
    }

    [Fact]
    public void RejectsMainWithUnsupportedReturnType()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze("bool main() { return true; }"));

        Assert.Contains("The 'main' function must return i32 or void, got bool", exception.Message);
    }

    [Fact]
    public void RejectsUndefinedVariables()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze("i32 main() { return missing; }"));

        Assert.Contains("Variable 'missing' is not declared", exception.Message);
        Assert.Equal(new SourceLocation(1, 21), exception.Location);
    }

    [Fact]
    public void RejectsAssignmentsToConstVariables()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { const i32 value = 1; value = 2; return value; }"));

        Assert.Contains("Cannot assign to const variable 'value'", exception.Message);
    }

    [Fact]
    public void RejectsIncompatibleReturnTypes()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze("i32 main() { return true; }"));

        Assert.Contains("Cannot return bool from function 'main'; expected i32", exception.Message);
    }

    [Fact]
    public void RejectsInvalidCalls()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 add(i32 left, i32 right) { return left + right; } i32 main() { return add(1); }"));

        Assert.Contains("expects 2 argument(s), got 1", exception.Message);
    }

    [Fact]
    public void RejectsDuplicateDeclarationsAndShadowingInOneScope()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32 value = 1; i32 value = 2; return value; }"));

        Assert.Contains("Variable 'value' is already declared in this scope", exception.Message);
    }

    private SemanticModel Analyze(string source)
    {
        var unit = new Parser(_tokenizer.Tokenize(source), Log.Logger).Parse();
        return new SemanticAnalyzer(Log.Logger).Analyze([unit]);
    }
}
