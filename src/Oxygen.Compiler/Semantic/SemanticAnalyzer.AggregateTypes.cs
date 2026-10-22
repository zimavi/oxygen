using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

// Aggregate types: names and normalizes generic aggregate types and converts them to and from syntax.
public sealed partial class SemanticAnalyzer
{
    private SemanticType? ConcreteType(TypeSyntax syntax, IReadOnlySet<string> genericParameters)
    {
        if (syntax.IsInferred)
            return null;
        if (syntax.IsArray)
            return SemanticType.ArrayOf(ConcreteType(syntax.ElementType!, genericParameters)!, syntax.ArrayLength!.Value);
        if (syntax.IsString)
            return SemanticType.String;
        if (syntax.IsPointer)
            return SemanticType.PointerTo(ConcreteType(syntax.Pointee!, genericParameters)!);
        if (syntax.NamedType is not null)
        {
            if (genericParameters.Contains(syntax.NamedType))
                return SemanticType.GenericParameter(syntax.NamedType);
            // An alias is the type it names, so a use of it is that type, whatever the name is in the generated names.
            if (VisibleTypeKeyOrNull(syntax.NamedType) is { } aliasKey && _aliasDeclarations.ContainsKey(aliasKey))
                return AliasType(aliasKey);
            var arguments = syntax.DeclaredTypeArguments
                .Select(item => ConcreteType(item, genericParameters)).ToArray();
            IReadOnlyList<SemanticType>? concreteArguments = arguments.Any(item => item is null)
                ? null
                : arguments.Select(item => item!).ToArrayOrNull();
            if (TypeNameOrNull(syntax.NamedType) is not { } typeName)
                return null;
            if (_classesByName.ContainsKey(typeName))
                return SemanticType.Class(typeName, concreteArguments);
            if (_interfacesByName.ContainsKey(typeName))
                return SemanticType.Interface(typeName, concreteArguments);
            if (_unionsByName.ContainsKey(typeName))
                return SemanticType.Union(typeName, concreteArguments);
            if (_structsByName.ContainsKey(typeName))
                return SemanticType.Struct(typeName, concreteArguments);
            return null;
        }
        return syntax.Primitive is null ? null : SemanticType.FromPrimitive(syntax.Primitive.Value);
    }

    private TypeSyntax RewriteTypeSyntax(TypeSyntax syntax,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames)
    {
        if (syntax.NamedType is not null && substitutions.TryGetValue(syntax.NamedType, out var replacement)
            && syntax.DeclaredTypeArguments.Count == 0)
            return TypeSyntaxFromSemantic(replacement, syntax.Location);
        // An alias is rewritten as the type it names, so the generated code refers to that type and not to a struct of the alias's name.
        if (syntax.NamedType is not null && VisibleTypeKeyOrNull(syntax.NamedType) is { } aliasKey && _aliasDeclarations.ContainsKey(aliasKey))
            return TypeSyntaxFromSemantic(AliasType(aliasKey), syntax.Location);
        if (syntax.IsPointer)
            return syntax with { Pointee = RewriteTypeSyntax(syntax.Pointee!, substitutions, aggregateNames) };
        if (syntax.IsArray)
            return syntax with { ElementType = RewriteTypeSyntax(syntax.ElementType!, substitutions, aggregateNames) };
        if (syntax.NamedType is not null)
        {
            var arguments = syntax.DeclaredTypeArguments
                .Select(item => RewriteTypeSyntax(item, substitutions, aggregateNames)).ToArray();
            // The specialization is keyed by the structural arguments (`Cell<i32>`), not by the
            // already renamed ones (`Cell__ox_i32`).
            // The key names the qualified type, as the model does, so the source name is resolved in the current scope.
            var typeName = TypeNameOrNull(syntax.NamedType) ?? syntax.NamedType;
            var baseType = new SemanticType(
                SemanticTypeKind.Struct, null, null, typeName,
                null, null) { TypeArguments = syntax.DeclaredTypeArguments.Select(item =>
                    SemanticTypeFromSyntax(item, substitutions, aggregateNames)).ToArray() };
            var kinds = new[] { SemanticTypeKind.Class, SemanticTypeKind.Interface, SemanticTypeKind.Union, SemanticTypeKind.Struct };
            foreach (var kind in kinds)
            {
                var candidate = baseType with { Kind = kind, EnumName = kind == SemanticTypeKind.Union ? typeName : null,
                    StructName = kind == SemanticTypeKind.Union ? null : typeName };
                if (aggregateNames.TryGetValue(AggregateKey(candidate), out var name))
                    return new TypeSyntax(null, false, syntax.Location, NamedType: name);
            }
            // A name that no aggregate specialization covers keeps its type, now named by its qualified name in the current scope.
            return syntax with { NamedType = TypeNameOrNull(syntax.NamedType) ?? syntax.NamedType, TypeArguments = arguments };
        }
        return syntax;
    }

    private SemanticType SemanticTypeFromSyntax(TypeSyntax syntax,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames) =>
        syntax.NamedType is not null && substitutions.TryGetValue(syntax.NamedType, out var replacement)
            ? replacement
            : syntax.Primitive is not null
                ? SemanticType.FromPrimitive(syntax.Primitive.Value)
                : syntax.IsString ? SemanticType.String
                : syntax.NamedType is not null
                    ? AggregateTypeFromSyntax(syntax, substitutions, aggregateNames)
                    : SemanticType.String;

    private SemanticType AggregateTypeFromSyntax(
        TypeSyntax syntax,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames)
    {
        if (VisibleTypeKeyOrNull(syntax.NamedType!) is { } aliasKey && _aliasDeclarations.ContainsKey(aliasKey))
            return AliasType(aliasKey);
        var arguments = syntax.DeclaredTypeArguments
            .Select(item => SemanticTypeFromSyntax(item, substitutions, aggregateNames)).ToArrayOrNull();
        var typeName = TypeNameOrNull(syntax.NamedType!) ?? syntax.NamedType!;
        foreach (var kind in new[]
                 {
                     SemanticTypeKind.Class, SemanticTypeKind.Interface,
                     SemanticTypeKind.Union, SemanticTypeKind.Struct
                 })
        {
            var candidate = kind == SemanticTypeKind.Union
                ? SemanticType.Union(typeName, arguments)
                : kind == SemanticTypeKind.Class
                    ? SemanticType.Class(typeName, arguments)
                    : kind == SemanticTypeKind.Interface
                        ? SemanticType.Interface(typeName, arguments)
                        : SemanticType.Struct(typeName, arguments);
            if (aggregateNames.ContainsKey(AggregateKey(candidate)))
                return candidate;
        }
        return SemanticType.Struct(typeName, arguments);
    }

    private static bool ContainsGenericParameter(SemanticType type) =>
        type.IsGenericParameter
        || type.IsPointer && ContainsGenericParameter(type.Pointee!)
        || type.IsArray && ContainsGenericParameter(type.ElementType!)
        || (type.TypeArguments ?? []).Any(ContainsGenericParameter);

    private bool IsGenericAggregate(string name, SemanticTypeKind kind) =>
        kind switch
        {
            SemanticTypeKind.Class => _classesByName.TryGetValue(name, out var cls) && cls.IsGeneric,
            SemanticTypeKind.Interface => _interfacesByName.TryGetValue(name, out var iface) && iface.IsGeneric,
            SemanticTypeKind.Union => _unionsByName.TryGetValue(name, out var union) && union.IsGeneric,
            SemanticTypeKind.Struct => _structsByName.TryGetValue(name, out var structure) && structure.IsGeneric,
            _ => false
        };

    private IReadOnlyList<string> DeclaredTypeParameters(string name, SemanticTypeKind kind) =>
        kind switch
        {
            SemanticTypeKind.Class => _classesByName[name].DeclaredTypeParameters,
            SemanticTypeKind.Interface => _interfacesByName[name].DeclaredTypeParameters,
            SemanticTypeKind.Union => _unionsByName[name].DeclaredTypeParameters,
            SemanticTypeKind.Struct => _structsByName[name].DeclaredTypeParameters,
            _ => []
        };

    private static string? AggregateBaseName(SemanticType type) =>
        type.Kind switch
        {
            SemanticTypeKind.Class or SemanticTypeKind.Interface or SemanticTypeKind.Struct => type.StructName,
            SemanticTypeKind.Union => type.EnumName,
            _ => null
        };

    // Uses the same separator as SemanticType.ToString so the key reads the same at every nesting
    // depth; the balanced angle brackets keep distinct argument groupings distinct. The arguments are qualified, so
    // types with one simple name in two modules have different keys.
    private static string AggregateKey(SemanticType type) =>
        $"{type.Kind}:{AggregateBaseName(type)}<{string.Join(", ", (type.TypeArguments ?? []).Select(argument => argument.ToQualifiedString()))}>";

    // Aggregate arguments are encoded by their own specialized name with a length prefix, so
    // `Box<Cell<i32>>` and `Box<Cell<i64>>` differ, and `Box<Cell<i32>, i64>` cannot collide with
    // `Box<Cell<i32, i64>>`.
    private static string EncodeAggregateArguments(IReadOnlyList<SemanticType> arguments) =>
        string.Join("_", arguments.Select(argument =>
        {
            if (argument.Kind == SemanticTypeKind.Primitive)
                return argument.Primitive!.Value.ToString().ToLowerInvariant();
            if (argument.Kind == SemanticTypeKind.Pointer)
                return "p" + EncodeAggregateArguments([argument.Pointee!]);
            var baseName = AggregateBaseName(argument);
            if (baseName is null)
                return argument.ToQualifiedString().Replace(" ", string.Empty);
            var name = argument.TypeArguments is { Count: > 0 }
                ? $"{baseName}__ox_{EncodeAggregateArguments(argument.TypeArguments)}"
                : baseName;
            return $"{name.Length}{name}";
        }));

    private static SemanticType SubstituteAggregate(
        SemanticType? type,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames)
    {
        if (type is null)
            return SemanticType.FromPrimitive(PrimitiveType.Void);
        if (type.IsGenericParameter && substitutions.TryGetValue(type.GenericParameterName!, out var replacement))
            return replacement;
        if (type.IsPointer)
            return SemanticType.PointerTo(SubstituteAggregate(type.Pointee, substitutions, aggregateNames));
        if (type.IsArray)
            return SemanticType.ArrayOf(SubstituteAggregate(type.ElementType, substitutions, aggregateNames), type.ArrayLength);
        if (type.TypeArguments is not { Count: > 0 })
            return type;
        // Look the specialization up by its structural arguments before inner arguments are
        // renamed: `Box<Cell<i32>>` is registered under that key, not under `Box<Cell__ox_i32>`.
        var structural = type with
        {
            TypeArguments = type.TypeArguments.Select(argument =>
                SubstituteAggregate(argument, substitutions, NoAggregateNames)).ToArray()
        };
        if (aggregateNames.TryGetValue(AggregateKey(structural), out var specialized))
            return structural.Kind == SemanticTypeKind.Union
                ? structural with { EnumName = specialized, TypeArguments = null }
                : structural with { StructName = specialized, TypeArguments = null };
        return structural with
        {
            TypeArguments = structural.TypeArguments!.Select(argument =>
                NormalizeAggregate(argument, aggregateNames)).ToArray()
        };
    }

    private static readonly IReadOnlyDictionary<string, string> NoAggregateNames =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static SemanticType NormalizeAggregate(
        SemanticType type, IReadOnlyDictionary<string, string> aggregateNames)
    {
        if (type.IsPointer)
            return SemanticType.PointerTo(NormalizeAggregate(type.Pointee!, aggregateNames));
        if (type.IsArray)
            return SemanticType.ArrayOf(NormalizeAggregate(type.ElementType!, aggregateNames), type.ArrayLength);
        if (type.TypeArguments is not { Count: > 0 })
            return type;
        if (aggregateNames.TryGetValue(AggregateKey(type), out var directSpecialized))
            return type.Kind == SemanticTypeKind.Union
                ? type with { EnumName = directSpecialized, TypeArguments = null }
                : type with { StructName = directSpecialized, TypeArguments = null };
        var normalized = type with
        {
            TypeArguments = type.TypeArguments.Select(argument => NormalizeAggregate(argument, aggregateNames)).ToArray()
        };
        if (!aggregateNames.TryGetValue(AggregateKey(normalized), out var specialized))
            return normalized;
        return normalized.Kind == SemanticTypeKind.Union
            ? normalized with { EnumName = specialized, TypeArguments = null }
            : normalized with { StructName = specialized, TypeArguments = null };
    }

    private static string RewriteNamedAggregate(
        string name,
        SemanticTypeKind kind,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames)
    {
        if (!aggregateNames.Values.Contains(name, StringComparer.Ordinal))
            return name;
        return name;
    }

    private static TypeSyntax TypeSyntaxFromSemantic(SemanticType type, SourceLocation location) =>
        type.IsPointer
            ? new TypeSyntax(null, false, location, Pointee: TypeSyntaxFromSemantic(type.Pointee!, location))
            : type.IsArray
                ? new TypeSyntax(null, false, location, ArrayLength: type.ArrayLength,
                    ElementType: TypeSyntaxFromSemantic(type.ElementType!, location))
                : type.Kind == SemanticTypeKind.Primitive
                    ? new TypeSyntax(type.Primitive, false, location)
                    : type.Kind == SemanticTypeKind.String
                        ? new TypeSyntax(null, false, location, IsString: true)
                        : new TypeSyntax(null, false, location,
                            NamedType: type.StructName ?? type.EnumName,
                            TypeArguments: type.TypeArguments?.Select(argument =>
                                TypeSyntaxFromSemantic(argument, location)).ToArray());

}
