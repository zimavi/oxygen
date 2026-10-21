using System;
using System.Collections.Generic;
using System.Linq;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Parsing;
using Oxygen.Compiler.Semantic;
using Oxygen.Llvm;
using Serilog;
using Xunit;

namespace Oxygen.Compiler.Tests;

// The types in a module's metadata: their layouts on the Windows target, their methods under their native names, and their enums.
public sealed class NativeModuleTypesTests
{
    private const string Program = """
        module program;
        struct Vector { i32 x; i64 y; }
        @value class Point { i32 x; i32 y; public i32 sum() { return x + y; } }
        class Counter { i32 count; public i32 next() { return count; } public static i32 zero() { return 0; } }
        class Box<T> { T item; }
        enum Mode : i32 { Read, Write = 4 }
        @flags enum Access { Read = 1, Write = 2 }
        enum Bits : u64 { Low = 1, High = 9223372036854775808 }
        i32 main() { Box<i32> made = new Box<i32>(); return 0; }
        """;

    [Fact]
    public void AStructIsDescribedWithItsLayout()
    {
        var vector = Assert.Single(Metadata().Types, type => type.Name == "Vector");

        Assert.Equal(NativeTypeKind.Struct, vector.Kind);
        Assert.Equal(16, vector.Size);
        Assert.Equal(8, vector.Alignment);
        Assert.Equal([("x", "i32", 0), ("y", "i64", 8)],
            vector.Fields.Select(field => (field.Name, field.Type, field.Offset)));
    }

    [Fact]
    public void AValueClassHasNoVtableAndItsMethodsAreDescribedUnderNativeNames()
    {
        var point = Assert.Single(Metadata().Types, type => type.Name == "Point");

        Assert.Equal(NativeTypeKind.Class, point.Kind);
        Assert.True(point.IsValue);
        Assert.Equal(8, point.Size);
        Assert.Equal([("x", 0), ("y", 4)], point.Fields.Select(field => (field.Name, field.Offset)));
        var sum = Assert.Single(point.Methods);
        Assert.Equal("sum", sum.SourceName);
        Assert.StartsWith("_Ox_v1_m7program_c5Point_f3sum", sum.NativeName, StringComparison.Ordinal);
        Assert.False(sum.IsStatic);
    }

    [Fact]
    public void AReferenceClassStartsAfterItsVtablePointer()
    {
        var counter = Assert.Single(Metadata().Types, type => type.Name == "Counter");

        Assert.False(counter.IsValue);
        Assert.Equal(16, counter.Size);
        Assert.Equal(8, counter.Alignment);
        Assert.Equal([("count", 8)], counter.Fields.Select(field => (field.Name, field.Offset)));
        Assert.Equal(["next", "zero"], counter.Methods.Select(method => method.SourceName));
        Assert.True(counter.Methods.Single(method => method.SourceName == "zero").IsStatic);
    }

    [Fact]
    public void EnumsCarryTheirUnderlyingTypeCasesAndFlags()
    {
        var mode = Assert.Single(Metadata().Types, type => type.Name == "Mode");
        var access = Assert.Single(Metadata().Types, type => type.Name == "Access");

        Assert.Equal(NativeTypeKind.Enum, mode.Kind);
        Assert.Equal("i32", mode.UnderlyingType);
        Assert.Equal([("Read", "0"), ("Write", "4")], mode.Cases.Select(enumCase => (enumCase.Name, enumCase.Value)));
        Assert.False(mode.IsFlags);
        Assert.True(access.IsFlags);
    }

    [Fact]
    public void AnEnumValueOutsideTheSignedRangeIsDescribedExactly()
    {
        var bits = Assert.Single(Metadata().Types, type => type.Name == "Bits");

        Assert.Equal("u64", bits.UnderlyingType);
        Assert.Equal([("Low", "1"), ("High", "9223372036854775808")],
            bits.Cases.Select(enumCase => (enumCase.Name, enumCase.Value)));
    }

    [Fact]
    public void AnInstantiationOfAGenericClassIsNotAType()
    {
        Assert.DoesNotContain(Metadata().Types, type => type.Name.Contains("__ox_", StringComparison.Ordinal));
        Assert.DoesNotContain(Metadata().Types, type => type.Name == "Box");
    }

    [Fact]
    public void MethodsAreNotListedAsFunctions()
    {
        var functions = Metadata().Functions.Select(function => function.SourceName).ToList();

        Assert.Equal(["main"], functions);
    }

    [Fact]
    public void TheTypesPassTheMetadataValidation()
    {
        var text = NativeModuleMetadataSerializer.Serialize(Metadata());

        Assert.Equal(text, NativeModuleMetadataSerializer.Serialize(NativeModuleMetadataSerializer.Deserialize(text)));
    }

    // The metadata is plain data, so the generated module is disposed once it has been read.
    private static NativeModuleMetadata Metadata()
    {
        var tokens = new Tokenizer(Log.Logger).Tokenize(Program);
        var unit = new Parser(tokens, Log.Logger).Parse();
        var model = new SemanticAnalyzer(Log.Logger).Analyze([unit]);
        using var generated = new LlvmModuleGenerator(OxygenTarget.WindowsMsvcX64).Generate("program", model);
        return generated.Metadata;
    }
}
