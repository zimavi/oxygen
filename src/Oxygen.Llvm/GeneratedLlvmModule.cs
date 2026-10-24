using System.Text;
using LLVMSharp.Interop;

namespace Oxygen.Llvm;

public sealed class GeneratedLlvmModule(
    LLVMContextRef context,
    LLVMModuleRef module,
    LLVMBuilderRef builder,
    NativeModuleMetadata metadata) : IDisposable
{
    public LLVMModuleRef Module => module;
    public NativeModuleMetadata Metadata => metadata;
    public override string ToString() => module.PrintToString();

    public void BuildNative(NativeBuildOptions options, INativeToolRunner? toolRunner = null) =>
        new NativeBuildPipeline(toolRunner).Build(this, options);

    // Puts the metadata into the object's .oxmeta section, so a library built from this module carries it. Adding it twice is an error.
    public void AddMetadataSection() =>
        NativeModuleSection.Add(module, context, Encoding.UTF8.GetBytes(NativeModuleMetadataSerializer.Serialize(metadata)));

    public void Dispose()
    {
        builder.Dispose();
        module.Dispose();
        context.Dispose();
    }
}
