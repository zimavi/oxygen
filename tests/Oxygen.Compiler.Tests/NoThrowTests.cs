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
        var result = Analyze(Failure + "i32 helper() { throw new Failure(); } @nothrow i32 f() { return helper(); } i32 main() { return f(); }");

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
        var result = Analyze(Failure +
            "class Tally { i32 count; public Tally(i32 start) { throw new Failure(); } } " +
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
            Failure + "class Guard { public ~Guard() { throw new Failure(); } } " +
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

    // Without exceptions, no function keeps a trace frame or unwinds, but the entry point still catches a throw from linked code that
    // was built with exceptions, so only main has a personality and an invoke.
    [Fact]
    public void AProgramWithoutExceptionsCatchesOnlyAtItsEntryPoint()
    {
        var model = AnalyzeModel("i32 main() { return 0; }", exceptions: false);

        using var generated = new LlvmModuleGenerator(exceptions: false).Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("call void @__oxygen_runtime_initialize(i64 0)", ir);
        Assert.Single(Regex.Matches(ir, "personality ptr"));
        Assert.Single(Regex.Matches(ir, "invoke "));
        Assert.DoesNotContain("call void @__oxygen_runtime_trace_push(", ir);
        // The entry point reports the object of a throw that reaches it, with a landing pad or with SEH, depending on the target.
        Assert.Single(Regex.Matches(ir, "call void @__oxygen_runtime_unhandled_object\\("));
    }

    [Fact]
    public void AThrowWithoutExceptionsReportsTheObjectInsteadOfUnwinding()
    {
        var model = AnalyzeModel(Failure + "i32 main() { throw new Failure(); }", exceptions: false);

        using var generated = new LlvmModuleGenerator(exceptions: false).Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("call void @__oxygen_runtime_unhandled_instance(", ir);
        // Only the entry point reports an object that unwinds to it, so the throw itself does not unwind.
        Assert.Single(Regex.Matches(ir, "call void @__oxygen_runtime_unhandled_object\\("));
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

    // A generic function may call a @nothrow generic function, and a call to a generic function that may throw is an error.
    [Fact]
    public void ANothrowGenericFunctionMayCallANothrowGenericFunction()
    {
        AnalyzeModel(Failure + "@nothrow T id<T>(T value) { return value; } @nothrow i32 f() { return id(4); } i32 main() { return f(); }");
    }

    [Fact]
    public void ACallToAGenericFunctionThatMayThrowIsAnErrorInANothrowFunction()
    {
        var result = Analyze(Failure + "T pass<T>(T value) { throw new Failure(); } @nothrow i32 f() { return pass(3); } i32 main() { return f(); }");

        Assert.Contains("'pass' may throw, so a @nothrow function cannot call it", Assert.Single(Errors(result)));
    }

    // The body of a @nothrow method of a generic class is checked too.
    [Fact]
    public void AThrowInANothrowMethodOfAGenericClassIsAnError()
    {
        var result = Analyze(Failure + "class Box<T> { public T value; @nothrow public T get() { throw new Failure(); } } " +
            "i32 main() { Box<i32> box = new Box<i32>(); return 0; }");

        Assert.Contains("A @nothrow function cannot throw", Assert.Single(Errors(result)));
    }

    [Fact]
    public void ACompoundAssignmentToAPropertyIsAnErrorInANothrowFunction()
    {
        var result = Analyze(Failure + "class Box { private i32 v; public i32 prop { get { return v; } set { v = value; } } } " +
            "@nothrow i32 f(Box b) { b.prop += 1; return 1; } i32 main() { return f(new Box()); }");

        Assert.Contains("'prop' is a property", Assert.Single(Errors(result)));
    }

    // A dispatched call is checked against the method it names, so an override that may throw is not hidden by a base method that does not.
    [Fact]
    public void ADispatchedCallToAMethodThatMayThrowIsAnErrorInANothrowFunction()
    {
        var result = Analyze(Failure + "class Base { public i32 m() { throw new Failure(); } } " +
            "class Derived : Base { public i32 m() { return 2; } } " +
            "@nothrow i32 f(Base b) { return b.m(); } i32 main() { return f(new Derived()); }");

        Assert.Contains("'m' may throw, so a @nothrow function cannot call it", Assert.Single(Errors(result)));
    }

    // A lambda is a function of its own, so a call in its body is not checked against the @nothrow function that it is written in.
    [Fact]
    public void ACallInsideALambdaIsNotCheckedAgainstTheEnclosingNothrowFunction()
    {
        AnalyzeModel(Failure + "i32 helper() { throw new Failure(); } " +
            "@nothrow i32 f() { var g = fn i32 () { return helper(); }; return 1; } i32 main() { return f(); }");
    }

    // Inference: a body that cannot throw is @nothrow without being declared so, and its IR has no unwinding and no trace frame. A body
    // with a runtime check, a recursive call, or a call that may throw is not inferred.
    [Fact]
    public void AFunctionWithoutThrowsOrRuntimeChecksIsInferredNothrow()
    {
        var model = AnalyzeModel("i32 add(i32 a, i32 b) { return a + b; } i32 main() { return add(1, 2); }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var body = FunctionDefinition(generated.ToString(), "_f3add_p2_");

        Assert.True(model.InferredNoThrow.Contains(model.Functions.Values.Single(function => function.Name == "add").Declaration));
        Assert.DoesNotContain("personality", body);
        Assert.DoesNotContain("trace_push", body);
    }

    [Fact]
    public void AFunctionWithAnIndexIsNotInferredNothrow()
    {
        var model = AnalyzeModel("i32 pick(i32 index) { i32[2] values = {1, 2}; return values[index]; } i32 main() { return pick(1); }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var body = FunctionDefinition(generated.ToString(), "_f4pick_p1_");

        Assert.Contains("personality", body);
        Assert.Contains("trace_push", body);
    }

    // An integer division is checked for a zero divisor, and a floating-point division is not checked at all.
    [Fact]
    public void AnIntegerDivisionIsARuntimeCheckButAFloatingPointDivisionIsNot()
    {
        var model = AnalyzeModel(
            "f64 half(f64 x) { return x / 2.0; } i32 quotient(i32 a, i32 b) { return a / b; } " +
            "i32 main() { return quotient(4, 2) + cast<i32>(half(4.0)); }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.DoesNotContain("personality", FunctionDefinition(ir, "_f4half_p1_"));
        Assert.Contains("personality", FunctionDefinition(ir, "_f8quotient_p2_"));
    }

    // A recursive function calls itself, which is not yet proven nothrow, so the cycle is never inferred.
    [Fact]
    public void ARecursiveFunctionIsNotInferredNothrow()
    {
        var model = AnalyzeModel("i32 countDown(i32 n) { if (n == 0) { return 0; } return countDown(n - 1); } i32 main() { return countDown(2); }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);

        Assert.Contains("personality", FunctionDefinition(generated.ToString(), "_f9countDown_p1_"));
    }

    [Fact]
    public void AFunctionThatCallsAFunctionThatMayThrowIsNotInferredNothrow()
    {
        var model = AnalyzeModel(Failure +
            "i32 fails() { throw new Failure(); } i32 relay() { return fails(); } i32 main() { return relay(); }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);

        Assert.Contains("personality", FunctionDefinition(generated.ToString(), "_f5relay_p0_"));
    }

    // A dispatched call runs the override, which may throw even though the base method cannot, so the caller is not inferred nothrow.
    [Fact]
    public void ADispatchedCallIsNotNothrowFromTheBaseMethodAlone()
    {
        var model = AnalyzeModel(Failure +
            "class Base { virtual i32 value() { return 1; } } " +
            "class Derived : Base { override i32 value() { throw new Failure(); } } " +
            "i32 read(Base b) { return b.value(); } i32 main() { return read(new Derived()); }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);

        Assert.Contains("personality", FunctionDefinition(generated.ToString(), "_f4read_p1_"));
    }

    // The inferred nothrow of a callee is what lets a declared @nothrow function call it, and create objects with its constructor.
    [Fact]
    public void ANothrowFunctionMayCallAnInferredFunction()
    {
        AnalyzeModel("i32 helper() { return 1; } @nothrow i32 f() { return helper(); } i32 main() { return f(); }");
    }

    [Fact]
    public void ANothrowFunctionMayCreateAnObjectWithAnInferredConstructor()
    {
        AnalyzeModel("class Tally { i32 count; public Tally(i32 start) { count = start; } } " +
            "@nothrow i32 make(i32 start) { Tally tally = new Tally(start); return 0; } i32 main() { return make(3); }");
    }

    [Fact]
    public void ANothrowFunctionMayHoldALocalOfAClassWithAnInferredNothrowDestructor()
    {
        AnalyzeModel("class Guard { public ~Guard() { } } @nothrow i32 f() { Guard guard = new Guard(); return 0; } i32 main() { return f(); }");
    }

    // A function with a runtime check is not inferred, so a declared @nothrow function may not call it.
    [Fact]
    public void ACallToAFunctionWithAnIndexIsAnErrorInANothrowFunction()
    {
        var result = Analyze("i32 pick(i32 index) { i32[2] values = {1, 2}; return values[index]; } " +
            "@nothrow i32 f() { return pick(1); } i32 main() { return f(); }");

        Assert.Contains("'pick' may throw", Assert.Single(Errors(result)));
    }

    // A temporary is an object that is not bound to a local. Its destructor runs at the end of its statement, so a destructor that may throw
    // makes the creation of the temporary a may-throw operation. An empty destructor is nothrow, so its temporaries are allowed.
    [Fact]
    public void ACreatedTemporaryWhoseDestructorMayThrowIsAnErrorInANothrowFunction()
    {
        var result = Analyze(Failure +
            "class Guard { public ~Guard() { throw new Failure(); } } " +
            "i32 use(Guard guard) { return 1; } @nothrow i32 f() { return use(new Guard()); } i32 main() { return f(); }");

        Assert.Contains("create a temporary of 'Guard'", Assert.Single(Errors(result)));
    }

    [Fact]
    public void AReturnedTemporaryWhoseDestructorMayThrowMakesItsCallMayThrow()
    {
        var result = Analyze(Failure +
            "class Guard { public ~Guard() { throw new Failure(); } } " +
            "Guard make() { return new Guard(); } @nothrow i32 f() { make(); return 1; } i32 main() { return f(); }");

        Assert.Contains("'make' may throw", Assert.Single(Errors(result)));
    }

    [Fact]
    public void ATemporaryWithAnEmptyDestructorIsAllowedInANothrowFunction()
    {
        AnalyzeModel("class Guard { public ~Guard() { } } i32 use(Guard guard) { return 1; } " +
            "@nothrow i32 f() { return use(new Guard()); } i32 main() { return f(); }");
    }

    // A @throws function promises that it may throw, and the analysis checks the promise: its body must contain something that may throw.
    [Fact]
    public void AThrowsFunctionThatMayThrowIsAllowed()
    {
        var model = AnalyzeModel(Failure + "@throws i32 f() { throw new Failure(); } i32 main() { return f(); }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);

        Assert.Contains("personality", FunctionDefinition(generated.ToString(), "_f1f_p0_"));
    }

    [Fact]
    public void AThrowsFunctionThatCannotThrowIsAnError()
    {
        var result = Analyze("@throws i32 f() { return 1; } i32 main() { return f(); }");

        Assert.Contains("is @throws, but nothing in it can throw", Assert.Single(Errors(result)));
    }

    // A runtime check may throw an exception, so a @throws function with an index has a reason for its annotation.
    [Fact]
    public void AThrowsFunctionWithARuntimeCheckIsAllowed()
    {
        AnalyzeModel("@throws i32 pick(i32 index) { i32[2] values = {1, 2}; return values[index]; } i32 main() { return pick(0); }");
    }

    [Fact]
    public void AFunctionCannotBeBothNothrowAndThrows()
    {
        var result = Analyze("@nothrow @throws i32 f() { return 1; } i32 main() { return f(); }");

        Assert.Contains("cannot be both @nothrow and @throws", Assert.Single(Errors(result)));
    }

    [Fact]
    public void ANothrowFunctionMayNotCallAThrowsFunction()
    {
        var result = Analyze(Failure +
            "@throws i32 f() { throw new Failure(); } @nothrow i32 g() { return f(); } i32 main() { return g(); }");

        Assert.Contains("'f' may throw, so a @nothrow function cannot call it", Assert.Single(Errors(result)));
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
