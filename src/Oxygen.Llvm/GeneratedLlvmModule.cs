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

    public void WriteMetadata(string path) => NativeModuleMetadataSerializer.Write(path, metadata);

    public void Dispose()
    {
        builder.Dispose();
        module.Dispose();
        context.Dispose();
    }
}
