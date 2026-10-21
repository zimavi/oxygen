using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Oxygen.Compiler.Ast;

namespace Oxygen.Llvm;

public sealed record NativeModuleMetadata
{
    // Version 2 names symbols with version 1 of the mangling scheme (_Ox_v1_, see docs/mangling.md). Version 1 metadata names
    // symbols that this compiler does not produce, so it is rejected. Version 3 adds the types and the generics of the module.
    // Version 2 documents have neither, so they are read as having none.
    public const int CurrentFormatVersion = 3;
    public const int PreviousFormatVersion = 2;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    // Every field that names the program is required, so no default such as "program" can stand in for the real name.
    public required string ModuleName { get; init; }
    public required string EntryPoint { get; init; }
    // The target that the native module was built for. A module can only be linked into a program for the same target.
    public required string TargetTriple { get; init; }
    public IReadOnlyList<NativeFunctionMetadata> Functions { get; init; } = [];
    public IReadOnlyList<NativeDependencyMetadata> Dependencies { get; init; } = [];
    // Version 3: the types that an importing program can use. Only the declarations are here; their code is in the module.
    public IReadOnlyList<NativeTypeMetadata> Types { get; init; } = [];
    // Version 3: the generic declarations of the module, in the binary syntax form, which an importing program instantiates.
    public IReadOnlyList<NativeGenericMetadata> Generics { get; init; } = [];
}

public sealed record NativeFunctionMetadata
{
    public required string ModuleName { get; init; }
    public required string SourceName { get; init; }
    public required string NativeName { get; init; }
    public required string ReturnType { get; init; }
    public string Visibility { get; init; } = "private";
    public IReadOnlyList<string> ParameterTypes { get; init; } = [];
    public string? ExternalName { get; init; }
    public string CallingConvention { get; init; } = "c";
}

public sealed record NativeDependencyMetadata
{
    public required string Path { get; init; }
    public required NativeDependencyKind Kind { get; init; }
}

public enum NativeTypeKind
{
    Class,
    Struct,
    Enum
}

// A type of a native module, described with its layout so that an importing program can use it without compiling the module.
// Each type is identified by its module and name, and its encoding in a signature is the one in docs/mangling.md. Fields keep
// their declaration order, since it decides the layout. Kind-specific parts are validated for that kind only (see the serializer).
public sealed record NativeTypeMetadata
{
    public required NativeTypeKind Kind { get; init; }
    public required string ModuleName { get; init; }
    public required string Name { get; init; }
    public string Visibility { get; init; } = "private";
    // The size and alignment in bytes of a class or struct. The size is a multiple of the alignment, and the alignment is a power of two.
    public int Size { get; init; }
    public int Alignment { get; init; } = 1;
    // Classes only: a @value class is stored inline, and other classes are references to heap objects.
    public bool IsValue { get; init; }
    // Enums only: the underlying type's encoding, whether the enum is a @flags enum, and its cases in declaration order.
    public string? UnderlyingType { get; init; }
    public bool IsFlags { get; init; }
    public IReadOnlyList<NativeEnumCaseMetadata> Cases { get; init; } = [];
    public IReadOnlyList<NativeFieldMetadata> Fields { get; init; } = [];
    public IReadOnlyList<NativeMethodMetadata> Methods { get; init; } = [];
}

public sealed record NativeFieldMetadata
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    // The byte offset of the field from the start of the type.
    public int Offset { get; init; }
}

public sealed record NativeEnumCaseMetadata
{
    public required string Name { get; init; }
    // The value in decimal, with an optional minus sign. Text holds any integer of the enum's underlying type, including 64-bit values
    // that a JSON number could not represent exactly.
    public required string Value { get; init; }
}

public sealed record NativeMethodMetadata
{
    public required string SourceName { get; init; }
    public required string NativeName { get; init; }
    public required string ReturnType { get; init; }
    public string Visibility { get; init; } = "private";
    public bool IsStatic { get; init; }
    public IReadOnlyList<string> ParameterTypes { get; init; } = [];
}

// The generic declarations of a module: a unit in the binary form of SyntaxTreeCodec, which holds only generic declarations,
// its imports and its module header. The form has no source text, comments or formatting.
public sealed record NativeGenericMetadata
{
    public required string ModuleName { get; init; }
    // The name of the source the declarations were parsed from, which a diagnostic for them shows.
    public required string SourceName { get; init; }
    public required byte[] Syntax { get; init; }

    // The generic declarations of a parsed unit, with the unit's module name. The unit must have a module header.
    public static NativeGenericMetadata Create(CompilationUnit unit, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.ModuleName is null)
            throw new ArgumentException("A generic unit must declare its module.", nameof(unit));
        var generic = GenericSyntax.Extract(unit);
        return new NativeGenericMetadata
        {
            ModuleName = unit.ModuleName,
            SourceName = sourceName,
            Syntax = SyntaxTreeCodec.Write(generic, sourceName)
        };
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(NativeModuleMetadata))]
[JsonSerializable(typeof(NativeFunctionMetadata))]
[JsonSerializable(typeof(NativeDependencyMetadata))]
[JsonSerializable(typeof(NativeTypeMetadata))]
[JsonSerializable(typeof(NativeFieldMetadata))]
[JsonSerializable(typeof(NativeEnumCaseMetadata))]
[JsonSerializable(typeof(NativeMethodMetadata))]
[JsonSerializable(typeof(NativeGenericMetadata))]
[JsonSerializable(typeof(string))]
internal partial class NativeModuleMetadataJsonContext : JsonSerializerContext;

public static class NativeModuleMetadataSerializer
{
    private static readonly byte[] EmbeddedMagic = "OXYGENMETA1"u8.ToArray();

    public static string Serialize(NativeModuleMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        Validate(metadata);

        // Normalize collections before serialization so equivalent metadata has byte-identical output.
        var normalized = metadata with
        {
            Functions = metadata.Functions
                .OrderBy(function => function.NativeName, StringComparer.Ordinal)
                .ThenBy(function => function.SourceName, StringComparer.Ordinal)
                .ToArray(),
            Dependencies = metadata.Dependencies
                .OrderBy(dependency => dependency.Kind)
                .ThenBy(dependency => dependency.Path, StringComparer.Ordinal)
                .ToArray(),
            Types = metadata.Types
                .OrderBy(type => type.ModuleName, StringComparer.Ordinal)
                .ThenBy(type => type.Name, StringComparer.Ordinal)
                .Select(type => type with
                {
                    // Methods are ordered by symbol. Fields and enum cases keep their order, since it is part of the type.
                    Methods = type.Methods.OrderBy(method => method.NativeName, StringComparer.Ordinal).ToArray()
                })
                .ToArray(),
            Generics = metadata.Generics
                .OrderBy(generic => generic.ModuleName, StringComparer.Ordinal)
                .ThenBy(generic => generic.SourceName, StringComparer.Ordinal)
                .ToArray()
        };
        return JsonSerializer.Serialize(
            normalized,
            NativeModuleMetadataJsonContext.Default.NativeModuleMetadata) + Environment.NewLine;
    }

    public static NativeModuleMetadata Deserialize(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var read = JsonSerializer.Deserialize(
                contents,
                NativeModuleMetadataJsonContext.Default.NativeModuleMetadata)
            ?? throw new InvalidDataException("The native metadata document is empty.");
        // An array that the document does not have is read as null, not as the empty default, so the arrays are made empty here.
        // Version 2 documents have no types or generics at all.
        var metadata = read with
        {
            Functions = read.Functions ?? [],
            Dependencies = read.Dependencies ?? [],
            Types = read.Types ?? [],
            Generics = read.Generics ?? []
        };
        Validate(metadata);
        return metadata;
    }

    public static void Write(string path, NativeModuleMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(fullPath, Serialize(metadata), new UTF8Encoding(false));
    }

    public static NativeModuleMetadata Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Deserialize(File.ReadAllText(path, Encoding.UTF8));
    }

    public static void Embed(string artifactPath, NativeModuleMetadata metadata)
    {
        var fullPath = Path.GetFullPath(artifactPath);
        var json = Encoding.UTF8.GetBytes(Serialize(metadata));
        using var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(EmbeddedMagic);
        stream.Write(BitConverter.GetBytes(json.Length));
        stream.Write(json);
    }

    public static NativeModuleMetadata? ReadEmbedded(string artifactPath)
    {
        var bytes = File.ReadAllBytes(Path.GetFullPath(artifactPath));
        var marker = LastIndexOf(bytes, EmbeddedMagic);
        if (marker < 0 || marker + EmbeddedMagic.Length + sizeof(int) > bytes.Length)
            return null;
        var length = BitConverter.ToInt32(bytes, marker + EmbeddedMagic.Length);
        var start = marker + EmbeddedMagic.Length + sizeof(int);
        if (length < 0 || start + length > bytes.Length)
            throw new InvalidDataException("Embedded Oxygen metadata has an invalid length.");
        return Deserialize(Encoding.UTF8.GetString(bytes, start, length));
    }

    public static NativeModuleMetadata ReadArtifact(string artifactPath)
    {
        var embedded = ReadEmbedded(artifactPath);
        if (embedded is not null)
            return embedded;
        var sidecar = artifactPath.EndsWith(".oxmeta", StringComparison.OrdinalIgnoreCase)
            ? artifactPath
            : artifactPath + ".oxmeta";
        return Read(sidecar);
    }

    private static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (var index = haystack.Length - needle.Length; index >= 0; index--)
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
                return index;
        return -1;
    }

    private static void Validate(NativeModuleMetadata metadata)
    {
        if (metadata.FormatVersion is not (NativeModuleMetadata.PreviousFormatVersion or NativeModuleMetadata.CurrentFormatVersion))
            throw new InvalidDataException(
                $"Unsupported native metadata format version {metadata.FormatVersion}; " +
                $"this compiler supports versions {NativeModuleMetadata.PreviousFormatVersion} and {NativeModuleMetadata.CurrentFormatVersion}.");
        if (metadata.FormatVersion == NativeModuleMetadata.PreviousFormatVersion &&
            (metadata.Types.Count > 0 || metadata.Generics.Count > 0))
            throw new InvalidDataException("Native metadata of format version 2 cannot contain types or generics.");
        if (string.IsNullOrWhiteSpace(metadata.ModuleName))
            throw new InvalidDataException("Native metadata must specify a module name.");
        if (string.IsNullOrWhiteSpace(metadata.EntryPoint))
            throw new InvalidDataException("Native metadata must specify an entry point.");
        if (string.IsNullOrWhiteSpace(metadata.TargetTriple))
            throw new InvalidDataException("Native metadata must specify a target triple.");
        if (metadata.Functions.Any(function =>
                string.IsNullOrWhiteSpace(function.SourceName) ||
                string.IsNullOrWhiteSpace(function.NativeName) ||
                string.IsNullOrWhiteSpace(function.ReturnType)))
            throw new InvalidDataException("Native metadata contains an incomplete function.");
        if (metadata.Dependencies.Any(dependency => string.IsNullOrWhiteSpace(dependency.Path)))
            throw new InvalidDataException("Native metadata contains a dependency without a path.");

        // Functions and methods share the module's symbols, so each native name appears once.
        var symbols = metadata.Functions.Select(function => function.NativeName)
            .Concat(metadata.Types.SelectMany(type => type.Methods.Select(method => method.NativeName)));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (!seen.Add(symbol))
                throw new InvalidDataException($"Native metadata names the symbol '{symbol}' more than once.");
        }

        ValidateTypes(metadata.Types);
        ValidateGenerics(metadata.Generics);
    }

    private static void ValidateTypes(IReadOnlyList<NativeTypeMetadata> types)
    {
        var names = new HashSet<(string, string)>();
        foreach (var type in types)
        {
            if (string.IsNullOrWhiteSpace(type.ModuleName) || string.IsNullOrWhiteSpace(type.Name))
                throw new InvalidDataException("Native metadata contains a type without a module or a name.");
            if (!names.Add((type.ModuleName, type.Name)))
                throw new InvalidDataException($"Native metadata declares the type '{type.ModuleName}.{type.Name}' more than once.");

            if (type.Kind == NativeTypeKind.Enum)
                ValidateEnum(type);
            else
                ValidateLayout(type);
        }
    }

    // A class or struct has a size and an alignment, fields and methods. Only a class can be @value.
    private static void ValidateLayout(NativeTypeMetadata type)
    {
        var where = $"'{type.ModuleName}.{type.Name}'";
        if (type.Cases.Count > 0 || type.UnderlyingType is not null || type.IsFlags)
            throw new InvalidDataException($"The type {where} is not an enum, so it cannot have cases, an underlying type or flags.");
        if (type.IsValue && type.Kind != NativeTypeKind.Class)
            throw new InvalidDataException($"The type {where} is marked @value, but only a class can be.");
        if (type.Alignment < 1 || (type.Alignment & (type.Alignment - 1)) != 0)
            throw new InvalidDataException($"The type {where} has an alignment of {type.Alignment}, which is not a power of two.");
        if (type.Size < 0 || type.Size % type.Alignment != 0)
            throw new InvalidDataException($"The type {where} has a size of {type.Size}, which is not a multiple of its alignment.");

        var fieldNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in type.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || string.IsNullOrWhiteSpace(field.Type))
                throw new InvalidDataException($"The type {where} contains a field without a name or a type.");
            if (!fieldNames.Add(field.Name))
                throw new InvalidDataException($"The type {where} declares the field '{field.Name}' more than once.");
            if (field.Offset < 0 || field.Offset > type.Size)
                throw new InvalidDataException($"The field '{field.Name}' of {where} is outside the type.");
        }

        foreach (var method in type.Methods)
            ValidateMethod(method, where);
    }

    private static void ValidateEnum(NativeTypeMetadata type)
    {
        var where = $"'{type.ModuleName}.{type.Name}'";
        if (type.Fields.Count > 0 || type.Methods.Count > 0 || type.IsValue || type.Size != 0 || type.Alignment != 1)
            throw new InvalidDataException($"The enum {where} has fields, methods, a layout or @value, which enums do not have.");
        if (string.IsNullOrWhiteSpace(type.UnderlyingType))
            throw new InvalidDataException($"The enum {where} must specify its underlying type.");
        if (type.Cases.Count == 0)
            throw new InvalidDataException($"The enum {where} has no cases.");

        var caseNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var enumCase in type.Cases)
        {
            if (string.IsNullOrWhiteSpace(enumCase.Name))
                throw new InvalidDataException($"The enum {where} contains a case without a name.");
            if (!caseNames.Add(enumCase.Name))
                throw new InvalidDataException($"The enum {where} declares the case '{enumCase.Name}' more than once.");
            if (!BigInteger.TryParse(enumCase.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException(
                    $"The case '{enumCase.Name}' of enum {where} has the value '{enumCase.Value}', which is not an integer.");
        }
    }

    private static void ValidateMethod(NativeMethodMetadata method, string where)
    {
        if (string.IsNullOrWhiteSpace(method.SourceName) ||
            string.IsNullOrWhiteSpace(method.NativeName) ||
            string.IsNullOrWhiteSpace(method.ReturnType) ||
            method.ParameterTypes.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException($"The type {where} contains an incomplete method.");
    }

    // Each entry must decode, must hold only generic declarations, and must name the module and the source that the entry names.
    // A damaged entry is reported with the source it came from.
    private static void ValidateGenerics(IReadOnlyList<NativeGenericMetadata> generics)
    {
        var files = new HashSet<(string, string)>();
        foreach (var generic in generics)
        {
            if (string.IsNullOrWhiteSpace(generic.ModuleName) || string.IsNullOrWhiteSpace(generic.SourceName) || generic.Syntax is null)
                throw new InvalidDataException("Native metadata contains generic syntax without a module, a source name or a tree.");
            if (!files.Add((generic.ModuleName, generic.SourceName)))
                throw new InvalidDataException($"Native metadata contains the generic syntax of '{generic.SourceName}' more than once.");

            DecodedUnit decoded;
            try
            {
                decoded = SyntaxTreeCodec.Read(generic.Syntax);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    $"The generic syntax of '{generic.SourceName}' is damaged: {exception.Message}", exception);
            }
            if (decoded.Unit.ModuleName != generic.ModuleName || decoded.SourceName != generic.SourceName)
                throw new InvalidDataException(
                    $"The generic syntax of '{generic.SourceName}' does not match the module and source that name it.");
            if (!GenericSyntax.IsGenericOnly(decoded.Unit))
                throw new InvalidDataException(
                    $"The generic syntax of '{generic.SourceName}' contains a declaration that is not generic.");
        }
    }
}
