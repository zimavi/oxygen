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
    [Fact]
    public void RequiresOverrideForVirtualBaseMethod()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Base { virtual i32 value() { return 1; } } " +
            "class Child : Base { i32 value() { return 2; } } i32 main() { return 0; }"));
        Assert.Contains("must be marked 'override'", exception.Message);
    }

    [Fact]
    public void ValidatesExceptionFlowAndCatchScope()
    {
        var model = Analyze(
            "class Exception { } class Failure : Exception { } " +
            "i32 main() { try { throw new Failure(); } catch (Failure error) { return 1; } catch { return 2; } }");

        Assert.Contains(model.Classes.Keys, name => name == "Failure");
    }

    [Fact]
    public void RejectsNonExceptionThrowsAndCatchTypes()
    {
        var thrown = Assert.Throws<SemanticException>(() =>
            Analyze("class Exception { } i32 main() { throw 1; return 0; }"));
        Assert.Contains("Only exception class values can be thrown", thrown.Message);

        var caught = Assert.Throws<SemanticException>(() =>
            Analyze("class Exception { } class NotAnException { } i32 main() { try { return 0; } catch (NotAnException error) { return 1; } }"));
        Assert.Contains("must be an exception class", caught.Message);
    }

    [Fact]
    public void CatchBindingIsScopedToCatchBody()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("class Exception { } i32 main() { try { return 0; } catch (Exception error) { return 1; } return error; }"));
        Assert.Contains("Variable 'error' is not declared", exception.Message);
    }

    [Fact]
    public void RequiresCatchAllToBeLast()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("class Exception { } class Failure : Exception { } i32 main() { try { return 0; } catch { return 1; } catch (Failure error) { return 2; } }"));
        Assert.Contains("catch-all clause must be last", exception.Message);
    }

    [Fact]
    public void AllowsFinallyWithoutCatch()
    {
        var model = Analyze(
            "class Exception { } i32 main() { try { return 1; } finally { return 2; } }");
        Assert.NotNull(model);
    }

    private readonly Tokenizer _tokenizer = new(Log.Logger);

    [Fact]
    public void AnalyzesClassConstructionAndExposesMembers()
    {
        var model = Analyze(
            "class Box { i32 value; Box(i32 initial) { value = initial; } i32 get() { return value; } } i32 main() { Box box = new Box(42); return box.get(); }");

        Assert.Contains("Box", model.Classes.Keys);
        Assert.Equal(SemanticType.Class("Box"), model.ExpressionTypes.Values.First(type => type.IsClass));
        Assert.Contains(model.ResolvedCalls.Values, method => method.Name == "get");
    }

    [Fact]
    public void ValidatesForInFixedArrayProtocol()
    {
        var model = Analyze("i32 main() { i32[2] values = { 1, 2 }; i32 total = 0; for (var value in values) { total = total + value; } return total; }");

        Assert.Contains(model.ExpressionTypes.Values, type => type == SemanticType.FromPrimitive(PrimitiveType.I32));
    }

    [Fact]
    public void RejectsForInNonArrayCollection()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32 value = 1; for (var item in value) { return item; } return 0; }"));

        Assert.Contains("For-in requires a fixed array or iterator-protocol collection", exception.Message);
    }

    [Fact]
    public void ResolvesForInIteratorProtocol()
    {
        var model = Analyze(
            "union Option<T> { Some(T); None; } " +
            "class Iter { i32 index; Option<i32> next() { return Option.None(); } } " +
            "class Values { Iter iter() { return new Iter(); } } " +
            "i32 main() { Values values = new Values(); for (var value in values) { return value; } return 0; }");

        Assert.Single(model.IteratorProtocols);
        var protocol = model.IteratorProtocols.Values.Single();
        Assert.Equal("iter", protocol.Iter.Name);
        Assert.Equal("next", protocol.Next.Name);
        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.I32), protocol.ElementType);
    }

    [Fact]
    public void ResolvesIteratorProtocolForNonIdentifierCollectionExpression()
    {
        var model = Analyze(
            "union Option<T> { Some(T); None; } " +
            "class Iter { Option<i64> next() { return Option.None(); } } " +
            "class Values { Iter iter() { return new Iter(); } } " +
            "Values getValues() { return new Values(); } " +
            "i32 main() { for (var value in getValues()) { return 0; } return 0; }");

        var protocol = Assert.Single(model.IteratorProtocols.Values);
        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.I64), protocol.ElementType);
    }

    [Fact]
    public void TracksClassLocalsThatEscapeTheirLexicalScope()
    {
        var model = Analyze(
            "class Box { } Box take(Box value) { return value; } i32 main() { Box outer = new Box(); { Box inner = new Box(); outer = inner; } take(outer); return 0; }");

        Assert.Contains(model.EscapingClassLocals, declaration => declaration.Name == "inner");
        Assert.Contains(model.EscapingClassLocals, declaration => declaration.Name == "outer");
    }

    [Fact]
    public void TracksClassLocalsReturnedFromAFunction()
    {
        var model = Analyze("class Box { } Box make() { Box value = new Box(); return value; } i32 main() { return 0; }");

        Assert.Contains(model.EscapingClassLocals, declaration => declaration.Name == "value");
    }

    [Fact]
    public void AllowsParametersAndLocalsToShadowClassFieldsWithThisAccess()
    {
        var model = Analyze(
            "class Box { i32 value; Box(i32 value) { this.value = value; } i32 get(i32 value) { i32 local = value; return this.value + local; } } i32 main() { Box box = new Box(41); return box.get(1); }");

        Assert.Contains(model.ExpressionTypes, pair =>
            pair.Key is ThisExpression && pair.Value == SemanticType.Class("Box"));
    }

    [Fact]
    public void RejectsThisOutsideClassInstanceBodies()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze("i32 main() { return this.value; }"));

        Assert.Contains("'this' is only valid inside a class instance body", exception.Message);
    }

    [Fact]
    public void AnalyzesCallsInConstructors()
    {
        var model = new SemanticAnalyzer(Log.Logger).Analyze(
        [
            Parse("import std.memory; class Box { i32* value; Box() { value = memory.alloc<i32>(1); } } i32 main() { Box box = new Box(); return 0; }"),
            Parse("public module std.memory; public T* alloc<T>(u64 count) { return 0; }")
        ]);

        Assert.Contains(model.ResolvedCalls.Values, function =>
            function.Name == "alloc"
            && function.Parameters[0].Type == SemanticType.FromPrimitive(PrimitiveType.U64));
    }

    [Fact]
    public void ValidatesInterfaceContracts()
    {
        var model = Analyze(
            "interface Printable { i32 value(); } class Item : Printable { i32 value() { return 1; } } i32 main() { return 0; }");

        Assert.Single(model.Interfaces["Printable"].Methods);
    }

    [Fact]
    public void RejectsClassesThatDoNotImplementInterfaces()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("interface Printable { i32 value(); } class Item : Printable { } i32 main() { return 0; }"));

        Assert.Contains("does not implement interface method", exception.Message);
    }

    [Fact]
    public void AllowsImplicitUpcastsAndExplicitDowncasts()
    {
        var model = Analyze(
            """
            interface Printable { i32 value(); }
            class Base { }
            class Item : Base, Printable { i32 value() { return 1; } }
            i32 main() {
                Base baseValue = new Item();
                Printable printable = new Item();
                Item fromBase = cast<Item>(baseValue);
                Item fromInterface = cast<Item>(printable);
                return fromBase.value() + fromInterface.value();
            }
            """);

        Assert.Contains(model.ExpressionTypes.Values, type => type == SemanticType.Interface("Printable"));
        Assert.Contains(model.ExpressionTypes.Values, type => type == SemanticType.Class("Item"));
    }

    [Fact]
    public void RejectsImplicitDowncastsAndUnrelatedReferenceCasts()
    {
        var implicitDowncast = Assert.Throws<SemanticException>(() =>
            Analyze("class Base { } class Item : Base { } i32 main() { Base value = new Item(); Item invalid = value; return 0; }"));
        Assert.Contains("Cannot initialize", implicitDowncast.Message);

        var unrelatedCast = Assert.Throws<SemanticException>(() =>
            Analyze("class Left { } class Right { } i32 main() { Left value = new Left(); Right invalid = cast<Right>(value); return 0; }"));
        Assert.Contains("Unsupported cast", unrelatedCast.Message);
    }

    [Fact]
    public void RejectsInvalidClassConstruction()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("class Box { Box(i32 value) { } } i32 main() { Box box = new Box(true); return 0; }"));

        Assert.Contains("No constructor", exception.Message);
    }

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
    public void AnalyzesIntegerSwitchAndAllowsSwitchBreak()
    {
        var model = Analyze(
            "i32 main() { i32 value = 1; switch (value) { case 0: break; case 1: return 42; default: return 0; } return 1; }");

        Assert.Equal("main", model.MainFunction!.Name);
    }

    [Fact]
    public void AnalyzesRuntimeTypePatternsAndBindings()
    {
        var model = Analyze(
            "class Base { } class Child : Base { } i32 main() { Base value = new Child(); " +
            "if (value is Child child) { return 1; } switch (value) { case is Child match: return 2; default: return 0; } }");

        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.Bool),
            model.ExpressionTypes.Values.First(type => type.Primitive == PrimitiveType.Bool));
    }

    [Fact]
    public void RejectsTypePatternsForUnrelatedTypes()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("class Left { } class Right { } i32 main() { Left value = new Left(); return value is Right ? 1 : 0; }"));

        Assert.Contains("are not related", exception.Message);
    }

    [Fact]
    public void RejectsDuplicateValueSwitchCases()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { switch (1) { case 1: return 1; case 1: return 2; default: return 0; } }"));

        Assert.Contains("Duplicate switch case value", exception.Message);
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
    public void SpecializesGenericCallsAndValidatesPointerAndSizeof()
    {
        var model = Analyze(
            "T* identity<T>(T* value) { return value; } i32 main() { i32 value = 1; i32* pointer = identity<i32>(&value); void* opaque = cast<void*>(pointer); i32* typed = cast<i32*>(opaque); u64 bytes = sizeof(typed); return 0; }");

        Assert.Contains(model.Functions.Values, function =>
            !function.IsGeneric && function.Name == "identity"
            && function.Parameters[0].Type == SemanticType.PointerTo(SemanticType.FromPrimitive(PrimitiveType.I32)));
        Assert.Contains(model.ExpressionTypes.Values, type =>
            type == SemanticType.FromPrimitive(PrimitiveType.U64));
    }

    [Fact]
    public void AnalyzesPointerIndexingAsPointeeAccess()
    {
        var model = Analyze(
            "i32 main() { i32 first = 1; i32* pointer = &first; pointer[0] = 2; return pointer[0]; }");

        Assert.Equal(
            SemanticType.FromPrimitive(PrimitiveType.I32),
            model.ExpressionTypes.Values.Last());
    }

    [Fact]
    public void RejectsIndexingOpaquePointers()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { void* pointer = 0; return pointer[0]; }"));

        Assert.Contains("Cannot dereference an opaque void pointer", exception.Message);
    }

    [Fact]
    public void InfersGenericFreeForImportedPointer()
    {
        var model = new SemanticAnalyzer(Log.Logger).Analyze(
        [
            Parse("import std.memory; i32 main() { i32* pointer = memory.alloc<i32>(1); memory.free(pointer); return 0; }"),
            Parse("public module std.memory; public T* alloc<T>(u64 count) { return 0; } public void free<T>(T* pointer) { return; }")
        ]);

        Assert.Contains(model.ResolvedCalls.Values, function =>
            function.Name == "free"
            && function.Parameters[0].Type == SemanticType.PointerTo(SemanticType.FromPrimitive(PrimitiveType.I32)));
    }

    [Fact]
    public void KeepsExplicitGenericSpecializationsWithDifferentReturnTypesSeparate()
    {
        var model = Analyze(
            "T* alloc<T>(u64 count) { return 0; } i32 main() { i32* first = alloc<i32>(1); i64* second = alloc<i64>(1); return 0; }");

        var allocations = model.ResolvedCalls.Values.Where(function => function.Name == "alloc").ToArray();
        Assert.Equal(2, allocations.Length);
        Assert.Contains(allocations, function =>
            function.ReturnType == SemanticType.PointerTo(SemanticType.FromPrimitive(PrimitiveType.I32)));
        Assert.Contains(allocations, function =>
            function.ReturnType == SemanticType.PointerTo(SemanticType.FromPrimitive(PrimitiveType.I64)));
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
    public void AllowsProofBasedConstantNumericConversions()
    {
        var model = Analyze("i32 main() { usize value = 1 + 2; return 0; }");
        Assert.NotNull(model.MainFunction);

        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { u8 value = 256; return 0; }"));
        Assert.Contains("Cannot initialize", exception.Message);
    }

    [Fact]
    public void AllowsExplicitPointerIntegerCasts()
    {
        var model = Analyze("i32 main() { i32 value = 1; i32* pointer = &value; usize address = cast<usize>(pointer); i32* restored = cast<i32*>(address); return 0; }");
        Assert.Contains(model.ExpressionTypes.Values, type => type == SemanticType.FromPrimitive(PrimitiveType.USize));
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
    public void AnalyzesConditionalExpressionsWithCompatibleBranches()
    {
        var model = Analyze("i32 main() { return true ? 1 : 2; }");

        Assert.Contains(model.ExpressionTypes, pair =>
            pair.Key is ConditionalExpression
            && pair.Value == SemanticType.FromPrimitive(PrimitiveType.I32));
    }

    [Fact]
    public void RejectsInvalidConditionalExpressionTypes()
    {
        var conditionError = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { return 1 ? 2 : 3; }"));
        Assert.Contains("conditions must be bool", conditionError.Message);

        var branchError = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { return true ? 1 : false; }"));
        Assert.Contains("compatible types", branchError.Message);
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
    public void AnalyzesIntegerAndPointerIncrementAndCompoundArithmetic()
    {
        var model = Analyze(
            """
            i32 main() {
                i32 value = 1;
                value++;
                ++value;
                value += 2;
                value ^= 1;
                i32* pointer = &value;
                pointer++;
                --pointer;
                pointer -= 1;
                return value;
            }
            """);

        Assert.Contains(model.ExpressionTypes.Values, type => type.IsPointer);
    }

    [Fact]
    public void RejectsBitwiseOperationsOnFloatingPointValues()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { f64 value = 1.0; value += 1.0; value ^= 1.0; return 0; }"));

        Assert.Contains("requires integer operands", exception.Message);
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
