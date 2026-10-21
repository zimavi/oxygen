using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using LLVMSharp.Interop;

namespace Oxygen.Llvm;

// The metadata section of a native object file. A module that adds it writes its metadata into a constant in the section, and
// the reader returns the payload of every object in a library file that has the section. The payload is the UTF-8 JSON of the
// module's NativeModuleMetadata, so readers of a payload use NativeModuleMetadataSerializer.
public static class NativeModuleSection
{
    // The section name in ELF and COFF objects. Mach-O targets have no section yet.
    public const string Name = ".oxmeta";

    private const string GlobalName = "oxygen-metadata-section";
    private static readonly byte[] ArchiveMagic = "!<arch>\n"u8.ToArray();
    private const int ArchiveHeaderLength = 60;

    // Each payload is written as an entry: this magic, its length as a little-endian int32, then the payload. A section can hold several
    // entries, since a linker merges the sections of several objects into one and pads it with zero bytes, so the entries are what
    // tells the payloads apart.
    private static readonly byte[] EntryMagic = "OXMD"u8.ToArray();
    private const int EntryHeaderLength = 8;

    // Adds the payload as a constant in the metadata section. The llvm.used array keeps the constant through optimization, so
    // the object file keeps the section even though nothing in the module refers to it.
    internal static void Add(LLVMModuleRef module, LLVMContextRef context, byte[] payload)
    {
        if (module.GetNamedGlobal(GlobalName).Handle != IntPtr.Zero)
            throw new InvalidOperationException("The metadata section was already added to this module.");

        var entry = Frame(payload);
        var int8 = context.Int8Type;
        var pointer = LLVMTypeRef.CreatePointer(int8, 0);
        var data = module.AddGlobal(LLVMTypeRef.CreateArray(int8, (uint)entry.Length), GlobalName);
        data.Initializer = LLVMValueRef.CreateConstArray(int8,
            entry.Select(value => LLVMValueRef.CreateConstInt(int8, value, false)).ToArray());
        data.Linkage = LLVMLinkage.LLVMInternalLinkage;
        data.IsGlobalConstant = true;
        data.Section = Name;

        var used = module.AddGlobal(LLVMTypeRef.CreateArray(pointer, 1), "llvm.used");
        used.Linkage = LLVMLinkage.LLVMAppendingLinkage;
        used.Section = "llvm.metadata";
        used.Initializer = LLVMValueRef.CreateConstArray(pointer, [data]);
    }

    // The payloads of the metadata sections in a library file: an object file, or an ar archive of object files. Each object
    // with the section gives one payload, and objects without it give none. Other archive members are skipped, since a library
    // can hold any object. A file that is neither an object nor an archive is invalid input.
    public static IReadOnlyList<byte[]> ReadPayloads(string path) =>
        TryReadPayloads(path, out var payloads)
            ? payloads
            : throw new InvalidDataException($"'{path}' is neither an object file nor an archive.");

    // The same payloads, or false when the file is neither an object file nor an archive. A section that is damaged still throws.
    public static bool TryReadPayloads(string path, out IReadOnlyList<byte[]> payloads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = File.ReadAllBytes(path);
        if (file.AsSpan().StartsWith(ArchiveMagic))
        {
            payloads = ReadArchive(file, path);
            return true;
        }

        var objectPayloads = new List<byte[]>();
        var isObject = TryReadObjectPayloads(file, objectPayloads);
        payloads = objectPayloads;
        return isObject;
    }

    private static byte[] Frame(byte[] payload)
    {
        var entry = new byte[EntryHeaderLength + payload.Length];
        EntryMagic.CopyTo(entry, 0);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(EntryMagic.Length), payload.Length);
        payload.CopyTo(entry, EntryHeaderLength);
        return entry;
    }

    // Splits the contents of a section into its entries. Zero bytes between entries are padding, and anything else that is not an
    // entry is a damaged section.
    private static void AddEntries(ReadOnlySpan<byte> contents, List<byte[]> payloads)
    {
        var offset = 0;
        while (offset < contents.Length)
        {
            if (contents[offset] == 0)
            {
                offset++;
                continue;
            }
            if (contents.Length - offset < EntryHeaderLength || !contents.Slice(offset, EntryMagic.Length).SequenceEqual(EntryMagic))
                throw new InvalidDataException("A metadata section holds bytes that are not a metadata entry.");

            var length = BinaryPrimitives.ReadInt32LittleEndian(contents.Slice(offset + EntryMagic.Length));
            if (length < 0 || length > contents.Length - offset - EntryHeaderLength)
                throw new InvalidDataException("A metadata entry is longer than its section.");
            payloads.Add(contents.Slice(offset + EntryHeaderLength, length).ToArray());
            offset += EntryHeaderLength + length;
        }
    }

    // Reads an archive member by member. Each member header is 60 bytes: a 16-byte name, a 10-byte size in decimal, and a
    // terminator. The names of GNU and COFF archives are not needed, since the object reader ignores the name, and a BSD name
    // is stored at the start of the member data and counted in its size.
    private static List<byte[]> ReadArchive(byte[] file, string path)
    {
        var payloads = new List<byte[]>();
        var offset = ArchiveMagic.Length;
        while (offset < file.Length)
        {
            // Metadata appended by an older build follows the last member, and it is not a member.
            if (file.AsSpan(offset).StartsWith(NativeModuleMetadataSerializer.EmbeddedMagic))
                break;
            if (offset + ArchiveHeaderLength > file.Length)
                throw Malformed(path, "an archive member header is cut short");
            if (file[offset + 58] != (byte)'`' || file[offset + 59] != (byte)'\n')
                throw Malformed(path, "an archive member header has no terminator");

            var sizeText = Encoding.ASCII.GetString(file, offset + 48, 10).TrimEnd(' ');
            if (!int.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
                size > file.Length - offset - ArchiveHeaderLength)
                throw Malformed(path, "an archive member has an invalid size");

            // Members are padded to an even length, so the next header starts after the padding.
            var next = offset + ArchiveHeaderLength + size + (size & 1);
            var dataStart = offset + ArchiveHeaderLength;
            var name = Encoding.ASCII.GetString(file, offset, 16);
            var dataLength = size;
            if (name.StartsWith("#1/", StringComparison.Ordinal))
            {
                var nameLength = int.TryParse(name.AsSpan(3).TrimEnd(' '), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                    ? length
                    : -1;
                if (nameLength < 0 || nameLength > dataLength)
                    throw Malformed(path, "an archive member has an invalid name length");
                dataStart += nameLength;
                dataLength -= nameLength;
            }

            TryReadObjectPayloads(file[dataStart..(dataStart + dataLength)], payloads);
            offset = next;
        }
        return payloads;
    }

    // Reads the sections of one object file into payloads. Gives false when LLVM does not read the bytes as an object file.
    private static unsafe bool TryReadObjectPayloads(byte[] bytes, List<byte[]> payloads)
    {
        if (bytes.Length == 0)
            return false;

        // LLVM takes ownership of the buffer, including when it cannot read the object, so the buffer is not disposed here.
        fixed (byte* data = bytes)
        fixed (byte* name = "oxygen-object\0"u8)
        {
            var buffer = LLVM.CreateMemoryBufferWithMemoryRangeCopy((sbyte*)data, (nuint)bytes.Length, (sbyte*)name);
            var file = LLVM.CreateObjectFile(buffer);
            if (file == null)
                return false;

            var sections = LLVM.GetSections(file);
            try
            {
                while (LLVM.IsSectionIteratorAtEnd(file, sections) == 0)
                {
                    if (Marshal.PtrToStringUTF8((nint)LLVM.GetSectionName(sections)) == Name)
                    {
                        var size = checked((int)LLVM.GetSectionSize(sections));
                        AddEntries(new ReadOnlySpan<byte>(LLVM.GetSectionContents(sections), size), payloads);
                    }
                    LLVM.MoveToNextSection(sections);
                }
            }
            finally
            {
                LLVM.DisposeSectionIterator(sections);
                LLVM.DisposeObjectFile(file);
            }
            return true;
        }
    }

    private static InvalidDataException Malformed(string path, string detail) =>
        new($"'{path}' is not a valid archive: {detail}.");
}
