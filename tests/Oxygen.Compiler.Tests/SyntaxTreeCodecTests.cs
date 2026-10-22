using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Parsing;
using Serilog;
using Xunit;

namespace Oxygen.Compiler.Tests;

// The binary form of a syntax tree. The corpus covers every node kind, so a round trip checks each one, and a test fails when a
// node kind is added without being put in the corpus.
public sealed class SyntaxTreeCodecTests
{
    // Declarations: imports, annotations, enums, structs, unions, interfaces, classes with every member kind, and a generic class.
    private const string Declarations = """
        module shapes;
        import std.memory{alloc, free as release};
        import std.io as io;
        @extern("puts") extern i32 puts(i32 value);
        const i32 limit = 100;
        i32 counter = 10;
        public type Count = u64;
        type Pointer = i32*;
        enum Color : i32 { Red, Green = 4, Blue }
        @flags enum Access { Read = 1, Write = 2 }
        struct Vector { i32 x; i32 y; }
        union Outcome { Ok(i32); Failed(i32); Empty; }
        interface Named { public string Name { get; } i32 size(); }
        class Failure { }
        class Base { public virtual i32 value() { return 1; } }
        class Child : Base, Named {
            const i32 scale = 4;
            i32 position = 0;
            static i32 seen = 0;
            public string Name { get; private set; }
            public i32 Id { get; init; }
            public Child(i32 x) : base(x) { }
            public Child() : this(1) { }
            public ~Child() { position = 0; }
            override public i32 value() { return base.value(); }
            public i32 size() { return this.scale; }
            public static i32 count() { return seen; }
        }
        @value class Point { i32 x; }
        class Box<T : Named> { T item; public T get() { return item; } }
        """;

    // Statements and expressions, in functions with and without parameters, pointers, default and named arguments, and a switch
    // on a union with patterns.
    private const string Statements = """
        i32 scale(i32 x, i32 factor = 2) { return x * factor; }
        void note() { return; }
        fn i32 op(i32, i32) = null;
        fn i32 (i32) make() { return null; }
        i32 main() {
            fn i32 (i32 x) { return x + 1; };
            fn i32 ops[2](i32) = null;
            i32 called = ops[0](1);
            i32 total = 0;
            i32[2] values = { 1, 2 };
            Child child = new Child(1);
            Child made = Child();
            Vector vector = Vector { x: 1, y: 2 };
            Outcome result = Outcome.Ok(42);
            bool flag = true;
            var empty = null;
            var size = sizeof<i32>() + sizeof(total);
            var casted = cast<i64>(total);
            var ratio = 1.5;
            var text = "text";
            i32* pointer = &total;
            *pointer = 4;
            pointer[0] = 5;
            total += values[0];
            total = scale(total, factor: 3);
            total = total > 0 ? total : -total;
            total = child.value() + child.Id + length(values);
            if (child is Child named) { total = total + 1; } else { total = total - 1; }
            if (total == 0 && !flag || total != 1) { note(); }
            for (i32 i = 0; i < length(values); i++) {
                if (i == 1) { continue; }
                if (i == 9) { break; }
            }
            for (var value in values) { total = total + value; }
            while (total > 0) { total--; }
            delete child;
            try { throw new Failure(); } catch (Failure caught) { throw; } catch { total = 2; } finally { total = 1; }
            switch (result) {
                case Ok(number): return number;
                case Failed(code): return code;
                case Empty: return 0;
            }
            switch (total) { case 0: return 10; default: return 12; }
            switch (child) { case is Child: return 2; default: return 0; }
            throw new Failure();
        }
        """;

    private const string SourceName = "shapes.oxy";

    [Fact]
    public void EveryNodeKindRoundTripsToTheSameTree()
    {
        foreach (var unit in Corpus())
        {
            var decoded = SyntaxTreeCodec.Read(SyntaxTreeCodec.Write(unit, SourceName));

            Assert.Equal(SourceName, decoded.SourceName);
            AssertSameTree(unit, decoded.Unit, "unit");
        }
    }

    [Fact]
    public void TheCorpusContainsEveryNodeKind()
    {
        var seen = new HashSet<Type>();
        foreach (var unit in Corpus())
            Collect(unit, seen);

        var kinds = typeof(SyntaxNode).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(SyntaxNode).Namespace && type.IsClass && !type.IsAbstract &&
                           typeof(SyntaxNode).IsAssignableFrom(type))
            .ToList();
        var missing = kinds.Where(kind => !seen.Contains(kind)).Select(kind => kind.Name).ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void AnotherFormatVersionIsRejected()
    {
        var bytes = SyntaxTreeCodec.Write(Parse("i32 main() { return 0; }"), SourceName);
        bytes[6] = 99;

        var exception = Assert.Throws<InvalidDataException>(() => SyntaxTreeCodec.Read(bytes));

        Assert.Contains("Unsupported syntax tree format version", exception.Message);
    }

    [Fact]
    public void ATruncatedTreeIsInvalidData()
    {
        var bytes = SyntaxTreeCodec.Write(Parse(Statements), SourceName);
        var truncated = bytes.AsSpan(0, bytes.Length / 2).ToArray();

        var exception = Assert.Throws<InvalidDataException>(() => SyntaxTreeCodec.Read(truncated));

        Assert.Contains("syntax tree", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BytesThatAreNotATreeAreInvalidData()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            SyntaxTreeCodec.Read("not a syntax tree at all"u8));

        Assert.Contains("not an Oxygen syntax tree", exception.Message);
    }

    // The parser does not build length(...) or union construction nodes: the analyzer builds them. They are built here by hand,
    // so the codec still covers them.
    private static CompilationUnit[] Corpus() => [Parse(Declarations), Parse(Statements), HandBuilt()];

    private static CompilationUnit HandBuilt()
    {
        var at = new SourceLocation(1, 1);
        var length = new LengthExpression(new IdentifierExpression("values", at), at);
        var construction = new UnionConstructionExpression("Outcome", "Ok", [new IntegerLiteralExpression("42", at)], at);
        var sum = new BinaryExpression(length, TokenKind.Plus, construction, at);
        var function = new FunctionDeclaration(new TypeSyntax(PrimitiveType.I32, false, at), "built", [],
            new BlockStatement([new ReturnStatement(sum, at)], at), Visibility.Public, at);
        return new CompilationUnit(null, Visibility.Private, [], [function], at);
    }

    private static CompilationUnit Parse(string source)
    {
        var tokens = new Tokenizer(Log.Logger).Tokenize(source);
        var result = new Parser(tokens, Log.Logger).ParseWithDiagnostics("corpus.oxy");
        Assert.Empty(result.Diagnostics);
        return result.Unit;
    }

    // Walks every syntax node and every value that a node holds, and records the node types it finds.
    private static void Collect(object? value, HashSet<Type> seen)
    {
        if (value is null || value is string || value is not (SyntaxNode or IEnumerable or StructLiteralField))
            return;
        if (value is SyntaxNode)
            seen.Add(value.GetType());
        if (value is IEnumerable items and not string)
        {
            foreach (var item in items)
                Collect(item, seen);
            return;
        }
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0)
                Collect(property.GetValue(value), seen);
        }
    }

    // Compares two trees by their public properties, so a field that the codec drops or reorders fails the test.
    private static void AssertSameTree(object? expected, object? actual, string path)
    {
        if (expected is null || actual is null)
        {
            Assert.True(expected is null && actual is null, $"{path}: one side is null");
            return;
        }
        // Lists are compared by their items. The parser builds some as List<T> and some as arrays, and the codec gives arrays.
        if (expected is IEnumerable expectedItems and not string)
        {
            var expectedList = expectedItems.Cast<object?>().ToList();
            var actualList = ((IEnumerable)actual).Cast<object?>().ToList();
            Assert.Equal(expectedList.Count, actualList.Count);
            for (var index = 0; index < expectedList.Count; index++)
                AssertSameTree(expectedList[index], actualList[index], $"{path}[{index}]");
            return;
        }
        Assert.True(expected.GetType() == actual.GetType(), $"{path}: {expected.GetType().Name} became {actual.GetType().Name}");
        if (expected is string or BigInteger || expected.GetType().IsPrimitive || expected.GetType().IsEnum)
        {
            Assert.Equal(expected, actual);
            return;
        }
        foreach (var property in expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0)
                AssertSameTree(property.GetValue(expected), property.GetValue(actual), $"{path}.{property.Name}");
        }
    }
}
