using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;
using Oxygen.Llvm;
using Xunit;

namespace Oxygen.Cli.Tests;

// Reads the Oxygen metadata of native libraries: the type encodings, the embedded copy, the sidecar, and the target check.
public sealed class NativeModuleLoaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public NativeModuleLoaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("i32", PrimitiveType.I32)]
    [InlineData("us", PrimitiveType.USize)]
    [InlineData("is", PrimitiveType.ISize)]
    [InlineData("b", PrimitiveType.Bool)]
    [InlineData("v", PrimitiveType.Void)]
    public void DecodesPrimitiveTypes(string encoding, PrimitiveType primitive)
    {
        Assert.Equal(SemanticType.FromPrimitive(primitive), NativeModuleLoader.DecodeType(encoding));
    }

    [Fact]
    public void DecodesStringPointersAndArrays()
    {
        Assert.Equal(SemanticType.String, NativeModuleLoader.DecodeType("str"));
        Assert.Equal(SemanticType.PointerTo(SemanticType.FromPrimitive(PrimitiveType.I32)),
            NativeModuleLoader.DecodeType("pi32"));
        Assert.Equal(SemanticType.ArrayOf(SemanticType.FromPrimitive(PrimitiveType.U8), 3),
            NativeModuleLoader.DecodeType("a3_u8"));
        Assert.Equal(SemanticType.PointerTo(SemanticType.FromPrimitive(PrimitiveType.Void)),
            NativeModuleLoader.DecodeType("pv"));
    }

    [Theory]
    [InlineData("S5Point")]
    [InlineData("C3Box_a1_i32")]
    [InlineData("E4Mode")]
    public void NamedTypesAreUnsupportedBecauseMetadataDoesNotDescribeThem(string encoding)
    {
        Assert.Throws<NativeModuleLoader.UnsupportedNativeTypeException>(() => NativeModuleLoader.DecodeType(encoding));
    }

    [Theory]
    [InlineData("")]
    [InlineData("q32")]
    [InlineData("i32x")]
    [InlineData("a3i32")]
    public void MalformedEncodingsAreInvalidData(string encoding)
    {
        Assert.Throws<InvalidDataException>(() => NativeModuleLoader.DecodeType(encoding));
    }

    [Fact]
    public void AnEmbeddedCopyIsReadWhenThereIsNoSidecar()
    {
        var library = Path.Combine(_directory, "lib.a");
        File.WriteAllBytes(library, [1, 2, 3, 4]);
        NativeModuleMetadataSerializer.Embed(library, Metadata("lib", OxygenTarget.WindowsMsvcX64.Triple, [Function("add", "i32")]));

        var loaded = Assert.Single(NativeModuleLoader.Load(new FileInfo(library), OxygenTarget.WindowsMsvcX64));

        Assert.Equal("lib", loaded.Module.ModuleName);
        Assert.Equal("add", Assert.Single(loaded.Module.Functions).Name);
        Assert.Empty(loaded.Warnings);
        Assert.False(File.Exists(library + ".oxmeta"));
    }

    [Fact]
    public void AFunctionWithANamedTypeIsSkippedWithAWarningAndTheRestAreImported()
    {
        var library = Path.Combine(_directory, "lib.a");
        File.WriteAllBytes(library, [1, 2, 3, 4]);
        NativeModuleMetadataSerializer.Write(library + ".oxmeta", Metadata("lib", OxygenTarget.WindowsMsvcX64.Triple,
            [Function("add", "i32", "i32"), Function("move", "v", "S5Point")]));

        var loaded = Assert.Single(NativeModuleLoader.Load(new FileInfo(library), OxygenTarget.WindowsMsvcX64));

        Assert.Equal("add", Assert.Single(loaded.Module.Functions).Name);
        var warning = Assert.Single(loaded.Warnings);
        Assert.Contains("'move'", warning);
        Assert.Contains("is not imported", warning);
    }

    [Fact]
    public void MetadataForAnotherTargetIsRejected()
    {
        var library = Path.Combine(_directory, "lib.a");
        File.WriteAllBytes(library, [1, 2, 3, 4]);
        NativeModuleMetadataSerializer.Embed(library, Metadata("lib", OxygenTarget.LinuxGnuX64.Triple, [Function("add", "i32")]));

        var exception = Assert.Throws<InvalidDataException>(() =>
            NativeModuleLoader.Load(new FileInfo(library), OxygenTarget.WindowsMsvcX64));

        Assert.Contains("was built for 'x86_64-unknown-linux-gnu'", exception.Message);
    }

    [Fact]
    public void ALibraryWithoutMetadataIsOpaque()
    {
        var library = Path.Combine(_directory, "plain.a");
        File.WriteAllBytes(library, [1, 2, 3, 4]);

        Assert.Empty(NativeModuleLoader.Load(new FileInfo(library), OxygenTarget.WindowsMsvcX64));
    }

    // A library built with another object layout cannot be used: its classes would be read at the wrong places.
    [Fact]
    public void MetadataWithAnotherObjectLayoutIsRejected()
    {
        var library = Path.Combine(_directory, "lib.a");
        File.WriteAllBytes(library, [1, 2, 3, 4]);
        NativeModuleMetadataSerializer.Embed(library,
            Metadata("lib", OxygenTarget.WindowsMsvcX64.Triple, [Function("add", "i32")]) with { ObjectLayoutVersion = 1 });

        var exception = Assert.Throws<InvalidDataException>(() =>
            NativeModuleLoader.Load(new FileInfo(library), OxygenTarget.WindowsMsvcX64));

        Assert.Contains("object layout 1", exception.Message);
        Assert.Contains("Rebuild the library", exception.Message);
    }

    // An archive that an older build appended metadata to: the section reader stops at the appended record, so the metadata is read.
    [Fact]
    public void AnArchiveWithAppendedMetadataGivesItsModule()
    {
        var library = Path.Combine(_directory, "lib.a");
        File.WriteAllBytes(library, "!<arch>\n"u8.ToArray());
        NativeModuleMetadataSerializer.Embed(library, Metadata("lib", OxygenTarget.WindowsMsvcX64.Triple, [Function("add", "i32")]));

        var loaded = Assert.Single(NativeModuleLoader.Load(new FileInfo(library), OxygenTarget.WindowsMsvcX64));

        Assert.Equal("add", Assert.Single(loaded.Module.Functions).Name);
    }

    // Types and generics are read and checked, but the loader does not use them yet, so it reports them rather than dropping them.
    [Fact]
    public void TypesAndGenericsAreNotImportedYetAndTheLibrarySaysSo()
    {
        var library = Path.Combine(_directory, "lib.a");
        File.WriteAllBytes(library, [1, 2, 3, 4]);
        var at = new SourceLocation(1, 1);
        var box = new StructDeclaration("Box", [], Visibility.Public, at,
            TypeParameters: [new TypeParameterSyntax("T", at)]);
        var generic = NativeGenericMetadata.Create(
            new CompilationUnit("lib", Visibility.Public, [], [], at) { Structs = [box] }, "lib.oxy");
        var metadata = Metadata("lib", OxygenTarget.WindowsMsvcX64.Triple, [Function("add", "i32")]) with
        {
            Types = [new NativeTypeMetadata { Kind = NativeTypeKind.Struct, ModuleName = "lib", Name = "Point" }],
            Generics = [generic]
        };
        NativeModuleMetadataSerializer.Embed(library, metadata);

        var loaded = Assert.Single(NativeModuleLoader.Load(new FileInfo(library), OxygenTarget.WindowsMsvcX64));

        Assert.Equal("add", Assert.Single(loaded.Module.Functions).Name);
        Assert.Contains(loaded.Notes, note => note.Contains("declares 1 type(s)"));
        // The generic struct is imported with the unit that declares it, so no note says that it is left out.
        Assert.DoesNotContain(loaded.Notes, note => note.Contains("generic struct"));
        Assert.Single(loaded.GenericUnits);
        Assert.Equal(["Box"], loaded.GenericUnits[0].Unit.Structs.Select(structure => structure.Name));
        Assert.Empty(loaded.Warnings);
    }

    private static NativeModuleMetadata Metadata(string moduleName, string triple, NativeFunctionMetadata[] functions) => new()
    {
        ModuleName = moduleName,
        EntryPoint = "main",
        TargetTriple = triple,
        ObjectLayoutVersion = NativeModuleMetadata.CurrentObjectLayoutVersion,
        Functions = functions
    };

    private static NativeFunctionMetadata Function(string name, string returnType, params string[] parameterTypes) => new()
    {
        ModuleName = "lib",
        SourceName = name,
        NativeName = "_Ox_v1_m3lib_f" + name.Length + name,
        ReturnType = returnType,
        ParameterTypes = parameterTypes,
        Visibility = "public"
    };
}
