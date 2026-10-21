using LLVMSharp.Interop;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

// Which code a library object defines. A library is linked with the program that uses it, and the program provides the code of
// every other module, so the library defines only what its own module owns. Other modules' code is declared, and the instances of
// generic declarations are defined where they are used. The instances are linkonce_odr, so two objects that both define one keep one.
public sealed partial class LlvmModuleGenerator
{
    // Whether the object defines the function. A program defines everything it generates, as before.
    private static bool DefinesInThisObject(
        FunctionSymbol symbol,
        bool library,
        string moduleName,
        IReadOnlyDictionary<FunctionSymbol, OxygenNameMangler.MethodOwner> methodOwners) =>
        !library || symbol.ModuleName == moduleName || IsInstance(symbol, methodOwners);

    // Whether the object defines the class's constructors and destructor.
    private static bool DefinesClassInThisObject(ClassSymbol cls, string moduleName) =>
        cls.ModuleName == moduleName || IsInstantiationName(cls.Name);

    // A method of an instantiated class is an instance, and so is a function whose name carries the instantiation suffix.
    private static bool IsInstance(
        FunctionSymbol symbol,
        IReadOnlyDictionary<FunctionSymbol, OxygenNameMangler.MethodOwner> methodOwners) =>
        IsInstantiationName(symbol.Name) ||
        (methodOwners.TryGetValue(symbol, out var owner) && IsInstantiationName(owner.TypeName));

    private static bool IsInstantiationName(string name) => name.Contains(InstantiationMarker, StringComparison.Ordinal);
}
