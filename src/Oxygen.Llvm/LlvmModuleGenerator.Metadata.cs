using System.Globalization;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

// The types of a module, as its metadata describes them. The layouts are the ones the generated code uses, read from the target
// data, so that a reader can check its own layout of a type against the library's.
public sealed partial class LlvmModuleGenerator
{
    // The suffix that the analyzer gives an instantiation of a generic type (see docs/generics.md, "Specialized names"). An
    // instantiation is not a type of the module, since the importing program makes its own.
    private const string InstantiationMarker = "__ox_";

    // The structs, classes and enums that the module declares and that are not generic. Interfaces and unions are not described yet.
    private unsafe IReadOnlyList<NativeTypeMetadata> TypeMetadataOf(
        SemanticModel model,
        string moduleName,
        IReadOnlyDictionary<FunctionSymbol, OxygenNameMangler.MethodOwner> methodOwners)
    {
        var data = LLVMTargetDataRef.FromStringRepresentation(_target.DataLayout);
        try
        {
            var types = new List<NativeTypeMetadata>();
            types.AddRange(model.Structs.Values
                .Where(structure => IsDeclaredType(structure.ModuleName, structure.Name, structure.IsGeneric, moduleName))
                .Select(structure => StructMetadata(structure, model, data)));
            types.AddRange(model.Classes.Values
                .Where(cls => IsDeclaredType(cls.ModuleName, cls.Name, cls.IsGeneric, moduleName))
                .Select(cls => ClassMetadata(cls, model, data, methodOwners)));
            types.AddRange(model.Enums.Values
                .Where(enumeration => enumeration.ModuleName == moduleName)
                .Select(EnumMetadata));
            return types;
        }
        finally
        {
            LLVM.DisposeTargetData(data);
        }
    }

    private static bool IsDeclaredType(string typeModule, string name, bool isGeneric, string moduleName) =>
        typeModule == moduleName && !isGeneric && !name.Contains(InstantiationMarker, StringComparison.Ordinal);

    private NativeTypeMetadata StructMetadata(StructSymbol structure, SemanticModel model, LLVMTargetDataRef data)
    {
        var layout = ToLlvmType(SemanticType.Struct(structure.QualifiedName), model);
        return new NativeTypeMetadata
        {
            Kind = NativeTypeKind.Struct,
            ModuleName = structure.ModuleName,
            Name = structure.Name,
            Visibility = VisibilityName(structure.Visibility),
            Size = checked((int)data.ABISizeOfType(layout)),
            Alignment = (int)data.ABIAlignmentOfType(layout),
            Fields = structure.Fields
                .Select((field, index) => new NativeFieldMetadata
                {
                    Name = field.Name,
                    Type = OxygenNameMangler.EncodeType(field.Type),
                    Offset = checked((int)data.OffsetOfElement(layout, (uint)index))
                })
                .ToArray()
        };
    }

    // A class's layout is its vtable pointer, one slot per virtual method, and then its fields. A @value class has no vtable.
    // The metadata lists the methods of the class, its static methods and its property accessors, each under its native name.
    private NativeTypeMetadata ClassMetadata(
        ClassSymbol cls,
        SemanticModel model,
        LLVMTargetDataRef data,
        IReadOnlyDictionary<FunctionSymbol, OxygenNameMangler.MethodOwner> methodOwners)
    {
        var layout = _classTypes[cls.QualifiedName];
        var methods = methodOwners
            .Where(pair => !pair.Value.IsInterface && pair.Value.ModuleName == cls.ModuleName && pair.Value.TypeName == cls.Name)
            .Select(pair => MethodMetadata(pair.Key, cls, methodOwners))
            .ToArray();
        return new NativeTypeMetadata
        {
            Kind = NativeTypeKind.Class,
            ModuleName = cls.ModuleName,
            Name = cls.Name,
            Visibility = VisibilityName(cls.Visibility),
            IsValue = cls.IsValue,
            Size = checked((int)data.ABISizeOfType(layout)),
            Alignment = (int)data.ABIAlignmentOfType(layout),
            Fields = cls.Fields
                .Select((field, index) => new NativeFieldMetadata
                {
                    Name = field.Name,
                    Type = OxygenNameMangler.EncodeType(field.Type),
                    Offset = checked((int)data.OffsetOfElement(layout, (uint)FieldOffset(cls, model, index)))
                })
                .ToArray(),
            Methods = methods
        };
    }

    private NativeMethodMetadata MethodMetadata(
        FunctionSymbol method,
        ClassSymbol cls,
        IReadOnlyDictionary<FunctionSymbol, OxygenNameMangler.MethodOwner> methodOwners) => new()
    {
        SourceName = method.Name,
        NativeName = MangledNameOf(method, methodOwners),
        ReturnType = OxygenNameMangler.EncodeType(method.ReturnType),
        Visibility = VisibilityName(method.Visibility),
        IsStatic = cls.StaticMethods.Contains(method),
        ParameterTypes = method.Parameters.Select(parameter => OxygenNameMangler.EncodeType(parameter.Type)).ToArray()
    };

    private static NativeTypeMetadata EnumMetadata(EnumSymbol enumeration) => new()
    {
        Kind = NativeTypeKind.Enum,
        ModuleName = enumeration.ModuleName,
        Name = enumeration.Name,
        Visibility = VisibilityName(enumeration.Visibility),
        UnderlyingType = OxygenNameMangler.EncodeType(SemanticType.FromPrimitive(enumeration.UnderlyingType)),
        IsFlags = enumeration.IsFlags,
        Cases = enumeration.Members
            .Select(member => new NativeEnumCaseMetadata
            {
                Name = member.Name,
                Value = member.Value.ToString(CultureInfo.InvariantCulture)
            })
            .ToArray()
    };

    private static string VisibilityName(Visibility visibility) => visibility.ToString().ToLowerInvariant();
}
