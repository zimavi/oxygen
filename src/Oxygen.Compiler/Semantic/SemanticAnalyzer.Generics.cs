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
