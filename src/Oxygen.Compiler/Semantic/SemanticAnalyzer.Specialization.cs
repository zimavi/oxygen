using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

// Specialization: builds the concrete class, interface, method, union and struct symbols for one set of type arguments.
public sealed partial class SemanticAnalyzer
{
    // Lowers an analyzed class instantiation to a concrete class named `name`: member bodies are
    // copied from the instantiation (whose expressions are already typed for these arguments) and
    // aggregate type uses are renamed to their specialized names.
    private ClassSymbol SpecializeClass(
        ClassSymbol source,
        ClassInstantiation instantiation,
        string name,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames,
        Dictionary<(FunctionDeclaration Declaration, string Owner), FunctionSymbol> specializedMethods,
        Dictionary<FunctionDeclaration, FunctionDeclaration> specializedDeclarations,
        Dictionary<Expression, SemanticType> expressionTypes,
        Dictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var declaration = source.Declaration;
        Expression? FieldInitializer(ClassFieldDeclaration field) =>
            instantiation.FieldInitializers.TryGetValue(field, out var initializer)
                ? CloneExpression(initializer, substitutions, aggregateNames, expressionTypes, source, resolvedCalls)
                : null;
        var fields = source.Fields.Select(field =>
        {
            var fieldDeclaration = field.Declaration with
            {
                Type = RewriteTypeSyntax(field.Declaration.Type, substitutions, aggregateNames),
                Initializer = FieldInitializer(field.Declaration)
            };
            return field with
            {
                Type = NormalizeAggregate(SubstituteAggregate(field.Type, substitutions, aggregateNames), aggregateNames),
                Declaration = fieldDeclaration
            };
        }).ToArray();
        var properties = source.DeclaredProperties.Select(property => property with
        {
            Type = NormalizeAggregate(SubstituteAggregate(property.Type, substitutions, aggregateNames), aggregateNames),
            Declaration = CloneProperty(property.Declaration, substitutions, aggregateNames, expressionTypes,
                source, resolvedCalls)
        }).ToArray();
        var methods = source.Methods.Select(method =>
            SpecializeMethod(method, instantiation.Methods.GetValueOrDefault(method.Declaration) ?? method.Declaration,
                $"{source.ModuleName}.{name}", substitutions, aggregateNames,
                specializedMethods, expressionTypes, resolvedCalls, $"{source.ModuleName}.{name}")).ToArray();
        var staticMethods = source.StaticMethods.Select(method =>
            SpecializeMethod(method, instantiation.Methods.GetValueOrDefault(method.Declaration) ?? method.Declaration,
                $"{source.ModuleName}.{name}", substitutions, aggregateNames,
                specializedMethods, expressionTypes, resolvedCalls, $"{source.ModuleName}.{name}")).ToArray();
        var constructors = source.Constructors.Select(constructor =>
        {
            var instantiated = instantiation.Constructors.GetValueOrDefault(constructor.Declaration)
                ?? constructor.Declaration;
            var body = CloneBlock(instantiated.Body, substitutions, aggregateNames, expressionTypes, source, resolvedCalls);
            var initializer = instantiated.Initializer is null
                ? null
                : CloneInitializer(instantiated.Initializer, substitutions, aggregateNames, expressionTypes, source,
                    resolvedCalls);
            var constructorDeclaration = constructor.Declaration with
            {
                ClassName = name,
                Parameters = constructor.Parameters.Select(parameter => parameter.Declaration with
                {
                    Type = RewriteTypeSyntax(parameter.Declaration.Type, substitutions, aggregateNames)
                }).ToArray(),
                Body = body,
                Initializer = initializer
            };
            if (_constructorChainTargets.TryGetValue(instantiated, out var chainTarget))
                _constructorChainTargets[constructorDeclaration] = chainTarget;
            return constructor with
            {
                ClassName = name,
                Parameters = constructor.Parameters.Select(parameter => parameter with
                {
                    Type = NormalizeAggregate(SubstituteAggregate(parameter.Type, substitutions, aggregateNames),
                        aggregateNames),
                    Declaration = constructorDeclaration.Parameters.First(item => item.Name == parameter.Name)
                }).ToArray(),
                Declaration = constructorDeclaration
            };
        }).ToArray();
        var destructor = source.Destructor is null
            ? null
            : source.Destructor with
            {
                ClassName = name,
                Body = CloneBlock((instantiation.Destructor ?? source.Destructor).Body, substitutions, aggregateNames,
                    expressionTypes, source, resolvedCalls)
            };
        var rewrittenBaseType = source.Declaration.BaseTypeSyntax is { Count: > 0 }
            ? RewriteTypeSyntax(source.Declaration.BaseTypeSyntax[0], substitutions, aggregateNames)
            : null;
        var baseType = rewrittenBaseType?.NamedType ?? source.BaseTypeName;
        var interfaceTypes = source.DeclaredInterfaceTypes.Select(type =>
            NormalizeAggregate(SubstituteAggregate(type, substitutions, aggregateNames), aggregateNames)).ToArray();
        var interfaceNames = interfaceTypes.Select(type => type.StructName ?? type.EnumName!).ToArray();
        var classDeclaration = declaration with
        {
            Name = name,
            BaseType = baseType,
            BaseTypeSyntax = rewrittenBaseType is null ? null : [rewrittenBaseType],
            Interfaces = interfaceNames,
            InterfaceTypeSyntaxes = interfaceTypes
                .Select(type => TypeSyntaxFromSemantic(type, declaration.Location)).ToArray(),
            Fields = declaration.Fields.Select(field => field with
            {
                Type = RewriteTypeSyntax(field.Type, substitutions, aggregateNames),
                Initializer = FieldInitializer(field)
            }).ToArray(),
            Properties = declaration.DeclaredProperties.Select(property =>
                CloneProperty(property, substitutions, aggregateNames, expressionTypes, source, resolvedCalls)).ToArray(),
            Methods = methods.Concat(staticMethods).Select(method => method.Declaration).ToArray(),
            Constructors = constructors.Select(constructor => constructor.Declaration).ToArray(),
            Destructor = destructor,
            TypeParameters = []
        };
        return source with
        {
            Name = name,
            BaseTypeName = baseType,
            InterfaceNames = interfaceNames,
            Fields = fields,
            Methods = methods,
            StaticMethods = staticMethods,
            StaticFields = InstanceStaticFields(source, substitutions),
            Constructors = constructors,
            Destructor = destructor,
            Declaration = classDeclaration,
            TypeParameters = [],
            Properties = properties,
            InterfaceTypes = interfaceTypes
        };
    }

    private InterfaceSymbol SpecializeInterface(
        InterfaceSymbol source,
        string name,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames,
        Dictionary<(FunctionDeclaration Declaration, string Owner), FunctionSymbol> specializedMethods,
        Dictionary<FunctionDeclaration, FunctionDeclaration> specializedDeclarations,
        Dictionary<Expression, SemanticType> expressionTypes,
        Dictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var methods = source.Methods.Select(method =>
            SpecializeMethod(method, method.Declaration, $"{source.ModuleName}.{name}", substitutions, aggregateNames,
                specializedMethods, expressionTypes, resolvedCalls, $"{source.ModuleName}.{name}")).ToArray();
        var properties = source.DeclaredProperties.Select(property => property with
        {
            Type = NormalizeAggregate(SubstituteAggregate(property.Type, substitutions, aggregateNames), aggregateNames),
            Declaration = CloneProperty(property.Declaration, substitutions, aggregateNames, expressionTypes,
                source, resolvedCalls)
        }).ToArray();
        return source with
        {
            Name = name,
            Methods = methods,
            Properties = properties,
            TypeParameters = []
        };
    }

    // `body` is the declaration to lower: the analyzed instantiation of a class method, or the
    // generic declaration itself for interface contracts, which have no bodies to analyze.
    private FunctionSymbol SpecializeMethod(
        FunctionSymbol source,
        FunctionDeclaration body,
        string moduleName,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames,
        Dictionary<(FunctionDeclaration Declaration, string Owner), FunctionSymbol> specializedMethods,
        Dictionary<Expression, SemanticType> expressionTypes,
        Dictionary<CallExpression, FunctionSymbol> resolvedCalls,
        string owner)
    {
        var declaration = CloneFunctionAggregate(body, substitutions, aggregateNames,
            expressionTypes, source, resolvedCalls);
        var parameters = source.Parameters.Select(parameter => parameter with
        {
            Type = NormalizeAggregate(SubstituteAggregate(parameter.Type, substitutions, aggregateNames), aggregateNames),
            Declaration = declaration.Parameters.First(item => item.Name == parameter.Name)
        }).ToArray();
        var symbol = source with
        {
            ReturnType = NormalizeAggregate(SubstituteAggregate(source.ReturnType, substitutions, aggregateNames), aggregateNames),
            Parameters = parameters,
            Declaration = declaration,
            ModuleName = moduleName,
            GenericParameters = [],
            SpecializationArguments = null
        };
        specializedMethods[(source.Declaration, owner)] = symbol;
        return symbol;
    }

    private static UnionSymbol SpecializeUnion(
        UnionSymbol source,
        string name,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames) =>
        source with
        {
            Name = name,
            Cases = source.Cases.Select(item => item with
            {
                PayloadType = NormalizeAggregate(SubstituteAggregate(item.PayloadType, substitutions, aggregateNames),
                    aggregateNames)
            }).ToArray(),
            TypeParameters = []
        };

    private static StructSymbol SpecializeStruct(
        StructSymbol source,
        string name,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames) =>
        source with
        {
            Name = name,
            Fields = source.Fields.Select(field => field with
            {
                Type = NormalizeAggregate(SubstituteAggregate(field.Type, substitutions, aggregateNames), aggregateNames)
            }).ToArray(),
            TypeParameters = []
        };

}
