using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LLVMSharp.Interop;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Parsing;
using Oxygen.Compiler.Semantic;
using Oxygen.Llvm;
using Serilog;
using Xunit;

namespace Oxygen.Compiler.Tests;

// The metadata section: written into a module, emitted as an object for each target in-process, and read back from the object
// and from archives of it. Objects are emitted by LLVM itself, so these tests need no clang.
public sealed unsafe class NativeModuleSectionTests : IDisposable
{
    private const string Program = "i32 main() { return 0; }";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygen-section-tests", Guid.NewGuid().ToString("N"));

    static NativeModuleSectionTests()
    {
        LLVM.InitializeX86TargetInfo();
        LLVM.InitializeX86Target();
        LLVM.InitializeX86TargetMC();
        LLVM.InitializeX86AsmPrinter();
    }

    public NativeModuleSectionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void AddMetadataSectionKeepsTheConstantInTheSectionThroughLlvmUsed()
    {
        using var generated = Generate(OxygenTarget.LinuxGnuX64);

        generated.AddMetadataSection();

        var ir = generated.ToString();
        Assert.Contains("section \".oxmeta\"", ir);
        Assert.Contains("@llvm.used", ir);
        Assert.Contains("section \"llvm.metadata\"", ir);
    }

    [Fact]
    public void AddingTheMetadataSectionTwiceIsAnError()
    {
        using var generated = Generate(OxygenTarget.LinuxGnuX64);
        generated.AddMetadataSection();

        Assert.Throws<InvalidOperationException>(() => generated.AddMetadataSection());
    }

    [Fact]
    public void AnElfObjectCarriesTheSerializedMetadataInItsSection()
    {
        using var generated = Generate(OxygenTarget.LinuxGnuX64);
        generated.AddMetadataSection();
        var path = WriteObject(generated, "x86_64-unknown-linux-gnu", "program.o");

        var payload = Assert.Single(NativeModuleSection.ReadPayloads(path));

        Assert.Equal(ExpectedPayload(generated), payload);
    }

    [Fact]
    public void ACoffObjectCarriesTheSerializedMetadataInItsSection()
    {
        using var generated = Generate(OxygenTarget.WindowsMsvcX64);
        generated.AddMetadataSection();
        var path = WriteObject(generated, "x86_64-pc-windows-msvc", "program.obj");

        var payload = Assert.Single(NativeModuleSection.ReadPayloads(path));

        Assert.Equal(ExpectedPayload(generated), payload);
    }

    [Fact]
    public void AnObjectWithoutTheSectionGivesNoPayload()
    {
        using var generated = Generate(OxygenTarget.LinuxGnuX64);
        var path = WriteObject(generated, "x86_64-unknown-linux-gnu", "program.o");

        Assert.Empty(NativeModuleSection.ReadPayloads(path));
    }

    [Fact]
    public void ObjectsInAGnuArchiveGiveTheirPayloadsAndOtherMembersAreSkipped()
    {
        var elf = BuildObject(OxygenTarget.LinuxGnuX64, "x86_64-unknown-linux-gnu");
        var coff = BuildObject(OxygenTarget.WindowsMsvcX64, "x86_64-pc-windows-msvc");
        var archive = Archive(bsdNames: false,
            ("/", new byte[] { 0, 0, 0, 0 }),
            ("notes.txt", Encoding.ASCII.GetBytes("not an object")),
            ("a_long_object_file_name_for_the_archive.o", elf.Object),
            ("second.obj", coff.Object));
        var path = WriteFile("library.a", archive);

        var payloads = NativeModuleSection.ReadPayloads(path);

        Assert.Equal(new[] { elf.Payload, coff.Payload }, payloads);
    }

    [Fact]
    public void ObjectsInABsdArchiveGiveTheirPayloads()
    {
        var coff = BuildObject(OxygenTarget.WindowsMsvcX64, "x86_64-pc-windows-msvc");
        var archive = Archive(bsdNames: true,
            ("/", new byte[] { 0, 0, 0, 0 }),
            ("program.obj", coff.Object));
        var path = WriteFile("library.lib", archive);

        var payload = Assert.Single(NativeModuleSection.ReadPayloads(path));

        Assert.Equal(coff.Payload, payload);
    }

    [Fact]
    public void AFileThatIsNeitherAnObjectNorAnArchiveIsInvalidData()
    {
        var path = WriteFile("notes.a", Encoding.ASCII.GetBytes("not a library"));

        var exception = Assert.Throws<InvalidDataException>(() => NativeModuleSection.ReadPayloads(path));

        Assert.Contains("neither an object file nor an archive", exception.Message);
    }

    [Fact]
    public void ATruncatedArchiveMemberIsInvalidData()
    {
        var bytes = Encoding.ASCII.GetBytes("!<arch>\n" + "short");
        var path = WriteFile("truncated.a", bytes);

        var exception = Assert.Throws<InvalidDataException>(() => NativeModuleSection.ReadPayloads(path));

        Assert.Contains("not a valid archive", exception.Message);
    }

    private static GeneratedLlvmModule Generate(OxygenTarget target)
    {
        var model = Analyze(Program);
        return new LlvmModuleGenerator(target).Generate("program", model);
    }

    private static SemanticModel Analyze(string source)
    {
        var tokenizer = new Tokenizer(Log.Logger);
        var unit = new Parser(tokenizer.Tokenize(source), Log.Logger).Parse();
        return new SemanticAnalyzer(Log.Logger).Analyze([unit]);
    }

    private static byte[] ExpectedPayload(GeneratedLlvmModule generated) =>
        Encoding.UTF8.GetBytes(NativeModuleMetadataSerializer.Serialize(generated.Metadata));

    private string WriteObject(GeneratedLlvmModule generated, string triple, string name) =>
        WriteFile(name, EmitObject(generated.Module, triple));

    // Emits the object for a fresh module of the program, and returns it with the payload its section should hold.
    private static (byte[] Object, byte[] Payload) BuildObject(OxygenTarget target, string triple)
    {
        using var generated = Generate(target);
        generated.AddMetadataSection();
        return (EmitObject(generated.Module, triple), ExpectedPayload(generated));
    }

    private static byte[] EmitObject(LLVMModuleRef module, string triple)
    {
        var target = LLVMTargetRef.GetTargetFromTriple(triple);
        var machine = target.CreateTargetMachine(triple, "generic", "",
            LLVMCodeGenOptLevel.LLVMCodeGenLevelNone, LLVMRelocMode.LLVMRelocDefault, LLVMCodeModel.LLVMCodeModelDefault);
        try
        {
            using var buffer = machine.EmitToMemoryBuffer(module, LLVMCodeGenFileType.LLVMObjectFile);
            return new ReadOnlySpan<byte>((void*)buffer.BufferStart, checked((int)buffer.BufferSize)).ToArray();
        }
        finally
        {
            LLVM.DisposeTargetMachine(machine);
        }
    }

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // An ar archive. BSD names are stored at the start of each member's data, and GNU names longer than fifteen characters go
    // to a "//" table that the header refers to by offset. The reader ignores names, so both forms only have to be well formed.
    private static byte[] Archive(bool bsdNames, params (string Name, byte[] Data)[] members)
    {
        var stream = new MemoryStream();
        stream.Write("!<arch>\n"u8);

        var longNames = new MemoryStream();
        var entries = new List<(string Field, byte[] Data)>();
        foreach (var (name, data) in members)
        {
            if (name == "/")
                entries.Add(("/", data));
            else if (bsdNames)
                entries.Add(($"#1/{name.Length}", Encoding.ASCII.GetBytes(name).Concat(data).ToArray()));
            else if (name.Length < 16)
                entries.Add((name + "/", data));
            else
            {
                entries.Add(($"/{longNames.Length}", data));
                longNames.Write(Encoding.ASCII.GetBytes(name + "/\n"));
            }
        }
        if (longNames.Length > 0)
            WriteMember(stream, "//", longNames.ToArray());
        foreach (var (field, data) in entries)
            WriteMember(stream, field, data);
        return stream.ToArray();
    }

    private static void WriteMember(Stream stream, string field, byte[] data)
    {
        stream.Write(Encoding.ASCII.GetBytes(field.PadRight(16)));
        stream.Write(Encoding.ASCII.GetBytes("0".PadRight(12)));
        stream.Write(Encoding.ASCII.GetBytes("0".PadRight(6)));
        stream.Write(Encoding.ASCII.GetBytes("0".PadRight(6)));
        stream.Write(Encoding.ASCII.GetBytes("100644".PadRight(8)));
        stream.Write(Encoding.ASCII.GetBytes(data.Length.ToString().PadRight(10)));
        stream.Write("`\n"u8);
        stream.Write(data);
        if (data.Length % 2 == 1)
            stream.WriteByte((byte)'\n');
    }
}
