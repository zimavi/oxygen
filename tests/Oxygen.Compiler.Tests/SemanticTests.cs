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
        var model = Analyze(
            "class Exception { } class Failure : Exception { } " +
            "i32 main() { try { throw new Failure(); } catch (Failure error) { return 1; } catch { return 2; } }");

        Assert.Contains(model.Classes.Keys, name => name == "Failure");
    }

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
        Assert.Contains(model.Classes["StackTrace"].Constructors,
            constructor => constructor.Parameters.Single().Type.IsClass
                && constructor.Parameters.Single().Type.StructName == "Exception");
        Assert.Contains(model.Classes["Exception"].Constructors,
            constructor => constructor.Parameters.Count == 1
                && constructor.Parameters[0].Type.Kind == SemanticTypeKind.String);
        Assert.Contains(model.Classes["Failure"].Constructors,
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
        var mainUnit = Parse("i32 main() { return 0; }");

        var model = new SemanticAnalyzer(Log.Logger).Analyze([standardUnit, mainUnit]);

        Assert.Contains("Exception", model.Classes.Keys);
        Assert.Contains(model.Functions.Values, symbol =>
            symbol.NativeName == "__oxygen_runtime_exception_trace_count");
        Assert.Contains(model.Classes["Exception"].Constructors,
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
            pair.Key.StartsWith("Option__ox_", StringComparison.Ordinal)).Value;
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
            name.StartsWith("Boxed__ox_", StringComparison.Ordinal));
        Assert.Contains(model.Classes.Keys, name =>
            name.StartsWith("Box__ox_", StringComparison.Ordinal));
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
        Assert.Equal(2, model.Classes.Keys.Count(name => name.StartsWith("Box__ox_", StringComparison.Ordinal)));
        var boxes = model.Classes.Values
            .Where(cls => cls.Name.StartsWith("Box__ox_", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            [SemanticType.Class("Cell__ox_i32"), SemanticType.Class("Cell__ox_i64")],
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
        Assert.Contains("Class:Pair<One<i32>, i64>", model.AggregateSpecializations.Keys);
        Assert.Contains("Class:One<Pair<i32, i64>>", model.AggregateSpecializations.Keys);
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

        Assert.Contains("Cell__ox_i32", model.Classes.Keys);
        var roundTrip = model.Classes["Holder__ox_i32"].Methods.Single(method => method.Name == "roundTrip");
        var read = Assert.Single(model.ResolvedCalls, pair =>
            ReferenceEquals(pair.Key, FindCall(roundTrip.Declaration.Body, "cell.read"))).Value;
        Assert.Equal("program.Cell__ox_i32", read.ModuleName);
        Assert.Equal(SemanticType.Class("Cell__ox_i32"),
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
        var constructor = model.Classes["Box__ox_i64"].Constructors.Single();
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
    public void ReportsErrorsThatOnlyOccurForAConcreteInstantiation()
    {
        var result = new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics(
            [
                Parse("module util; public T negate<T>(T value) { return -value; }"),
                Parse("import util; " +
                    "class Wrapper<T> { T value; Wrapper(T value) { this.value = util.negate<T>(value); } } " +
                    "i32 main() { Wrapper<bool> wrapper = new Wrapper<bool>(true); return 0; }")
            ],
            ["util.oxy", "main.oxy"]);

        // The generic declarations are valid; only `negate<bool>` is not, and the error points at
        // the generic's body in util.oxy rather than at the use in main.oxy.
        var error = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("in instantiation 'util.negate<", error.Message);
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

        Assert.True(model.Classes["Point"].IsValue);
        Assert.Contains(model.ExpressionTypes, pair =>
            pair.Key is ClassConstructionExpression && pair.Value == SemanticType.Class("Point"));
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

        Assert.Single(model.Interfaces["Printable"].Methods);
    }

    [Fact]
    public void ValidatesGenericInterfaceContracts()
    {
        var model = Analyze(
            "interface Iterator<T> { T next(T value); } class Item<T> : Iterator<T> { T next(T value) { return value; } } i32 main() { return 0; }");

        Assert.Single(model.Interfaces["Iterator"].Methods);
    }

    [Fact]
    public void ValidatesGenericInterfaceContractsWithGenericReturnTypes()
    {
        var model = Analyze(
            "union Option<T> { Some(T); None; } interface Iterator<T> { Option<T> next(); } class Item<T> : Iterator<T> { Option<T> next() { return Option<T>.None; } } i32 main() { return 0; }");

        Assert.Single(model.Interfaces["Iterator"].Methods);
    }

    [Fact]
    public void SpecializesGenericInterfaceContractsForConcreteArguments()
    {
        var model = Analyze(
            "interface Producer<T> { T value(); } class Item : Producer<i32> { i32 value() { return 1; } } i32 main() { return 0; }");

        Assert.Single(model.Interfaces["Producer"].Methods);
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
        Assert.Equal("A", call.Parameters[0].Type.StructName);
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
            "T make<T>() { return 0; } i32 main() { i32 result = make(); return result; }");

        Assert.Contains(model.ResolvedCalls.Values, function =>
            function.Name == "make"
            && function.ReturnType == SemanticType.FromPrimitive(PrimitiveType.I32));
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

        Assert.Contains(model.ExpressionTypes.Values, type => type == SemanticType.Class("Child"));
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

        var constructor = model.Classes["Child"].Constructors.Single().Declaration;
        Assert.Equal(1, model.ConstructorChainTargets[constructor]);
    }

    [Fact]
    public void ResolvesThisConstructorAndNewExpressionByArgumentType()
    {
        var model = Analyze(
            "class Item { i32 value; Item() : this(1) { } Item(i32 value) { this.value = value; } " +
            "Item(string text) { value = 2; } } " +
            "i32 main() { Item item = new Item(\"text\"); return item.value; }");

        var parameterless = model.Classes["Item"].Constructors[0].Declaration;
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

        var constructor = model.Classes["Child"].Constructors.Single().Declaration;
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
