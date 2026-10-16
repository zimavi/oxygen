using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Semantic;
using Oxygen.Llvm;

namespace Oxygen.Cli;

internal static class NativeModuleLoader
{
    public static ExternalModuleDeclaration? Load(FileInfo file)
    {
        var metadataPath = file.FullName.EndsWith(".oxmeta", StringComparison.OrdinalIgnoreCase)
            ? file.FullName
            : file.FullName + ".oxmeta";
        if (!File.Exists(metadataPath))
            return null;

        var metadata = NativeModuleMetadataSerializer.ReadArtifact(file.FullName);
        return new ExternalModuleDeclaration(
            metadata.ModuleName,
            metadata.Functions.Select(function => new ExternalFunctionDeclaration(
                function.SourceName,
                ParseMetadataType(function.ReturnType),
                function.ParameterTypes.Select(ParseMetadataType).ToArray(),
                function.NativeName,
                string.Equals(function.Visibility, "public", StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Public
                    : Visibility.Private,
                function.ExternalName is null
                    ? null
                    : new CInteropFunctionMetadata(function.ExternalName, function.CallingConvention))).ToArray());
    }

    private static SemanticType ParseMetadataType(string encoding) => encoding switch
    {
        "i8" => SemanticType.FromPrimitive(PrimitiveType.I8),
        "i16" => SemanticType.FromPrimitive(PrimitiveType.I16),
        "i32" => SemanticType.FromPrimitive(PrimitiveType.I32),
        "i64" => SemanticType.FromPrimitive(PrimitiveType.I64),
        "u8" => SemanticType.FromPrimitive(PrimitiveType.U8),
        "u16" => SemanticType.FromPrimitive(PrimitiveType.U16),
        "u32" => SemanticType.FromPrimitive(PrimitiveType.U32),
        "u64" => SemanticType.FromPrimitive(PrimitiveType.U64),
        "f32" => SemanticType.FromPrimitive(PrimitiveType.F32),
        "f64" => SemanticType.FromPrimitive(PrimitiveType.F64),
        "b" => SemanticType.FromPrimitive(PrimitiveType.Bool),
        "v" => SemanticType.FromPrimitive(PrimitiveType.Void),
        "s" => SemanticType.String,
        _ => throw new InvalidDataException($"Unsupported Oxygen metadata type '{encoding}'.")
    };
}
