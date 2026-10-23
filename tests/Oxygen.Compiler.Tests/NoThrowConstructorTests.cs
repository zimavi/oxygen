using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Parsing;
using Oxygen.Compiler.Semantic;
using Oxygen.Llvm;
using Serilog;
using Xunit;

namespace Oxygen.Compiler.Tests;

// @nothrow on constructors (docs/exceptions.md): creating an object with a @nothrow constructor is allowed in a @nothrow function, and the
// constructor's own body, initializers and base chain are checked.
public sealed class NoThrowConstructorTests
{
    private const string Failure = "class Failure : Exception { } ";

    private static SemanticResult Analyze(string source)
    {
        var tokenizer = new Tokenizer(Log.Logger);
        var standard = new Parser(tokenizer.Tokenize("module std.exception; public class Exception { }"), Log.Logger).Parse();
        var program = new Parser(tokenizer.Tokenize("import std.exception; " + source), Log.Logger).Parse();
        return new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics([standard, program], ["std.oxy", "main.oxy"]);
    }

    private static IEnumerable<string> Errors(SemanticResult result) =>
        result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.Message);

    [Fact]
    public void ANothrowConstructorCanBeRunByANothrowFunction()
    {
        var result = Analyze(
            "class Tally { i32 count; @nothrow public Tally(i32 start) { count = start; } } " +
            "@nothrow i32 make(i32 start) { Tally tally = new Tally(start); return tally.count; } " +
            "i32 main() { return make(3); }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ACallToAPlainConstructorIsStillAnErrorInANothrowFunction()
    {
        var result = Analyze(Failure +
            "class Tally { i32 count; public Tally(i32 start) { throw new Failure(); } } " +
            "@nothrow i32 make(i32 start) { Tally tally = new Tally(start); return tally.count; } " +
            "i32 main() { return make(3); }");

        Assert.Contains(Errors(result), message => message.Contains("runs a constructor that may throw", StringComparison.Ordinal));
    }

    [Fact]
    public void ANothrowConstructorBodyIsCheckedAsANothrowFunction()
    {
        var result = Analyze(Failure +
            "class Tally { i32 count; @nothrow public Tally(i32 start) { throw new Failure(); } } " +
            "i32 main() { return 0; }");

        Assert.Contains("A @nothrow function cannot throw", Assert.Single(Errors(result)));
    }

    [Fact]
    public void ANothrowConstructorMayNotChainToAnotherConstructor()
    {
        var result = Analyze(
            "class Base { i32 value; public Base(i32 start) { value = start; } } " +
            "class Child : Base { @nothrow public Child() : base(1) { } } " +
            "i32 main() { return 0; }");

        Assert.Contains("cannot chain to another constructor", Assert.Single(Errors(result)));
    }

    [Fact]
    public void ANothrowConstructorMayNotRunABaseConstructor()
    {
        var result = Analyze(Failure +
            "class Base { public Base() { throw new Failure(); } } " +
            "class Child : Base { @nothrow public Child() { } } " +
            "i32 main() { return 0; }");

        Assert.Contains("cannot run the constructor of its base class 'Base'", Assert.Single(Errors(result)));
    }

    [Fact]
    public void AFieldInitializerThatMayThrowIsAnErrorInANothrowConstructor()
    {
        var result = Analyze(Failure +
            "i32 helper() { throw new Failure(); } " +
            "class Tally { i32 count = helper(); @nothrow public Tally() { } } " +
            "i32 main() { return 0; }");

        Assert.Contains("'helper' may throw", Assert.Single(Errors(result)));
    }

    [Fact]
    public void ANothrowConstructorMayBeCreatedInANothrowFunctionWhileItsBaseHasNoConstructor()
    {
        var result = Analyze(
            "class Base { i32 value; } " +
            "class Tally : Base { i32 count; @nothrow public Tally() { count = 1; } } " +
            "@nothrow i32 make() { Tally tally = new Tally(); return tally.count; } " +
            "i32 main() { return make(); }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AConstructorAcceptsNothrowAndNothingElse()
    {
        var tokenizer = new Tokenizer(Log.Logger);
        var parsed = new Parser(tokenizer.Tokenize("class Tally { @noreturn public Tally() { } } i32 main() { return 0; }"), Log.Logger)
            .ParseWithDiagnostics("main.oxy");

        Assert.Contains(parsed.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("'@noreturn' is not supported on constructors", StringComparison.Ordinal));
    }

    // A @nothrow constructor is emitted without a personality or a trace frame, like a @nothrow function.
    [Fact]
    public void ANothrowConstructorHasNoPersonalityAndNoTraceFrame()
    {
        var result = Analyze(
            "class Tally { i32 count; @nothrow public Tally(i32 start) { count = start; } } " +
            "i32 main() { Tally tally = new Tally(3); return 0; }");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        using var generated = new LlvmModuleGenerator().Generate("program", result.Model!);
        var body = Definition(generated.ToString(), "_k_p1_i32");

        Assert.DoesNotContain("personality", body);
        Assert.DoesNotContain("trace_push", body);
        Assert.DoesNotContain("invoke", body);
    }

    private static string Definition(string ir, string nameFragment)
    {
        var match = Regex.Match(ir, @"define [^\n]*" + Regex.Escape(nameFragment) + @"[\s\S]*?\n}");
        Assert.True(match.Success, $"No definition of a function named like '{nameFragment}'.");
        return match.Value;
    }
}
