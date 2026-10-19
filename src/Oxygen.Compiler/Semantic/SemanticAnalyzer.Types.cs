using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

// Types: resolving type syntax, generic type parameters and struct layout.
public sealed partial class SemanticAnalyzer
{
    private SemanticType ResolveType(TypeSyntax syntax)
    {
        if (syntax.IsInferred)
        {
            throw Error("Type inference is not valid in this context", syntax.Location);
        }

        if (syntax.IsArray)
            return SemanticType.ArrayOf(ResolveType(syntax.ElementType!), syntax.ArrayLength!.Value);
        if (syntax.IsString)
            return syntax.IsPointer
                ? SemanticType.PointerTo(ResolveType(syntax.Pointee!))
                : SemanticType.String;

        if (syntax.NamedType is not null)
        {
            if (_genericTypes.TryGetValue(syntax.NamedType, out var generic))
            {
                if (syntax.DeclaredTypeArguments.Count != 0)
                    throw Error($"Generic parameter '{syntax.NamedType}' cannot have type arguments", syntax.Location);
                return syntax.IsPointer ? SemanticType.PointerTo(generic) : generic;
            }
            if (_enumsByName.ContainsKey(syntax.NamedType))
            {
                if (syntax.IsPointer)
                    return SemanticType.PointerTo(SemanticType.Enum(syntax.NamedType));
                return SemanticType.Enum(syntax.NamedType);
            }
            if (_unionsByName.ContainsKey(syntax.NamedType))
            {
                ValidateTypeArgumentCount(syntax, _unionsByName[syntax.NamedType].DeclaredTypeParameters.Count);
                var union = SemanticType.Union(syntax.NamedType, ResolveTypeArguments(syntax));
                if (syntax.IsPointer)
                    return SemanticType.PointerTo(union);
                return union;
            }
            if (_classesByName.ContainsKey(syntax.NamedType))
            {
                ValidateTypeArgumentCount(syntax, _classesByName[syntax.NamedType].DeclaredTypeParameters.Count);
                var namedClass = SemanticType.Class(syntax.NamedType, ResolveTypeArguments(syntax));
                if (namedClass.TypeArguments is not null)
                    ValidateTypeArgumentConstraints(_classesByName[syntax.NamedType].Declaration.DeclaredTypeParameters,
                        namedClass.TypeArguments, syntax.Location);
                return syntax.IsPointer ? SemanticType.PointerTo(namedClass) : namedClass;
            }
            if (_interfacesByName.ContainsKey(syntax.NamedType))
            {
                ValidateTypeArgumentCount(syntax, _interfacesByName[syntax.NamedType].DeclaredTypeParameters.Count);
                var namedInterface = SemanticType.Interface(syntax.NamedType, ResolveTypeArguments(syntax));
                return syntax.IsPointer ? SemanticType.PointerTo(namedInterface) : namedInterface;
            }
            if (!_structsByName.ContainsKey(syntax.NamedType))
                throw Error($"Struct '{syntax.NamedType}' is not declared", syntax.Location, syntax.NamedType.Length);
            ValidateTypeArgumentCount(syntax, _structsByName[syntax.NamedType].DeclaredTypeParameters.Count);
            var named = SemanticType.Struct(syntax.NamedType, ResolveTypeArguments(syntax));
            return syntax.IsPointer ? SemanticType.PointerTo(named) : named;
        }

        if (syntax.Primitive is null)
            return syntax.IsPointer
                ? SemanticType.PointerTo(ResolveType(syntax.Pointee!))
                : SemanticType.String;

        var type = SemanticType.FromPrimitive(syntax.Primitive.Value);
        return syntax.IsPointer ? SemanticType.PointerTo(type) : type;
    }

    private IReadOnlyList<SemanticType>? ResolveTypeArguments(TypeSyntax syntax) =>
        syntax.DeclaredTypeArguments.Count == 0
            ? null
            : syntax.DeclaredTypeArguments.Select(ResolveType).ToArray();

    private void ValidateTypeArgumentCount(TypeSyntax syntax, int expected)
    {
        if (syntax.DeclaredTypeArguments.Count != expected)
            throw Error($"Type '{syntax.NamedType}' expects {expected} type argument(s), but received " +
                        $"{syntax.DeclaredTypeArguments.Count}", syntax.Location);
    }

    private void ValidateTypeParameters(IReadOnlyList<TypeParameterSyntax> parameters)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
            if (!names.Add(parameter.Name))
                throw Error($"Duplicate generic type parameter '{parameter.Name}'", parameter.Location,
                    parameter.Name.Length);
    }

    private T WithGenericTypes<T>(IReadOnlyList<TypeParameterSyntax> parameters, Func<T> action)
    {
        var previous = _genericTypes;
        _genericTypes = parameters.ToDictionary(
            parameter => parameter.Name,
            parameter => SemanticType.GenericParameter(parameter.Name),
            StringComparer.Ordinal);
        try
        {
            return action();
        }
        finally
        {
            _genericTypes = previous;
        }
    }

    private static (bool IsPacked, uint? Alignment) ParseStructLayout(StructDeclaration declaration)
    {
        var packed = false;
        uint? alignment = null;
        foreach (var annotation in declaration.DeclaredAnnotations)
        {
            switch (annotation.Name)
            {
                case "packed" when annotation.Arguments.Count == 0:
                    packed = true;
                    break;
                case "align" when annotation.Arguments.Count == 1
                    && uint.TryParse(annotation.Arguments[0], out var value)
                    && value > 0 && (value & (value - 1)) == 0 && value <= 4096:
                    alignment = value;
                    break;
                case "packed":
                    throw Error("Annotation '@packed' does not accept arguments", annotation.Location);
                case "align":
                    throw Error("Annotation '@align' expects one power-of-two byte alignment from 1 through 4096", annotation.Location);
                default:
                    throw Error($"Unknown struct annotation '@{annotation.Name}'", annotation.Location);
            }
        }
        return (packed, alignment);
    }

    private void ValidateStructLayout(StructSymbol structure, HashSet<string> active)
    {
        if (!active.Add(structure.Name))
            throw DeclarationError(structure.Name, $"Struct '{structure.Name}' contains a recursive by-value field", structure.Declaration.Location,
                structure.Name.Length);
        foreach (var field in structure.Fields.Where(field => field.Type.IsStruct))
            ValidateStructLayout(_structsByName[field.Type.StructName!], active);
        active.Remove(structure.Name);
    }

}
