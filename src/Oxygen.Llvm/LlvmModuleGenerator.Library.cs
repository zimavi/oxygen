using LLVMSharp.Interop;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

// Which code a library object defines. A library is linked with the program that uses it, and the program provides the code of
// every other module, so the library defines only what its own module owns. Other modules' code is declared, and the instances of
// generic declarations are defined where they are used. The instances are linkonce_odr, so two objects that both define one keep one.
public sealed partial class LlvmModuleGenerator
{
    // Whether the object defines the function. A program defines everything it generates, as before. A method belongs to the module of
    // its class, which the method's own module name does not always give (the analyzer leaves a method's module at its default).
    private static bool DefinesInThisObject(
        FunctionSymbol symbol,
        bool library,
        string moduleName,
        IReadOnlyDictionary<FunctionSymbol, OxygenNameMangler.MethodOwner> methodOwners) =>
        !library || OwningModuleOf(symbol, methodOwners) == moduleName || IsInstance(symbol, methodOwners);

    private static string OwningModuleOf(
        FunctionSymbol symbol,
        IReadOnlyDictionary<FunctionSymbol, OxygenNameMangler.MethodOwner> methodOwners) =>
        methodOwners.TryGetValue(symbol, out var owner) ? owner.ModuleName : symbol.ModuleName;

    // Whether a declaration of a native library is defined by that library, so that this object only declares it. An instance of a
    // generic declaration is not: every object that uses an instance defines its own, which the linker merges.
    private bool DefinedByLibrary(string owningModule, bool isInstance, SemanticModel model) =>
        model.ExternalModules.Contains(owningModule) && !isInstance;

    // Whether the object owns a type, so that it defines the type's information with external linkage. Only the type's module owns it,
    // and an instance of a generic type is owned by no module, so every object with the instance defines the same copy.
    private static bool OwnsTypeInThisObject(string typeModule, string typeName, string moduleName) =>
        typeModule == moduleName && !IsInstantiationName(typeName);

    // Whether the object defines the class's constructors and destructor.
    private static bool DefinesClassInThisObject(ClassSymbol cls, string moduleName) =>
        cls.ModuleName == moduleName || IsInstantiationName(cls.Name);

    // A method of an instantiated class is an instance, and so is a function whose name carries the instantiation suffix.
    // A function is an instance when it is a generic function specialized with type arguments, or a method of an instantiated class.
    private static bool IsInstance(
        FunctionSymbol symbol,
        IReadOnlyDictionary<FunctionSymbol, OxygenNameMangler.MethodOwner> methodOwners) =>
        symbol.SpecializationArguments is { Count: > 0 } ||
        IsInstantiationName(symbol.Name) ||
        (methodOwners.TryGetValue(symbol, out var owner) && IsInstantiationName(owner.TypeName));

    private static bool IsInstantiationName(string name) => name.Contains(InstantiationMarker, StringComparison.Ordinal);
}
