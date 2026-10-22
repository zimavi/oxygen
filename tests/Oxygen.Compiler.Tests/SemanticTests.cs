using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
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
        // A thrown class derives from the standard library's Exception, which the program imports.
        var model = AnalyzeWithStdException(
            "class Failure : Exception { } " +
            "i32 main() { try { throw new Failure(); } catch (Failure error) { return 1; } catch { return 2; } }");

        Assert.Contains(model.Classes.Keys, name => name == "program.Failure");
    }

    // The standard library's Option, as the module a program imports. The for-in protocol requires that Option.
    private SemanticModel AnalyzeWithStdOption(string source) =>
        new SemanticAnalyzer(Log.Logger).Analyze([
            Parse("module std.collection; public union Option<T> { Some(T); None; }"),
            Parse("import std.collection; " + source)
        ]);

    // The standard library's Exception, as the module a program imports. A program that throws derives from it.
    private SemanticModel AnalyzeWithStdException(string source) =>
        new SemanticAnalyzer(Log.Logger).Analyze([
            Parse("module std.exception; public class Exception { }"),
            Parse("import std.exception; " + source)
        ]);

    [Fact]
    public void ExceptionConstructorAcceptsMessageAndInitializesTraceInternally()
    {
        var model = Analyze(
            "@extern(\"__oxygen_runtime_exception_trace_count\") public extern u64 runtime_trace_count(Exception exception_object); " +
            "class StackTrace { u64 count; StackTrace(Exception exception_object) { count = program.runtime_trace_count(exception_object); } } " +
            "class Exception { string message; StackTrace stack_trace; Exception(string message) { this.message = message; this.stack_trace = new StackTrace(this); } } " +
            "class Failure : Exception { Failure(string message) : base(message) { } } " +
            "i32 main() { Exception error = new Exception(\"failure\"); Failure derived = new Failure(\"derived\"); return 0; }");

        Assert.Contains(model.Functions.Values, symbol =>
            symbol.IsExternal && symbol.NativeName == "__oxygen_runtime_exception_trace_count");
        Assert.Contains(model.Classes["program.StackTrace"].Constructors,
            constructor => constructor.Parameters.Single().Type.IsClass
                && constructor.Parameters.Single().Type.StructName == "program.Exception");
        Assert.Contains(model.Classes["program.Exception"].Constructors,
            constructor => constructor.Parameters.Count == 1
                && constructor.Parameters[0].Type.Kind == SemanticTypeKind.String);
        Assert.Contains(model.Classes["program.Failure"].Constructors,
            constructor => constructor.Parameters.Count == 1
                && constructor.Parameters[0].Type.Kind == SemanticTypeKind.String);
    }

    [Fact]
    public void StandardExceptionSourceRemainsSemanticallySupported()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "stdlib", "exception.oxy")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "stdlib", "exception.oxy");
        var source = File.ReadAllText(path);
        var standardUnit = new Parser(_tokenizer.Tokenize(source), Log.Logger).Parse();
        // std.exception imports std.strings for its C string conversion, so the strings module is analyzed with it.
        var stringsSource = File.ReadAllText(Path.Combine(directory.FullName, "stdlib", "strings.oxy"));
        var stringsUnit = new Parser(_tokenizer.Tokenize(stringsSource), Log.Logger).Parse();
        var mainUnit = Parse("i32 main() { return 0; }");

        var model = new SemanticAnalyzer(Log.Logger).Analyze([standardUnit, stringsUnit, mainUnit]);

        Assert.Contains("std.exception.Exception", model.Classes.Keys);
        Assert.Contains(model.Functions.Values, symbol =>
            symbol.NativeName == "__oxygen_runtime_exception_trace_count");
        Assert.Contains(model.Classes["std.exception.Exception"].Constructors,
            constructor => constructor.Parameters.Count == 1
                && constructor.Parameters[0].Type.Kind == SemanticTypeKind.String);
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
            AnalyzeWithStdException("i32 main() { try { return 0; } catch (Exception error) { return 1; } return error; }"));
        Assert.Contains("Variable 'error' is not declared", exception.Message);
    }

    [Fact]
    public void RequiresCatchAllToBeLast()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            AnalyzeWithStdException("class Failure : Exception { } i32 main() { try { return 0; } catch { return 1; } catch (Failure error) { return 2; } }"));
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

        Assert.Contains("program.Box", model.Classes.Keys);
        Assert.Equal(SemanticType.Class("program.Box"), model.ExpressionTypes.Values.First(type => type.IsClass));
        Assert.Contains(model.ResolvedCalls.Values, method => method.Name == "get");
    }

    [Fact]
    public void MonomorphizesGenericClassUsesBeforeBackend()
    {
        var model = Analyze(
            "class Box<T> { T value; Box(T value) { this.value = value; } T get() { return value; } } " +
            "i32 main() { Box<i32> box = new Box<i32>(42); return box.get(); }");

        var specialized = Assert.Single(model.Classes.Values, item =>
            item.Name.StartsWith("Box__ox_", StringComparison.Ordinal));
        Assert.False(specialized.IsGeneric);
        Assert.Contains(specialized.Fields, field =>
            field.Type == SemanticType.FromPrimitive(PrimitiveType.I32));
        Assert.Contains(specialized.Methods, method =>
            method.Name == "get" && method.ReturnType == SemanticType.FromPrimitive(PrimitiveType.I32));
        Assert.Contains(model.ResolvedCalls.Values, call =>
            call.Name == "get" && call.ReturnType == SemanticType.FromPrimitive(PrimitiveType.I32));
    }

    [Fact]
    public void MonomorphizesGenericUnionUsesBeforeBackend()
    {
        var model = Analyze(
            "union Option<T> { Some(T); None; } " +
            "i32 main() { Option<i32> value = Option<i32>.Some(42); " +
            "switch (value) { case Some(number): return number; case None: return 0; } }");

        var specialized = Assert.Single(model.Unions, pair =>
            pair.Key.StartsWith("program.Option__ox_", StringComparison.Ordinal)).Value;
        Assert.False(specialized.IsGeneric);
        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.I32), specialized.Cases[0].PayloadType);
    }

    [Fact]
    public void MonomorphizesGenericInterfaceAndImplementingClassUses()
    {
        var model = Analyze(
            "interface Boxed<T> { T get(); } " +
            "class Box<T> : Boxed<T> { T value; Box(T value) { this.value = value; } T get() { return value; } } " +
            "i32 main() { Boxed<i32> box = new Box<i32>(42); return box.get(); }");

        Assert.Contains(model.Interfaces.Keys, name =>
            name.StartsWith("program.Boxed__ox_", StringComparison.Ordinal));
        Assert.Contains(model.Classes.Keys, name =>
            name.StartsWith("program.Box__ox_", StringComparison.Ordinal));
        Assert.Contains(model.ResolvedCalls.Values, call =>
            call.Name == "get" && call.ReturnType == SemanticType.FromPrimitive(PrimitiveType.I32));
    }

    [Fact]
    public void KeepsMultipleGenericClassSpecializationsSeparate()
    {
        var model = Analyze(
            "class Box<T> { T value; Box(T value) { this.value = value; } T get() { return value; } } " +
            "i32 main() { Box<i32> first = new Box<i32>(1); Box<i64> second = new Box<i64>(2); " +
            "return first.get() + cast<i32>(second.get()); }");

        Assert.Equal(2, model.Classes.Values.Count(item =>
            item.Name.StartsWith("Box__ox_", StringComparison.Ordinal)));
        Assert.Contains(model.ResolvedCalls.Values, call =>
            call.Name == "get" && call.ReturnType == SemanticType.FromPrimitive(PrimitiveType.I32));
        Assert.Contains(model.ResolvedCalls.Values, call =>
            call.Name == "get" && call.ReturnType == SemanticType.FromPrimitive(PrimitiveType.I64));
    }

    [Fact]
    public void SpecializesGenericAggregatesUsedAsTypeArguments()
    {
        var model = Analyze(
            "class Cell<T> { T value; Cell(T value) { this.value = value; } T read() { return value; } } " +
            "class Box<T> { T item; Box(T item) { this.item = item; } T get() { return item; } } " +
            "i32 main() { Box<Cell<i32>> small = new Box<Cell<i32>>(new Cell<i32>(40)); " +
            "Box<Cell<i64>> large = new Box<Cell<i64>>(new Cell<i64>(2)); " +
            "Cell<i32> first = small.get(); Cell<i64> second = large.get(); " +
            "return first.read() + cast<i32>(second.read()); }");

        // Each nested argument produces its own specialization, and the outer ones hold the
        // specialized inner class rather than an unspecialized `Cell<...>`.
        Assert.Equal(2, model.Classes.Keys.Count(name => name.StartsWith("program.Box__ox_", StringComparison.Ordinal)));
        var boxes = model.Classes.Values
            .Where(cls => cls.Name.StartsWith("Box__ox_", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            [SemanticType.Class("program.Cell__ox_i32"), SemanticType.Class("program.Cell__ox_i64")],
            boxes.Select(cls => cls.Fields.Single().Type).OrderBy(type => type.ToString()).ToArray());
    }

    [Fact]
    public void KeepsSpecializationNamesDistinctForDifferentArgumentGroupings()
    {
        var model = Analyze(
            "class Pair<A, B> { A first; B second; Pair(A first, B second) { this.first = first; this.second = second; } } " +
            "class One<T> { T value; One(T value) { this.value = value; } } " +
            "i32 main() { " +
            "Pair<One<i32>, i64> split = new Pair<One<i32>, i64>(new One<i32>(1), 2); " +
            "One<Pair<i32, i64>> nested = new One<Pair<i32, i64>>(new Pair<i32, i64>(1, 2)); " +
            "return 0; }");

        // Length-prefixed argument encoding keeps `Pair<One<i32>, i64>` and `One<Pair<i32, i64>>`
        // from mangling to the same specialization name.
        var names = model.AggregateSpecializations.Values.ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("Class:program.Pair<program.One<i32>, i64>", model.AggregateSpecializations.Keys);
        Assert.Contains("Class:program.One<program.Pair<i32, i64>>", model.AggregateSpecializations.Keys);
    }

    [Fact]
    public void RequiresEveryInterfaceOfAMultipleConstraint()
    {
        var declarations = "interface A { void a(); } interface B { void b(); } " +
            "class Both : A, B { void a() { } void b() { } } class OnlyA : A { void a() { } } " +
            "void both<T : A & B>(T value) { } ";

        var model = new SemanticAnalyzer(Log.Logger).Analyze(
        [
            Parse(declarations + "i32 main() { both(new Both()); return 0; }")
        ]);
        Assert.NotNull(model.MainFunction);

        var exception = Assert.Throws<SemanticException>(() => new SemanticAnalyzer(Log.Logger).Analyze(
        [
            Parse(declarations + "i32 main() { both(new OnlyA()); return 0; }")
        ]));
        Assert.Contains("does not satisfy the constraint 'B'", exception.Message);
    }

    [Fact]
    public void ChecksTypeArgumentsAgainstTheirConstraints()
    {
        var printable = "module std.io; public interface Printable { void write(); } " +
            "public void show<T : Printable>(T value) { }";

        var model = new SemanticAnalyzer(Log.Logger).Analyze(
        [
            Parse(printable),
            Parse("import std.io; class Point : Printable { void write() { } } " +
                "i32 main() { show(1); show(\"text\"); show(true); show(new Point()); return 0; }")
        ]);
        Assert.NotNull(model.MainFunction);

        var exception = Assert.Throws<SemanticException>(() => new SemanticAnalyzer(Log.Logger).Analyze(
        [
            Parse(printable),
            Parse("import std.io; class Plain { } i32 main() { show(new Plain()); return 0; }")
        ]));
        Assert.Contains("does not satisfy the constraint 'Printable'", exception.Message);
    }

    [Fact]
    public void VoidDoesNotSatisfyAConstraintThatPrimitivesDo()
    {
        var printable = "module std.io; public interface Printable { void write(); } " +
            "public void show<T : Printable>() { }";

        var exception = Assert.Throws<SemanticException>(() => new SemanticAnalyzer(Log.Logger).Analyze(
        [
            Parse(printable),
            Parse("import std.io; i32 main() { show<void>(); return 0; }")
        ]));
        Assert.Contains("Type 'void' does not satisfy the constraint 'Printable'", exception.Message);
    }

    [Fact]
    public void KeepsPrimitiveWriteFunctionsOutOfOrdinaryCalls()
    {
        var exception = Assert.Throws<SemanticException>(() => new SemanticAnalyzer(Log.Logger).Analyze(
        [
            Parse("module std.io; void write(i32 value) { }"),
            Parse("import std.io; i32 main() { io.write(1); return 0; }")
        ]));

        Assert.Contains("Function 'io.write' is not declared", exception.Message);
    }

    [Fact]
    public void RejectsConstraintsOnUndeclaredInterfaces()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "void show<T : Missing>(T value) { } i32 main() { show(1); return 0; }"));
        Assert.Contains("Interface 'Missing' is not declared", exception.Message);
    }

    [Fact]
    public void ResolvesMethodCallsOnPrimitiveReceiversByExactType()
    {
        var model = Analyze(
            "void write(i64 value) { } void write(i32 value) { } " +
            "i32 main() { i32 value = 1; value.write(); return 0; }");

        var call = Assert.Single(model.ReceiverArgumentCalls);
        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.I32),
            Assert.Single(model.ResolvedCalls[call].Parameters).Type);
    }

    [Fact]
    public void ReusesAnInferredSpecializationAcrossCallsAndIgnoresOthers()
    {
        var model = Analyze(
            "T identity<T>(T value) { return value; } " +
            "i32 main() { i32 a = identity(1); i32 b = identity(2); i64 c = identity<i64>(3); " +
            "f64 d = identity(2.5); return a + b + cast<i32>(c) + cast<i32>(d); }");

        Assert.Equal(3, model.Modules["program"].Count(function => function.IsSpecialized && function.Name == "identity"));
    }

    [Fact]
    public void KeepsIntegerLiteralsInTheirOwnRange()
    {
        Analyze("i32 main() { i64 big = 9223372036854775807; u64 max = 18446744073709551615; return 0; }");

        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "i32 main() { u64 value = 18446744073709551616; return 0; }"));
        Assert.Contains("does not fit any supported integer type", exception.Message);
    }

    [Fact]
    public void AcceptsBooleanEqualityButNotBooleanOrdering()
    {
        Analyze("i32 main() { bool done = false; if (done == false) { return 1; } if (done != true) { return 2; } return 0; }");

        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { bool done = false; if (done < true) { return 1; } return 0; }"));
        Assert.Contains("requires numeric operands", exception.Message);
    }

    [Fact]
    public void InstantiatesGenericClassesThatOnlyAppearInsideGenericBodies()
    {
        var model = Analyze(
            "class Cell<T> { T value; Cell(T value) { this.value = value; } T read() { return value; } } " +
            "class Holder<T> { T value; Holder(T value) { this.value = value; } " +
            "T roundTrip() { var cell = Cell(value); return cell.read(); } } " +
            "i32 main() { Holder<i32> holder = new Holder<i32>(42); return holder.roundTrip(); }");

        Assert.Contains("program.Cell__ox_i32", model.Classes.Keys);
        var roundTrip = model.Classes["program.Holder__ox_i32"].Methods.Single(method => method.Name == "roundTrip");
        var read = Assert.Single(model.ResolvedCalls, pair =>
            ReferenceEquals(pair.Key, FindCall(roundTrip.Declaration.Body, "cell.read"))).Value;
        Assert.Equal("program.Cell__ox_i32", read.ModuleName);
        Assert.Equal(SemanticType.Class("program.Cell__ox_i32"),
            model.CallReceiverTypes[FindCall(roundTrip.Declaration.Body, "cell.read")]);
    }

    [Fact]
    public void SpecializesGenericFunctionCallsInsideGenericClassInstantiations()
    {
        var model = new SemanticAnalyzer(Log.Logger).Analyze(
        [
            Parse("module util; public T first<T>(T value) { return value; }"),
            Parse("import util; " +
                "class Box<T> { T value; Box(T value) { this.value = util.first<T>(value); } T get() { return value; } } " +
                "i32 main() { Box<i64> box = new Box<i64>(5); return cast<i32>(box.get()); }")
        ]);

        // Checking the generic constructor sees `first<T>` with T unresolved; that open
        // specialization must not become a function of its own.
        var specializations = model.Modules["util"].Where(function => function.Name == "first").ToArray();
        var concrete = Assert.Single(specializations, function => function.IsSpecialized);
        Assert.Equal([SemanticType.FromPrimitive(PrimitiveType.I64)], concrete.SpecializationArguments);
        var constructor = model.Classes["program.Box__ox_i64"].Constructors.Single();
        Assert.Same(concrete, model.ResolvedCalls[FindCall(constructor.Declaration.Body, "util.first")]);
    }

    [Fact]
    public void KeepsExpressionTypesOfEachSpecializationSeparate()
    {
        var model = Analyze(
            "T identity<T>(T value) { return value; } " +
            "i32 main() { i32 small = identity<i32>(40); i64 large = identity<i64>(2); " +
            "return small + cast<i32>(large); }");

        var specializations = model.Modules["program"]
            .Where(function => function.Name == "identity" && function.IsSpecialized).ToArray();
        Assert.Equal(2, specializations.Length);
        foreach (var specialization in specializations)
        {
            var returned = ((ReturnStatement)specialization.Declaration.Body.Statements.Single()).Value!;
            Assert.Equal(specialization.ReturnType, model.ExpressionTypes[returned]);
        }
    }

    [Fact]
    public void ReportsOperatorsNoTypeArgumentSupportsAtTheGenericDeclaration()
    {
        var result = new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics(
            [
                Parse("module util; public T negate<T>(T value) { return -value; }"),
                Parse("import util; " +
                    "class Wrapper<T> { T value; Wrapper(T value) { this.value = util.negate<T>(value); } } " +
                    "i32 main() { Wrapper<bool> wrapper = new Wrapper<bool>(true); return 0; }")
            ],
            ["util.oxy", "main.oxy"]);

        // A generic body is checked once, with its type parameter opaque, so `-value` is rejected in util.oxy
        // at the declaration, before any instantiation.
        var error = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("requires a numeric operand", error.Message);
        Assert.Equal("util.oxy", error.SourceName);
    }

    [Fact]
    public void EnforcesReadOnlyAndConstructorOnlyPropertyWrites()
    {
        Analyze(
            "class Item { public i32 Value { get; init; } Item(i32 value) { this.Value = value; } } " +
            "i32 main() { Item item = new Item(1); return item.Value; }");

        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Item { public i32 Value { get; init; } Item(i32 value) { this.Value = value; } } " +
            "i32 main() { Item item = new Item(1); item.Value = 2; return 0; }"));
        Assert.Contains("only be assigned in a constructor", exception.Message);

        exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Item { public i32 Value { get; } } " +
            "i32 main() { Item item = new Item(); item.Value = 2; return 0; }"));
        Assert.Contains("read-only", exception.Message);
    }

    [Fact]
    public void EnforcesAccessorSpecificVisibility()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Item { public i32 Value { get; private set; } Item(i32 value) { this.Value = value; } } " +
            "i32 main() { Item item = new Item(1); item.Value = 2; return 0; }"));
        Assert.Contains("not accessible", exception.Message);

        Analyze(
            "class Base { public i32 Value { get; protected set; } } " +
            "class Child : Base { Child() { this.Value = 1; } } " +
            "i32 main() { Child child = new Child(); return child.Value; }");
    }

    [Fact]
    public void RequiresOverrideForVirtualBaseProperty()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Base { virtual i32 Value { get; } } " +
            "class Child : Base { i32 Value { get; } } i32 main() { return 0; }"));
        Assert.Contains("must be marked 'override'", exception.Message);

        Analyze(
            "class Base { virtual i32 Value { get; } } " +
            "class Child : Base { override i32 Value { get; } } i32 main() { return 0; }");
    }

    [Fact]
    public void PreservesValueClassMetadataAndAnalyzesDirectConstruction()
    {
        var model = Analyze("@value class Point { } i32 main() { Point point = Point(); return 0; }");

        Assert.True(model.Classes["program.Point"].IsValue);
        Assert.Contains(model.ExpressionTypes, pair =>
            pair.Key is ClassConstructionExpression && pair.Value == SemanticType.Class("program.Point"));
    }

    [Fact]
    public void RejectsInvalidValueClassAnnotations()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("@value(1) class Point { } i32 main() { return 0; }"));

        Assert.Contains("does not accept arguments", exception.Message);
    }

    [Fact]
    public void RejectsValueClassInheritanceAndVirtualMethods()
    {
        var inheritance = Assert.Throws<SemanticException>(() =>
            Analyze("@value class Point : Base { } class Base { } i32 main() { return 0; }"));
        Assert.Contains("cannot inherit", inheritance.Message);

        var virtualMethod = Assert.Throws<SemanticException>(() =>
            Analyze("@value class Point { virtual i32 value() { return 1; } } i32 main() { return 0; }"));
        Assert.Contains("virtual", virtualMethod.Message);
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
    public void ManualIterCallIsRejectedUntilCallResultsAreOwned()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Iter { } class Values { Iter iter() { return new Iter(); } } " +
            "i32 main() { Values v = new Values(); var it = v.iter(); return 0; }"));

        Assert.Contains("Calling iter() by hand is not supported yet", exception.Message);
    }

    [Fact]
    public void ResolvesForInIteratorProtocol()
    {
        var model = AnalyzeWithStdOption(
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
        var model = AnalyzeWithStdOption(
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
            pair.Key is ThisExpression && pair.Value == SemanticType.Class("program.Box"));
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
            Parse("public module std.memory; public T* alloc<T>(u64 count) { return null; }")
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

        Assert.Single(model.Interfaces["program.Printable"].Methods);
    }

    [Fact]
    public void ValidatesGenericInterfaceContracts()
    {
        var model = Analyze(
            "interface Iterator<T> { T next(T value); } class Item<T> : Iterator<T> { T next(T value) { return value; } } i32 main() { return 0; }");

        Assert.Single(model.Interfaces["program.Iterator"].Methods);
    }

    [Fact]
    public void ValidatesGenericInterfaceContractsWithGenericReturnTypes()
    {
        var model = Analyze(
            "union Option<T> { Some(T); None; } interface Iterator<T> { Option<T> next(); } class Item<T> : Iterator<T> { Option<T> next() { return Option<T>.None; } } i32 main() { return 0; }");

        Assert.Single(model.Interfaces["program.Iterator"].Methods);
    }

    [Fact]
    public void SpecializesGenericInterfaceContractsForConcreteArguments()
    {
        var model = Analyze(
            "interface Producer<T> { T value(); } class Item : Producer<i32> { i32 value() { return 1; } } i32 main() { return 0; }");

        Assert.Single(model.Interfaces["program.Producer"].Methods);
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

        Assert.Contains(model.ExpressionTypes.Values, type => type == SemanticType.Interface("program.Printable"));
        Assert.Contains(model.ExpressionTypes.Values, type => type == SemanticType.Class("program.Item"));
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
                Color color = cast<Color>(4);
                return cast<i32>(color);
            }
            """);

        Assert.Equal(3, model.Enums["program.Color"].Members.Count);
        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.I32), model.ExpressionTypes.Values.Last());
    }

    [Fact]
    public void AnalyzesTaggedUnionConstructionAndExhaustiveSwitch()
    {
        var model = Analyze(
            "union Result { Ok(i32); Error(i32); Empty; } i32 main() { Result value = Result.Ok(42); switch (value) { case Ok(number): return number; case Error(code): return code; case Empty: return 0; } }");

        Assert.Equal(3, model.Unions["program.Result"].Cases.Count);
        Assert.Contains(model.ExpressionTypes.Values, type => type == SemanticType.Union("program.Result"));
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
            Analyze("enum Color : i32 { Red, Green } i32 main() { return cast<i32>(cast<Color>(5)); }"));

        Assert.Contains("is not a member of enum 'Color'", exception.Message);
    }

    [Fact]
    public void EnumMembersHoldTheFullRangeOfTheirUnderlyingType()
    {
        var model = Analyze(
            "enum Big : u64 { Top = 18446744073709551615, Zero = 0 } i32 main() { return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void RejectsEnumMemberOutsideItsUnderlyingType()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("enum Small : u8 { A = 255, B } i32 main() { return 0; }"));

        Assert.Contains("Enum member value 256 does not fit underlying type U8", exception.Message);
    }

    [Fact]
    public void RejectsNegativeEnumMemberForUnsignedUnderlyingType()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("enum Count : u32 { A = -1 } i32 main() { return 0; }"));

        Assert.Contains("Enum member value -1 does not fit underlying type U32", exception.Message);
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
            Analyze("i32 main() { void* pointer = null; return pointer[0]; }"));

        Assert.Contains("Cannot dereference an opaque void pointer", exception.Message);
    }

    [Fact]
    public void InfersGenericFreeForImportedPointer()
    {
        var model = new SemanticAnalyzer(Log.Logger).Analyze(
        [
            Parse("import std.memory; i32 main() { i32* pointer = memory.alloc<i32>(1); memory.free(pointer); return 0; }"),
            Parse("public module std.memory; public T* alloc<T>(u64 count) { return null; } public void free<T>(T* pointer) { return; }")
        ]);

        Assert.Contains(model.ResolvedCalls.Values, function =>
            function.Name == "free"
            && function.Parameters[0].Type == SemanticType.PointerTo(SemanticType.FromPrimitive(PrimitiveType.I32)));
    }

    [Fact]
    public void KeepsExplicitGenericSpecializationsWithDifferentReturnTypesSeparate()
    {
        var model = Analyze(
            "T* alloc<T>(u64 count) { return null; } i32 main() { i32* first = alloc<i32>(1); i64* second = alloc<i64>(1); return 0; }");

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
    public void RejectsReturnOnlyOverloads()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "i32 value(i32 input) { return input; } f64 value(i32 input) { return 1.0; } i32 main() { return 0; }"));

        Assert.Contains("same parameter types", exception.Message);
    }

    [Fact]
    public void ExactMatchBeatsAWideningOverloadWhicheverArgumentIsGiven()
    {
        var model = Analyze(
            "i64 pick(i64 value) { return value + 100; } i32 pick(i32 value) { return value; } " +
            "i32 main() { i32 small = pick(5); return small; }");

        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.I32), model.ResolvedCalls.Values.Single().Parameters[0].Type);
    }

    [Fact]
    public void WideningIsChosenWhenNoOverloadMatchesExactly()
    {
        var model = Analyze(
            "i32 pick(i32 value) { return value; } i64 pick(i64 value) { return value + 100; } " +
            "i32 main() { i64 large = pick(cast<i64>(5)); return 0; }");

        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.I64), model.ResolvedCalls.Values.Single().Parameters[0].Type);
    }

    [Fact]
    public void ExactMatchBeatsWideningForMethodCalls()
    {
        var model = Analyze(
            "class Box { i32 pick(i32 value) { return value; } i64 pick(i64 value) { return value; } } " +
            "i32 main() { Box box = new Box(); i32 result = box.pick(5); return result; }");

        var call = model.ResolvedCalls.Values.Single(function => function.Name == "pick");
        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.I32), call.Parameters[0].Type);
    }

    [Fact]
    public void IntegerConstantReachesF32OnlyWhenF32HoldsItsExactValue()
    {
        var accepted = Analyze(
            "f32 take(f32 value) { return value; } i32 main() { f32 result = take(5); return 0; }");
        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.F32), accepted.ResolvedCalls.Values.Single().Parameters[0].Type);

        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "f32 take(f32 value) { return value; } i32 main() { f32 result = take(16777217); return 0; }"));
        Assert.Contains("expected f32", exception.Message);
    }

    [Fact]
    public void IntegerConvertsToFloatOnlyWhenTheFloatHoldsEveryValue()
    {
        var model = Analyze(
            "f64 pick(f64 value) { return value; } i32 main() { f64 result = pick(7); return 0; }");

        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.F64),
            model.ResolvedCalls.Values.Single().Parameters[0].Type);
    }

    [Fact]
    public void RejectsAnI64ArgumentForAnF64ParameterBecauseF64CannotHoldEveryValue()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "f64 take(f64 value) { return value; } i32 main() { i64 big = 1; take(big); return 0; }"));

        Assert.Contains("expected f64", exception.Message);
    }

    [Fact]
    public void SameKindWideningBeatsIntegerToFloatWidening()
    {
        var model = Analyze(
            "i64 pick(i64 value) { return value; } f64 pick(f64 value) { return 1.0; } " +
            "i32 main() { i64 result = pick(7); return 0; }");

        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.I64),
            model.ResolvedCalls.Values.Single().Parameters[0].Type);
    }

    [Fact]
    public void RejectsSignedToUnsignedWidening()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "u16 widen(u16 value) { return value; } i32 main() { i8 small = 1; widen(small); return 0; }"));

        Assert.Contains("expected u16", exception.Message);
    }

    [Fact]
    public void ClosestClassInTheHierarchyWins()
    {
        var model = Analyze(
            "interface B { i32 name(); } class A : B { i32 name() { return 1; } } class C : A { } " +
            "i32 f(A value) { return 1; } i32 f(B value) { return 2; } i32 main() { C item = new C(); return f(item); }");

        var call = model.ResolvedCalls.Values.Single(function => function.Name == "f");
        Assert.Equal("program.A", call.Parameters[0].Type.StructName);
    }

    [Fact]
    public void AcceptsASwitchWhoseCasesAllReturnThroughFallThrough()
    {
        // Analysis succeeds only when every path returns, so the absence of an exception is the assertion.
        var model = Analyze(
            "i32 pick(i32 x) { i32 y = 0; switch (x) { case 1: y = 1; case 2: return 2; default: return 3; } } i32 main() { return pick(1); }");

        Assert.NotNull(model);
    }

    [Fact]
    public void RejectsAnAmbiguousInterfaceAndBaseClassOverload()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "interface B { i32 name(); } class A { i32 name() { return 1; } } class C : A, B { i32 name() { return 2; } } " +
            "i32 f(A value) { return 1; } i32 f(B value) { return 2; } i32 main() { C item = new C(); return f(item); }"));

        Assert.Contains("is ambiguous: no overload is closer than the others", exception.Message);
    }

    [Fact]
    public void InfersGenericReturnTypeFromExpectedType()
    {
        var model = Analyze(
            "union Option<T> { Some(T); None; } Option<T> none<T>() { return Option<T>.None; } " +
            "i32 main() { Option<i32> result = none(); return 0; }");

        // The call is specialized to the expected Option<i32>; a wrong binding would have failed the assignment.
        Assert.Contains(model.ResolvedCalls.Values, function =>
            function.Name == "none" && function.IsSpecialized && function.ReturnType.IsUnion);
    }

    [Fact]
    public void GenericBodyCallsMethodsAndPropertiesOfItsConstraint()
    {
        var model = Analyze(
            """
            interface Shape { i32 Area(); i32 Sides { get; } }
            class Square : Shape {
                public i32 Sides { get; }
                public i32 Area() { return 4; }
            }
            i32 total<T : Shape>(T value) {
                Shape shape = value;
                return value.Area() + value.Sides + shape.Area();
            }
            i32 main() { return total(new Square()); }
            """);

        Assert.NotNull(model);
    }

    [Fact]
    public void RejectsCallsThatTheConstraintDoesNotDeclareEvenWithoutInstantiation()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "interface Shape { i32 Area(); } i32 total<T : Shape>(T value) { return value.Nope(); } i32 main() { return 0; }"));

        Assert.Contains("accepts a receiver of type T", exception.Message);
    }

    [Fact]
    public void RejectsConstraintNamingAnUndeclaredInterface()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "i32 total<T : Missing>(T value) { return 0; } i32 main() { return 0; }"));

        Assert.Contains("Interface 'Missing' is not declared", exception.Message);
    }

    [Fact]
    public void GenericBodyUsesMembersOfEveryBoundOfATypeParameter()
    {
        var model = Analyze(
            "interface A { i32 a(); } interface B { i32 b(); } " +
            "class C : A, B { public i32 a() { return 1; } public i32 b() { return 2; } } " +
            "i32 f<T : A & B>(T value) { return value.a() + value.b(); } i32 main() { return f(new C()); }");

        Assert.NotNull(model);
    }

    [Fact]
    public void RejectsAMemberDeclaredByTwoBoundsOfOneTypeParameter()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "interface A { i32 same(); } interface B { i32 same(); } " +
            "i32 f<T : A & B>(T value) { return value.same(); } i32 main() { return 0; }"));

        Assert.Contains("declared by more than one constraint", exception.Message);
    }

    [Fact]
    public void GenericClassMethodUsesItsConstraint()
    {
        var model = Analyze(
            "interface Shape { i32 Area(); } " +
            "class Square : Shape { public i32 Area() { return 4; } } " +
            "class Box<T : Shape> { T item; Box(T item) { this.item = item; } i32 Twice() { return item.Area() * 2; } } " +
            "i32 main() { Box<Square> box = new Box<Square>(new Square()); return box.Twice(); }");

        Assert.NotNull(model);
    }

    [Fact]
    public void CompoundDivisionByConstantZeroIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32 x = 1; x /= 0; return x; }"));

        Assert.Contains("Division by zero", exception.Message);
    }

    [Fact]
    public void CompoundShiftByConstantOutOfRangeIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32 x = 1; x <<= 40; return x; }"));

        Assert.Contains("Shift count 40 is out of range for i32", exception.Message);
    }

    [Fact]
    public void CompoundShiftWithinRangeIsAccepted()
    {
        var model = Analyze("i32 main() { i32 x = 1; x <<= 3; return x; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void StructurallyEqualGenericTypesAreEqualWhenBuiltSeparately()
    {
        // The type arguments are lists, so a record's default equality would compare them by reference.
        var first = SemanticType.Class("program.Box", [SemanticType.FromPrimitive(PrimitiveType.I32)]);
        var second = SemanticType.Class("program.Box", [SemanticType.FromPrimitive(PrimitiveType.I32)]);

        Assert.True(first == second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void GenericOverloadsTiedOnTheirArgumentsAreAmbiguousAndTheExpectedTypeDoesNotChooseBetweenThem()
    {
        // Both fit the arguments equally well. Only their return types differ, and the return type never selects an overload.
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "i32 f<T>(T x, i32 y) { return 1; } i64 f<T>(i32 x, T y) { return 2; } " +
            "i32 main() { i32 r = f(1, 2); return r; }"));

        Assert.Contains("ambiguous", exception.Message);
    }

    [Fact]
    public void RejectsASwitchCaseThatBreaksBeforeALaterCaseReturns()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "i32 pick(i32 x) { switch (x) { case 1: break; case 2: return 2; default: return 3; } } i32 main() { return 0; }"));

        Assert.Contains("must return a value", exception.Message);
    }

    [Fact]
    public void RejectsPrivateFieldAccessFromOutsideTheClass()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Box { private i32 secret = 1; } i32 main() { Box b = new Box(); return b.secret; }"));

        Assert.Contains("'secret' of class 'Box' is private", exception.Message);
    }

    [Fact]
    public void AllowsProtectedFieldAccessFromADerivedClass()
    {
        var model = Analyze(
            "class Base { protected i32 level = 1; } class Child : Base { i32 read() { return this.level; } } " +
            "i32 main() { return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void RejectsProtectedFieldAccessFromOutsideTheHierarchy()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Base { protected i32 level = 1; } class Child : Base { } " +
            "i32 main() { Child c = new Child(); return c.level; }"));

        Assert.Contains("'level' of class 'Child' is protected", exception.Message);
    }

    [Fact]
    public void MembersWithoutAVisibilityKeywordArePublic()
    {
        var model = Analyze(
            "class Box { i32 value = 3; i32 get() { return value; } } i32 main() { Box b = new Box(); return b.get() + b.value; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void MethodCallsAFunctionOfItsModule()
    {
        var model = Analyze("i32 helper(i32 x) { return x * 2; } class Plain { i32 method() { return helper(1); } } " +
            "i32 main() { return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void ConstructorsOfPlainAndGenericClassesCallAFunctionOfTheirModule()
    {
        var model = Analyze("i32 helper(i32 x) { return x * 2; } " +
            "class Plain { i32 value; Plain(i32 item) { value = helper(item); } } " +
            "class Box<T> { T value; Box(T item) { value = item; i32 unused = helper(1); } } " +
            "i32 main() { Plain p = new Plain(1); Box<i32> b = new Box<i32>(2); return p.value; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void DestructorCallsAFunctionOfItsModule()
    {
        var model = Analyze("i32 helper(i32 x) { return x * 2; } class Plain { ~Plain() { i32 unused = helper(1); } } " +
            "i32 main() { return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void RejectsTypeArgumentsOnAMethodCall()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Inner { i32 id(i32 x) { return x; } } i32 main() { Inner i = new Inner(); return i.id<i32>(1); }"));

        Assert.Contains("Method 'id' does not accept explicit type arguments", exception.Message);
    }

    [Fact]
    public void RejectsTypeArgumentsOnAMethodCallThroughAFieldChain()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Inner { i32 id(i32 x) { return x; } } class Holder { Inner inner; } " +
            "i32 main() { Holder h = new Holder(); return h.inner.id<i32>(1); }"));

        Assert.Contains("Method 'id' does not accept explicit type arguments", exception.Message);
    }

    [Fact]
    public void RejectsCallsToPrivateMethodsFromOutsideTheClass()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Box { private i32 hidden() { return 1; } } i32 main() { Box b = new Box(); return b.hidden(); }"));

        Assert.Contains("'hidden' of class 'Box'", exception.Message);
    }

    [Fact]
    public void RejectsConstructingAClassWithAPrivateConstructorFromOutside()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Box { private Box() { } } i32 main() { Box b = new Box(); return 0; }"));

        Assert.Contains("No accessible constructor", exception.Message);
    }

    [Fact]
    public void RejectsOrderingComparisonOfStrings()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "i32 main() { string a = \"a\"; string b = \"b\"; if (a < b) { return 1; } return 0; }"));

        Assert.Contains("requires numeric operands", exception.Message);
    }

    [Fact]
    public void AcceptsNullForAnyPointerType()
    {
        var model = Analyze(
            "i32 main() { i32* ptr = null; i64* other = null; if (ptr == null) { return 1; } return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void RejectsNullForANonPointerType()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze("i32 main() { i32 value = null; return value; }"));

        Assert.Contains("Cannot initialize 'value'", exception.Message);
    }

    [Fact]
    public void RejectsIntegerZeroAsANullPointer()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze("i32 main() { i32* ptr = 0; return 0; }"));

        Assert.Contains("expected i32*", exception.Message);
    }

    [Fact]
    public void RejectsProtectedAccessThroughAReceiverOfTheBaseType()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class A { protected i32 level = 1; } class B : A { i32 read(A other) { return other.level; } } " +
            "i32 main() { return 0; }"));

        Assert.Contains("'level' of class 'A' is protected", exception.Message);
    }

    [Fact]
    public void RejectsDeletingAnObjectTwice()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Box { i32 value = 1; } i32 main() { Box b = new Box(); delete b; delete b; return 0; }"));

        Assert.Contains("'b' is already deleted", exception.Message);
    }

    [Fact]
    public void RejectsDeletingALocalBeforeAssigningIt()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Box { i32 value = 1; } i32 main() { Box b; delete b; return 0; }"));

        Assert.Contains("'b' may be read before it is assigned", exception.Message);
    }

    [Fact]
    public void RejectsReadingAVariableAfterItIsDeleted()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Box { i32 value = 1; } i32 main() { Box b = new Box(); delete b; return b.value; }"));

        Assert.Contains("'b' was deleted and cannot be used", exception.Message);
    }

    [Fact]
    public void AllowsGivingADeletedVariableANewObject()
    {
        var model = Analyze(
            "class Box { i32 value = 1; } i32 main() { Box b = new Box(); delete b; b = new Box(); delete b; return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void RejectsDeletingANonClassValue()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze("i32 main() { i32 x = 1; delete x; return 0; }"));

        Assert.Contains("requires a reference to a class object", exception.Message);
    }

    [Fact]
    public void WarnsWhenAnAllocationIsNeverFreedInAManualFunction()
    {
        var tokenizer = new Tokenizer();
        var tokens = tokenizer.TokenizeWithDiagnostics(
            "class Box { i32 value = 1; } i32 main() { Box b = new Box(); return b.value; }",
            "main.oxy").Tokens;
        var unit = new Parsing.Parser(tokens).ParseWithDiagnostics("main.oxy").Unit;

        var result = new Oxygen.Compiler.Semantic.SemanticAnalyzer { DefaultMemoryMode = MemoryMode.Manual }
            .AnalyzeWithDiagnostics([unit], ["main.oxy"]);

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Memory allocated for 'b' is never freed", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotWarnAboutAnAllocationInAnAutomaticFunction()
    {
        var tokenizer = new Tokenizer();
        var tokens = tokenizer.TokenizeWithDiagnostics(
            "class Box { i32 value = 1; } i32 main() { Box b = new Box(); return b.value; }",
            "main.oxy").Tokens;
        var unit = new Parsing.Parser(tokens).ParseWithDiagnostics("main.oxy").Unit;

        var result = new Oxygen.Compiler.Semantic.SemanticAnalyzer()
            .AnalyzeWithDiagnostics([unit], ["main.oxy"]);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Message.Contains("never freed", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotWarnWhenAnAllocationIsDeleted()
    {
        var tokenizer = new Tokenizer();
        var tokens = tokenizer.TokenizeWithDiagnostics(
            "class Box { i32 value = 1; } i32 main() { Box b = new Box(); i32 v = b.value; delete b; return v; }",
            "main.oxy").Tokens;
        var unit = new Parsing.Parser(tokens).ParseWithDiagnostics("main.oxy").Unit;

        var result = new Oxygen.Compiler.Semantic.SemanticAnalyzer()
            .AnalyzeWithDiagnostics([unit], ["main.oxy"]);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Message.Contains("never freed", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAPrivateImplementationOfAnInterfaceMethod()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "interface Shape { i32 area(); } class Square : Shape { private i32 area() { return 4; } } i32 main() { return 0; }"));

        Assert.Contains("must be implemented as public", exception.Message);
    }

    [Fact]
    public void AcceptsAPublicImplementationOfAnInterfaceMethod()
    {
        var model = Analyze(
            "interface Shape { i32 area(); } class Square : Shape { i32 area() { return 4; } } i32 main() { return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void FunctionsUseAutomaticMemoryByDefault()
    {
        var model = Analyze("i32 f() { return 0; } i32 main() { return f(); }");

        Assert.Equal(MemoryMode.Automatic, model.Functions.Values.Single(function => function.Name == "f").Memory);
    }

    [Fact]
    public void ManualMemoryAnnotationMarksOnlyItsFunction()
    {
        var model = Analyze("@manual_memory i32 f() { return 0; } i32 main() { return f(); }");

        Assert.Equal(MemoryMode.Manual, model.Functions.Values.Single(function => function.Name == "f").Memory);
        Assert.Equal(MemoryMode.Automatic, model.Functions.Values.Single(function => function.Name == "main").Memory);
    }

    [Fact]
    public void AutoMemoryAnnotationOverridesAManualDefault()
    {
        var model = new SemanticAnalyzer(Log.Logger) { DefaultMemoryMode = MemoryMode.Manual }.Analyze(
        [
            Parse("@auto_memory i32 f() { return 0; } i32 main() { return f(); }")
        ]);

        Assert.Equal(MemoryMode.Automatic, model.Functions.Values.Single(function => function.Name == "f").Memory);
        Assert.Equal(MemoryMode.Manual, model.Functions.Values.Single(function => function.Name == "main").Memory);
    }

    [Fact]
    public void RejectsAFunctionMarkedWithBothMemoryAnnotations()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "@manual_memory @auto_memory i32 f() { return 0; } i32 main() { return 0; }"));

        Assert.Contains("cannot be both @manual_memory and @auto_memory", exception.Message);
    }

    [Fact]
    public void RecordsAnAllocationThatDoesNotEscapeAsFreedAtScopeEnd()
    {
        var model = Analyze(
            "class Box { i32 value = 1; } void f() { Box b = new Box(); i32 v = b.value; } i32 main() { f(); return 0; }");

        Assert.Contains(model.AutomaticallyFreedLocals, declaration => declaration.Name == "b");
    }

    [Fact]
    public void DoesNotRecordAnAllocationThatIsReturned()
    {
        var model = Analyze(
            "class Box { i32 value = 1; } Box make() { Box b = new Box(); return b; } i32 main() { Box x = make(); delete x; return 0; }");

        Assert.Empty(model.AutomaticallyFreedLocals);
    }

    [Fact]
    public void RejectsDeletingAgainAfterEveryPathDeletedTheObject()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Box { i32 value = 1; } i32 main() { Box b = new Box(); i32 c = 1; " +
            "if (c == 1) { delete b; } else { delete b; } delete b; return 0; }"));

        Assert.Contains("'b' is already deleted", exception.Message);
    }

    [Fact]
    public void AllowsReadingAfterOnlySomePathsDeletedTheObject()
    {
        var model = Analyze(
            "class Box { i32 value = 1; } i32 main() { Box b = new Box(); i32 c = 1; " +
            "if (c == 1) { delete b; } i32 v = b.value; return v; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void AllowsReadingAfterABranchThatDeletedAndReturned()
    {
        var model = Analyze(
            "class Box { i32 value = 1; } i32 main() { Box b = new Box(); i32 c = 1; " +
            "if (c == 1) { delete b; return 0; } i32 v = b.value; return v; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void RejectsReadingAfterEveryPathDeletedTheObject()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Box { i32 value = 1; } i32 main() { Box b = new Box(); i32 c = 1; " +
            "if (c == 1) { delete b; } else { delete b; } return b.value; }"));

        Assert.Contains("'b' was deleted and cannot be used", exception.Message);
    }

    [Fact]
    public void AllowsDeletingInALoopBodyWithoutAnErrorAfterTheLoop()
    {
        var model = Analyze(
            "class Box { i32 value = 1; } i32 main() { Box b = new Box(); i32 i = 0; " +
            "while (i < 1) { delete b; i = i + 1; } return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void AllowsReadingAfterAForInBodyThatDeletedTheObject()
    {
        var model = Analyze(
            "class Box { i32 value = 1; } i32 main() { Box b = new Box(); " +
            "i32[2] values = {1, 2}; for (var x in values) { delete b; } return b.value; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void ChecksAtRunTimeADeleteInALoopBodyThatRepeats()
    {
        var model = Analyze(
            "class Box { i32 value = 1; } i32 main() { Box b = new Box(); i32 i = 0; " +
            "while (i < 2) { delete b; i = i + 1; } return 0; }");

        Assert.Single(model.RuntimeCheckedDeletes);
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
    public void PromotesSmallerNumericOperandsInBinaryExpressions()
    {
        var model = Analyze("i32 main() { i8 small = 1; i64 large = 2; i64 result = small + large; return 0; }");

        var addition = Assert.Single(model.ExpressionTypes,
            pair => pair.Key is BinaryExpression binary && binary.Operator == TokenKind.Plus);
        Assert.Equal(SemanticType.FromPrimitive(PrimitiveType.I64), addition.Value);
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
    public void TypeSwitchWithoutDefaultMustStillReturnOnTheFallThroughPath()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Base { } class Child : Base { } " +
            "i32 f(Base b) { switch (b) { case is Child c: return 1; } } i32 main() { return 0; }"));

        Assert.Contains("must return a value", exception.Message);
    }

    [Fact]
    public void SwitchWhoseLastCaseFallsOutStillNeedsAReturn()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "i32 f(i32 x) { switch (x) { case 1: return 1; case 2: x = 2; } } i32 main() { return 0; }"));

        Assert.Contains("must return a value", exception.Message);
    }

    [Fact]
    public void WhileTrueWhoseBodyReturnsNeedsNoReturnAfterIt()
    {
        var model = Analyze("i32 f() { while (true) { return 1; } } i32 main() { return f(); }");

        Assert.NotNull(model);
    }

    [Fact]
    public void WhileTrueWithoutBreakNeedsNoReturnAfterIt()
    {
        var model = Analyze("i32 f() { while (true) { } } i32 main() { return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void WhileTrueWithABreakStillNeedsAReturnAfterIt()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 f() { while (true) { break; } } i32 main() { return 0; }"));

        Assert.Contains("must return a value", exception.Message);
    }

    [Fact]
    public void SuffixedLiteralOutOfRangeForItsTypeIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze("i32 main() { u8 x = 300u8; return 0; }"));

        Assert.Contains("'300u8' does not fit in u8", exception.Message);
    }

    [Fact]
    public void SuffixedLiteralAtTheLimitOfItsTypeIsAccepted()
    {
        var model = Analyze("i32 main() { u8 x = 255u8; return cast<i32>(x); }");

        Assert.NotNull(model);
    }

    [Fact]
    public void NegatedSuffixedLiteralMayReachTheLowestSignedValue()
    {
        var model = Analyze("i32 main() { i8 x = -128i8; return cast<i32>(x); }");

        Assert.NotNull(model);
    }

    [Fact]
    public void PositiveSuffixedLiteralOneAboveTheSignedLimitIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze("i32 main() { i8 x = 128i8; return 0; }"));

        Assert.Contains("'128i8' does not fit in i8", exception.Message);
    }

    [Fact]
    public void ConstantZeroDivisorIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32 x = 1; return x / 0; }"));

        Assert.Contains("Division by zero", exception.Message);
    }

    [Fact]
    public void ConstantZeroRemainderIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32 x = 1; return x % 0; }"));

        Assert.Contains("Division by zero", exception.Message);
    }

    [Fact]
    public void ConstantFloatDivisionByZeroIsAcceptedBecauseItIsIeeeArithmetic()
    {
        var model = Analyze("i32 main() { f64 x = 1.0; f64 y = x / 0.0; return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void ShiftCountAboveTheWidthOfTheOperandIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32 x = 1; return x << 40; }"));

        Assert.Contains("Shift count 40 is out of range for i32", exception.Message);
    }

    [Fact]
    public void NegativeShiftCountIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32 x = 1; return x >> -1; }"));

        Assert.Contains("Shift count -1 is out of range for i32", exception.Message);
    }

    [Fact]
    public void ShiftCountWithinAWiderOperandIsAccepted()
    {
        var model = Analyze("i32 main() { i64 y = 1; return cast<i32>(y << 40); }");

        Assert.NotNull(model);
    }

    [Fact]
    public void ConstantFoldingIsExactBeyondDoublePrecision()
    {
        // 9007199254740993 - 9007199254740992 + 2147483647 is 2147483648, one above i32's limit. In double arithmetic
        // the first two literals round to the same value, so the sum wrongly fits.
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32 x = 9007199254740993 - 9007199254740992 + 2147483647; return x; }"));

        Assert.Contains("Cannot initialize 'x' with i64; expected i32", exception.Message);
    }

    [Fact]
    public void IntegerConstantThatRoundsWhenConvertedToFloatIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { f64 x = 9007199254740993; return 0; }"));

        Assert.Contains("f64", exception.Message);
    }

    [Fact]
    public void IntegerConstantExactAsFloatIsAccepted()
    {
        var model = Analyze("i32 main() { f64 x = 9007199254740991 - 1; f32 y = 16777216 + 0; return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void DivisorThatFoldsToZeroIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32 x = 1; return x / (3 - 3); }"));

        Assert.Contains("Division by zero", exception.Message);
    }

    [Fact]
    public void ShiftCountThatFoldsOutOfRangeIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32 x = 1; return x << (20 + 20); }"));

        Assert.Contains("Shift count 40 is out of range for i32", exception.Message);
    }

    [Fact]
    public void ArrayLiteralTakesTheDeclaredElementType()
    {
        var model = Analyze("i32 main() { i64[2] wide = { 1, 2 }; f64[2] half = { 1, 2 }; return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void ArrayLiteralElementThatDoesNotFitTheDeclaredElementTypeIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { u8[2] values = { 1, 300 }; return 0; }"));

        Assert.Contains("Array literal element has type i32; expected u8", exception.Message);
    }

    [Fact]
    public void ArrayLiteralElementOfAnotherTypeIsRejectedAgainstTheDeclaredElementType()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32[2] values = { 1, cast<i64>(2) }; return 0; }"));

        Assert.Contains("Array literal element has type i64; expected i32", exception.Message);
    }

    [Fact]
    public void GlobalVariableIsReadAndAssignedByItsFunctions()
    {
        var model = Analyze(
            "i32 counter = 1; void bump() { counter = counter + 1; } i32 main() { bump(); return counter; }");

        Assert.NotNull(model);
        Assert.Equal("counter", Assert.Single(model.Globals).Name);
    }

    [Fact]
    public void LocalVariableShadowsAGlobalOfTheSameName()
    {
        var model = Analyze("i32 value = 1; i32 main() { i32 value = 2; return value; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void FieldOfConstReferenceCannotBeAssignedOutsideItsClass()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Inner { i32 x; } class Outer { Inner inner; } " +
            "i32 main() { const Outer o = new Outer(); o.inner = new Inner(); return 0; }"));

        Assert.Contains("Cannot assign to a field of const variable 'o' outside class 'Outer'", exception.Message);
    }

    [Fact]
    public void FieldOfAnObjectReachedThroughConstReferenceCannotBeAssignedOutsideItsClass()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Inner { i32 x; } class Outer { Inner inner; } " +
            "i32 main() { const Outer o = new Outer(); o.inner.x = 5; return 0; }"));

        Assert.Contains("outside class 'Inner'", exception.Message);
    }

    [Fact]
    public void ConstReferenceCanBeChangedInsideItsClass()
    {
        var model = Analyze(
            "class Counter { i32 count; void bump() { const Counter self = this; self.count = self.count + 1; } } " +
            "i32 main() { return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void BaseCallOutsideAClassIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32 x = base.f(); return x; }"));

        Assert.Contains("'base' is only valid inside a class member", exception.Message);
    }

    [Fact]
    public void FieldOfAClassObjectCanBeAddressed()
    {
        var model = Analyze("class A { i32 x; } i32 main() { A a = new A(); i32* p = &a.x; return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void ReturningAStackObjectIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("class Box { i32 v; } Box make() { return Box(); } i32 main() { return 0; }"));

        Assert.Contains("A stack object cannot be returned", exception.Message);
    }

    [Fact]
    public void ReturningAStackLocalIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("class Box { i32 v; } Box make() { Box b = Box(); return b; } i32 main() { return 0; }"));

        Assert.Contains("A stack object cannot be returned", exception.Message);
    }

    [Fact]
    public void StoringAStackObjectInAFieldIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("class Box { i32 v; } class Holder { Box b; } " +
                "i32 main() { Holder h = new Holder(); Box local = Box(); h.b = local; return 0; }"));

        Assert.Contains("A stack object cannot be stored in a field", exception.Message);
    }

    [Fact]
    public void AssigningAStackObjectToAnOuterVariableIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("class Box { i32 v; } i32 main() { Box outer = new Box(); " +
                "if (true) { Box inner = Box(); outer = inner; } return 0; }"));

        Assert.Contains("A stack object cannot be assigned to a variable of an outer scope", exception.Message);
    }

    [Fact]
    public void PassingAStackObjectToAParameterThatStoresItIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("class Box { i32 v; } class Holder { Box kept; void keep(Box x) { this.kept = x; } } " +
                "i32 main() { Holder h = new Holder(); h.keep(Box()); return 0; }"));

        Assert.Contains("A stack object cannot be passed to 'keep', whose parameter 'x' may keep it", exception.Message);
    }

    [Fact]
    public void PassingAStackLocalToAParameterThatStoresItIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("class Box { i32 v; } class Holder { Box kept; void keep(Box x) { this.kept = x; } } " +
                "i32 main() { Holder h = new Holder(); Box b = Box(); h.keep(b); return 0; }"));

        Assert.Contains("A stack object cannot be passed to 'keep'", exception.Message);
    }

    [Fact]
    public void PassingAStackObjectToAParameterThatOnlyReadsItsFieldsIsAccepted()
    {
        var model = Analyze("class Box { i32 v; } i32 read(Box b) { return b.v; } " +
            "i32 main() { return read(Box()); }");

        Assert.NotNull(model);
    }

    [Fact]
    public void PassingAStackObjectToAVirtualParameterIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("class Box { i32 v; } class Reader { virtual i32 read(Box b) { return b.v; } } " +
                "i32 main() { Reader r = new Reader(); return r.read(Box()); }"));

        Assert.Contains("A stack object cannot be passed to 'read'", exception.Message);
    }

    [Fact]
    public void ReturningANewObjectIsAccepted()
    {
        var model = Analyze("class Box { i32 v; } Box make() { return new Box(); } i32 main() { return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void CapitalizedCallOfAFunctionIsACall()
    {
        var model = Analyze("i32 Make(i32 x) { return x + 1; } i32 main() { return Make(3); }");

        Assert.NotNull(model);
    }

    [Fact]
    public void PrivateFieldOfABaseClassCannotBeNamedBySubclass()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("class A { private i32 x; } class B : A { i32 g() { return x; } } i32 main() { return 0; }"));

        Assert.Contains("Field 'x' of class 'A' is private and cannot be used here", exception.Message);
    }

    [Fact]
    public void PrivateFieldOfABaseClassCannotBeReadThroughThisInSubclass()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("class A { private i32 x; } class B : A { i32 g() { return this.x; } } i32 main() { return 0; }"));

        Assert.Contains("Field 'x' of class 'A' is private and cannot be used here", exception.Message);
    }

    [Fact]
    public void BaseClassUsesItsOwnPrivateFieldThroughThis()
    {
        var model = Analyze(
            "class A { private i32 x; i32 get() { return this.x; } } class B : A { } i32 main() { return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void PrivateDestructorBlocksCreationOutsideTheClass()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("class A { private ~A() { } } i32 main() { A a = new A(); return 0; }"));

        Assert.Contains("has a private destructor, so its objects can only be created inside the class", exception.Message);
    }

    [Fact]
    public void PrivateDestructorAllowsCreationInsideTheClass()
    {
        var model = Analyze(
            "class A { private ~A() { } i32 make() { A b = new A(); return 1; } } i32 main() { return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void PropertyCannotBeAddressed()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("class A { public i32 P { get; set; } } i32 main() { A a = new A(); i32* p = &a.P; return 0; }"));

        Assert.Contains("requires an addressable operand", exception.Message);
    }

    [Fact]
    public void VoidParameterFieldAndArrayElementAreRejected()
    {
        Assert.Contains("Parameter 'v' cannot have type void", Assert.Throws<SemanticException>(() =>
            Analyze("void f(void v) { } i32 main() { return 0; }")).Message);
        Assert.Contains("Field 'v' cannot have type void", Assert.Throws<SemanticException>(() =>
            Analyze("class A { void v; } i32 main() { return 0; }")).Message);
        Assert.Contains("An array element cannot have type void", Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { void[2] a; return 0; }")).Message);
    }

    [Fact]
    public void PointersToDifferentTypesCannotBeCompared()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32* p = null; i64* q = null; bool same = p == q; return 0; }"));

        Assert.Contains("the pointers point to different types", exception.Message);
    }

    [Fact]
    public void StringLengthAndByteIndexAreReadable()
    {
        var model = Analyze("i32 main() { string a = \"hey\"; usize n = a.length; u8 b = a[1]; return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void LengthOfAnArrayOrStringCannotBeAssigned()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { string a = \"hey\"; a.length = 3; return 0; }"));

        Assert.Contains("The length of an array or a string cannot be assigned", exception.Message);
    }

    [Fact]
    public void StringByteCannotBeAssigned()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { string a = \"hey\"; a[0] = 5u8; return 0; }"));

        Assert.Contains("A string is immutable", exception.Message);
    }

    [Fact]
    public void EnumValuesOfOneEnumCompareForEquality()
    {
        var model = Analyze(
            "enum Code : i32 { Low = -1, High = 1 } i32 main() { Code c = Code.Low; bool same = c == Code.High; return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void FlagsEnumCombinesWithBitwiseOperatorsAndComparesWithZero()
    {
        var model = Analyze(
            """
            @flags enum Access : i32 { Empty = 0, Read = 1, Write = 2, Exec = 4 }
            i32 main() {
                Access a = Access.Read | Access.Write;
                Access b = a & Access.Read;
                Access c = a ^ Access.Exec;
                Access d = ~a;
                bool any = (a & Access.Read) != 0;
                bool none = 0 == (a & Access.Exec);
                return 0;
            }
            """);

        Assert.True(model.Enums["program.Access"].IsFlags);
        Assert.Equal(0b111, model.Enums["program.Access"].FlagsMask);
    }

    [Fact]
    public void RejectsFlagsMemberThatIsNotASingleBit()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("@flags enum Access { Read = 1, Both = 3 } i32 main() { return 0; }"));

        Assert.Contains("must be 0 or a single bit, got 3", exception.Message);
    }

    [Fact]
    public void RejectsNegativeFlagsMember()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("@flags enum Access : i32 { Read = -1 } i32 main() { return 0; }"));

        Assert.Contains("must be 0 or a single bit", exception.Message);
    }

    [Fact]
    public void RejectsBitwiseOperatorOnEnumThatIsNotFlags()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("enum Color { Red = 1, Blue = 2 } i32 main() { Color c = Color.Red | Color.Blue; return 0; }"));

        Assert.Contains("is not declared @flags", exception.Message);
    }

    [Fact]
    public void RejectsCombiningValuesOfDifferentFlagsEnums()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze(
                """
                @flags enum Access { Read = 1 }
                @flags enum Mode { Fast = 1 }
                i32 main() { Access a = Access.Read | Mode.Fast; return 0; }
                """));

        Assert.Contains("combines values of one @flags enum", exception.Message);
    }

    [Fact]
    public void RejectsComplementOfEnumThatIsNotFlags()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("enum Color { Red = 1 } i32 main() { Color c = ~Color.Red; return 0; }"));

        Assert.Contains("requires an integer operand", exception.Message);
    }

    [Fact]
    public void RejectsFlagsComparisonWithNonZeroInteger()
    {
        Assert.Throws<SemanticException>(() =>
            Analyze("@flags enum Access { Read = 1 } i32 main() { bool b = Access.Read == 1; return 0; }"));
    }

    [Fact]
    public void RejectsUnknownEnumAnnotation()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("@value enum Color { Red } i32 main() { return 0; }"));

        Assert.Contains("Unknown enum annotation '@value'", exception.Message);
    }

    [Fact]
    public void SetOnlyPropertyCanBeAssigned()
    {
        var model = Analyze(
            "class Box { public i32 Value { set; } } i32 main() { Box b = new Box(); b.Value = 5; return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void PropertyMayNameATypeDeclaredLater()
    {
        var model = Analyze(
            "class Holder { public Item Current { get; set; } } class Item { public i32 Id { get; set; } } " +
            "i32 main() { return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void PropertyAccessorBodyIsChecked()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("class Box { public i32 Value { get { } } } i32 main() { return 0; }"));

        Assert.Contains("must return a value", exception.Message);
    }

    [Fact]
    public void GenericClassAccessorBodyIsCheckedWithoutAnInstantiation()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Box<T> { i32 P { get { return \"x\"; } } } i32 main() { return 0; }"));

        Assert.Contains("Cannot return string from function 'get_P'", exception.Message);
    }

    [Fact]
    public void GenericClassAccessorBodyIsCheckedForEachInstantiation()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Box<T> { T item; i32 P { get { return \"x\"; } } } i32 main() { Box<i32> b = new Box<i32>(); return b.P; }"));

        Assert.Contains("Cannot return string from function 'get_P'", exception.Message);
    }

    [Fact]
    public void RethrowOutsideACatchClauseIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { throw; return 0; }"));

        Assert.Contains("'throw;' rethrows the exception of a catch clause", exception.Message);
    }

    [Fact]
    public void ConstLocalNeedsAnInitializer()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { const i32 x; return 0; }"));

        Assert.Contains("Const variable 'x' needs an initializer", exception.Message);
    }

    [Fact]
    public void PartOfAConstValueTypeCannotBeAssigned()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "@value class Inner { i32 x; } @value class Outer { Inner inner; } " +
            "i32 main() { const Outer o = Outer(); o.inner.x = 5; return 0; }"));

        Assert.Contains("Cannot assign to a part of const variable 'o'", exception.Message);
    }

    [Fact]
    public void ConstGlobalCannotBeAssigned()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("const i32 limit = 5; i32 main() { limit = 6; return 0; }"));

        Assert.Contains("Cannot assign to const variable 'limit'", exception.Message);
    }

    [Fact]
    public void ConstGlobalNeedsAnInitializer()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("const i32 limit; i32 main() { return 0; }"));

        Assert.Contains("Const global variable 'limit' needs an initializer", exception.Message);
    }

    [Fact]
    public void GlobalInitializerThatIsNotAConstantIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 first = 1; i32 second = first; i32 main() { return 0; }"));

        Assert.Contains("must be initialized with a literal or an integer constant expression", exception.Message);
    }

    [Fact]
    public void GlobalInitializerThatDoesNotFitItsTypeIsRejected()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("u8 small = 300; i32 main() { return 0; }"));

        Assert.Contains("Cannot initialize 'small' with i32; expected u8", exception.Message);
    }

    [Fact]
    public void GlobalOfANonPrimitiveTypeIsRejectedForNow()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("string text = \"hi\"; i32 main() { return 0; }"));

        Assert.Contains("only numeric and bool globals are supported", exception.Message);
    }

    [Fact]
    public void GlobalNameIsDeclaredOncePerModule()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 value = 1; i32 value = 2; i32 main() { return 0; }"));

        Assert.Contains("A global variable named 'value' is already declared", exception.Message);
    }

    [Fact]
    public void IntegerConstantInitializesAFloatGlobal()
    {
        var model = Analyze("f64 scale = 2; f32 ratio = -0.5f32; i32 main() { return 0; }");

        Assert.Equal(2, model.Globals.Count);
    }

    [Fact]
    public void StatementAfterBreakIsReportedAsUnreachable()
    {
        var result = AnalyzeWithDiagnostics(
            "i32 main() { i32 i = 0; while (i < 3) { break; i = 5; } return 0; }");

        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Message == "Unreachable statement");
    }

    [Fact]
    public void StatementAfterContinueIsReportedAsUnreachable()
    {
        var result = AnalyzeWithDiagnostics(
            "i32 main() { i32 i = 0; while (i < 3) { i = i + 1; continue; i = 5; } return 0; }");

        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Message == "Unreachable statement");
    }

    [Fact]
    public void StatementAfterAWhileTrueWithoutBreakIsReportedAsUnreachable()
    {
        var result = AnalyzeWithDiagnostics("i32 main() { while (true) { } return 0; }");

        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Message == "Unreachable statement");
    }

    [Fact]
    public void AConditionalBreakMakesNoStatementUnreachable()
    {
        var result = AnalyzeWithDiagnostics(
            "i32 main() { i32 i = 0; while (i < 3) { i = i + 1; if (i == 2) { break; } } return 0; }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Message == "Unreachable statement");
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
        var main = Parse("import math; i32 main() { return add(10, 32) + math.add(10, 32) + float.pow(20.4, 2.4); }");
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
    public void AllowsUnqualifiedFunctionsFromAnImportedModule()
    {
        var model = new SemanticAnalyzer(Log.Logger).Analyze(
        [
            Parse("import math; i32 main() { return add(10, 32); }"),
            Parse("public module math; public i32 add(i32 left, i32 right) { return left + right; }")
        ]);

        Assert.Equal("math.add", model.ResolvedCalls.Values.Single().QualifiedName);
    }

    [Fact]
    public void RejectsImportsThatDoNotMatchAnyKnownModule()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            new SemanticAnalyzer(Log.Logger).Analyze(
            [
                Parse("import missing.pkg; i32 main() { return 0; }")
            ]));

        Assert.Contains("Imported module 'missing.pkg' could not be found", exception.Message);
    }

    [Fact]
    public void AcceptsImportsResolvedByNativeModuleMetadataEvenWhenUnused()
    {
        var model = new SemanticAnalyzer(Log.Logger).Analyze(
            [Parse("import foo.bar; i32 main() { return 0; }")],
            [
                new ExternalModuleDeclaration(
                    "foo.bar",
                    [new ExternalFunctionDeclaration(
                        "value",
                        SemanticType.FromPrimitive(PrimitiveType.I32),
                        [],
                        "_Ox_native_value")])
            ]);

        Assert.NotNull(model.MainFunction);
    }

    [Fact]
    public void ResolvesAliasedImportsAndRejectsUnqualifiedCollisions()
    {
        var aliased = new SemanticAnalyzer(Log.Logger).Analyze(
        [
            Parse("import foo.value as left; import bar.value as right; i32 main() { return left.get() + right.value(); }"),
            Parse("public module foo.value; public i32 get() { return 1; }"),
            Parse("public module bar.value; public i32 value() { return 2; }")
        ]);

        Assert.Contains(aliased.ResolvedCalls.Values, symbol => symbol.QualifiedName == "foo.value.get");
        Assert.Contains(aliased.ResolvedCalls.Values, symbol => symbol.QualifiedName == "bar.value.value");

        var exception = Assert.Throws<SemanticException>(() =>
            new SemanticAnalyzer(Log.Logger).Analyze(
            [
                Parse("import foo; import bar; i32 main() { return value(); }"),
                Parse("public module foo; public i32 value() { return 1; }"),
                Parse("public module bar; public i32 value() { return 2; }")
            ]));

        Assert.Contains("ambiguous", exception.Message);
    }

    [Fact]
    public void SupportsAsCastsWithoutChangingTypePatterns()
    {
        var model = Analyze(
            "class Base { } class Child : Base { } i32 main() { Base value = new Child(); Child child = value as Child; return child is Child ? 1 : 0; }");

        Assert.Contains(model.ExpressionTypes.Values, type => type == SemanticType.Class("program.Child"));
        Assert.Contains(model.ExpressionTypes.Keys, expression => expression is IsExpression);
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
                pointer = null;

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
            Analyze("i32 main() { return *(null); }"));

        Assert.Contains("Cannot dereference a null pointer", exception.Message);
    }

    [Fact]
    public void RejectsDereferenceOfPointerKnownToBeNull()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32* ptr = null; *ptr = 10; return 0; }"));

        Assert.Contains("Cannot dereference a null pointer", exception.Message);
    }

    [Fact]
    public void AcceptsDereferenceOfPointerThatIsNullOnlyOnOneBranch()
    {
        var model = Analyze("i32 main() { i32 x = 3; bool c = true; i32* p = &x; if (c) { p = null; } return *p; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void RejectsDereferenceOfPointerThatIsNullOnEveryBranch()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "i32 main() { i32 x = 3; bool c = true; i32* p = &x; if (c) { p = null; } else { p = null; } return *p; }"));

        Assert.Contains("Cannot dereference a null pointer", exception.Message);
    }

    [Fact]
    public void AcceptsDereferenceAfterALoopThatMayNotAssignNull()
    {
        var model = Analyze("i32 main() { i32 x = 3; bool c = false; i32* p = &x; while (c) { p = null; } return *p; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void RejectsDereferenceAfterABreakThatLeavesNull()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "i32 main() { i32 x = 3; i32* p = &x; while (true) { p = null; break; } return *p; }"));

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

    [Fact]
    public void AttributesDuplicateTypeDiagnosticsToTheDeclaringSourceFile()
    {
        var result = new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics(
            [
                Parse("import std.collection; i32 main() { return 0; }"),
                Parse("module std.collection; public union Option<T> { Some(T); None; }"),
                Parse("module std.collection; public union Option<T> { Some(T); None; }")
            ],
            ["main.oxy", "first/collection.oxy", "second/collection.oxy"]);

        var error = Assert.Single(result.Diagnostics, diagnostic =>
            diagnostic.Message == "A type named 'Option' is already declared");
        Assert.Equal("second/collection.oxy", error.SourceName);
    }

    [Fact]
    public void ResolvesBaseConstructorByArgumentType()
    {
        var model = Analyze(
            "class Base { Base(i32 value) { } Base(string text) { } } " +
            "class Child : Base { Child() : base(\"text\") { } } i32 main() { return 0; }");

        var constructor = model.Classes["program.Child"].Constructors.Single().Declaration;
        Assert.Equal(1, model.ConstructorChainTargets[constructor]);
    }

    [Fact]
    public void ResolvesThisConstructorAndNewExpressionByArgumentType()
    {
        var model = Analyze(
            "class Item { i32 value; Item() : this(1) { } Item(i32 value) { this.value = value; } " +
            "Item(string text) { value = 2; } } " +
            "i32 main() { Item item = new Item(\"text\"); return item.value; }");

        var parameterless = model.Classes["program.Item"].Constructors[0].Declaration;
        Assert.Equal(1, model.ConstructorChainTargets[parameterless]);
        var creation = Assert.IsType<NewExpression>(
            Assert.IsType<VariableDeclarationStatement>(model.MainFunction!.Declaration.Body.Statements[0]).Initializer);
        Assert.Equal(2, model.ConstructorSelections[creation]);
    }

    [Fact]
    public void RecordsImplicitParameterlessBaseConstructorForDerivedConstructors()
    {
        var model = Analyze(
            "class Base { i32 value = 1; } class Child : Base { i32 extra = 2; Child() { } } " +
            "i32 main() { return 0; }");

        var constructor = model.Classes["program.Child"].Constructors.Single().Declaration;
        Assert.Equal(0, model.ConstructorChainTargets[constructor]);
    }

    [Fact]
    public void AnalyzesConstructorInitializerOfGenericBaseClass()
    {
        var model = Analyze(
            "class Boxed<T> { T value; Boxed(T value) { this.value = value; } } " +
            "class Box<T> : Boxed<T> { Box(T value) : base(value) { } } " +
            "i32 main() { Box<i32> box = new Box<i32>(4); return 0; }");

        Assert.Contains(model.Classes.Values, cls => cls.Name.StartsWith("Boxed__", StringComparison.Ordinal));
    }

    [Fact]
    public void RequiresBaseConstructorWhenBaseHasNoParameterlessConstructor()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Base { Base(i32 value) { } } class Child : Base { } i32 main() { return 0; }"));
        Assert.Contains("must call a base constructor", exception.Message);
    }

    [Fact]
    public void RejectsBaseInitializerWithoutBaseClass()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "interface Printable { } class Item : Printable { Item() : base() { } } i32 main() { return 0; }"));
        Assert.Contains("has no base class", exception.Message);
    }

    [Fact]
    public void RejectsConstructorInitializerWithoutMatchingConstructor()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Base { Base(i32 value) { } } class Child : Base { Child() : base(\"text\") { } } " +
            "i32 main() { return 0; }"));
        Assert.Contains("No constructor of 'Base' matches", exception.Message);
    }

    [Fact]
    public void RejectsConstructorChainCycles()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Item { Item() : this(1) { } Item(i32 value) : this() { } } i32 main() { return 0; }"));
        Assert.Contains("forms a cycle", exception.Message);
    }

    [Fact]
    public void RejectsThisAndFieldsInConstructorInitializer()
    {
        var thisError = Assert.Throws<SemanticException>(() => Analyze(
            "class Base { Base(i32 value) { } } class Child : Base { i32 value; Child() : base(this.value) { } } " +
            "i32 main() { return 0; }"));
        Assert.Contains("cannot be used in a constructor initializer", thisError.Message);

        var fieldError = Assert.Throws<SemanticException>(() => Analyze(
            "class Base { Base(i32 value) { } } class Child : Base { i32 value; Child() : base(value) { } } " +
            "i32 main() { return 0; }"));
        Assert.Contains("'value' is not declared", fieldError.Message);
    }

    [Fact]
    public void ReadingALocalBeforeAssigningItIsAnError()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze("i32 main() { i32 x; return x; }"));

        Assert.Contains("'x' may be read before it is assigned", exception.Message);
    }

    [Fact]
    public void AssigningALocalBeforeReadingItIsAccepted()
    {
        var model = Analyze("i32 main() { i32 x; x = 3; return x; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void AssignmentOnOnlyOneBranchLeavesTheLocalUnassigned()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32 x; if (true) { x = 1; } return x; }"));

        Assert.Contains("'x' may be read before it is assigned", exception.Message);
    }

    [Fact]
    public void AssignmentOnEveryBranchIsAccepted()
    {
        var model = Analyze("i32 main() { i32 x; if (true) { x = 1; } else { x = 2; } return x; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void ABranchThatReturnsDoesNotLeaveTheLocalUnassigned()
    {
        var model = Analyze("i32 main() { i32 x; if (true) { return 0; } else { x = 1; } return x; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void AWhileLoopBodyDoesNotAssignTheLocalAfterTheLoop()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32 x; while (false) { x = 1; } return x; }"));

        Assert.Contains("'x' may be read before it is assigned", exception.Message);
    }

    [Fact]
    public void WhileTrueLeavesOnlyTheBreakPathsAfterTheLoop()
    {
        var model = Analyze("i32 main() { i32 x; while (true) { x = 1; break; } return x; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void ASwitchWithoutDefaultLeavesTheLocalUnassignedAfterIt()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32 x; switch (1) { case 1: x = 1; } return x; }"));

        Assert.Contains("'x' may be read before it is assigned", exception.Message);
    }

    [Fact]
    public void RejectsReadingAFieldOfAStructBeforeItIsAssigned()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "struct Point { i32 x; i32 y; } i32 main() { Point p; p.x = 1; return p.y; }"));

        Assert.Contains("'p.y' may be read before it is assigned", exception.Message);
    }

    [Fact]
    public void AllowsWritingAndReadingEachFieldOfAStructOnceItIsAssigned()
    {
        var model = Analyze("struct Point { i32 x; i32 y; } i32 main() { Point p; p.x = 1; p.y = 2; return p.x + p.y; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void RejectsReadingTheWholeStructBeforeEveryFieldIsAssigned()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "struct Point { i32 x; i32 y; } i32 main() { Point p; p.x = 1; Point q = p; return 0; }"));

        Assert.Contains("'p.y' may be read before it is assigned", exception.Message);
    }

    [Fact]
    public void AWholeStructAssignmentAssignsEveryField()
    {
        var model = Analyze("struct Point { i32 x; i32 y; } i32 main() { Point p; p = Point { x: 1, y: 2 }; return p.y; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void ZeroLiteralInitializesEveryFieldOfAStruct()
    {
        var model = Analyze(
            "struct Point { i32 x; i32 y; } i32 main() { Point p = {0}; Point q; q = {0}; return p.y + q.x; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void ZeroLiteralIsTheOnlyBraceLiteralForAStruct()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "struct Point { i32 x; } i32 main() { Point p = {1}; return 0; }"));

        Assert.Contains("A struct brace literal must be {0}", exception.Message);
    }

    [Fact]
    public void ReadingANestedFieldBeforeItIsAssignedIsAnError()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "struct Point { i32 x; i32 y; } struct Line { Point a; Point b; } " +
            "i32 main() { Line l; l.a.x = 1; l.a.y = 2; return l.b.x; }"));

        Assert.Contains("'l.b.x' may be read before it is assigned", exception.Message);
    }

    [Fact]
    public void AssigningANestedStructAsAWholeAssignsItsFields()
    {
        var model = Analyze(
            "struct Point { i32 x; i32 y; } struct Line { Point a; Point b; } " +
            "i32 main() { Line l; l.a.x = 1; l.a.y = 2; l.b = {0}; return l.b.x + l.a.y; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void AFieldAssignedInOnlyOneBranchStaysUnassignedAfterIt()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "struct Point { i32 x; i32 y; } i32 main() { Point p; if (true) { p.x = 1; p.y = 2; } return p.x; }"));

        Assert.Contains("'p.x' may be read before it is assigned", exception.Message);
    }

    [Fact]
    public void AClassFieldOfAStructLocalIsCheckedAsAValue()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Box { i32 v; } struct Holder { Box box; i32 n; } i32 main() { Holder h; h.n = 1; return h.box.v; }"));

        Assert.Contains("'h.box' may be read before it is assigned", exception.Message);
    }

    [Fact]
    public void AssignmentInTheTryBodyCountsOnlyWhenNoCatchCanSkipIt()
    {
        var exception = Assert.Throws<SemanticException>(() => AnalyzeWithStdException(
            "class Failure : Exception { } " +
            "i32 main() { i32 x; try { x = 1; } catch (Failure error) { } return x; }"));

        Assert.Contains("'x' may be read before it is assigned", exception.Message);
    }

    [Fact]
    public void AssignmentInTheTryBodyCountsWhenThereIsNoCatch()
    {
        var model = Analyze(
            "class Exception { } " +
            "i32 main() { i32 x; try { x = 1; } finally { } return x; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void ASwitchWithDefaultAssignsOnEveryCase()
    {
        var model = Analyze("i32 main() { i32 x; switch (1) { case 1: x = 1; default: x = 2; } return x; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void ErrorsInAClassFromAnotherFileAreAttributedToThatFile()
    {
        var library = new Parser(_tokenizer.Tokenize("class Broken : Missing { }"), Log.Logger).Parse();
        var main = new Parser(_tokenizer.Tokenize("i32 main() { return 0; }"), Log.Logger).Parse();

        var result = new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics(
            [main, library], ["main.oxy", "stdlib/exception.oxy"]);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Contains("'Missing' is not declared", diagnostic.Message);
        Assert.Equal("stdlib/exception.oxy", diagnostic.SourceName);
    }

    [Fact]
    public void StructLayoutErrorsInAnotherFileAreAttributedToThatFile()
    {
        var library = new Parser(_tokenizer.Tokenize("struct Node { Node next; }"), Log.Logger).Parse();
        var main = new Parser(_tokenizer.Tokenize("i32 main() { return 0; }"), Log.Logger).Parse();

        var result = new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics(
            [main, library], ["main.oxy", "stdlib/memory.oxy"]);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Contains("recursive by-value field", diagnostic.Message);
        Assert.Equal("stdlib/memory.oxy", diagnostic.SourceName);
    }

    [Fact]
    public void AnErrorInAGenericBodyIsReportedOnceNotOncePerInstantiation()
    {
        var unit = new Parser(_tokenizer.Tokenize(
            "class Box<T> { T value; i32 bad() { return value.missing; } } " +
            "i32 main() { Box<i32> b = new Box<i32>(); return b.bad(); }"), Log.Logger).Parse();

        var result = new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics([unit], ["main.oxy"]);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Contains("requires a struct or class value, got T", diagnostic.Message);
    }

    [Fact]
    public void MemberAccessOnTheNullLiteralIsRejectedAsNullHavingNoMembers()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Box { i32 value; } i32 main() { return null.value; }"));

        Assert.Contains("null literal", exception.Message);
        Assert.Contains("'value'", exception.Message);
    }

    [Fact]
    public void InheritedFieldsAppearOnceInEveryDescendantOfAThreeLevelChain()
    {
        var model = Analyze(
            "class A { i32 x; } " +
            "class B : A { } " +
            "class C : B { } " +
            "i32 main() { return 0; }");

        Assert.Equal(["x"], model.Classes["program.C"].Fields.Select(field => field.Name));
    }

    [Fact]
    public void AcceptsMethodCallsOnCallResultsAndNestedFields()
    {
        var result = AnalyzeWithDiagnostics(
            """
            class Inner {
                i32 value;
                Inner(i32 value) { this.value = value; }
                i32 get() { return value; }
            }
            class Outer {
                Inner inner;
                Outer() { this.inner = new Inner(1); }
            }
            Inner make() { return new Inner(2); }
            i32 main() {
                Outer o = new Outer();
                return make().get() + o.inner.get();
            }
            """);

        Assert.NotNull(result.Model);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void RejectsAMethodThatTheCallResultTypeDoesNotHave()
    {
        var result = AnalyzeWithDiagnostics(
            """
            class Inner {
                i32 value;
                Inner(i32 value) { this.value = value; }
            }
            Inner make() { return new Inner(2); }
            i32 main() {
                return make().missing();
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("Method 'missing' is not declared for a receiver of type Inner", diagnostic.Message);
        Assert.Equal(DiagnosticCode.NotDeclared, diagnostic.Code);
    }

    [Fact]
    public void AcceptsCallsThatOmitTrailingParametersWithDefaults()
    {
        var result = AnalyzeWithDiagnostics(
            "i32 scale(i32 value, i32 factor = 2) { return value * factor; } i32 main() { return scale(5); }");

        Assert.NotNull(result.Model);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void RejectsARequiredParameterAfterOneWithADefault()
    {
        var result = AnalyzeWithDiagnostics("i32 f(i32 a = 1, i32 b) { return a + b; } i32 main() { return 0; }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("follows a parameter that has one", diagnostic.Message);
        Assert.Equal(DiagnosticCode.DefaultParameter, diagnostic.Code);
    }

    [Fact]
    public void RejectsADefaultThatIsNotAConstant()
    {
        var result = AnalyzeWithDiagnostics(
            "i32 pick() { return 3; } i32 f(i32 a = pick()) { return a; } i32 main() { return 0; }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("must be a bool literal, a number, or an integer constant expression", diagnostic.Message);
        Assert.Equal(DiagnosticCode.DefaultParameter, diagnostic.Code);
    }

    [Fact]
    public void RejectsADefaultThatDoesNotConvertToTheParameterType()
    {
        var result = AnalyzeWithDiagnostics("u8 f(u8 a = 300) { return a; } i32 main() { return 0; }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("The default value of 'a' has type i32; expected u8", diagnostic.Message);
    }

    [Fact]
    public void RejectsDefaultsOnExternFunctions()
    {
        var result = AnalyzeWithDiagnostics(
            "@extern(\"puts\") extern i32 puts(i32 value = 0); i32 main() { return 0; }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("cannot have a default value", diagnostic.Message);
    }

    [Fact]
    public void RejectsDefaultsOnParametersOfGenericType()
    {
        var result = AnalyzeWithDiagnostics("i32 gen<T>(T x = 1) { return 0; } i32 main() { return 0; }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("has a generic type, so it cannot have a default value", diagnostic.Message);
    }

    [Fact]
    public void RejectsOverloadsThatACallCouldMatchBoth()
    {
        var result = AnalyzeWithDiagnostics(
            "i32 process(i32 code) { return code; } i32 process(i32 code, i32 extra = 0) { return code + extra; } " +
            "i32 main() { return 0; }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("a call could match both", diagnostic.Message);
        Assert.Equal(DiagnosticCode.OverloadConflict, diagnostic.Code);
    }

    [Fact]
    public void AcceptsOverloadsThatDifferInATypeAtEveryPositionTheyCouldBeCalledWith()
    {
        var result = AnalyzeWithDiagnostics(
            "i32 pair(i32 a, i32 b) { return a - b; } i32 pair(i32 a, i64 b = 0) { return a; } " +
            "i32 main() { return pair(1, cast<i32>(2)); }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void BindsNamedArgumentsToTheirParametersInAnyOrder()
    {
        var result = AnalyzeWithDiagnostics(
            "i32 pair(i32 a, i32 b) { return a - b; } i32 main() { return pair(b: 1, a: 10); }");

        Assert.NotNull(result.Model);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void RejectsAParameterThatIsGivenTwice()
    {
        var result = AnalyzeWithDiagnostics(
            "i32 scale(i32 value, i32 factor = 2) { return value * factor; } i32 main() { return scale(5, value: 4); }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("Parameter 'value' of 'scale' is given more than once", diagnostic.Message);
        Assert.Equal(DiagnosticCode.ArgumentCount, diagnostic.Code);
    }

    [Fact]
    public void RejectsARequiredParameterThatNoArgumentFills()
    {
        var result = AnalyzeWithDiagnostics(
            "i32 scale(i32 value, i32 factor = 2) { return value * factor; } i32 main() { return scale(factor: 3); }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("Parameter 'value' of 'scale' needs an argument", diagnostic.Message);
    }

    [Fact]
    public void RejectsANameThatIsNoParameter()
    {
        var result = AnalyzeWithDiagnostics(
            "i32 scale(i32 value, i32 factor = 2) { return value * factor; } i32 main() { return scale(vale: 4); }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("has no parameter named 'vale'", diagnostic.Message);
    }

    [Fact]
    public void RejectsAPositionalArgumentAfterANamedOne()
    {
        var result = AnalyzeWithDiagnostics(
            "i32 scale(i32 value, i32 factor = 2) { return value * factor; } i32 main() { return scale(value: 4, 3); }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("A positional argument cannot follow a named argument", diagnostic.Message);
    }

    [Fact]
    public void RejectsANamedArgumentForAUnionPayload()
    {
        var result = AnalyzeWithDiagnostics(OptionUnion + "i32 main() { var o = Option.Some(value: 4); return 0; }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("is given by position, not by name", diagnostic.Message);
    }

    private const string CounterClass = "class Counter { static i32 count = 0; static const i32 limit = 3; i32 id; " +
        "static i32 next(i32 step = 1) { count = count + step; return count; } i32 get() { return id; } } ";

    [Fact]
    public void AcceptsStaticFieldsAndStaticCallsThroughTheClassName()
    {
        var result = AnalyzeWithDiagnostics(CounterClass +
            "i32 main() { Counter.count = Counter.count + 1; return Counter.next() + Counter.next(10) + Counter.limit; }");

        Assert.NotNull(result.Model);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("i32 main() { return Counter.get(); }", "is an instance method; call it on an object")]
    [InlineData("i32 main() { return Counter.id; }", "is an instance field; access it through an object")]
    [InlineData("i32 main() { Counter c = new Counter(); return c.next(); }", "is called through its class")]
    [InlineData("i32 main() { Counter.limit = 4; return 0; }", "Cannot assign to const static field 'limit'")]
    [InlineData("i32 main() { return Counter.missing(); }", "has no static method named 'missing'")]
    public void RejectsStaticMembersUsedAsInstanceMembersOrWrittenAsConstants(string body, string message)
    {
        var result = AnalyzeWithDiagnostics(CounterClass + body);

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(message, diagnostic.Message);
    }

    [Fact]
    public void RejectsThisAndInstanceFieldsInsideAStaticMethod()
    {
        var thisResult = AnalyzeWithDiagnostics(
            "class C { i32 id; static i32 f() { return this.id; } } i32 main() { return C.f(); }");
        Assert.Contains("'this' cannot be used in a static method",
            Assert.Single(thisResult.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Message);

        var fieldResult = AnalyzeWithDiagnostics(
            "class C { i32 id; static i32 f() { return id; } } i32 main() { return C.f(); }");
        Assert.Contains("Instance field 'id' cannot be used in a static method",
            Assert.Single(fieldResult.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Message);
    }

    [Fact]
    public void AcceptsBaseClassStaticMembersThroughASubclassNameOrAnObjectOfIt()
    {
        var result = AnalyzeWithDiagnostics(
            "class Base { static i32 n = 1; static i32 twice() { return n * 2; } } class Child : Base { } " +
            "i32 main() { Child c = new Child(); return Child.n + Child.twice() + c.n + c.twice(); }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void RejectsAStaticFieldOfTheObjectsOwnClassThroughAnObject()
    {
        var result = AnalyzeWithDiagnostics("class Child { static i32 own = 1; } i32 main() { Child c = new Child(); return c.own; }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("is accessed through its class, as Child.own", diagnostic.Message);
    }

    [Theory]
    [InlineData("class C { i32 h() { return 1; } i32 f() { return h(); } } i32 main() { return 0; }", "Function 'h' is not declared")]
    [InlineData("class C { static i32 h() { return 1; } static i32 f() { return h(); } } i32 main() { return C.f(); }",
        "Function 'h' is not declared")]
    public void MethodsOfAClassAreNotCalledByTheirBareName(string source, string message)
    {
        var result = AnalyzeWithDiagnostics(source);

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(message, diagnostic.Message);
    }

    [Fact]
    public void RejectsAPrivateStaticFieldOutsideItsClass()
    {
        var result = AnalyzeWithDiagnostics("class C { private static i32 secret = 1; } i32 main() { return C.secret; }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("cannot be used here", diagnostic.Message);
    }

    [Fact]
    public void RejectsAStaticFieldInitializerThatIsNotConstant()
    {
        var result = AnalyzeWithDiagnostics(
            "i32 pick() { return 3; } class C { static i32 x = pick(); } i32 main() { return 0; }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("must be initialized with a literal or an integer constant expression", diagnostic.Message);
    }

    [Fact]
    public void AcceptsStaticFieldsOfClassTypeThatStartAsNull()
    {
        var model = Analyze("class Node { i32 value; } class Holder { static Node head; static Node tail = null; } " +
            "i32 main() { return 0; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void RejectsStaticFieldOfClassTypeInitializedWithAnObject()
    {
        var result = AnalyzeWithDiagnostics(
            "class Node { i32 value; } class Holder { static Node head = new Node(); } i32 main() { return 0; }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("can only be initialized with null", diagnostic.Message);
    }

    [Fact]
    public void RejectsAStackObjectStoredInAStaticField()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Node { i32 value; } class Holder { static Node head; } " +
            "i32 main() { Holder.head = Node(); return 0; }"));

        Assert.Contains("A stack object cannot be stored in a field", exception.Message);
    }

    [Fact]
    public void AcceptsAStaticFieldOfOneInstanceOfAGenericClassFromOutside()
    {
        var model = Analyze("class Box<T> { static i32 touches = 0; } " +
            "i32 main() { Box<i32>.touches = Box<i32>.touches + 1; return Box<string>.touches; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void RejectsConstStaticFieldOfAGenericInstanceAssignedFromOutside()
    {
        var result = AnalyzeWithDiagnostics(
            "class Box<T> { static const i32 limit = 3; } i32 main() { Box<i32>.limit = 4; return 0; }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("Cannot assign to const static field 'limit'", diagnostic.Message);
    }

    [Fact]
    public void AcceptsReadingAndWritingAStaticProperty()
    {
        var model = Analyze("class Counter { static i32 seen = 0; static i32 Seen { get { return seen; } set { seen = value; } } } " +
            "i32 main() { Counter.Seen = 4; Counter.Seen += 3; return Counter.Seen; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void RejectsWritingAStaticPropertyWithoutASetter()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Counter { static i32 Seen { get { return 1; } } } i32 main() { Counter.Seen = 4; return 0; }"));

        Assert.Contains("Property 'Seen' is read-only", exception.Message);
    }

    [Fact]
    public void AcceptsReadingAndWritingAStaticPropertyOfOneInstanceOfAGenericClass()
    {
        var model = Analyze("class Box<T> { static i32 seen = 0; static i32 Seen { get { return seen; } set { seen = value; } } } " +
            "i32 main() { Box<i32>.Seen = 4; Box<i32>.Seen += 3; return Box<string>.Seen; }");

        Assert.NotNull(model);
    }

    [Fact]
    public void RejectsAGenericStaticPropertyNamedWithoutTypeArguments()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Box<T> { static i32 Seen { get { return 1; } } } i32 main() { return Box.Seen; }"));

        Assert.Contains("Static property 'Seen' of generic class 'Box' is named with its type arguments, such as Box<T>.Seen",
            exception.Message);
    }

    [Fact]
    public void RejectsWritingAReadOnlyStaticPropertyOfAGenericInstance()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Box<T> { static i32 Seen { get { return 1; } } } i32 main() { Box<i32>.Seen = 4; return 0; }"));

        Assert.Contains("Property 'Seen' is read-only", exception.Message);
    }

    [Fact]
    public void AcceptsIdentityComparisonOfReferencesAndComparisonWithNull()
    {
        var result = AnalyzeWithDiagnostics(
            "interface Named { i32 id(); } class Node : Named { i32 id() { return 1; } } " +
            "i32 main() { Node a = new Node(); Named x = a; Node n = null; Named y = null; " +
            "if (a == a && x == a && n == null && y != x && a != null) { return 1; } return 0; }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void RejectsComparingReferencesOfUnrelatedClasses()
    {
        var result = AnalyzeWithDiagnostics(
            "class A { } class B { } i32 main() { A a = new A(); B b = new B(); if (a == b) { return 1; } return 0; }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("references compare only when one of them converts to the other", diagnostic.Message);
    }

    [Fact]
    public void RejectsComparingValueClassesBecauseTheyHaveNoIdentity()
    {
        var result = AnalyzeWithDiagnostics(
            "@value class P { i32 x; P(i32 x) { this.x = x; } } i32 main() { P a = P(1); P b = P(1); if (a == b) { return 1; } return 0; }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("has no identity", diagnostic.Message);
    }

    private const string OptionUnion = "union Option<T> { Some(T); None; } ";

    [Fact]
    public void InfersAUnionCaseTypeArgumentFromItsPayload()
    {
        var result = AnalyzeWithDiagnostics(OptionUnion + "i32 main() { var res = Option.Some(\"hello\"); return 0; }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void InfersAUnionCaseTypeArgumentFromTheExpectedReturnType()
    {
        var result = AnalyzeWithDiagnostics(OptionUnion + "Option<i32> none() { return Option.None(); } i32 main() { return 0; }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void RejectsAUnionCaseWhoseTypeArgumentNothingDetermines()
    {
        var result = AnalyzeWithDiagnostics(OptionUnion + "i32 main() { var n = Option.None(); return 0; }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("Cannot infer type parameter 'T' of union case 'None'", diagnostic.Message);
        Assert.Equal(DiagnosticCode.CannotInfer, diagnostic.Code);
    }

    [Fact]
    public void InfersAPointerParameterFromAPointerArgumentWhileAnIntegerLiteralConvertsToU64()
    {
        var result = AnalyzeWithDiagnostics(
            "i64 first<T>(T* values, u64 count) { return 0; } " +
            "i64 use(i64* values) { return first(values, 4); } i32 main() { return 0; }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void RejectsArgumentsThatBindOneTypeParameterToUnrelatedTypes()
    {
        var result = AnalyzeWithDiagnostics(
            "T first<T>(T a, T b) { return a; } i32 main() { return first(1, \"x\"); }");

        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("do not agree on its type arguments", diagnostic.Message);
        Assert.Equal(DiagnosticCode.CannotInfer, diagnostic.Code);
    }

    [Fact]
    public void BindsTwoClassesToTheirNearestCommonBaseClass()
    {
        var result = AnalyzeWithDiagnostics(
            "class Animal { i32 legs; Animal(i32 legs) { this.legs = legs; } } " +
            "class Dog : Animal { Dog() : base(4) { } } class Cat : Animal { Cat() : base(4) { } } " +
            "T pick<T>(T first, T second) { return first; } " +
            "i32 main() { Animal a = pick(new Dog(), new Cat()); return a.legs; }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void BindsTwoUnrelatedClassesToTheirSharedInterface()
    {
        var result = AnalyzeWithDiagnostics(
            "interface Shape { i32 sides(); } " +
            "class Circle : Shape { i32 sides() { return 0; } } class Square : Shape { i32 sides() { return 4; } } " +
            "T pick<T>(T first, T second) { return first; } " +
            "i32 main() { Shape s = pick(new Circle(), new Square()); return s.sides(); }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void WidensAClassBindingToTheExpectedInterfaceOfAUnionCase()
    {
        var result = AnalyzeWithDiagnostics(
            "interface Iterator<T> { Option<T> next(); } interface Iterable<T> { Iterator<T> iter(); } " + OptionUnion +
            "class Empty : Iterator<i32> { Option<i32> next() { return Option.None(); } } " +
            "class Items : Iterable<i32> { Iterator<i32> iter() { return new Empty(); } } " +
            "i32 main() { Items items = new Items(); Option<Iterable<i32>> it = Option.Some(items); return 0; }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ASymbolListBringsOnlyItsNamesIntoScope()
    {
        var model = AnalyzeModules(
            "module lib; public i32 one() { return 1; } public i32 two() { return 2; }",
            "import lib{one}; i32 main() { return one(); }");

        Assert.NotNull(model.MainFunction);
    }

    [Fact]
    public void AnUnlistedNameOfASymbolImportIsNotInScope()
    {
        var exception = Assert.Throws<SemanticException>(() => AnalyzeModules(
            "module lib; public i32 one() { return 1; } public i32 two() { return 2; }",
            "import lib{one}; i32 main() { return two(); }"));

        Assert.Contains("two", exception.Message);
    }

    [Fact]
    public void ASymbolImportDoesNotAddTheLeafQualifierOfItsModule()
    {
        // The leaf form `lib.one` needs a plain import of outer.lib. The fully qualified `outer.lib.one` stays valid.
        var exception = Assert.Throws<SemanticException>(() => AnalyzeModules(
            "module outer.lib; public i32 one() { return 1; }",
            "import outer.lib{one}; i32 main() { return lib.one(); }"));

        Assert.Contains("lib.one", exception.Message);
    }

    [Fact]
    public void ASymbolImportOfAPrivateOrMissingNameIsReportedAtTheImport()
    {
        var exception = Assert.Throws<SemanticException>(() => AnalyzeModules(
            "module lib; i32 hidden() { return 1; }",
            "import lib{hidden}; i32 main() { return 0; }"));

        Assert.Contains("does not declare a public 'hidden'", exception.Message);
    }

    [Fact]
    public void ASymbolImportClashingWithALocalDeclarationOfTheSameKindIsReportedAtTheImport()
    {
        var exception = Assert.Throws<SemanticException>(() => AnalyzeModules(
            "module lib; public i32 one() { return 1; }",
            "import lib{one}; i32 one() { return 0; } i32 main() { return 0; }"));

        Assert.Contains("clashes with the function 'one'", exception.Message);
    }

    [Fact]
    public void TwoSymbolImportsOfOneNameAreAmbiguousAtTheUse()
    {
        var exception = Assert.Throws<SemanticException>(() => AnalyzeModules(
            "module first; public i32 pick() { return 1; }",
            "module second; public i32 pick() { return 2; }",
            "import first{pick}; import second{pick}; i32 main() { return pick(); }"));

        Assert.Contains("ambiguous", exception.Message);
    }

    [Fact]
    public void ASymbolAliasNamesTheImportedDeclarationLocally()
    {
        var model = AnalyzeModules(
            "module lib; public i32 one() { return 1; }",
            "import lib{one as uno}; i32 main() { return uno(); }");

        Assert.NotNull(model.MainFunction);
    }

    [Fact]
    public void ABraceLiteralOfOneNonZeroValueDoesNotFillAnArray()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { i32[3] values = {1}; return 0; }"));

        Assert.Contains("expects 3 element(s), got 1", exception.Message);
    }

    [Fact]
    public void TheAddressOfAStringElementIsRejectedBecauseStringBytesAreReadOnly()
    {
        var exception = Assert.Throws<SemanticException>(() =>
            Analyze("i32 main() { string s = \"abc\"; u8* q = &s[1]; return 0; }"));

        Assert.Contains("string bytes are read-only", exception.Message);
    }

    [Fact]
    public void ForInRejectsAnIteratorThatIsNeitherAClassNorAnInterface()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "class Bad { i32 iter() { return 1; } } i32 main() { Bad bad = new Bad(); for (var v in bad) { } return 0; }"));

        Assert.Contains("must return a class or an interface", exception.Message);
    }

    // Indexing a pointer dereferences it, so a pointer that is null on every path is an error there too, as for `*p`.
    [Fact]
    public void IndexingADefinitelyNullPointerIsAnError()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze("i32 main() { i32* p = null; return p[0]; }"));

        Assert.Contains("Cannot dereference a null pointer", exception.Message);
    }

    // The dotted form does not dereference a pointer, so it is rejected and names the struct or class value it needs.
    [Fact]
    public void DotDoesNotDereferenceAPointer()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "struct Point { i32 x; } i32 main() { Point point = {0}; Point* p = &point; return p.x; }"));

        Assert.Contains("Member access requires a struct or class value", exception.Message);
    }

    // `->` needs a pointer. It is `(*value).x`, so the error is the one that the dereference gives.
    [Fact]
    public void ArrowNeedsAPointerOperand()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "struct Point { i32 x; } i32 main() { Point point = {0}; return point->x; }"));

        Assert.Contains("The '*' operator requires a pointer operand", exception.Message);
    }

    // The arrow and the explicit dereference are the same tree, so a program that is wrong in one is wrong in the other, the same way.
    [Fact]
    public void ArrowAndExplicitDereferenceReportTheSameError()
    {
        var arrow = Assert.Throws<SemanticException>(() => Analyze(
            "struct Point { i32 x; } i32 main() { Point point = {0}; return point->x; }")).Message;
        var explicitForm = Assert.Throws<SemanticException>(() => Analyze(
            "struct Point { i32 x; } i32 main() { Point point = {0}; return (*point).x; }")).Message;

        Assert.Equal(explicitForm, arrow);
    }

    // An alias is the type it names, so a value of the target type passes where the alias is declared, with no conversion.
    [Fact]
    public void AnAliasIsTheTypeItNamesInASignature()
    {
        var model = Analyze(
            "type Count = u64; u64 twice(Count value) { return value; } i32 main() { Count count = 1; return cast<i32>(twice(count)); }");

        Assert.NotNull(model.MainFunction);
    }

    [Fact]
    public void AliasesOfAliasesResolve()
    {
        var model = Analyze("type A = B; type B = i32; i32 main() { A value = 3; return value; }");

        Assert.NotNull(model.MainFunction);
    }

    // The cycle is reported at the alias that the cycle starts from, which is the first one declared in it.
    [Fact]
    public void ACycleOfAliasesIsAnErrorAtTheFirstAlias()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze("type A = B; type B = A; i32 main() { return 0; }"));

        Assert.Contains("'A' is part of a cycle of type aliases", exception.Message);
    }

    [Fact]
    public void AnAliasCannotShareANameWithAType()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze("struct Point { i32 x; } type Point = i32; i32 main() { return 0; }"));

        Assert.Contains("already declared", exception.Message);
    }

    [Fact]
    public void AnAliasOfAnUndeclaredTypeIsAnError()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze("type A = Missing; i32 main() { return 0; }"));

        Assert.Contains("Missing", exception.Message);
    }

    // An alias of a struct names that struct, so it builds a value of the struct and reads its fields.
    [Fact]
    public void AnAliasOfAStructBuildsAValueOfTheStruct()
    {
        var model = Analyze("struct Point { i32 x; } type P = Point; i32 main() { P point = P { x: 3 }; return point.x; }");

        Assert.NotNull(model.MainFunction);
    }

    [Fact]
    public void AGenericInstanceCannotBeGivenTypeArgumentsThroughAnAlias()
    {
        var exception = Assert.Throws<SemanticException>(() => Analyze(
            "struct Box<T> { T item; } type IntBox = Box<i32>; i32 main() { IntBox<i32> box = {0}; return 0; }"));

        Assert.Contains("cannot have type arguments", exception.Message);
    }

    // Two imports that each provide an alias of one name are an ambiguity at the use, as for any two types of one name.
    [Fact]
    public void AnAliasThatTwoImportsProvideIsAmbiguousAtItsUse()
    {
        var exception = Assert.Throws<SemanticException>(() => AnalyzeModules(
            "module a; public type Count = u64;",
            "module b; public type Count = i32;",
            "import a; import b; i32 main() { Count c = 1; return 0; }"));

        Assert.Contains("ambiguous", exception.Message);
    }

    private SemanticModel AnalyzeModules(params string[] sources)
    {
        var units = sources.Select(source => new Parser(_tokenizer.Tokenize(source), Log.Logger).Parse()).ToList();
        return new SemanticAnalyzer(Log.Logger).Analyze(units);
    }

    private SemanticModel Analyze(string source)
    {
        var unit = new Parser(_tokenizer.Tokenize(source), Log.Logger).Parse();
        return new SemanticAnalyzer(Log.Logger).Analyze([unit]);
    }

    private CompilationUnit Parse(string source) =>
        new Parser(_tokenizer.Tokenize(source), Log.Logger).Parse();

    private static CallExpression FindCall(Statement statement, string name) =>
        FindCalls(statement).Single(call => call.Name == name);

    private static IEnumerable<CallExpression> FindCalls(Statement statement) => statement switch
    {
        BlockStatement block => block.Statements.SelectMany(FindCalls),
        VariableDeclarationStatement { Initializer: not null } variable => FindCalls(variable.Initializer),
        AssignmentStatement assignment => FindCalls(assignment.Value),
        ReturnStatement { Value: not null } @return => FindCalls(@return.Value),
        ExpressionStatement expression => FindCalls(expression.Expression),
        _ => []
    };

    private static IEnumerable<CallExpression> FindCalls(Expression expression) => expression switch
    {
        CallExpression call => call.Arguments.SelectMany(FindCalls).Prepend(call),
        CastExpression cast => FindCalls(cast.Operand),
        BinaryExpression binary => FindCalls(binary.Left).Concat(FindCalls(binary.Right)),
        _ => []
    };

    private SemanticResult AnalyzeWithDiagnostics(string source)
    {
        var tokenization = _tokenizer.TokenizeWithDiagnostics(source, "main.oxy");
        var parsed = new Parser(tokenization.Tokens, Log.Logger).ParseWithDiagnostics("main.oxy");
        return new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics([parsed.Unit], ["main.oxy"]);
    }
}
