using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Parsing;
using Oxygen.Compiler.Semantic;
using Oxygen.Llvm;
using Serilog;
using Xunit;

namespace Oxygen.Compiler.Tests;

// @nothrow and the no-exceptions mode (docs/exceptions.md): the rules of the analysis, and the code that the backend emits without exceptions.
public sealed class NoThrowTests
{
    private const string Failure = "class Failure : Exception { } ";

    // The program is analysed with a standard exception class, as a build of a program is, in the given mode.
    private static SemanticResult Analyze(string source, bool exceptions = true)
    {
        var tokenizer = new Tokenizer(Log.Logger);
        var standard = new Parser(tokenizer.Tokenize("module std.exception; public class Exception { }"), Log.Logger).Parse();
        var program = new Parser(tokenizer.Tokenize("import std.exception; " + source), Log.Logger).Parse();
        return new SemanticAnalyzer(Log.Logger) { ExceptionsEnabled = exceptions }
            .AnalyzeWithDiagnostics([standard, program], ["std.oxy", "main.oxy"]);
    }

    private static SemanticModel AnalyzeModel(string source, bool exceptions = true)
    {
        var result = Analyze(source, exceptions);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return result.Model!;
    }

    private static IEnumerable<string> Errors(SemanticResult result) =>
        result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.Message);

    private static IEnumerable<string> Warnings(SemanticResult result) =>
        result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning).Select(diagnostic => diagnostic.Message);

    [Fact]
    public void AThrowInANothrowFunctionIsAnError()
    {
        var result = Analyze(Failure + "@nothrow i32 f() { throw new Failure(); } i32 main() { return f(); }");

        Assert.Contains("A @nothrow function cannot throw", Assert.Single(Errors(result)));
    }

    [Fact]
    public void AThrowIsAllowedInAFunctionWithoutNothrow()
    {
        var result = Analyze(Failure + "i32 f() { throw new Failure(); } i32 main() { return f(); }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ACallToAFunctionThatMayThrowIsAnErrorInANothrowFunction()
    {
        var result = Analyze("i32 helper() { return 1; } @nothrow i32 f() { return helper(); } i32 main() { return f(); }");

        Assert.Contains("'helper' may throw", Assert.Single(Errors(result)));
    }

    [Fact]
    public void ACallToANothrowFunctionAndToACFunctionIsAllowed()
    {
        var result = Analyze(
            "@extern(\"abs\") extern i32 c_abs(i32 value); " +
            "@nothrow i32 helper() { return 1; } " +
            "@nothrow i32 f() { return helper() + c_abs(-1); } " +
            "i32 main() { return f(); }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ACallThroughAFunctionPointerIsAnErrorInANothrowFunction()
    {
        var result = Analyze(
            "@nothrow i32 helper() { return 1; } " +
            "@nothrow i32 f(fn i32 op()) { return op(); } " +
            "i32 main() { return 0; }");

        Assert.Contains("is not a function the compiler can check", Assert.Single(Errors(result)));
    }

    [Fact]
    public void CreatingAnObjectWithAConstructorIsAnErrorInANothrowFunction()
    {
        var result = Analyze(
            "class Tally { i32 count; public Tally(i32 start) { count = start; } } " +
            "@nothrow i32 f() { Tally tally = new Tally(3); return 0; } i32 main() { return f(); }");

        Assert.Contains(Errors(result), message => message.Contains("runs a constructor that may throw", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadingAPropertyIsAnErrorInANothrowFunction()
    {
        var result = Analyze(
            "class Counter { i32 hidden; public i32 Value { get { return hidden; } set { hidden = value; } } } " +
            "@nothrow i32 f(Counter counter) { return counter.Value; } i32 main() { return 0; }");

        Assert.Contains(Errors(result), message => message.Contains("is a property, and reading or writing it", StringComparison.Ordinal));
    }

    [Fact]
    public void ALocalWithADestructorIsAnErrorInANothrowFunction()
    {
        var result = Analyze(
            "class Guard { public ~Guard() { } } " +
            "@nothrow i32 f() { Guard guard = new Guard(); return 0; } i32 main() { return f(); }");

        Assert.Contains(Errors(result), message => message.Contains("its destructor runs at the end of the scope", StringComparison.Ordinal));
    }

    [Fact]
    public void ATryInANothrowFunctionIsAWarning()
    {
        var result = Analyze(Failure +
            "@nothrow i32 f() { try { return 1; } catch (Failure error) { return 2; } } i32 main() { return f(); }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(Warnings(result), message => message.Contains("catches nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void NothrowTakesNoArguments()
    {
        var result = Analyze("@nothrow(1) i32 f() { return 0; } i32 main() { return f(); }");

        Assert.Contains("'@nothrow' does not accept arguments", Assert.Single(Errors(result)));
    }

    [Fact]
    public void NothrowOnAStructIsAnError()
    {
        var result = Analyze("@nothrow struct Point { i32 x; } i32 main() { return 0; }");

        Assert.Contains(Errors(result), message => message.Contains("'@nothrow'", StringComparison.Ordinal));
    }

    [Fact]
    public void AnInterfaceContractThatIsNothrowNeedsANothrowImplementation()
    {
        var result = Analyze(
            "interface Source { @nothrow i32 value(); } " +
            "class Box : Source { public i32 value() { return 1; } } " +
            "i32 main() { return 0; }");

        Assert.Contains("must implement it as @nothrow", Assert.Single(Errors(result)));
    }

    [Fact]
    public void AnOverrideOfANothrowMethodMustBeNothrow()
    {
        var result = Analyze(
            "class Base { @nothrow public virtual i32 value() { return 1; } } " +
            "class Derived : Base { public override i32 value() { return 2; } } " +
            "i32 main() { return 0; }");

        Assert.Contains("overrides a @nothrow method", Assert.Single(Errors(result)));
    }

    [Fact]
    public void AnNothrowMethodCanBeCalledFromANothrowFunctionThroughItsInterface()
    {
        var result = Analyze(
            "interface Source { @nothrow i32 value(); } " +
            "class Box : Source { @nothrow public i32 value() { return 1; } } " +
            "@nothrow i32 read(Source source) { return source.value(); } " +
            "i32 main() { return 0; }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AnAnnotationOnAFieldIsRejectedByTheParser()
    {
        var tokenizer = new Tokenizer(Log.Logger);
        var parsed = new Parser(tokenizer.Tokenize("class Box { @nothrow i32 count; } i32 main() { return 0; }"), Log.Logger)
            .ParseWithDiagnostics("main.oxy");

        Assert.Contains(parsed.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("'@nothrow' is not supported on fields", StringComparison.Ordinal));
    }

    [Fact]
    public void ACatchClauseWithoutExceptionsIsAWarningThatItCanNeverRun()
    {
        var result = Analyze(Failure + "i32 main() { try { return 1; } catch (Failure error) { return 2; } }", exceptions: false);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(Warnings(result), message => message.Contains("never runs", StringComparison.Ordinal));
    }

    [Fact]
    public void AThrowOutsideANothrowFunctionIsAllowedWithoutExceptions()
    {
        var result = Analyze(Failure + "i32 main() { throw new Failure(); }", exceptions: false);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AThrowInANothrowFunctionIsAnErrorWithoutExceptionsToo()
    {
        var result = Analyze(Failure + "@nothrow i32 f() { throw new Failure(); } i32 main() { return f(); }", exceptions: false);

        Assert.Contains("A @nothrow function cannot throw", Assert.Single(Errors(result)));
    }

    [Fact]
    public void AProgramWithoutExceptionsHasNoExceptionMachinery()
    {
        var model = AnalyzeModel("i32 main() { return 0; }", exceptions: false);

        using var generated = new LlvmModuleGenerator(exceptions: false).Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("call void @__oxygen_runtime_initialize(i64 0)", ir);
        Assert.DoesNotContain("personality ptr", ir);
        Assert.DoesNotContain("call void @__oxygen_runtime_trace_push(", ir);
        Assert.DoesNotContain("invoke ", ir);
    }

    [Fact]
    public void AThrowWithoutExceptionsReportsTheObjectInsteadOfUnwinding()
    {
        var model = AnalyzeModel(Failure + "i32 main() { throw new Failure(); }", exceptions: false);

        using var generated = new LlvmModuleGenerator(exceptions: false).Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("call void @__oxygen_runtime_unhandled_instance(", ir);
        Assert.DoesNotContain("call void @__oxygen_runtime_unhandled_object(", ir);
    }

    // A @nothrow function is a plain call to the functions it calls, with no personality, unwind cleanup or trace frame, even when its body has
    // a try statement, since the catch clauses of a @nothrow body can never run.
    [Fact]
    public void ANothrowFunctionIsPlainCallsWithNoUnwindingOrTraceFrame()
    {
        var model = AnalyzeModel(
            Failure +
            "@nothrow i32 helper() { return 1; } " +
            "@nothrow i32 f() { try { return helper(); } catch (Failure error) { return 2; } } " +
            "i32 main() { return f(); }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var body = FunctionDefinition(generated.ToString(), "_f1f_p0_");

        Assert.Contains("call i32 @", body);
        Assert.DoesNotContain("invoke", body);
        Assert.DoesNotContain("personality", body);
        Assert.DoesNotContain("trace_push", body);
        Assert.DoesNotContain("trace_pop", body);
        Assert.DoesNotContain("landingpad", body);
    }

    // The functions that are not @nothrow keep their trace frame and unwinding.
    [Fact]
    public void AFunctionThatMayThrowKeepsItsTraceFrameAndUnwinding()
    {
        var model = AnalyzeModel(
            Failure +
            "i32 helper() { throw new Failure(); } " +
            "i32 f() { try { return helper(); } catch (Failure error) { return 2; } } " +
            "i32 main() { return f(); }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var body = FunctionDefinition(generated.ToString(), "_f1f_p0_");

        Assert.Contains("invoke", body);
        Assert.Contains("personality", body);
        Assert.Contains("trace_push", body);
    }

    private static string FunctionDefinition(string ir, string nameFragment)
    {
        var match = Regex.Match(ir, @"define [^\n]*" + Regex.Escape(nameFragment) + @"[\s\S]*?\n}");
        Assert.True(match.Success, $"No definition of a function named like '{nameFragment}'.");
        return match.Value;
    }

    [Fact]
    public void AProgramWithExceptionsStartsWithTheExceptionsFlag()
    {
        var model = AnalyzeModel("i32 main() { return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);

        Assert.Contains("call void @__oxygen_runtime_initialize(i64 1)", generated.ToString());
    }
}
