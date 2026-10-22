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
    IReadOnlyList<LoadedGenericUnit> GenericUnits,
    string SourcePath);

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
        var documents = ReadMetadata(file);
        foreach (var metadata in documents)
        {
            if (metadata.TargetTriple != target.Triple)
                throw new InvalidDataException(
                    $"The native module '{file.FullName}' was built for '{metadata.TargetTriple}', but the target is '{target.Triple}'.");
            // A class of the library is laid out by the rule that built it, so a library with another rule cannot be used.
            if (metadata.ObjectLayoutVersion != NativeModuleMetadata.CurrentObjectLayoutVersion)
                throw new InvalidDataException(
                    $"The native module '{file.FullName}' was built with object layout {metadata.ObjectLayoutVersion}, but this compiler " +
                    $"lays out objects as layout {NativeModuleMetadata.CurrentObjectLayoutVersion}. Rebuild the library with this compiler.");
        }
        return documents.Select(metadata => Decode(file, metadata)).ToList();
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
        // A function can use the classes, structs and interfaces that its own module declares, since the module's generic units give them.
        var genericUnits = metadata.Generics.Select(DecodeGenericUnit).ToList();
        var declared = DeclaredNamedTypes(genericUnits.Select(unit => unit.Unit));
        var functions = new List<ExternalFunctionDeclaration>();
        var warnings = new List<string>();
        foreach (var function in metadata.Functions)
        {
            try
            {
                functions.Add(DecodeFunction(function, declared));
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
        return new LoadedNativeModule(new ExternalModuleDeclaration(metadata.ModuleName, functions), warnings, notes, genericUnits, file.FullName);
    }

    private static LoadedGenericUnit DecodeGenericUnit(NativeGenericMetadata generic)
    {
        var decoded = SyntaxTreeCodec.Read(generic.Syntax);
        return new LoadedGenericUnit(decoded.Unit, decoded.SourceName);
    }

    // The named types that the generic units declare without bodies: the non-generic classes, structs and interfaces, keyed by their
    // qualified name, which is how the mangled encodings name them. Enums and unions are not declared in the units, so they stay unsupported.
    private static IReadOnlyDictionary<string, SemanticType> DeclaredNamedTypes(IEnumerable<CompilationUnit> units)
    {
        var declared = new Dictionary<string, SemanticType>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            var module = unit.ModuleName ?? "program";
            foreach (var cls in unit.Classes.Where(cls => cls.TypeParameters is null or { Count: 0 }))
                declared.TryAdd($"{module}.{cls.Name}", SemanticType.Class($"{module}.{cls.Name}"));
            foreach (var structure in unit.Structs.Where(structure => structure.TypeParameters is null or { Count: 0 }))
                declared.TryAdd($"{module}.{structure.Name}", SemanticType.Struct($"{module}.{structure.Name}"));
            foreach (var iface in unit.Interfaces.Where(iface => iface.TypeParameters is null or { Count: 0 }))
                declared.TryAdd($"{module}.{iface.Name}", SemanticType.Interface($"{module}.{iface.Name}"));
        }
        return declared;
    }

    private static ExternalFunctionDeclaration DecodeFunction(
        NativeFunctionMetadata function,
        IReadOnlyDictionary<string, SemanticType> declared) =>
        new(
            function.SourceName,
            DecodeType(function.ReturnType, declared),
            function.ParameterTypes.Select(parameter => DecodeType(parameter, declared)).ToArray(),
            function.NativeName,
            string.Equals(function.Visibility, "public", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Public
                : Visibility.Private,
            function.ExternalName is null
                ? null
                : new CInteropFunctionMetadata(function.ExternalName, function.CallingConvention));

    // Decodes a type encoding of the mangling scheme (docs/mangling.md). The encodings of named types and generic parameters
    // need the declarations of their types, which metadata does not carry, so those are reported as unsupported.
    internal static SemanticType DecodeType(string encoding, IReadOnlyDictionary<string, SemanticType>? declared = null)
    {
        var reader = new TypeReader(encoding, declared ?? new Dictionary<string, SemanticType>());
        var type = reader.ReadType();
        if (!reader.AtEnd)
            throw new InvalidDataException($"Unexpected text after the type encoding '{encoding}'.");
        return type;
    }

    // Raised for a type that the metadata cannot describe. The function that has it is not imported.
    internal sealed class UnsupportedNativeTypeException(string message) : Exception(message);

    private sealed class TypeReader(string encoding, IReadOnlyDictionary<string, SemanticType> declared)
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
            if (!AtEnd && encoding[_position] is 'C' or 'S' or 'I')
                return ReadDeclaredType();
            if (!AtEnd && encoding[_position] is 'E' or 'U')
                throw new UnsupportedNativeTypeException(
                    "it uses an enum or union type, which native metadata does not describe yet.");
            if (!AtEnd && encoding[_position] == 'g')
                throw new UnsupportedNativeTypeException("it uses a generic parameter, so it is not a single function.");
            throw new InvalidDataException($"Unsupported Oxygen metadata type '{encoding}'.");
        }

        // A class ('C'), struct ('S') or interface ('I') by its qualified name. It decodes only when the library declares it without type
        // arguments; a generic instance, or a type of another library, is reported as unsupported.
        private SemanticType ReadDeclaredType()
        {
            var tag = encoding[_position++];
            var length = ReadNumber();
            if (_position + length > encoding.Length)
                throw new InvalidDataException($"Malformed Oxygen metadata type '{encoding}'.");
            var name = encoding.Substring(_position, length);
            _position += length;
            var (kind, word) = tag switch
            {
                'C' => (SemanticTypeKind.Class, "class"),
                'S' => (SemanticTypeKind.Struct, "struct"),
                _ => (SemanticTypeKind.Interface, "interface")
            };
            if (TryConsume("_a"))
                throw new UnsupportedNativeTypeException($"it uses a generic instance of the {word} '{name}', which is not imported yet.");
            if (declared.TryGetValue(name, out var type) && type.Kind == kind)
                return type;
            throw new UnsupportedNativeTypeException($"it uses the {word} '{name}', which the library does not declare.");
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
