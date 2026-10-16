using System.Linq;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
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
    public void AnalyzesEnumMembersAndCheckedCasts()
    {
        var model = Analyze(
            """
            enum Color : i32 { Red, Green = 4, Blue }
            i32 main() {
                Color color = (Color) 4;
                return (i32) color;
            }
            """);

        Assert.Equal(3, model.Enums["Color"].Members.Count);
        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.I32), model.ExpressionTypes.Values.Last());
    }

    [Fact]
    public void AnalyzesTaggedUnionConstructionAndExhaustiveSwitch()
    {
        var model = Analyze(
            "union Result { Ok(i32); Error(i32); Empty; } i32 main() { Result value = Result.Ok(42); switch (value) { case Ok(number): return number; case Error(code): return code; case Empty: return 0; } }");

        Assert.Equal(3, model.Unions["Result"].Cases.Count);
        Assert.Contains(model.ExpressionTypes.Values, type => type == SemanticType.Union("Result"));
    }

    [Fact]
    public void RejectsNonExhaustiveUnionSwitch()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("union Result { Ok(i32); Empty; } i32 main() { Result value = Result.Ok(1); switch (value) { case Ok(number): return number; } }"));

        Assert.Contains("must handle every case", exception.Message);
    }

    [Fact]
    public void RejectsInvalidConstantEnumCast()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("enum Color : i32 { Red, Green } i32 main() { return (i32) (Color) 5; }"));

        Assert.Contains("is not a member of enum 'Color'", exception.Message);
    }

    [Fact]
    public void FindsVoidMainFunction()
    {
        var model = Analyze("void main() { return; }");

        Assert.NotNull(model.MainFunction);
        Assert.True(model.MainFunction!.ReturnType.IsVoid);
    }

    [Fact]
    public void AllowsVoidCallsAsExpressionStatements()
    {
        var model = Analyze(
            """
            void print() { return; }
            i32 main() {
                print();
                return 0;
            }
            """);

        Assert.Contains(model.ResolvedCalls.Values, function => function.Name == "print");
    }

    [Fact]
    public void RejectsVoidCallsWhenAValueIsRequired()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("void print() { return; } i32 main() { return print(); }"));

        Assert.Contains("cannot be used as an expression", exception.Message);
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

    [Fact]
    public void ChecksFixedArrayLiteralLengthAndConstantIndices()
    {
        var model = Analyze("i32 main() { i32[2] values = { 1, 2 }; values[1] = 3; return values[0]; }");

        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.I32), model.ExpressionTypes.Values.Last());

        var lengthError = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32[2] values = { 1 }; return 0; }"));
        Assert.Contains("expects 2 element(s)", lengthError.Message);

        var indexError = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32[2] values = { 1, 2 }; return values[2]; }"));
        Assert.Contains("out of bounds", indexError.Message);
    }

    [Fact]
    public void ReportsUnusedDeclarationsAndUnreachableStatementsAsWarnings()
    {
        var result = AnalyzeWithDiagnostics(
            """
            i32 helper(i32 unused) {
                return 1;
                i32 unreachable = 2;
            }

            i32 main() {
                i32 unusedLocal = 3;
                return helper(0);
            }
            """);

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning
            && diagnostic.Message.Contains("Parameter 'unused' is never used"));
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning
            && diagnostic.Message.Contains("Variable 'unusedLocal' is never used"));
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning
            && diagnostic.Message == "Unreachable statement");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ReportsUnusedNonMainFunctions()
    {
        var result = AnalyzeWithDiagnostics(
            "i32 unused() { return 1; } i32 main() { return 0; }");

        var warning = Assert.Single(result.Diagnostics, diagnostic =>
            diagnostic.Message == "Function 'unused' is never called");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
    }

    [Fact]
    public void DoesNotReportUnusedPublicOrExternalFunctions()
    {
        var result = AnalyzeWithDiagnostics(
            """
            @extern("native_function") public extern void external_function();
            public void api_function() {
                return;
            }
            i32 main() {
                return 0;
            }
            """);

        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Function 'external_function' is never called"));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Function 'api_function' is never called"));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void RequiresBooleanControlFlowConditionsAndValidLoopJumps()
    {
        var model = Analyze(
            "i32 main() { i32 value = 0; for (var i = 0; i < 3; i = i + 1) { if (i == 1) continue; value = value + 1; } while (value < 4) { break; } return value; }");
        Assert.NotNull(model.MainFunction);

        var conditionError = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { while (1) { break; } return 0; }"));
        Assert.Contains("conditions must be bool", conditionError.Message);

        var jumpError = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { break; return 0; }"));
        Assert.Contains("only valid inside a loop", jumpError.Message);
    }

    [Fact]
    public void ResolvesPublicFunctionsThroughImportedNestedModules()
    {
        var main = Parse("import math; i32 main() { return math.add(10, 32) + float.pow(20.4, 2.4); }");
        var library = Parse("public module math; public i32 add(i32 left, i32 right) { return left + right; }");
        var nested = Parse("public module math.float; public i32 pow(f64 value, f64 exponent) { return 1; }");

        var model = new SemanticAnalyzer(Log.Logger).Analyze([main, library, nested]);

        Assert.Contains(model.ResolvedCalls.Values, symbol => symbol.QualifiedName == "math.add");
        Assert.Contains(model.ResolvedCalls.Values, symbol => symbol.QualifiedName == "math.float.pow");
    }

    [Fact]
    public void ResolvesStringMarshalingFunctionsThroughTheStdlibModule()
    {
        var main = Parse(
            """
            import stdlib.strings;
            i32 main() {
                i8* native = strings.to_c_string("hello");
                string value = strings.from_c_string(native);
                return 0;
            }
            """);
        var library = Parse(
            """
            public module stdlib.strings;
            @extern("oxygen_string_to_c_string")
            public extern i8* to_c_string(string value);
            @extern("oxygen_c_string_to_string")
            public extern string from_c_string(i8* value);
            """);

        var model = new SemanticAnalyzer(Log.Logger).Analyze([main, library]);

        Assert.Contains(model.ResolvedCalls.Values, symbol => symbol.QualifiedName == "stdlib.strings.to_c_string");
        Assert.Contains(model.ResolvedCalls.Values, symbol => symbol.QualifiedName == "stdlib.strings.from_c_string");
    }

    [Fact]
    public void ResolvesStandardIoFunctionsThroughTheStdlibModule()
    {
        var main = Parse(
            """
            import stdlib.io;
            i32 main() {
                io.print("hello");
                io.println("world");
                return 0;
            }
            """);
        var library = Parse(
            """
            public module stdlib.io;
            @extern("oxygen_write_stdout")
            public extern void write_stdout(string value);
            @extern("oxygen_write_stderr")
            public extern void write_stderr(string value);
            public void print(string value) {
                write_stdout(value);
            }
            public void println(string value) {
                write_stdout(value);
                write_stdout("\n");
            }
            """);

        var model = new SemanticAnalyzer(Log.Logger).Analyze([main, library]);

        Assert.Contains(model.ResolvedCalls.Values, symbol => symbol.QualifiedName == "stdlib.io.print");
        Assert.Contains(model.ResolvedCalls.Values, symbol => symbol.QualifiedName == "stdlib.io.println");
    }

    [Fact]
    public void ResolvesStandardIoPrimitiveOverloads()
    {
        var main = Parse(
            """
            import stdlib.io;
            i32 main() {
                io.print(42);
                io.println(2.5);
                io.print(true);
                return 0;
            }
            """);
        var library = Parse(
            """
            public module stdlib.io;
            @extern("oxygen_print_i32")
            public extern void print(i32 value);
            @extern("oxygen_println_f64")
            public extern void println(f64 value);
            @extern("oxygen_print_bool")
            public extern void print(bool value);
            """);

        var model = new SemanticAnalyzer(Log.Logger).Analyze([main, library]);

        Assert.Contains(model.ResolvedCalls.Values, symbol =>
            symbol.QualifiedName == "stdlib.io.print" &&
            symbol.Parameters.Single().Type == SemanticType.FromPrimitive(PrimitiveType.I32));
        Assert.Contains(model.ResolvedCalls.Values, symbol =>
            symbol.QualifiedName == "stdlib.io.println" &&
            symbol.Parameters.Single().Type == SemanticType.FromPrimitive(PrimitiveType.F64));
        Assert.Contains(model.ResolvedCalls.Values, symbol =>
            symbol.QualifiedName == "stdlib.io.print" &&
            symbol.Parameters.Single().Type == SemanticType.FromPrimitive(PrimitiveType.Bool));
    }

    [Fact]
    public void FullyQualifiedCallsDoNotRequireAnImport()
    {
        var model = new SemanticAnalyzer(Log.Logger).Analyze(
        [
            Parse("i32 main() { return math.add(10, 32); }"),
            Parse("public module math; public i32 add(i32 left, i32 right) { return left + right; }")
        ]);

        Assert.Equal("math.add", model.ResolvedCalls.Values.Single().QualifiedName);
    }

    [Fact]
    public void RejectsUnqualifiedImportedFunctions()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            new SemanticAnalyzer(Log.Logger).Analyze(
            [
                Parse("import math; i32 main() { return add(10, 32); }"),
                Parse("public module math; public i32 add(i32 left, i32 right) { return left + right; }")
            ]));

        Assert.Contains("Function 'add' is not declared", exception.Message);
    }

    [Fact]
    public void RejectsPrivateFunctionsAcrossModules()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            new SemanticAnalyzer(Log.Logger).Analyze(
            [
                Parse("import foo.bar; i32 main() { return bar.value(); }"),
                Parse("module foo.bar; i32 value() { return 42; }")
            ]));

        Assert.Contains("Function 'bar.value' is not declared", exception.Message);
    }

    [Fact]
    public void AllowsOneModuleToBeSplitAcrossMultipleCompilationUnits()
    {
        var model = new SemanticAnalyzer(Log.Logger).Analyze(
        [
            Parse("i32 main() { return math.add(10, 32); }"),
            Parse("public module math; public i32 add(i32 left, i32 right) { return left + right; }"),
            Parse("public module math; public i32 other() { return 1; }")
        ]);

        Assert.Contains(model.Functions.Values, symbol => symbol.QualifiedName == "math.add");
        Assert.Contains(model.Functions.Values, symbol => symbol.QualifiedName == "math.other");
    }

    [Fact]
    public void ResolvesFunctionsDeclaredByNativeModuleMetadata()
    {
        var model = new SemanticAnalyzer(Log.Logger).Analyze(
            [Parse("import foo.bar; i32 main() { return bar.value(); }")],
            [
                new ExternalModuleDeclaration(
                    "foo.bar",
                    [new ExternalFunctionDeclaration(
                        "value",
                        SemanticType.FromPrimitive(PrimitiveType.I32),
                        [],
                        "_Ox_native_value")])
            ]);

        Assert.Equal("_Ox_native_value", model.ResolvedCalls.Values.Single().NativeName);
        Assert.True(model.ResolvedCalls.Values.Single().IsExternal);
    }

    [Fact]
    public void PreservesCInteropFunctionMetadata()
    {
        var model = Analyze("@extern(\"puts\") @calling_convention(c) i32 main() { return 0; }");

        Assert.Equal(
            new CInteropFunctionMetadata("puts", "c"),
            model.MainFunction!.CInterop);
    }

    [Fact]
    public void ResolvesCallsToExternCFunctions()
    {
        var model = Analyze(
            """
            @extern("puts") extern i32 puts(i32 value);
            i32 main() { return puts(0); }
            """);

        var external = model.ResolvedCalls.Values.Single();
        Assert.True(external.IsExternal);
        Assert.Equal("puts", external.NativeName);
        Assert.Equal("puts", external.CInterop!.ExternalName);
    }

    [Fact]
    public void RejectsLayoutAnnotationsUntilStructsExist()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("@packed i32 main() { return 0; }"));

        Assert.Contains("reserved for struct declarations", exception.Message);
    }

    [Fact]
    public void InfersAndChecksImmutableStringValues()
    {
        var model = Analyze(
            """
            string echo(string value) { return value; }
            i32 main() {
                const string message = "hello";
                return 0;
            }
            """);

        Assert.Equal(SemanticType.String, model.Functions.Values.Single(function => function.Name == "echo").Parameters[0].Type);
    }

    [Fact]
    public void AnalyzesRawPointersNullAndScaledArithmetic()
    {
        var model = Analyze(
            """
            i32 read(i32* value) { return *value; }
            i32 main() {
                i32 value = 41;
                i32* pointer = &value;
                pointer = pointer + 1;
                pointer = 0;
                return 0;
            }

            """);

        Assert.Contains(model.Functions.Values, function => function.Parameters.Any(parameter =>
            parameter.Type.IsPointer && parameter.Type.Pointee == SemanticType.FromPrimitive(PrimitiveType.I32)));
    }

    [Fact]
    public void AnalyzesDataOnlyStructsAndFieldAssignments()
    {
        var model = Analyze(
            """
            struct Point { i32 x; i32 y; }
            struct Line { Point start; Point end; }
            i32 main() {
                Line line = Line {
                    start: Point { x: 1, y: 2 },
                    end: Point { x: 3, y: 4 }
                };
                line.start.x = 10;
                return line.end.y;
            }
            """);

        Assert.Equal(2, model.Structs.Count);
        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.I32), model.ExpressionTypes.Values.Last());
    }

    [Fact]
    public void RejectsIncompleteStructLiteralsAndRecursiveLayouts()
    {
        var incomplete = Assert.Throws<SemanticException>(() =>
            Analyze("struct Point { i32 x; i32 y; } i32 main() { return Point { x: 1 }.x; }"));
        Assert.Contains("must initialize every field", incomplete.Message);

        var recursive = Assert.Throws<SemanticException>(() =>
            Analyze("struct Node { Node next; } i32 main() { return 0; }"));
        Assert.Contains("recursive by-value field", recursive.Message);
    }

    [Fact]
    public void RejectsCompileTimeNullDereference()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { return *(0); }"));

        Assert.Contains("Cannot dereference a null pointer", exception.Message);
    }

    [Fact]
    public void RejectsDereferenceOfPointerKnownToBeNull()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32* ptr = 0; *ptr = 10; return 0; }"));

        Assert.Contains("Cannot dereference a null pointer", exception.Message);
    }

    [Fact]
    public void RejectsAssignmentToConstString()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { const string value = \"hello\"; value = \"world\"; return 0; }"));

        Assert.Contains("Cannot assign to const variable 'value'", exception.Message);
    }

    [Fact]
    public void AllowsStringsInExternalCDeclarationsWithTheDefinedAbi()
    {
        var model = Analyze(
            "@extern(\"oxygen_string_to_c_string\") extern i8* to_c_string(string value); i32 main() { return 0; }");

        Assert.Contains(model.Functions.Values, symbol => symbol.Name == "to_c_string" && symbol.IsExternal);
    }

    [Fact]
    public void WarningsCanBePromotedToBlockingDiagnostics()
    {
        var bag = new DiagnosticBag();
        bag.AddWarning(DiagnosticPhase.Semantic, "unused", new SourceLocation(1, 1));

        Assert.False(bag.HasBlockingDiagnostics());
        Assert.True(bag.HasBlockingDiagnostics(warningsAsErrors: true));
    }

    private SemanticModel Analyze(string source)
    {
        var unit = new Parser(_tokenizer.Tokenize(source), Log.Logger).Parse();
        return new SemanticAnalyzer(Log.Logger).Analyze([unit]);
    }

    private CompilationUnit Parse(string source) =>
        new Parser(_tokenizer.Tokenize(source), Log.Logger).Parse();

    private SemanticResult AnalyzeWithDiagnostics(string source)
    {
        var tokenization = _tokenizer.TokenizeWithDiagnostics(source, "main.oxy");
        var parsed = new Parser(tokenization.Tokens, Log.Logger).ParseWithDiagnostics("main.oxy");
        return new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics([parsed.Unit], ["main.oxy"]);
    }
}
