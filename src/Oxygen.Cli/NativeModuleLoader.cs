using System.Text.Json;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Semantic;
using Oxygen.Llvm;

namespace Oxygen.Cli;

// A native module's declarations, and a message for each function of it that cannot be imported.
internal sealed record LoadedNativeModule(ExternalModuleDeclaration Module, IReadOnlyList<string> Warnings);

internal static class NativeModuleLoader
{
    // Loads the Oxygen metadata of a native library: the copy embedded in the library when it has one, and otherwise the sidecar
    // next to it. A library without metadata is opaque, and gives null. Metadata for another target is rejected, since its symbols
    // belong to a different platform.
    public static LoadedNativeModule? Load(FileInfo file, OxygenTarget target)
    {
        var metadata = ReadMetadata(file);
        if (metadata is null)
            return null;
        if (metadata.TargetTriple != target.Triple)
            throw new InvalidDataException(
                $"The native module '{file.FullName}' was built for '{metadata.TargetTriple}', but the target is '{target.Triple}'.");

        var functions = new List<ExternalFunctionDeclaration>();
        var warnings = new List<string>();
        foreach (var function in metadata.Functions)
        {
            try
            {
                functions.Add(DecodeFunction(function));
            }
            catch (UnsupportedNativeTypeException exception)
            {
                warnings.Add($"Native function '{function.SourceName}' in '{file.FullName}' is not imported: {exception.Message}");
            }
        }
        return new LoadedNativeModule(new ExternalModuleDeclaration(metadata.ModuleName, functions), warnings);
    }

    // The metadata of a library: the embedded copy first, then the sidecar. A sidecar that is not valid JSON is invalid input.
    private static NativeModuleMetadata? ReadMetadata(FileInfo file)
    {
        try
        {
            var embedded = NativeModuleMetadataSerializer.ReadEmbedded(file.FullName);
            if (embedded is not null)
                return embedded;

            var sidecar = file.FullName.EndsWith(".oxmeta", StringComparison.OrdinalIgnoreCase)
                ? file.FullName
                : file.FullName + ".oxmeta";
            return File.Exists(sidecar) ? NativeModuleMetadataSerializer.Read(sidecar) : null;
        }
        catch (JsonException exception)
        {
            // Malformed metadata is invalid input, reported like any other unusable library rather than as a crash.
            throw new InvalidDataException(
                $"The native module metadata for '{file.FullName}' is not valid JSON: {exception.Message}", exception);
        }
    }

    private static ExternalFunctionDeclaration DecodeFunction(NativeFunctionMetadata function) =>
        new(
            function.SourceName,
            DecodeType(function.ReturnType),
            function.ParameterTypes.Select(DecodeType).ToArray(),
            function.NativeName,
            string.Equals(function.Visibility, "public", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Public
                : Visibility.Private,
            function.ExternalName is null
                ? null
                : new CInteropFunctionMetadata(function.ExternalName, function.CallingConvention));

    // Decodes a type encoding of the mangling scheme (docs/mangling.md). The encodings of named types and generic parameters
    // need the declarations of their types, which metadata does not carry, so those are reported as unsupported.
    internal static SemanticType DecodeType(string encoding)
    {
        var reader = new TypeReader(encoding);
        var type = reader.ReadType();
        if (!reader.AtEnd)
            throw new InvalidDataException($"Unexpected text after the type encoding '{encoding}'.");
        return type;
    }

    // Raised for a type that the metadata cannot describe. The function that has it is not imported.
    internal sealed class UnsupportedNativeTypeException(string message) : Exception(message);

    private sealed class TypeReader(string encoding)
    {
        // Each code, longest first where one code begins with another.
        private static readonly (string Code, PrimitiveType Primitive)[] PrimitiveCodes =
        [
            ("i16", PrimitiveType.I16), ("i32", PrimitiveType.I32), ("i64", PrimitiveType.I64), ("i8", PrimitiveType.I8),
            ("u16", PrimitiveType.U16), ("u32", PrimitiveType.U32), ("u64", PrimitiveType.U64), ("u8", PrimitiveType.U8),
            ("us", PrimitiveType.USize), ("is", PrimitiveType.ISize),
            ("f32", PrimitiveType.F32), ("f64", PrimitiveType.F64),
            ("b", PrimitiveType.Bool), ("v", PrimitiveType.Void)
        ];

        private int _position;

        public bool AtEnd => _position == encoding.Length;

        public SemanticType ReadType()
        {
            foreach (var (code, primitive) in PrimitiveCodes)
            {
                if (TryConsume(code))
                    return SemanticType.FromPrimitive(primitive);
            }
            if (TryConsume("str"))
                return SemanticType.String;
            if (TryConsume("p"))
                return SemanticType.PointerTo(ReadType());
            if (TryConsume("a"))
            {
                var length = ReadNumber();
                Expect("_");
                return SemanticType.ArrayOf(ReadType(), length);
            }
            if (!AtEnd && encoding[_position] is 'E' or 'U' or 'S' or 'C' or 'I')
                throw new UnsupportedNativeTypeException(
                    "it uses a class, struct, enum, union or interface type, which native metadata does not describe yet.");
            if (!AtEnd && encoding[_position] == 'g')
                throw new UnsupportedNativeTypeException("it uses a generic parameter, so it is not a single function.");
            throw new InvalidDataException($"Unsupported Oxygen metadata type '{encoding}'.");
        }

        private bool TryConsume(string code)
        {
            if (_position + code.Length > encoding.Length || string.CompareOrdinal(encoding, _position, code, 0, code.Length) != 0)
                return false;
            _position += code.Length;
            return true;
        }

        private void Expect(string text)
        {
            if (!TryConsume(text))
                throw new InvalidDataException($"Malformed Oxygen metadata type '{encoding}'.");
        }

        private int ReadNumber()
        {
            var start = _position;
            while (!AtEnd && char.IsAsciiDigit(encoding[_position]))
                _position++;
            if (_position == start || !int.TryParse(encoding.AsSpan(start, _position - start), out var number))
                throw new InvalidDataException($"Malformed Oxygen metadata type '{encoding}'.");
            return number;
        }
    }
}
