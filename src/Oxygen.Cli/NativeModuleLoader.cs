using System.Text;
using System.Text.Json;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Semantic;
using Oxygen.Llvm;

namespace Oxygen.Cli;

// A native module's declarations, a warning for each function of it that cannot be imported, a note for each part of it that the
// compiler reads but does not use yet (types), and the generic declarations that the program instantiates from it.
internal sealed record LoadedNativeModule(
    ExternalModuleDeclaration Module,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notes,
    IReadOnlyList<LoadedGenericUnit> GenericUnits);

// The generic unions and functions of a native module, as units the program analyzes, and the name of the source they came from.
internal sealed record LoadedGenericUnit(CompilationUnit Unit, string SourceName);

internal static class NativeModuleLoader
{
    // Loads the modules of a native library. A library built by oxygenc carries one metadata document per module, in the .oxmeta
    // section of each object, and each document is a module. An older library has its metadata appended, or in a sidecar, and is one
    // module. A library without metadata is opaque, and gives no modules. Metadata for another target is rejected, since its symbols
    // belong to a different platform.
    public static IReadOnlyList<LoadedNativeModule> Load(FileInfo file, OxygenTarget target)
    {
        var modules = new List<LoadedNativeModule>();
        foreach (var metadata in ReadMetadata(file))
        {
            if (metadata.TargetTriple != target.Triple)
                throw new InvalidDataException(
                    $"The native module '{file.FullName}' was built for '{metadata.TargetTriple}', but the target is '{target.Triple}'.");
            // A class of the library is laid out by the rule that built it, so a library with another rule cannot be used.
            if (metadata.ObjectLayoutVersion != NativeModuleMetadata.CurrentObjectLayoutVersion)
                throw new InvalidDataException(
                    $"The native module '{file.FullName}' was built with object layout {metadata.ObjectLayoutVersion}, but this compiler " +
                    $"lays out objects as layout {NativeModuleMetadata.CurrentObjectLayoutVersion}. Rebuild the library with this compiler.");
            modules.Add(Decode(file, metadata));
        }
        return modules;
    }

    // The metadata documents of a library: one per object with the .oxmeta section, then the copy that older builds appended, then
    // the sidecar. A metadata file given directly is read as it is. Malformed metadata is invalid input.
    private static IReadOnlyList<NativeModuleMetadata> ReadMetadata(FileInfo file)
    {
        try
        {
            if (file.FullName.EndsWith(".oxmeta", StringComparison.OrdinalIgnoreCase))
                return [NativeModuleMetadataSerializer.Read(file.FullName)];

            var payloads = ReadSectionPayloads(file.FullName);
            if (payloads.Count > 0)
                return payloads.Select(payload => NativeModuleMetadataSerializer.Deserialize(Encoding.UTF8.GetString(payload))).ToList();

            var embedded = NativeModuleMetadataSerializer.ReadEmbedded(file.FullName);
            if (embedded is not null)
                return [embedded];

            var sidecar = file.FullName + ".oxmeta";
            return File.Exists(sidecar) ? [NativeModuleMetadataSerializer.Read(sidecar)] : [];
        }
        catch (JsonException exception)
        {
            // Malformed metadata is invalid input, reported like any other unusable library rather than as a crash.
            throw new InvalidDataException(
                $"The native module metadata for '{file.FullName}' is not valid JSON: {exception.Message}", exception);
        }
    }

    // The .oxmeta payloads of a file. A file that is neither an object nor an archive has none, and it may still carry older metadata
    // or none, so it is not an error here.
    private static IReadOnlyList<byte[]> ReadSectionPayloads(string path) =>
        NativeModuleSection.TryReadPayloads(path, out var payloads) ? payloads : [];

    private static LoadedNativeModule Decode(FileInfo file, NativeModuleMetadata metadata)
    {
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
        // Types are read and checked, but nothing uses them yet, so the library says so as a note, with --verbose.
        var notes = new List<string>();
        if (metadata.Types.Count > 0)
            notes.Add($"Native module '{metadata.ModuleName}' in '{file.FullName}' declares {metadata.Types.Count} type(s), which are not imported yet.");

        // The structs, the classes (generic ones with their bodies, others as declarations), the generic unions and functions, and the
        // interfaces are all imported.
        var genericUnits = new List<LoadedGenericUnit>();
        foreach (var generic in metadata.Generics)
        {
            var decoded = SyntaxTreeCodec.Read(generic.Syntax);
            genericUnits.Add(new LoadedGenericUnit(decoded.Unit, decoded.SourceName));
        }
        return new LoadedNativeModule(new ExternalModuleDeclaration(metadata.ModuleName, functions), warnings, notes, genericUnits);
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
            // A function pointer: its return type, then its parameter types as a counted list, as a function's signature is written.
            if (TryConsume("F"))
            {
                var returnType = ReadType();
                Expect("_p");
                var count = ReadNumber();
                Expect("_");
                var parameterTypes = new SemanticType[count];
                for (var index = 0; index < count; index++)
                    parameterTypes[index] = ReadType();
                return SemanticType.FunctionPointer(returnType, parameterTypes);
            }
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
