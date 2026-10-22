using System.Numerics;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    // The LLVM global that holds each module-level variable.
    private readonly Dictionary<GlobalVariableSymbol, LLVMValueRef> _globalVariables = new(ReferenceEqualityComparer.Instance);

    // Each module-level variable is an internal LLVM global named by its module and name. The module is part of the name, so two
    // modules can declare variables with the same name.
    // A static field is named by its class, which is its variable's module name, so its global is `module.Class.field`. A static field's
    // owner is its class's module, and a module-level variable's owner is its module, so both follow the same rules below.
    // A variable of a library module is declared here, not defined. A library exports its own variables, so one storage serves every program
    // whose generic declarations use them. An instance of a generic class has its own static fields, as its code does, so it is owned by
    // no module.
    private void DeclareGlobalVariables(LLVMModuleRef module, SemanticModel model, string moduleName, bool library)
    {
        _globalVariables.Clear();
        foreach (var variable in model.Globals)
        {
            var type = ToLlvmType(variable.Type, model);
            var global = module.AddGlobal(type, $"{variable.ModuleName}.{variable.Name}");
            global.IsGlobalConstant = variable.IsConst;
            var owningClass = model.Classes.GetValueOrDefault(variable.ModuleName);
            var owningModule = owningClass?.ModuleName ?? variable.ModuleName;
            var isInstance = owningClass is not null && IsInstantiationName(owningClass.Name);
            if (DefinedByLibrary(owningModule, isInstance, model))
            {
                // Declared only: the library defines the storage, and on Windows a program imports it.
                global.Linkage = LLVMLinkage.LLVMExternalLinkage;
                if (_target.UsesSeh)
                    global.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
                _globalVariables[variable] = global;
                continue;
            }
            var exported = library && owningModule == moduleName && !isInstance;
            global.Linkage = exported ? LLVMLinkage.LLVMExternalLinkage : LLVMLinkage.LLVMInternalLinkage;
            global.Initializer = GlobalInitializer(type, variable);
            _globalVariables[variable] = global;
        }
    }

    // The constant a variable starts with. The analyzer has already checked that the value converts to the variable's type.
    private static LLVMValueRef GlobalInitializer(LLVMTypeRef type, GlobalVariableSymbol variable) => variable.Value switch
    {
        null => LLVMValueRef.CreateConstNull(type),
        bool flag => LLVMValueRef.CreateConstInt(type, flag ? 1UL : 0UL, false),
        double real => LLVMValueRef.CreateConstReal(type, real),
        BigInteger integer when variable.Type.Primitive is PrimitiveType.F32 or PrimitiveType.F64 =>
            LLVMValueRef.CreateConstReal(type, (double)integer),
        // The low 64 bits of a negative value are its two's complement, which LLVM truncates to the variable's width.
        BigInteger integer => LLVMValueRef.CreateConstInt(type, (ulong)(integer & ulong.MaxValue), false),
        _ => throw new InvalidOperationException(
            $"Internal compiler error: global '{variable.Name}' has an unsupported constant.")
    };

    // The address of the variable an identifier names. The analyzer records the identifiers that name a module-level variable, so
    // a local of the same name is never taken for it.
    private LLVMValueRef VariableAddress(
        IdentifierExpression identifier,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model) =>
        model.GlobalReferences.TryGetValue(identifier, out var global)
            ? _globalVariables[global]
            : storage[identifier.Name];
}
