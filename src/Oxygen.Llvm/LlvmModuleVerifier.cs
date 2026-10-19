using LLVMSharp.Interop;

namespace Oxygen.Llvm;

// Checks that a generated module is well-formed LLVM IR. The language rules were already checked by semantic analysis,
// so a failure here is a compiler bug and is reported as an internal compiler error.
public static class LlvmModuleVerifier
{
    public static void Verify(LLVMModuleRef module)
    {
        // TryVerify returns true when the module is valid, so an invalid module is the one that must be rejected.
        if (!module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out var error))
            throw new LlvmVerificationException(error);
    }
}

public sealed class LlvmVerificationException : Exception
{
    public LlvmVerificationException(string details)
        : base($"LLVM verification failed: {details}")
    {
    }
}
