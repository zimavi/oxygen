using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

// Types: resolving type syntax, generic type parameters and struct layout.
public sealed partial class SemanticAnalyzer
{
    // The type of something that holds a value, such as a field, a parameter or an array element. It cannot be void.
    private SemanticType ResolveValueType(TypeSyntax syntax, string subject, SourceLocation location)
    {
        var type = ResolveType(syntax);
        if (type.IsVoid)
            throw Error($"{subject} cannot have type void", location);
        return type;
    }

    // Resolves a type syntax in the current scope, and records the result on the node for the backend.
    private SemanticType ResolveType(TypeSyntax syntax)
    {
        var resolved = ResolveTypeCore(syntax);
        _resolvedTypeSyntax[syntax] = resolved;
        return resolved;
    }

    private SemanticType ResolveTypeCore(TypeSyntax syntax)
    {
        if (syntax.IsInferred)
        {
            throw Error("Type inference is not valid in this context", syntax.Location);
        }

        if (syntax.IsArray)
            return SemanticType.ArrayOf(ResolveValueType(syntax.ElementType!, "An array element", syntax.ElementType!.Location),
                syntax.ArrayLength!.Value);
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
            // The name denotes a type of the current module or of one it imports; the type is keyed by its qualified name.
            var visibleName = FindVisibleTypeKey(syntax.NamedType, syntax.Location);
            if (visibleName is not null && _aliasDeclarations.ContainsKey(visibleName))
            {
                // An alias is the type it names, so it has no type arguments of its own.
                if (syntax.DeclaredTypeArguments.Count != 0)
                    throw Error($"Type alias '{syntax.NamedType}' cannot have type arguments", syntax.Location, syntax.NamedType.Length);
                return AliasType(visibleName);
            }
            var typeName = visibleName
                ?? throw ErrorWithCode(DiagnosticCode.NotDeclared, $"Struct '{syntax.NamedType}' is not declared", syntax.Location, syntax.NamedType.Length);
            if (_enumsByName.ContainsKey(typeName))
            {
                if (syntax.IsPointer)
                    return SemanticType.PointerTo(SemanticType.Enum(typeName));
                return SemanticType.Enum(typeName);
            }
            if (_unionsByName.ContainsKey(typeName))
            {
                ValidateTypeArgumentCount(syntax, _unionsByName[typeName].DeclaredTypeParameters.Count);
                var union = SemanticType.Union(typeName, ResolveTypeArguments(syntax));
                if (syntax.IsPointer)
                    return SemanticType.PointerTo(union);
                return union;
            }
            if (_classesByName.ContainsKey(typeName))
            {
                ValidateTypeArgumentCount(syntax, _classesByName[typeName].DeclaredTypeParameters.Count);
                var namedClass = SemanticType.Class(typeName, ResolveTypeArguments(syntax));
                if (namedClass.TypeArguments is not null)
                    ValidateTypeArgumentConstraints(_classesByName[typeName].Declaration.DeclaredTypeParameters,
                        namedClass.TypeArguments, syntax.Location);
                return syntax.IsPointer ? SemanticType.PointerTo(namedClass) : namedClass;
            }
            if (_interfacesByName.ContainsKey(typeName))
            {
                ValidateTypeArgumentCount(syntax, _interfacesByName[typeName].DeclaredTypeParameters.Count);
                var namedInterface = SemanticType.Interface(typeName, ResolveTypeArguments(syntax));
                return syntax.IsPointer ? SemanticType.PointerTo(namedInterface) : namedInterface;
            }
            ValidateTypeArgumentCount(syntax, _structsByName[typeName].DeclaredTypeParameters.Count);
            var named = SemanticType.Struct(typeName, ResolveTypeArguments(syntax));
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
            throw ErrorWithCode(DiagnosticCode.ArgumentCount, $"Type '{syntax.NamedType}' expects {expected} type argument(s), but received " +
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
            throw DeclarationError(structure.QualifiedName, $"Struct '{structure.Name}' contains a recursive by-value field", structure.Declaration.Location,
                structure.Name.Length);
        foreach (var field in structure.Fields.Where(field => field.Type.IsStruct))
            ValidateStructLayout(_structsByName[field.Type.StructName!], active);
        active.Remove(structure.Name);
    }

}
