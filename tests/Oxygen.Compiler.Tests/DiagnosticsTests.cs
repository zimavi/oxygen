using System;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Xunit;

namespace Oxygen.Compiler.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void CollectsMultipleLexicalErrorsWithoutThrowing()
    {
        var result = new Tokenizer().TokenizeWithDiagnostics("i32 # main() { return $; }", "main.oxy");

        Assert.Equal(2, result.Diagnostics.Count);
        Assert.All(result.Diagnostics, diagnostic => Assert.Equal(DiagnosticPhase.Lexing, diagnostic.Phase));
    }

    [Fact]
    public void FormatsSourceContextAndUnderline()
    {
        var diagnostic = new Diagnostic(
            DiagnosticPhase.Semantic,
            DiagnosticSeverity.Error,
            "Unknown name",
            "main.oxy",
            new SourceSpan(new SourceLocation(2, 5), 4));

        var formatted = new DiagnosticFormatter().Format(
            diagnostic,
            new SourceText("main.oxy", "i32 main() {\n    miss;\n}"));

        Assert.Contains("main.oxy:2:5: error: Unknown name", formatted);
        Assert.Contains("2 |     miss;", formatted);
        Assert.Contains("^~~~", formatted);
        Assert.DoesNotContain("\u001b[", formatted);
    }

    [Fact]
    public void AddsAnExplanationAndConcreteFixToCompilerErrors()
    {
        var result = new Tokenizer().TokenizeWithDiagnostics("i32 #", "main.oxy");
        var diagnostic = Assert.Single(result.Diagnostics);
        var formatted = new DiagnosticFormatter().Format(
            diagnostic,
            new SourceText("main.oxy", "i32 #"));

        Assert.Contains("help: This character is not part of Oxygen's lexical grammar", formatted);
        Assert.Contains("fix: Remove the character", formatted);
    }

    [Fact]
    public void ExplainsWhyMixedSignednessOperandsHaveNoCommonType()
    {
        var bag = new DiagnosticBag();
        bag.Add(
            DiagnosticPhase.Semantic,
            "Operands of type 'u64' and 'i32' have no common type. Convert one operand explicitly with cast<T>(x).",
            new SourceLocation(1, 1),
            1,
            "main.oxy");
        var diagnostic = Assert.Single(bag.Diagnostics);

        var formatted = new DiagnosticFormatter().Format(diagnostic, new SourceText("main.oxy", "x"));

        Assert.Contains("help: Implicit conversions only widen losslessly", formatted);
        Assert.Contains("fix: Convert one operand with cast<T>(x)", formatted);
    }

    [Fact]
    public void HighlightsTheEntireSemanticNameWhenColorIsEnabled()
    {
        var diagnostic = new Diagnostic(
            DiagnosticPhase.Semantic,
            DiagnosticSeverity.Error,
            "Cannot assign to const variable 'ret'",
            "main.oxy",
            new SourceSpan(new SourceLocation(2, 3), 3));

        var formatted = new DiagnosticFormatter().Format(
            diagnostic,
            new SourceText("main.oxy", "i32 main() {\n  ret = 0;\n}"),
            useColor: true);

        Assert.Contains("\u001b[31mret\u001b[0m", formatted);
        Assert.Contains("\u001b[90m  2 | \u001b[0m", formatted);
        Assert.Contains("^~~", formatted);
        Assert.DoesNotContain(" | \n", formatted);
    }

    [Fact]
    public void FormatsWarningsWithWarningSeverityAndColor()
    {
        var diagnostic = new Diagnostic(
            DiagnosticPhase.Semantic,
            DiagnosticSeverity.Warning,
            "Unused variable",
            "main.oxy",
            new SourceSpan(new SourceLocation(1, 5), 3));

        var formatted = new DiagnosticFormatter().Format(
            diagnostic,
            new SourceText("main.oxy", "i32 x;"),
            useColor: true);

        Assert.Contains("main.oxy:1:5: warning: Unused variable", formatted);
        Assert.Contains("\u001b[33m", formatted);
    }

    [Fact]
    public void AppliesIdeLikeSyntaxColorsToContextLines()
    {
        var diagnostic = new Diagnostic(
            DiagnosticPhase.Semantic,
            DiagnosticSeverity.Error,
            "Invalid expression",
            "main.oxy",
            new SourceSpan(new SourceLocation(1, 5), 4));

        var formatted = new DiagnosticFormatter().Format(
            diagnostic,
            new SourceText("main.oxy", "i32 main() { return 42; }"),
            useColor: true);

        Assert.Contains("\u001b[36mi32\u001b[0m", formatted);
        Assert.Contains("\u001b[35mreturn\u001b[0m", formatted);
        Assert.Contains("\u001b[33m42\u001b[0m", formatted);
    }

    [Fact]
    public void FormatsCompilerDiagnosticsWithoutSourceContext()
    {
        var diagnostic = new Diagnostic(
            DiagnosticPhase.Backend,
            DiagnosticSeverity.Error,
            "LLVM verification failed",
            null,
            new SourceSpan(new SourceLocation(1, 1), 1));

        var formatted = new DiagnosticFormatter().Format(diagnostic);

        Assert.Contains("compiler: error: LLVM verification failed", formatted);
        Assert.DoesNotContain(" | ", formatted);
    }

    [Fact]
    public void SemanticDiagnosticsDoNotEscapeAsExceptions()
    {
        var tokenizer = new Tokenizer();
        var tokens = tokenizer.TokenizeWithDiagnostics(
            "i32 main() { return missing; }",
            "main.oxy").Tokens;
        var unit = new Parsing.Parser(tokens).ParseWithDiagnostics("main.oxy").Unit;

        var result = new Oxygen.Compiler.Semantic.SemanticAnalyzer()
            .AnalyzeWithDiagnostics([unit], ["main.oxy"]);

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("not declared", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CollectsUnterminatedBlockCommentWithoutThrowing()
    {
        var result = new Tokenizer().TokenizeWithDiagnostics("i32 /*", "main.oxy");

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticPhase.Lexing, diagnostic.Phase);
        Assert.Equal("Unterminated block comment", diagnostic.Message);
    }
}
