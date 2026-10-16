using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oxygen.Llvm;

public sealed record NativeModuleMetadata
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string ModuleName { get; init; } = "program";
    public required string EntryPoint { get; init; }
    public string? TargetTriple { get; init; }
    public IReadOnlyList<NativeFunctionMetadata> Functions { get; init; } = [];
    public IReadOnlyList<NativeDependencyMetadata> Dependencies { get; init; } = [];
}

public sealed record NativeFunctionMetadata
{
    public string ModuleName { get; init; } = "program";
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

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(NativeModuleMetadata))]
[JsonSerializable(typeof(NativeFunctionMetadata))]
[JsonSerializable(typeof(NativeDependencyMetadata))]
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
                .ToArray()
        };
        return JsonSerializer.Serialize(
            normalized,
            NativeModuleMetadataJsonContext.Default.NativeModuleMetadata) + Environment.NewLine;
    }

    public static NativeModuleMetadata Deserialize(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var metadata = JsonSerializer.Deserialize(
                contents,
                NativeModuleMetadataJsonContext.Default.NativeModuleMetadata)
            ?? throw new InvalidDataException("The native metadata document is empty.");
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
        if (metadata.FormatVersion != NativeModuleMetadata.CurrentFormatVersion)
            throw new InvalidDataException(
                $"Unsupported native metadata format version {metadata.FormatVersion}; " +
                $"this compiler supports version {NativeModuleMetadata.CurrentFormatVersion}.");
        if (string.IsNullOrWhiteSpace(metadata.ModuleName))
            throw new InvalidDataException("Native metadata must specify a module name.");
        if (string.IsNullOrWhiteSpace(metadata.EntryPoint))
            throw new InvalidDataException("Native metadata must specify an entry point.");
        if (metadata.Functions.Any(function =>
                string.IsNullOrWhiteSpace(function.SourceName) ||
                string.IsNullOrWhiteSpace(function.NativeName) ||
                string.IsNullOrWhiteSpace(function.ReturnType)))
            throw new InvalidDataException("Native metadata contains an incomplete function.");
        if (metadata.Dependencies.Any(dependency => string.IsNullOrWhiteSpace(dependency.Path)))
            throw new InvalidDataException("Native metadata contains a dependency without a path.");
    }
}
