using System.Text;
using LLVMSharp.Interop;
using Oxygen.Llvm;

namespace Oxygen.Cli.Tests;

// Writes a native library object for the loader tests. Its .oxmeta section holds the payload, as the objects that oxygenc builds do, and
// LLVM emits the object for the target, so the loader reads it the way it reads a library that oxygenc built.
internal static unsafe class NativeLibraryFixture
{
    static NativeLibraryFixture()
    {
        LLVM.InitializeX86TargetInfo();
        LLVM.InitializeX86Target();
        LLVM.InitializeX86TargetMC();
        LLVM.InitializeX86AsmPrinter();
    }

    public static string WriteObject(string path, NativeModuleMetadata metadata) =>
        WriteObject(path, metadata.TargetTriple, Encoding.UTF8.GetBytes(NativeModuleMetadataSerializer.Serialize(metadata)));

    // The payload goes into the section as it is, which lets a test write a document that is not valid metadata.
    public static string WriteObject(string path, string triple, byte[] payload)
    {
        using var context = LLVMContextRef.Create();
        using var module = context.CreateModuleWithName("library");
        module.Target = triple;
        NativeModuleSection.Add(module, context, payload);
        File.WriteAllBytes(path, EmitObject(module, triple));
        return path;
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
}
