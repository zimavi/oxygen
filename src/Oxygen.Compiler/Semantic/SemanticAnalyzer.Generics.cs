using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

// Generics: substitution, inference and specialization of generic functions and classes.
public sealed partial class SemanticAnalyzer
{
    private static FunctionSymbol SubstituteFunctionTypes(
        FunctionSymbol function,
        IReadOnlyDictionary<string, SemanticType> substitutions) =>
        function with
        {
            ReturnType = SubstituteGenericType(function.ReturnType, substitutions),
            Parameters = function.Parameters
                .Select(parameter => parameter with { Type = SubstituteGenericType(parameter.Type, substitutions) })
                .ToArray()
        };

    private static void InferClassTypeArguments(
        SemanticType pattern,
        SemanticType actual,
        IReadOnlyList<string> parameters,
        IDictionary<string, SemanticType> substitutions)
    {
        if (pattern.IsGenericParameter && parameters.Contains(pattern.GenericParameterName!, StringComparer.Ordinal))
        {
            if (substitutions.TryGetValue(pattern.GenericParameterName!, out var existing))
            {
                if (existing != actual)
                    substitutions.Remove(pattern.GenericParameterName!);
            }
            else
                substitutions[pattern.GenericParameterName!] = actual;
            return;
        }

        if (pattern.IsPointer && actual.IsPointer)
        {
            InferClassTypeArguments(pattern.Pointee!, actual.Pointee!, parameters, substitutions);
            return;
        }

        if (pattern.Kind != actual.Kind || pattern.StructName != actual.StructName)
            return;
        var patternArguments = pattern.TypeArguments ?? [];
        var actualArguments = actual.TypeArguments ?? [];
        foreach (var pair in patternArguments.Zip(actualArguments))
            InferClassTypeArguments(pair.First, pair.Second, parameters, substitutions);
    }

    private static IReadOnlyDictionary<string, SemanticType>? InferGenericSubstitutions(
        FunctionSymbol generic,
        IReadOnlyList<SemanticType> argumentTypes,
        SemanticType? expectedReturnType)
    {
        var substitutions = new Dictionary<string, SemanticType>(StringComparer.Ordinal);
        for (var index = 0; index < generic.Parameters.Count; index++)
        {
            var syntax = generic.Declaration.Parameters[index].Type;
            var actual = argumentTypes[index];
            if (!ContainsGenericType(syntax, generic.DeclaredGenericParameters))
            {
                if (generic.Parameters[index].Type != actual)
                    return null;
                continue;
            }
            while (syntax.IsPointer)
            {
                if (!actual.IsPointer || actual.Pointee is null || syntax.Pointee is null)
                    return null;

                syntax = syntax.Pointee;
                actual = actual.Pointee;
            }

            if (!TryInferGenericType(syntax, actual, generic.DeclaredGenericParameters, substitutions))
                return null;
        }

        if (expectedReturnType is not null)
        {
            if (!ContainsGenericType(generic.Declaration.ReturnType, generic.DeclaredGenericParameters))
            {
                if (generic.ReturnType != expectedReturnType)
                    return null;
            }
            else if (!TryInferGenericType(generic.Declaration.ReturnType, expectedReturnType,
                         generic.DeclaredGenericParameters, substitutions))
                return null;
        }
        return substitutions.Count == generic.DeclaredGenericParameters.Count ? substitutions : null;
    }

    private static bool ContainsGenericType(TypeSyntax syntax, IReadOnlyList<string> genericParameters) =>
        syntax.NamedType is not null && genericParameters.Contains(syntax.NamedType)
        || syntax.Pointee is not null && ContainsGenericType(syntax.Pointee, genericParameters)
        || syntax.ElementType is not null && ContainsGenericType(syntax.ElementType, genericParameters)
        || syntax.TypeArguments is not null && syntax.TypeArguments.Any(argument =>
            ContainsGenericType(argument, genericParameters));

    private static bool TryInferGenericType(
        TypeSyntax syntax,
        SemanticType actual,
        IReadOnlyList<string> genericParameters,
        IDictionary<string, SemanticType> substitutions)
    {
        while (syntax.IsPointer)
        {
            if (!actual.IsPointer || actual.Pointee is null || syntax.Pointee is null)
                return false;
            syntax = syntax.Pointee;
            actual = actual.Pointee;
        }

        if (syntax.NamedType is null || !genericParameters.Contains(syntax.NamedType))
            return false;
        if (substitutions.TryGetValue(syntax.NamedType, out var existing))
            return existing == actual;
        substitutions[syntax.NamedType] = actual;
        return true;
    }

    private static SemanticType SubstituteGenericType(
        SemanticType type,
        IReadOnlyDictionary<string, SemanticType> substitutions) =>
        type.IsGenericParameter && substitutions.TryGetValue(type.GenericParameterName!, out var replacement)
            ? replacement
            : type.IsPointer && type.Pointee is not null
                ? SemanticType.PointerTo(SubstituteGenericType(type.Pointee, substitutions))
            : type.IsArray && type.ElementType is not null
                ? SemanticType.ArrayOf(SubstituteGenericType(type.ElementType, substitutions), type.ArrayLength)
            : type.TypeArguments is { Count: > 0 }
                ? type with
                {
                    TypeArguments = type.TypeArguments
                        .Select(argument => SubstituteGenericType(argument, substitutions))
                        .ToArray()
                }
                : type;

    private FunctionSymbol SpecializeFunction(
        FunctionSymbol generic,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        SourceLocation location)
    {
        ValidateTypeArgumentConstraints(generic.Declaration.DeclaredTypeParameters,
            generic.DeclaredGenericParameters.Select(name => substitutions[name]).ToArray(), location);
        var declaration = CloneFunction(generic.Declaration, substitutions);
        var previous = _genericTypes;
        _genericTypes = substitutions;
        var specializationArguments = generic.DeclaredGenericParameters
            .Select(parameter => substitutions[parameter])
            .ToArray();
        var symbol = new FunctionSymbol(
            generic.Name,
            ResolveType(declaration.ReturnType),
            declaration.Parameters.Select(parameter =>
                new ParameterSymbol(parameter.Name, ResolveType(parameter.Type), parameter)).ToArray(),
            declaration, generic.ModuleName, generic.Visibility, generic.NativeName, false, generic.CInterop, [],
            specializationArguments);
        _genericTypes = previous;
        _usedFunctions.Add(generic);
        // Inside a generic body (e.g. `alloc<T>` in `List<T>`) the arguments are still type
        // parameters. The signature is enough to type-check that body; the concrete
        // specialization is created when the enclosing generic is instantiated.
        if (IsOpenSpecialization(symbol))
            return symbol;
        var mutable = (Dictionary<string, List<FunctionSymbol>>)functions;
        var list = mutable[generic.QualifiedName];
        var existing = list.FirstOrDefault(candidate =>
            candidate.IsSpecialized
            && candidate.SpecializationArguments!.SequenceEqual(specializationArguments)
            && HasSameParameterTypes(candidate, symbol));
        if (existing is not null)
            return existing;
        list.Add(symbol);
        _functionSymbols?.Add(symbol.Declaration, symbol);
        return symbol;
    }

    private static FunctionDeclaration CloneFunction(
        FunctionDeclaration source, IReadOnlyDictionary<string, SemanticType> substitutions) =>
        new InstantiationCloner(substitutions).Function(source, substitutesOwnTypeParameters: true);

}
