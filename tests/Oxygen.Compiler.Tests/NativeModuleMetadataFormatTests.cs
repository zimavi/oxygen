using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Oxygen.Llvm;
using Xunit;

namespace Oxygen.Compiler.Tests;

// The version 3 document: the types and the sources of a native module, their order, and the checks that reject a type or a
// source that cannot be used. Version 2 documents are still read, and they have neither.
public sealed class NativeModuleMetadataFormatTests
{
    private const string Triple = "x86_64-pc-windows-msvc";

    [Fact]
    public void TypesAndSourcesRoundTripWithTheirOrderNormalized()
    {
        var metadata = Module(
            Class("Zeta", size: 8, alignment: 8),
            Struct("Point", size: 8, alignment: 4,
                fields: [Field("y", "i32", 4), Field("x", "i32", 0)],
                methods: [Method("_b", "length"), Method("_a", "scale")]))
            with
            {
                Sources = [NativeSourceMetadata.Create("lib", "lib.oxy", "i32 answer() { return 42; }")]
            };

        var text = NativeModuleMetadataSerializer.Serialize(metadata);
        var read = NativeModuleMetadataSerializer.Deserialize(text);

        Assert.Equal(text, NativeModuleMetadataSerializer.Serialize(read));
        Assert.Equal(["Point", "Zeta"], read.Types.Select(type => type.Name));
        var point = read.Types[0];
        Assert.Equal(["y", "x"], point.Fields.Select(field => field.Name));
        Assert.Equal(["_a", "_b"], point.Methods.Select(method => method.NativeName));
        Assert.Equal("i32 answer() { return 42; }", read.Sources.Single().Text);
    }

    [Fact]
    public void AVersionTwoDocumentIsReadWithoutTypesOrSources()
    {
        var metadata = NativeModuleMetadataSerializer.Deserialize(
            """{"formatVersion":2,"moduleName":"lib","entryPoint":"main","targetTriple":"x86_64-pc-windows-msvc"}""");

        Assert.Empty(metadata.Types);
        Assert.Empty(metadata.Sources);
    }

    [Fact]
    public void AVersionTwoDocumentCannotCarryTypesOrSources()
    {
        var metadata = Module(Struct("Point")) with { FormatVersion = NativeModuleMetadata.PreviousFormatVersion };

        var exception = Assert.Throws<InvalidDataException>(() => NativeModuleMetadataSerializer.Serialize(metadata));

        Assert.Contains("cannot contain types or sources", exception.Message);
    }

    [Fact]
    public void AClassAlignmentMustBeAPowerOfTwo()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            NativeModuleMetadataSerializer.Serialize(Module(Class("Box", size: 12, alignment: 3))));

        Assert.Contains("not a power of two", exception.Message);
    }

    [Fact]
    public void ASizeMustBeAMultipleOfTheAlignment()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            NativeModuleMetadataSerializer.Serialize(Module(Struct("Pair", size: 6, alignment: 4))));

        Assert.Contains("not a multiple of its alignment", exception.Message);
    }

    [Fact]
    public void AFieldOutsideTheTypeIsRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            NativeModuleMetadataSerializer.Serialize(Module(
                Struct("Pair", size: 4, alignment: 4, fields: [Field("second", "i32", 8)]))));

        Assert.Contains("outside the type", exception.Message);
    }

    [Fact]
    public void OnlyAClassCanBeAValueType()
    {
        var value = Struct("Pair", size: 4, alignment: 4) with { IsValue = true };

        var exception = Assert.Throws<InvalidDataException>(() => NativeModuleMetadataSerializer.Serialize(Module(value)));

        Assert.Contains("only a class can be", exception.Message);
    }

    [Fact]
    public void AnEnumNeedsAnUnderlyingTypeAndCases()
    {
        var withoutCases = Enum("Mode", underlying: "i32") with { Cases = [] };
        var withoutUnderlying = Enum("Mode", underlying: null, cases: ["Read"]);

        Assert.Contains("has no cases",
            Assert.Throws<InvalidDataException>(() => NativeModuleMetadataSerializer.Serialize(Module(withoutCases))).Message);
        Assert.Contains("must specify its underlying type",
            Assert.Throws<InvalidDataException>(() => NativeModuleMetadataSerializer.Serialize(Module(withoutUnderlying))).Message);
    }

    [Fact]
    public void AnEnumCaseIsDeclaredOnce()
    {
        var metadata = Module(Enum("Mode", underlying: "i32", cases: ["Read", "Read"]));

        var exception = Assert.Throws<InvalidDataException>(() => NativeModuleMetadataSerializer.Serialize(metadata));

        Assert.Contains("'Read' more than once", exception.Message);
    }

    [Fact]
    public void ATypeIsDeclaredOnce()
    {
        var metadata = Module(Struct("Point"), Struct("Point"));

        var exception = Assert.Throws<InvalidDataException>(() => NativeModuleMetadataSerializer.Serialize(metadata));

        Assert.Contains("'lib.Point' more than once", exception.Message);
    }

    [Fact]
    public void AMethodAndAFunctionCannotShareASymbol()
    {
        var metadata = Module(Struct("Point", methods: [Method("_Ox_v1_m3lib_f3run_p0_", "run")])) with
        {
            Functions = [new NativeFunctionMetadata
            {
                ModuleName = "lib",
                SourceName = "run",
                NativeName = "_Ox_v1_m3lib_f3run_p0_",
                ReturnType = "v"
            }]
        };

        var exception = Assert.Throws<InvalidDataException>(() => NativeModuleMetadataSerializer.Serialize(metadata));

        Assert.Contains("more than once", exception.Message);
    }

    [Fact]
    public void ASourceChangedAfterItWasHashedIsRejected()
    {
        var metadata = Module() with
        {
            Sources = [NativeSourceMetadata.Create("lib", "lib.oxy", "i32 answer() { return 42; }")]
        };
        var text = NativeModuleMetadataSerializer.Serialize(metadata).Replace("return 42", "return 43");

        var exception = Assert.Throws<InvalidDataException>(() => NativeModuleMetadataSerializer.Deserialize(text));

        Assert.Contains("does not match its hash", exception.Message);
    }

    private static NativeModuleMetadata Module(params NativeTypeMetadata[] types) => new()
    {
        ModuleName = "lib",
        EntryPoint = "main",
        TargetTriple = Triple,
        Types = types
    };

    private static NativeTypeMetadata Class(string name, int size = 0, int alignment = 1) => new()
    {
        Kind = NativeTypeKind.Class,
        ModuleName = "lib",
        Name = name,
        Size = size,
        Alignment = alignment
    };

    private static NativeTypeMetadata Struct(
        string name,
        int size = 0,
        int alignment = 1,
        IReadOnlyList<NativeFieldMetadata>? fields = null,
        IReadOnlyList<NativeMethodMetadata>? methods = null) => new()
    {
        Kind = NativeTypeKind.Struct,
        ModuleName = "lib",
        Name = name,
        Size = size,
        Alignment = alignment,
        Fields = fields ?? [],
        Methods = methods ?? []
    };

    private static NativeTypeMetadata Enum(string name, string? underlying, IReadOnlyList<string>? cases = null) => new()
    {
        Kind = NativeTypeKind.Enum,
        ModuleName = "lib",
        Name = name,
        UnderlyingType = underlying,
        Cases = (cases ?? ["Read"]).Select(caseName => new NativeEnumCaseMetadata { Name = caseName }).ToArray()
    };

    private static NativeFieldMetadata Field(string name, string type, int offset) => new()
    {
        Name = name,
        Type = type,
        Offset = offset
    };

    private static NativeMethodMetadata Method(string nativeName, string sourceName) => new()
    {
        SourceName = sourceName,
        NativeName = nativeName,
        ReturnType = "i32"
    };
}
