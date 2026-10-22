using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Parsing;
using Oxygen.Llvm;
using Serilog;
using Xunit;

namespace Oxygen.Compiler.Tests;

// The version 3 document: the types and the sources of a native module, their order, and the checks that reject a type or a
// source that cannot be used. Version 2 documents are still read, and they have neither.
public sealed class NativeModuleMetadataFormatTests
{
    private const string Triple = "x86_64-pc-windows-msvc";

    [Fact]
    public void TypesAndGenericsRoundTripWithTheirOrderNormalized()
    {
        var metadata = Module(
            Class("Zeta", size: 8, alignment: 8),
            Struct("Point", size: 8, alignment: 4,
                fields: [Field("y", "i32", 4), Field("x", "i32", 0)],
                methods: [Method("_b", "length"), Method("_a", "scale")]))
            with
            {
                Generics = [Generic("lib", "lib.oxy", "struct Box<T> { T item; } i32 plain() { return 0; }")]
            };

        var text = NativeModuleMetadataSerializer.Serialize(metadata);
        var read = NativeModuleMetadataSerializer.Deserialize(text);

        Assert.Equal(text, NativeModuleMetadataSerializer.Serialize(read));
        Assert.Equal(["Point", "Zeta"], read.Types.Select(type => type.Name));
        var point = read.Types[0];
        Assert.Equal(["y", "x"], point.Fields.Select(field => field.Name));
        Assert.Equal(["_a", "_b"], point.Methods.Select(method => method.NativeName));
        var generic = Assert.Single(read.Generics);
        Assert.Equal("lib.oxy", generic.SourceName);
        var unit = SyntaxTreeCodec.Read(generic.Syntax).Unit;
        Assert.Equal(["Box"], unit.Structs.Select(structure => structure.Name));
        Assert.Empty(unit.Functions);
    }

    [Fact]
    public void AVersionTwoDocumentIsReadWithoutTypesOrGenerics()
    {
        var metadata = NativeModuleMetadataSerializer.Deserialize(
            """{"formatVersion":2,"moduleName":"lib","entryPoint":"main","targetTriple":"x86_64-pc-windows-msvc"}""");

        Assert.Empty(metadata.Types);
        Assert.Empty(metadata.Generics);
    }

    // A document without the object layout field was built before the prefix layout, so it has layout 1.
    [Fact]
    public void ADocumentWithoutAnObjectLayoutIsLayoutOne()
    {
        var metadata = NativeModuleMetadataSerializer.Deserialize(
            """{"formatVersion":3,"moduleName":"lib","entryPoint":"main","targetTriple":"x86_64-pc-windows-msvc"}""");

        Assert.Equal(1, metadata.ObjectLayoutVersion);
    }

    [Fact]
    public void TheObjectLayoutRoundTrips()
    {
        var metadata = Module() with { ObjectLayoutVersion = NativeModuleMetadata.CurrentObjectLayoutVersion };

        var read = NativeModuleMetadataSerializer.Deserialize(NativeModuleMetadataSerializer.Serialize(metadata));

        Assert.Equal(NativeModuleMetadata.CurrentObjectLayoutVersion, read.ObjectLayoutVersion);
    }

    [Fact]
    public void AVersionTwoDocumentCannotCarryTypesOrGenerics()
    {
        var metadata = Module(Struct("Point")) with { FormatVersion = NativeModuleMetadata.PreviousFormatVersion };

        var exception = Assert.Throws<InvalidDataException>(() => NativeModuleMetadataSerializer.Serialize(metadata));

        Assert.Contains("cannot contain types or generics", exception.Message);
    }

    [Fact]
    public void DamagedGenericSyntaxIsRejectedWithItsSource()
    {
        var metadata = Module() with
        {
            Generics = [new NativeGenericMetadata { ModuleName = "lib", SourceName = "lib.oxy", Syntax = [1, 2, 3] }]
        };

        var exception = Assert.Throws<InvalidDataException>(() => NativeModuleMetadataSerializer.Serialize(metadata));

        Assert.Contains("'lib.oxy' is damaged", exception.Message);
    }

    [Fact]
    public void GenericSyntaxWithANonGenericDeclarationIsRejected()
    {
        // The whole parsed unit is not generic, so it is not a generic unit.
        var unit = Parse("module lib; i32 plain() { return 0; } struct Box<T> { T item; }");
        var metadata = Module() with
        {
            Generics = [new NativeGenericMetadata
            {
                ModuleName = "lib",
                SourceName = "lib.oxy",
                Syntax = SyntaxTreeCodec.Write(unit, "lib.oxy")
            }]
        };

        var exception = Assert.Throws<InvalidDataException>(() => NativeModuleMetadataSerializer.Serialize(metadata));

        Assert.Contains("contains a declaration that is not generic", exception.Message);
    }

    [Fact]
    public void GenericSyntaxMustNameTheModuleItBelongsTo()
    {
        // The entry says module lib, and the tree it carries declares module other.
        var unit = Parse("struct Box<T> { T item; }") with { ModuleName = "other" };
        var metadata = Module() with
        {
            Generics = [new NativeGenericMetadata
            {
                ModuleName = "lib",
                SourceName = "lib.oxy",
                Syntax = SyntaxTreeCodec.Write(unit, "lib.oxy")
            }]
        };

        var exception = Assert.Throws<InvalidDataException>(() => NativeModuleMetadataSerializer.Serialize(metadata));

        Assert.Contains("does not match the module and source", exception.Message);
    }

    private static CompilationUnit Parse(string source)
    {
        var tokens = new Tokenizer(Log.Logger).Tokenize(source);
        var result = new Parser(tokens, Log.Logger).ParseWithDiagnostics("lib.oxy");
        Assert.Empty(result.Diagnostics);
        return result.Unit;
    }

    // The generic entry for a module that a source declares, as a library build writes it.
    private static NativeGenericMetadata Generic(string moduleName, string sourceName, string source)
    {
        var unit = Parse(source) with { ModuleName = moduleName };
        return NativeGenericMetadata.Create(unit, sourceName);
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
        Cases = (cases ?? ["Read"]).Select(caseName => new NativeEnumCaseMetadata { Name = caseName, Value = "0" }).ToArray()
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
