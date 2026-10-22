using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Parsing;
using Oxygen.Compiler.Semantic;
using Oxygen.Llvm;
using Serilog;
using Xunit;

namespace Oxygen.Compiler.Tests;

// @noreturn (docs/exceptions.md): the path rules of the analysis, and the attribute and terminator of the emitted IR.
public sealed class NoReturnTests
{
    private const string Failure = "class Failure : Exception { } ";

    // The program is analysed with the standard exception class, as a program is.
    private static SemanticResult Analyze(string source)
    {
        var tokenizer = new Tokenizer(Log.Logger);
        var standard = new Parser(tokenizer.Tokenize("module std.exception; public class Exception { }"), Log.Logger).Parse();
        var program = new Parser(tokenizer.Tokenize("import std.exception; " + source), Log.Logger).Parse();
        return new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics([standard, program], ["std.oxy", "main.oxy"]);
    }

    private static IEnumerable<string> Errors(SemanticResult result) =>
        result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.Message);

    private static IEnumerable<string> Warnings(SemanticResult result) =>
        result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning).Select(diagnostic => diagnostic.Message);

    [Fact]
    public void ANoreturnFunctionCannotReturn()
    {
        var result = Analyze("@noreturn i32 f() { return 1; } i32 main() { return 0; }");

        Assert.Contains("is @noreturn, so it cannot return", Assert.Single(Errors(result)));
    }

    [Fact]
    public void ANoreturnFunctionThatCanEndIsAnError()
    {
        var result = Analyze("@noreturn void f() { } i32 main() { return 0; }");

        Assert.Contains("is @noreturn, but it can return", Assert.Single(Errors(result)));
    }

    [Fact]
    public void AThrowEndsANoreturnFunction()
    {
        var result = Analyze(Failure + "@noreturn i32 f() { throw new Failure(); } i32 main() { return 0; }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ACallToANoreturnFunctionEndsANoreturnFunction()
    {
        var result = Analyze(
            "@extern(\"oxygen_runtime_abort\") @noreturn extern void stop(); " +
            "@noreturn i32 f() { stop(); } i32 main() { return 0; }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void TheStatementsAfterACallToANoreturnFunctionAreUnreachable()
    {
        var result = Analyze(
            "@noreturn void stop_it() { throw new Failure(); } " +
            Failure +
            "i32 main() { stop_it(); return 0; }");

        Assert.Contains(Warnings(result), message => message.Contains("nreachable", StringComparison.Ordinal));
    }

    [Fact]
    public void NoreturnTakesNoArguments()
    {
        var result = Analyze("@noreturn(1) i32 f() { throw new Exception(); } i32 main() { return 0; }");

        Assert.Contains("'@noreturn' does not accept arguments", Assert.Single(Errors(result)));
    }

    [Fact]
    public void NoreturnOnAStructIsAnError()
    {
        var result = Analyze("@noreturn struct Point { i32 x; } i32 main() { return 0; }");

        Assert.Contains(Errors(result), message => message.Contains("'@noreturn'", StringComparison.Ordinal));
    }

    // A @noreturn function is emitted with the noreturn attribute, and a call to it that is a statement ends the block.
    [Fact]
    public void ANoreturnFunctionHasTheNoreturnAttributeAndACallEndsTheBlock()
    {
        var result = Analyze(
            "@extern(\"oxygen_runtime_abort\") @noreturn extern void stop(); " +
            "@noreturn i32 f() { stop(); } i32 main() { return f(); }");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        using var generated = new LlvmModuleGenerator().Generate("program", result.Model!);
        var ir = generated.ToString();
        var body = FunctionDefinition(ir, "_f1f_p0_");

        Assert.Contains("call void @oxygen_runtime_abort()", body);
        Assert.Contains("unreachable", body);
        Assert.Matches(@"attributes #\d+ = \{[^}]*noreturn", ir);
    }

    private static string FunctionDefinition(string ir, string nameFragment)
    {
        var match = Regex.Match(ir, @"define [^\n]*" + Regex.Escape(nameFragment) + @"[\s\S]*?\n}");
        Assert.True(match.Success, $"No definition of a function named like '{nameFragment}'.");
        return match.Value;
    }
}
