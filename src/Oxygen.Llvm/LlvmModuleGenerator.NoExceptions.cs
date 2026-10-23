using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

// Code for a program built without exceptions (--no-exceptions) and for the bodies of @nothrow functions. Neither can unwind, so a throw
// ends the program, and a runtime failure stops it (docs/exceptions.md).
public sealed partial class LlvmModuleGenerator
{
    // The flag of __oxygen_runtime_initialize that says the program has exceptions (OXYGEN_RUNTIME_EXCEPTIONS_ENABLED in oxygen_runtime.h).
    private const ulong ExceptionsEnabledFlag = 1;

    // True while the body of a @nothrow function is emitted. Nothing in such a body can throw, so its calls are plain calls, it sets up
    // no unwinding and no trace frame, and a runtime failure in it stops the program instead of throwing.
    private bool _noThrowFunction;

    // Whether the function being emitted sets up unwinding: its calls can throw, and it may need cleanup when an exception leaves it.
    private bool UnwindsHere => _usesExceptions && !_noThrowFunction;

    // A throw in a program without exceptions: the object is reported, and the program ends. The block ends here.
    private bool EmitUnhandledThrow(LLVMBuilderRef builder, LLVMValueRef thrown)
    {
        BuildNoThrowCall(builder, _unhandledInstance, [thrown]);
        builder.BuildUnreachable();
        return true;
    }

    // Without exceptions a try statement is its body. Its catch clauses can never run, so they are not emitted; the analysis warns about
    // them. Its finally clause still runs on every way out of the body: a return, break or continue copies it (see FinallyScope), and
    // the normal exit copies it here, as the exception paths do. Returns true when the statement does not finish.
    private bool EmitTryWithoutExceptions(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMValueRef function,
        TryStatement statement,
        Dictionary<string, LLVMValueRef> values,
        Dictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions,
        SemanticType emittedReturnType,
        Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)> loops)
    {
        var scope = statement.Finally is null ? null : new FinallyScope(statement.Finally, _exceptionContexts.Count, loops.Count);
        if (scope is not null)
            _finallyScopes.Push(scope);

        var bodyReturned = EmitStatement(context, builder, function, statement.Body, values, storage,
            model, llvmFunctions, emittedReturnType, loops);
        // The copy is emitted while the scope is still pushed, as in the exception paths, so a jump out of it finds the scope.
        var finished = bodyReturned || (scope is not null && EmitFinallyCopy(context, builder, function, scope, values,
            storage, model, llvmFunctions, emittedReturnType, loops));
        if (scope is not null)
            _finallyScopes.Pop();
        return finished;
    }
}
