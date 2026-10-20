using System.Globalization;
using System.Text.Json;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    private IReadOnlyList<FunctionSymbol> GetVirtualMethods(ClassSymbol cls, SemanticModel model)
    {
        var methods = cls.BaseTypeName is not null && model.Classes.TryGetValue(cls.BaseTypeName, out var baseClass)
            ? GetVirtualMethods(baseClass, model).ToList()
            : [];
        foreach (var method in cls.Methods.Where(method => method.Declaration.IsVirtual || method.Declaration.IsOverride))
        {
            var slot = methods.FindIndex(candidate => candidate.Name == method.Name
                && candidate.Parameters.Select(parameter => parameter.Type)
                    .SequenceEqual(method.Parameters.Select(parameter => parameter.Type)));
            if (slot >= 0) methods[slot] = method;
            else methods.Add(method);
        }
        foreach (var accessor in GetClassAccessors(cls, model)
                     .Where(accessor => accessor.Declaration.IsVirtual || accessor.Declaration.IsOverride))
        {
            var slot = methods.FindIndex(candidate => candidate.Name == accessor.Name
                && candidate.Parameters.Select(parameter => parameter.Type)
                    .SequenceEqual(accessor.Parameters.Select(parameter => parameter.Type)));
            if (slot >= 0) methods[slot] = accessor;
            else methods.Add(accessor);
        }
        return methods;
    }

    private IReadOnlyList<FunctionSymbol> GetClassAccessors(ClassSymbol cls, SemanticModel model) =>
        cls.DeclaredProperties.SelectMany(property => property.Declaration.Accessors
            .Where(accessor => accessor.Kind is PropertyAccessorKind.Get or PropertyAccessorKind.Set)
            .Select(accessor => _propertyAccessors[(cls.Name, property.Name, accessor.Kind)])).ToArray();

    private IEnumerable<FunctionSymbol> CreatePropertyAccessorSymbols(ClassSymbol cls, SemanticModel model)
    {
        foreach (var property in cls.DeclaredProperties)
            foreach (var accessor in property.Declaration.Accessors.Where(accessor =>
                         accessor.Kind is PropertyAccessorKind.Get or PropertyAccessorKind.Set))
            {
                var name = accessor.Kind == PropertyAccessorKind.Get
                    ? $"get_{property.Name}" : $"set_{property.Name}";
                var parameters = accessor.Kind == PropertyAccessorKind.Set
                    ? new[] { new ParameterSymbol("value", property.Type,
                    new ParameterDeclaration(ToTypeSyntax(property.Type, accessor.Location), "value", accessor.Location)) }
                    : Array.Empty<ParameterSymbol>();
                var declaration = new FunctionDeclaration(
                    accessor.Kind == PropertyAccessorKind.Get
                        ? ToTypeSyntax(property.Type, accessor.Location)
                        : new TypeSyntax(PrimitiveType.Void, false, accessor.Location),
                    name, parameters.Select(parameter => parameter.Declaration).ToArray(),
                    accessor.Body ?? new BlockStatement([], accessor.Location), accessor.Visibility, accessor.Location,
                    IsVirtual: property.Declaration.IsVirtual, IsOverride: property.Declaration.IsOverride);
                var symbol = new FunctionSymbol(name,
                    accessor.Kind == PropertyAccessorKind.Get ? property.Type : SemanticType.FromPrimitive(PrimitiveType.Void),
                    parameters, declaration, $"{cls.ModuleName}.{cls.Name}", accessor.Visibility);
                _propertyAccessors[(cls.Name, property.Name, accessor.Kind)] = symbol;
                _accessorSymbols.Add(symbol);
                yield return symbol;
            }
    }

    private IEnumerable<FunctionSymbol> CreateInterfaceAccessorSymbols(InterfaceSymbol iface)
    {
        foreach (var property in iface.DeclaredProperties)
            foreach (var accessor in property.Declaration.Accessors.Where(accessor =>
                         accessor.Kind is PropertyAccessorKind.Get or PropertyAccessorKind.Set))
            {
                var name = accessor.Kind == PropertyAccessorKind.Get
                    ? $"get_{property.Name}" : $"set_{property.Name}";
                var parameters = accessor.Kind == PropertyAccessorKind.Set
                    ? new[] { new ParameterSymbol("value", property.Type,
                        new ParameterDeclaration(ToTypeSyntax(property.Type, accessor.Location), "value", accessor.Location)) }
                    : Array.Empty<ParameterSymbol>();
                var declaration = new FunctionDeclaration(
                    accessor.Kind == PropertyAccessorKind.Get
                        ? ToTypeSyntax(property.Type, accessor.Location)
                        : new TypeSyntax(PrimitiveType.Void, false, accessor.Location),
                    name, parameters.Select(parameter => parameter.Declaration).ToArray(),
                    new BlockStatement([], accessor.Location), accessor.Visibility, accessor.Location,
                    IsExternalDeclaration: true);
                var symbol = new FunctionSymbol(name,
                    accessor.Kind == PropertyAccessorKind.Get ? property.Type : SemanticType.FromPrimitive(PrimitiveType.Void),
                    parameters, declaration, $"{iface.ModuleName}.{iface.Name}", accessor.Visibility,
                    IsExternal: true);
                _propertyAccessors[(iface.Name, property.Name, accessor.Kind)] = symbol;
                _accessorSymbols.Add(symbol);
                yield return symbol;
            }
    }

    private IReadOnlyList<FunctionSymbol> GetInterfaceSlots(InterfaceSymbol iface)
    {
        var slots = iface.Methods.ToList();
        foreach (var property in iface.DeclaredProperties)
            foreach (var accessor in property.Declaration.Accessors.Where(accessor =>
                         accessor.Kind is PropertyAccessorKind.Get or PropertyAccessorKind.Set))
                if (_propertyAccessors.TryGetValue((iface.Name, property.Name, accessor.Kind), out var symbol))
                    slots.Add(symbol);
        return slots;
    }

    private void AddInterfaceTable(
            LLVMModuleRef module,
            ClassSymbol cls,
            InterfaceSymbol iface,
            IReadOnlyList<FunctionSymbol> slots,
            IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions,
            SemanticModel model)
    {
        var entries = slots.Select(slot => ResolveInterfaceImplementation(cls, iface, slot, model))
            .Select(implementation => LLVMValueRef.CreateConstBitCast(
                llvmFunctions[implementation], LLVMTypeRef.CreatePointer(_context.Int8Type, 0)))
            .ToArray();
        var tableType = LLVMTypeRef.CreateArray(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), (uint)Math.Max(1, entries.Length));
        var initializer = entries.Length == 0
            ? LLVMValueRef.CreateConstNull(tableType)
            : LLVMValueRef.CreateConstArray(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), entries);
        var table = module.AddGlobal(tableType, $"oxygen.interface.table.{cls.Name}.{iface.Name}");
        table.Linkage = LLVMLinkage.LLVMInternalLinkage;
        table.Initializer = initializer;
        _interfaceTables[(cls.Name, iface.Name)] = table;
    }

    private FunctionSymbol ResolveInterfaceImplementation(ClassSymbol cls, InterfaceSymbol iface, FunctionSymbol contract,
            SemanticModel model)
    {
        if (contract.Name.StartsWith("get_", StringComparison.Ordinal)
            || contract.Name.StartsWith("set_", StringComparison.Ordinal))
        {
            var propertyName = contract.Name[4..];
            var kind = contract.Name.StartsWith("get_", StringComparison.Ordinal)
                ? PropertyAccessorKind.Get : PropertyAccessorKind.Set;
            var (owner, property) = FindClassProperty(cls, propertyName, model);
            return _propertyAccessors[(owner.Name, property.Name, kind)];
        }
        return GetClassMethodsForDispatch(cls, model)
            .First(method => method.Name == contract.Name
                && method.Parameters.Select(parameter => parameter.Type)
                    .SequenceEqual(contract.Parameters.Select(parameter => parameter.Type)));
    }

    private static (ClassSymbol Owner, PropertySymbol Property) FindClassProperty(ClassSymbol cls, string name, SemanticModel model)
    {
        var property = cls.DeclaredProperties.FirstOrDefault(candidate => candidate.Name == name);
        if (property is not null) return (cls, property);
        if (cls.BaseTypeName is not null && model.Classes.TryGetValue(cls.BaseTypeName, out var baseClass))
            return FindClassProperty(baseClass, name, model);
        throw new InvalidOperationException($"Class '{cls.Name}' has no property '{name}'.");
    }

    private static IReadOnlyList<FunctionSymbol> GetClassMethodsForDispatch(ClassSymbol cls, SemanticModel model)
    {
        var methods = cls.BaseTypeName is not null && model.Classes.TryGetValue(cls.BaseTypeName, out var baseClass)
            ? GetClassMethodsForDispatch(baseClass, model).ToList()
            : [];
        foreach (var method in cls.Methods)
        {
            var index = methods.FindIndex(candidate => candidate.Name == method.Name
                && candidate.Parameters.Select(parameter => parameter.Type)
                    .SequenceEqual(method.Parameters.Select(parameter => parameter.Type)));
            if (index >= 0) methods[index] = method;
            else methods.Add(method);
        }
        return methods;
    }

    private static TypeSyntax ToTypeSyntax(SemanticType type, SourceLocation location) =>
        type.Kind == SemanticTypeKind.Primitive
            ? new TypeSyntax(type.Primitive, false, location)
            : type.Kind == SemanticTypeKind.String
                ? new TypeSyntax(null, false, location, IsString: true)
                : new TypeSyntax(null, false, location, NamedType: type.StructName ?? type.EnumName);

    private int FieldOffset(ClassSymbol cls, SemanticModel model, int fieldIndex) =>
        cls.IsValue ? fieldIndex : 1 + _virtualSlotCount + fieldIndex;

    // The data pointer of a reference value: an interface value holds it in its first field.
    private LLVMValueRef ObjectIdentity(LLVMBuilderRef builder, LLVMValueRef value, SemanticType type) =>
        type.IsInterface ? builder.BuildExtractValue(value, 0, "interface.data") : value;

    private static bool IsValueClass(SemanticType type, SemanticModel model) =>
        type.IsClass && model.Classes.TryGetValue(type.StructName!, out var cls) && cls.IsValue;

    private LLVMTypeRef ClassPointerType(string name) =>
        LLVMTypeRef.CreatePointer(_classTypes[name], 0);

    // Every native symbol the module declares goes through here: runtime functions, the native entry point, externs and
    // Oxygen functions. A name that already exists is reused only with the same signature. LLVM would otherwise rename
    // the new declaration (memcmp.1, main.8), and calls would reach a symbol that does not exist.
    private static LLVMValueRef DeclareNativeFunction(
        LLVMModuleRef module,
        string nativeName,
        LLVMTypeRef functionType)
    {
        var existing = module.GetNamedFunction(nativeName);
        if (existing.Handle == IntPtr.Zero)
            return module.AddFunction(nativeName, functionType);
        if (existing.FunctionType != functionType)
            throw new InvalidOperationException(
                $"Native symbol '{nativeName}' is declared twice with different signatures.");
        return existing;
    }

}
