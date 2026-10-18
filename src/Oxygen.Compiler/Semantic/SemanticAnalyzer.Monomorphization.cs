using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

public sealed partial class SemanticAnalyzer
{
    private IReadOnlyDictionary<string, string> MonomorphizeAggregates(
        IReadOnlyList<CompilationUnit> units,
        Dictionary<FunctionDeclaration, FunctionSymbol> functions,
        Dictionary<string, List<FunctionSymbol>> functionsByName,
        Dictionary<Expression, SemanticType> expressionTypes,
        Dictionary<CallExpression, FunctionSymbol> resolvedCalls,
        FunctionSymbol? mainFunction)
    {
        var concreteTypes = new Dictionary<string, SemanticType>(StringComparer.Ordinal);
        var aggregateNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var instantiations = new Dictionary<string, ClassInstantiation>(StringComparer.Ordinal);
        var pending = new Queue<SemanticType>();

        void AddType(SemanticType? type)
        {
            if (type is null || type.IsGenericParameter)
                return;
            if (type.IsPointer)
            {
                AddType(type.Pointee);
                return;
            }
            if (type.IsArray)
            {
                AddType(type.ElementType);
                return;
            }

            foreach (var argument in type.TypeArguments ?? [])
                AddType(argument);
            if (type.Kind is not (SemanticTypeKind.Class or SemanticTypeKind.Interface
                or SemanticTypeKind.Union or SemanticTypeKind.Struct))
                return;
            var name = AggregateBaseName(type);
            if (name is null || !IsGenericAggregate(name, type.Kind)
                || type.TypeArguments is not { Count: > 0 }
                || type.TypeArguments.Any(ContainsGenericParameter))
                return;
            var key = AggregateKey(type);
            if (concreteTypes.TryAdd(key, type))
                pending.Enqueue(type);
        }

        void AddSyntax(TypeSyntax syntax, IReadOnlySet<string> genericParameters)
        {
            var type = ConcreteType(syntax, genericParameters);
            AddType(type);
            if (syntax.Pointee is not null)
                AddSyntax(syntax.Pointee, genericParameters);
            if (syntax.ElementType is not null)
                AddSyntax(syntax.ElementType, genericParameters);
            foreach (var argument in syntax.DeclaredTypeArguments)
                AddSyntax(argument, genericParameters);
        }

        foreach (var type in expressionTypes.Values)
            AddType(type);
        foreach (var symbol in functions.Values.Concat(functionsByName.Values.SelectMany(item => item)))
        {
            AddType(symbol.ReturnType);
            foreach (var parameter in symbol.Parameters)
                AddType(parameter.Type);
        }
        foreach (var cls in _classesByName.Values)
        {
            foreach (var field in cls.Fields)
                AddType(field.Type);
            foreach (var property in cls.DeclaredProperties)
                AddType(property.Type);
            foreach (var method in cls.Methods)
            {
                AddType(method.ReturnType);
                foreach (var parameter in method.Parameters)
                    AddType(parameter.Type);
            }
            foreach (var constructor in cls.Constructors)
                foreach (var parameter in constructor.Parameters)
                    AddType(parameter.Type);
        }
        foreach (var iface in _interfacesByName.Values)
        {
            foreach (var property in iface.DeclaredProperties)
                AddType(property.Type);
            foreach (var method in iface.Methods)
            {
                AddType(method.ReturnType);
                foreach (var parameter in method.Parameters)
                    AddType(parameter.Type);
            }
        }
        foreach (var union in _unionsByName.Values)
            foreach (var item in union.Cases)
                AddType(item.PayloadType);

        foreach (var unit in units)
        {
            foreach (var structure in unit.Structs)
            {
                var parameters = structure.DeclaredTypeParameters.Select(item => item.Name)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var field in structure.Fields)
                    AddSyntax(field.Type, parameters);
            }
            foreach (var union in unit.Unions)
            {
                var parameters = union.DeclaredTypeParameters.Select(item => item.Name)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var item in union.Cases)
                    if (item.PayloadType is not null)
                        AddSyntax(item.PayloadType, parameters);
            }
            foreach (var iface in unit.Interfaces)
                CollectInterfaceSyntax(iface, AddSyntax);
            foreach (var cls in unit.Classes)
                CollectClassSyntax(cls, AddSyntax);
            foreach (var function in unit.Functions)
                CollectFunctionSyntax(function, AddSyntax);
        }

        while (pending.Count != 0)
        {
            var aggregate = pending.Dequeue();
            var name = AggregateBaseName(aggregate)!;
            aggregateNames[AggregateKey(aggregate)] =
                $"{name}__ox_{EncodeAggregateArguments(aggregate.TypeArguments!)}";
            var arguments = aggregate.TypeArguments!;
            var substitutions = DeclaredTypeParameters(name, aggregate.Kind)
                .Zip(arguments, (parameter, argument) => (parameter, argument))
                .ToDictionary(pair => pair.parameter, pair => pair.argument, StringComparer.Ordinal);
            if (aggregate.Kind == SemanticTypeKind.Class && _classesByName.TryGetValue(name, out var cls))
            {
                foreach (var field in cls.Fields)
                    AddType(SubstituteAggregate(field.Type, substitutions, aggregateNames));
                foreach (var property in cls.DeclaredProperties)
                    AddType(SubstituteAggregate(property.Type, substitutions, aggregateNames));
                foreach (var method in cls.Methods)
                {
                    AddType(SubstituteAggregate(method.ReturnType, substitutions, aggregateNames));
                    foreach (var parameter in method.Parameters)
                        AddType(SubstituteAggregate(parameter.Type, substitutions, aggregateNames));
                }
                foreach (var constructor in cls.Constructors)
                    foreach (var parameter in constructor.Parameters)
                        AddType(SubstituteAggregate(parameter.Type, substitutions, aggregateNames));
                AddType(SubstituteAggregate(cls.DeclaredInterfaceTypes.FirstOrDefault(), substitutions, aggregateNames));
                // The member bodies are analyzed for these concrete arguments. That types every
                // expression concretely, specializes the generic functions they call, and
                // reveals aggregates that only appear inside bodies (e.g. `Iterator(this)`).
                var instantiation = InstantiateClass(cls, aggregate, substitutions, functionsByName,
                    expressionTypes, resolvedCalls);
                instantiations[AggregateKey(aggregate)] = instantiation;
                foreach (var expression in instantiation.Expressions())
                    if (expressionTypes.TryGetValue(expression, out var expressionType))
                        AddType(expressionType);
            }
            else if (aggregate.Kind == SemanticTypeKind.Interface && _interfacesByName.TryGetValue(name, out var iface))
            {
                foreach (var property in iface.DeclaredProperties)
                    AddType(SubstituteAggregate(property.Type, substitutions, aggregateNames));
                foreach (var method in iface.Methods)
                {
                    AddType(SubstituteAggregate(method.ReturnType, substitutions, aggregateNames));
                    foreach (var parameter in method.Parameters)
                        AddType(SubstituteAggregate(parameter.Type, substitutions, aggregateNames));
                }
            }
            else if (aggregate.Kind == SemanticTypeKind.Union && _unionsByName.TryGetValue(name, out var union))
            {
                foreach (var item in union.Cases)
                    AddType(SubstituteAggregate(item.PayloadType, substitutions, aggregateNames));
            }
            else if (aggregate.Kind == SemanticTypeKind.Struct && _structsByName.TryGetValue(name, out var structure))
            {
                foreach (var field in structure.Fields)
                    AddType(SubstituteAggregate(field.Type, substitutions, aggregateNames));
            }

            foreach (var type in expressionTypes.Values)
                AddType(type);
        }

        foreach (var aggregate in concreteTypes.Values)
        {
            var baseName = AggregateBaseName(aggregate)!;
            aggregateNames.TryAdd(
                AggregateKey(aggregate),
                $"{baseName}__ox_{EncodeAggregateArguments(aggregate.TypeArguments!)}");
        }

        var specializedMethods = new Dictionary<(FunctionDeclaration Declaration, string Owner), FunctionSymbol>();
        var specializedDeclarations = new Dictionary<FunctionDeclaration, FunctionDeclaration>();
        var newExpressionTypes = new Dictionary<Expression, SemanticType>(ReferenceEqualityComparer.Instance);
        foreach (var pair in expressionTypes)
            newExpressionTypes[pair.Key] = NormalizeAggregate(
                SubstituteAggregate(pair.Value, new Dictionary<string, SemanticType>(), aggregateNames), aggregateNames);
        var newResolvedCalls = new Dictionary<CallExpression, FunctionSymbol>(resolvedCalls,
            ReferenceEqualityComparer.Instance);

        foreach (var aggregate in concreteTypes.Values)
        {
            var key = AggregateKey(aggregate);
            var specializedName = aggregateNames[key];
            var baseName = AggregateBaseName(aggregate)!;
            var substitutions = DeclaredTypeParameters(baseName, aggregate.Kind)
                .Zip(aggregate.TypeArguments!, (parameter, argument) => (parameter, argument))
                .ToDictionary(pair => pair.parameter, pair => pair.argument, StringComparer.Ordinal);

            if (aggregate.Kind == SemanticTypeKind.Class && _classesByName.TryGetValue(baseName, out var cls))
            {
                var specialized = SpecializeClass(
                    cls, instantiations[key], specializedName, substitutions, aggregateNames, specializedMethods,
                    specializedDeclarations, newExpressionTypes, newResolvedCalls);
                _classesByName[specializedName] = specialized;
                AddSpecializedMethods(functions, functionsByName, specialized.Methods);
                foreach (var field in specialized.Fields)
                    AddType(field.Type);
                foreach (var method in specialized.Methods)
                {
                    AddType(method.ReturnType);
                    foreach (var parameter in method.Parameters)
                        AddType(parameter.Type);
                }
                foreach (var constructor in specialized.Constructors)
                    foreach (var parameter in constructor.Parameters)
                        AddType(parameter.Type);
            }
            else if (aggregate.Kind == SemanticTypeKind.Interface
                     && _interfacesByName.TryGetValue(baseName, out var iface))
            {
                var specialized = SpecializeInterface(
                    iface, specializedName, substitutions, aggregateNames, specializedMethods,
                    specializedDeclarations, newExpressionTypes, newResolvedCalls);
                _interfacesByName[specializedName] = specialized;
                AddSpecializedMethods(functions, functionsByName, specialized.Methods);
            }
            else if (aggregate.Kind == SemanticTypeKind.Union && _unionsByName.TryGetValue(baseName, out var union))
            {
                _unionsByName[specializedName] = SpecializeUnion(union, specializedName, substitutions, aggregateNames);
            }
            else if (aggregate.Kind == SemanticTypeKind.Struct && _structsByName.TryGetValue(baseName, out var structure))
            {
                _structsByName[specializedName] = SpecializeStruct(structure, specializedName, substitutions, aggregateNames);
            }
        }

        var normalizedFunctions = functions.Values
            .Where(symbol => symbol.IsSpecialized)
            .ToDictionary(
                symbol => symbol,
                symbol => symbol with
                {
                    ReturnType = NormalizeAggregate(symbol.ReturnType, aggregateNames),
                    Parameters = symbol.Parameters.Select(parameter => parameter with
                    {
                        Type = NormalizeAggregate(parameter.Type, aggregateNames)
                    }).ToArray()
                });
        foreach (var pair in normalizedFunctions)
            functions[pair.Key.Declaration] = pair.Value;
        foreach (var overloads in functionsByName.Values)
            for (var index = 0; index < overloads.Count; index++)
                if (normalizedFunctions.TryGetValue(overloads[index], out var replacement))
                    overloads[index] = replacement;
        // Covers both the original calls and the calls cloned into specialized bodies; the latter
        // still point at the generic declaration until they are resolved against their receiver.
        foreach (var pair in newResolvedCalls.ToArray())
        {
            var symbol = normalizedFunctions.TryGetValue(pair.Value, out var replacement) ? replacement : pair.Value;
            newResolvedCalls[pair.Key] = ResolveSpecializedCall(pair.Key, symbol, expressionTypes, aggregateNames,
                specializedMethods);
        }

        expressionTypes.Clear();
        foreach (var pair in newExpressionTypes)
            expressionTypes[pair.Key] = pair.Value;
        resolvedCalls.Clear();
        foreach (var pair in newResolvedCalls)
            resolvedCalls[pair.Key] = pair.Value;
        foreach (var pair in _callReceiverTypes.ToArray())
            _callReceiverTypes[pair.Key] = NormalizeAggregate(
                SubstituteAggregate(pair.Value, new Dictionary<string, SemanticType>(), aggregateNames), aggregateNames);

        return aggregateNames;
    }

    private static void AddSpecializedMethods(
        Dictionary<FunctionDeclaration, FunctionSymbol> functions,
        Dictionary<string, List<FunctionSymbol>> functionsByName,
        IEnumerable<FunctionSymbol> methods)
    {
        foreach (var method in methods)
        {
            functions[method.Declaration] = method;
            if (!functionsByName.TryGetValue(method.QualifiedName, out var overloads))
                functionsByName[method.QualifiedName] = overloads = [];
            if (!overloads.Contains(method))
                overloads.Add(method);
        }
    }

    private FunctionSymbol ResolveSpecializedCall(
        CallExpression call,
        FunctionSymbol symbol,
        IReadOnlyDictionary<Expression, SemanticType> expressionTypes,
        IReadOnlyDictionary<string, string> aggregateNames,
        IReadOnlyDictionary<(FunctionDeclaration Declaration, string Owner), FunctionSymbol> specializedMethods)
    {
        var owner = symbol.ModuleName[(symbol.ModuleName.LastIndexOf('.') + 1)..];
        if ((!_classesByName.TryGetValue(owner, out var cls) || !cls.IsGeneric)
            && (!_interfacesByName.TryGetValue(owner, out var iface) || !iface.IsGeneric))
            return symbol;
        var receiverName = call.Name.Contains('.', StringComparison.Ordinal)
            ? call.Name[..call.Name.LastIndexOf('.')]
            : string.Empty;
        var receiverType = _callReceiverTypes.TryGetValue(call, out var recordedReceiver)
            ? NormalizeAggregate(recordedReceiver, aggregateNames)
            : expressionTypes.Keys
                .Where(item => item is IdentifierExpression identifier && identifier.Name == receiverName
                    || item is ThisExpression && receiverName == "this")
                .Select(item => expressionTypes[item])
                .Where(type => !ContainsGenericParameter(type))
                .Select(type => NormalizeAggregate(type, aggregateNames))
                .FirstOrDefault(type => type.IsClass || type.IsInterface);
        var specializedOwner = receiverType?.StructName;
        if (specializedOwner is not null
            && specializedMethods.TryGetValue((symbol.Declaration, specializedOwner), out var specialized))
            return specialized;
        var candidates = specializedMethods
            .Where(pair => ReferenceEquals(pair.Key.Declaration, symbol.Declaration))
            .Select(pair => pair.Value)
            .ToArray();
        if (candidates.Length == 1)
            return candidates[0];
        return symbol;
    }

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
                specializedMethods, expressionTypes, resolvedCalls, name)).ToArray();
        var constructors = source.Constructors.Select(constructor =>
        {
            var body = CloneBlock(
                instantiation.Constructors.GetValueOrDefault(constructor.Declaration)?.Body
                    ?? constructor.Declaration.Body,
                substitutions, aggregateNames, expressionTypes, source, resolvedCalls);
            var constructorDeclaration = constructor.Declaration with
            {
                ClassName = name,
                Parameters = constructor.Parameters.Select(parameter => parameter.Declaration with
                {
                    Type = RewriteTypeSyntax(parameter.Declaration.Type, substitutions, aggregateNames)
                }).ToArray(),
                Body = body
            };
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
            Methods = methods.Select(method => method.Declaration).ToArray(),
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
                specializedMethods, expressionTypes, resolvedCalls, name)).ToArray();
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

    private static IEnumerable<Expression> EnumerateExpressions(Statement? statement) =>
        statement switch
        {
            BlockStatement block => block.Statements.SelectMany(EnumerateExpressions),
            VariableDeclarationStatement variable => EnumerateExpressions(variable.Initializer),
            AssignmentStatement assignment => EnumerateExpressions(assignment.Target)
                .Concat(EnumerateExpressions(assignment.Value)),
            ReturnStatement @return => EnumerateExpressions(@return.Value),
            ExpressionStatement expression => EnumerateExpressions(expression.Expression),
            IfStatement conditional => EnumerateExpressions(conditional.Condition)
                .Concat(EnumerateExpressions(conditional.Then))
                .Concat(EnumerateExpressions(conditional.Else)),
            WhileStatement loop => EnumerateExpressions(loop.Condition).Concat(EnumerateExpressions(loop.Body)),
            ForStatement loop => EnumerateExpressions(loop.Initializer)
                .Concat(EnumerateExpressions(loop.Condition))
                .Concat(EnumerateExpressions(loop.Increment))
                .Concat(EnumerateExpressions(loop.Body)),
            ForInStatement loop => EnumerateExpressions(loop.Collection).Concat(EnumerateExpressions(loop.Body)),
            ThrowStatement @throw => EnumerateExpressions(@throw.Value),
            TryStatement @try => EnumerateExpressions(@try.Body)
                .Concat(@try.Catches.SelectMany(item => EnumerateExpressions(item.Body)))
                .Concat(EnumerateExpressions(@try.Finally)),
            SwitchStatement @switch => EnumerateExpressions(@switch.Value)
                .Concat(@switch.Cases.SelectMany(item =>
                    EnumerateExpressions(item.Value).Concat(EnumerateExpressions(item.Body)))),
            _ => []
        };

    private static IEnumerable<Expression> EnumerateExpressions(Expression? expression)
    {
        if (expression is null)
            return [];
        var nested = expression switch
        {
            UnaryExpression item => EnumerateExpressions(item.Operand),
            BinaryExpression item => EnumerateExpressions(item.Left).Concat(EnumerateExpressions(item.Right)),
            ConditionalExpression item => EnumerateExpressions(item.Condition)
                .Concat(EnumerateExpressions(item.WhenTrue)).Concat(EnumerateExpressions(item.WhenFalse)),
            IsExpression item => EnumerateExpressions(item.Operand),
            CallExpression item => item.Arguments.SelectMany(EnumerateExpressions),
            ClassConstructionExpression item => item.Arguments.SelectMany(EnumerateExpressions),
            NewExpression item => item.Arguments.SelectMany(EnumerateExpressions),
            CastExpression item => EnumerateExpressions(item.Operand),
            SizeOfExpression item => EnumerateExpressions(item.Operand),
            LengthExpression item => EnumerateExpressions(item.Target),
            MemberAccessExpression item => EnumerateExpressions(item.Target),
            IndexExpression item => EnumerateExpressions(item.Target).Concat(EnumerateExpressions(item.Index)),
            ArrayLiteralExpression item => item.Elements.SelectMany(EnumerateExpressions),
            StructLiteralExpression item => item.Fields.SelectMany(field => EnumerateExpressions(field.Value)),
            UnionConstructionExpression item => item.Arguments.SelectMany(EnumerateExpressions),
            _ => []
        };
        return nested.Prepend(expression);
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

    private static void CollectFunctionSyntax(FunctionDeclaration function, Action<TypeSyntax, IReadOnlySet<string>> add)
    {
        var parameters = function.DeclaredTypeParameters.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        add(function.ReturnType, parameters);
        foreach (var parameter in function.Parameters)
            add(parameter.Type, parameters);
        CollectBlockSyntax(function.Body, parameters, add);
    }

    private static void CollectClassSyntax(ClassDeclaration declaration, Action<TypeSyntax, IReadOnlySet<string>> add)
    {
        var parameters = declaration.DeclaredTypeParameters.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var field in declaration.Fields)
        {
            add(field.Type, parameters);
            CollectExpressionSyntax(field.Initializer, parameters, add);
        }
        foreach (var property in declaration.DeclaredProperties)
        {
            add(property.Type, parameters);
            foreach (var accessor in property.Accessors)
                CollectBlockSyntax(accessor.Body, parameters, add);
        }
        foreach (var method in declaration.Methods)
            CollectFunctionSyntax(method, add);
        foreach (var constructor in declaration.Constructors)
        {
            foreach (var parameter in constructor.Parameters)
                add(parameter.Type, parameters);
            CollectBlockSyntax(constructor.Body, parameters, add);
        }
        if (declaration.Destructor is not null)
            CollectBlockSyntax(declaration.Destructor.Body, parameters, add);
    }

    private static void CollectInterfaceSyntax(InterfaceDeclaration declaration, Action<TypeSyntax, IReadOnlySet<string>> add)
    {
        var parameters = declaration.DeclaredTypeParameters.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var property in declaration.DeclaredProperties)
            add(property.Type, parameters);
        foreach (var method in declaration.Methods)
            CollectFunctionSyntax(method, add);
    }

    private static void CollectBlockSyntax(BlockStatement? block, IReadOnlySet<string> parameters,
        Action<TypeSyntax, IReadOnlySet<string>> add)
    {
        if (block is null)
            return;
        foreach (var statement in block.Statements)
            CollectStatementSyntax(statement, parameters, add);
    }

    private static void CollectStatementSyntax(Statement statement, IReadOnlySet<string> parameters,
        Action<TypeSyntax, IReadOnlySet<string>> add)
    {
        switch (statement)
        {
            case BlockStatement block:
                CollectBlockSyntax(block, parameters, add);
                break;
            case VariableDeclarationStatement variable:
                add(variable.Type, parameters);
                CollectExpressionSyntax(variable.Initializer, parameters, add);
                break;
            case AssignmentStatement assignment:
                CollectExpressionSyntax(assignment.Target, parameters, add);
                CollectExpressionSyntax(assignment.Value, parameters, add);
                break;
            case ReturnStatement @return:
                CollectExpressionSyntax(@return.Value, parameters, add);
                break;
            case ExpressionStatement expression:
                CollectExpressionSyntax(expression.Expression, parameters, add);
                break;
            case IfStatement conditional:
                CollectExpressionSyntax(conditional.Condition, parameters, add);
                CollectStatementSyntax(conditional.Then, parameters, add);
                if (conditional.Else is not null) CollectStatementSyntax(conditional.Else, parameters, add);
                break;
            case WhileStatement loop:
                CollectExpressionSyntax(loop.Condition, parameters, add);
                CollectStatementSyntax(loop.Body, parameters, add);
                break;
            case ForStatement loop:
                if (loop.Initializer is not null) CollectStatementSyntax(loop.Initializer, parameters, add);
                CollectExpressionSyntax(loop.Condition, parameters, add);
                if (loop.Increment is not null) CollectStatementSyntax(loop.Increment, parameters, add);
                CollectStatementSyntax(loop.Body, parameters, add);
                break;
            case ForInStatement loop:
                add(loop.VariableType, parameters);
                CollectExpressionSyntax(loop.Collection, parameters, add);
                CollectStatementSyntax(loop.Body, parameters, add);
                break;
            case ThrowStatement @throw:
                CollectExpressionSyntax(@throw.Value, parameters, add);
                break;
            case TryStatement @try:
                CollectBlockSyntax(@try.Body, parameters, add);
                foreach (var item in @try.Catches)
                {
                    if (item.ExceptionType is not null) add(item.ExceptionType, parameters);
                    CollectBlockSyntax(item.Body, parameters, add);
                }
                CollectBlockSyntax(@try.Finally, parameters, add);
                break;
            case SwitchStatement @switch:
                CollectExpressionSyntax(@switch.Value, parameters, add);
                foreach (var item in @switch.Cases)
                {
                    if (item.PatternType is not null) add(item.PatternType, parameters);
                    CollectExpressionSyntax(item.Value, parameters, add);
                    CollectStatementSyntax(item.Body, parameters, add);
                }
                break;
        }
    }

    private static void CollectExpressionSyntax(Expression? expression, IReadOnlySet<string> parameters,
        Action<TypeSyntax, IReadOnlySet<string>> add)
    {
        if (expression is null)
            return;
        switch (expression)
        {
            case IsExpression item:
                add(item.TargetType, parameters);
                CollectExpressionSyntax(item.Operand, parameters, add);
                break;
            case UnaryExpression item:
                CollectExpressionSyntax(item.Operand, parameters, add);
                break;
            case BinaryExpression item:
                CollectExpressionSyntax(item.Left, parameters, add);
                CollectExpressionSyntax(item.Right, parameters, add);
                break;
            case ConditionalExpression item:
                CollectExpressionSyntax(item.Condition, parameters, add);
                CollectExpressionSyntax(item.WhenTrue, parameters, add);
                CollectExpressionSyntax(item.WhenFalse, parameters, add);
                break;
            case CallExpression item:
                foreach (var type in item.ExplicitTypeArguments) add(type, parameters);
                foreach (var argument in item.Arguments) CollectExpressionSyntax(argument, parameters, add);
                break;
            case ClassConstructionExpression item:
                foreach (var type in item.ExplicitTypeArguments) add(type, parameters);
                foreach (var argument in item.Arguments) CollectExpressionSyntax(argument, parameters, add);
                break;
            case NewExpression item:
                add(item.Type, parameters);
                foreach (var argument in item.Arguments) CollectExpressionSyntax(argument, parameters, add);
                break;
            case CastExpression item:
                add(item.TargetType, parameters);
                CollectExpressionSyntax(item.Operand, parameters, add);
                break;
            case SizeOfExpression item:
                if (item.TargetType is not null) add(item.TargetType, parameters);
                CollectExpressionSyntax(item.Operand, parameters, add);
                break;
            case MemberAccessExpression item:
                CollectExpressionSyntax(item.Target, parameters, add);
                break;
            case IndexExpression item:
                CollectExpressionSyntax(item.Target, parameters, add);
                CollectExpressionSyntax(item.Index, parameters, add);
                break;
            case ArrayLiteralExpression item:
                foreach (var element in item.Elements) CollectExpressionSyntax(element, parameters, add);
                break;
            case StructLiteralExpression item:
                foreach (var field in item.Fields) CollectExpressionSyntax(field.Value, parameters, add);
                break;
            case UnionConstructionExpression item:
                foreach (var argument in item.Arguments) CollectExpressionSyntax(argument, parameters, add);
                break;
        }
    }

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
            var arguments = syntax.DeclaredTypeArguments
                .Select(item => ConcreteType(item, genericParameters)).ToArray();
            IReadOnlyList<SemanticType>? concreteArguments = arguments.Any(item => item is null)
                ? null
                : arguments.Select(item => item!).ToArrayOrNull();
            if (_classesByName.ContainsKey(syntax.NamedType))
                return SemanticType.Class(syntax.NamedType, concreteArguments);
            if (_interfacesByName.ContainsKey(syntax.NamedType))
                return SemanticType.Interface(syntax.NamedType, concreteArguments);
            if (_unionsByName.ContainsKey(syntax.NamedType))
                return SemanticType.Union(syntax.NamedType, concreteArguments);
            if (_structsByName.ContainsKey(syntax.NamedType))
                return SemanticType.Struct(syntax.NamedType, concreteArguments);
            return null;
        }
        return syntax.Primitive is null ? null : SemanticType.FromPrimitive(syntax.Primitive.Value);
    }

    private static TypeSyntax RewriteTypeSyntax(TypeSyntax syntax,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames)
    {
        if (syntax.NamedType is not null && substitutions.TryGetValue(syntax.NamedType, out var replacement)
            && syntax.DeclaredTypeArguments.Count == 0)
            return TypeSyntaxFromSemantic(replacement, syntax.Location);
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
            var baseType = new SemanticType(
                SemanticTypeKind.Struct, null, null, syntax.NamedType,
                null, null) { TypeArguments = syntax.DeclaredTypeArguments.Select(item =>
                    SemanticTypeFromSyntax(item, substitutions, aggregateNames)).ToArray() };
            var kinds = new[] { SemanticTypeKind.Class, SemanticTypeKind.Interface, SemanticTypeKind.Union, SemanticTypeKind.Struct };
            foreach (var kind in kinds)
            {
                var candidate = baseType with { Kind = kind, EnumName = kind == SemanticTypeKind.Union ? syntax.NamedType : null,
                    StructName = kind == SemanticTypeKind.Union ? null : syntax.NamedType };
                if (aggregateNames.TryGetValue(AggregateKey(candidate), out var name))
                    return new TypeSyntax(null, false, syntax.Location, NamedType: name);
            }
            return syntax with { TypeArguments = arguments };
        }
        return syntax;
    }

    private static SemanticType SemanticTypeFromSyntax(TypeSyntax syntax,
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

    private static SemanticType AggregateTypeFromSyntax(
        TypeSyntax syntax,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames)
    {
        var arguments = syntax.DeclaredTypeArguments
            .Select(item => SemanticTypeFromSyntax(item, substitutions, aggregateNames)).ToArrayOrNull();
        foreach (var kind in new[]
                 {
                     SemanticTypeKind.Class, SemanticTypeKind.Interface,
                     SemanticTypeKind.Union, SemanticTypeKind.Struct
                 })
        {
            var candidate = kind == SemanticTypeKind.Union
                ? SemanticType.Union(syntax.NamedType!, arguments)
                : kind == SemanticTypeKind.Class
                    ? SemanticType.Class(syntax.NamedType!, arguments)
                    : kind == SemanticTypeKind.Interface
                        ? SemanticType.Interface(syntax.NamedType!, arguments)
                        : SemanticType.Struct(syntax.NamedType!, arguments);
            if (aggregateNames.ContainsKey(AggregateKey(candidate)))
                return candidate;
        }
        return SemanticType.Struct(syntax.NamedType!, arguments);
    }

    private FunctionDeclaration CloneFunctionAggregate(
        FunctionDeclaration source,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames,
        Dictionary<Expression, SemanticType> expressionTypes,
        object owner,
        Dictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var parameterTypes = source.Parameters.Select(parameter => parameter with
        {
            Type = RewriteTypeSyntax(parameter.Type, substitutions, aggregateNames)
        }).ToArray();
        var result = source with
        {
            ReturnType = RewriteTypeSyntax(source.ReturnType, substitutions, aggregateNames),
            Parameters = parameterTypes,
            Body = CloneBlock(source.Body, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
            TypeParameters = []
        };
        return result;
    }

    private BlockStatement CloneBlock(
        BlockStatement source,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames,
        Dictionary<Expression, SemanticType> expressionTypes,
        object owner,
        Dictionary<CallExpression, FunctionSymbol> resolvedCalls) =>
        source with { Statements = source.Statements.Select(statement =>
            CloneStatement(statement, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)).ToArray() };

    private Statement CloneStatement(
        Statement source,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames,
        Dictionary<Expression, SemanticType> expressionTypes,
        object owner,
        Dictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var result = source switch
        {
            BlockStatement item => CloneBlock(item, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
            VariableDeclarationStatement item => item with
            {
                Type = RewriteTypeSyntax(item.Type, substitutions, aggregateNames),
                Initializer = item.Initializer is null ? null :
                    CloneExpression(item.Initializer, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            AssignmentStatement item => item with
            {
                Target = CloneExpression(item.Target, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Value = CloneExpression(item.Value, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            ReturnStatement item => item with
            {
                Value = item.Value is null ? null :
                    CloneExpression(item.Value, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            ExpressionStatement item => item with
            {
                Expression = CloneExpression(item.Expression, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            IfStatement item => item with
            {
                Condition = CloneExpression(item.Condition, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Then = CloneStatement(item.Then, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Else = item.Else is null ? null :
                    CloneStatement(item.Else, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            WhileStatement item => item with
            {
                Condition = CloneExpression(item.Condition, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Body = CloneStatement(item.Body, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            ForStatement item => item with
            {
                Initializer = item.Initializer is null ? null :
                    CloneStatement(item.Initializer, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Condition = item.Condition is null ? null :
                    CloneExpression(item.Condition, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Increment = item.Increment is null ? null :
                    CloneStatement(item.Increment, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Body = CloneStatement(item.Body, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            ForInStatement item => item with
            {
                VariableType = RewriteTypeSyntax(item.VariableType, substitutions, aggregateNames),
                Collection = CloneExpression(item.Collection, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Body = CloneStatement(item.Body, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            ThrowStatement item => item with
            {
                Value = CloneExpression(item.Value, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            SwitchStatement item => item with
            {
                Value = CloneExpression(item.Value, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Cases = item.Cases.Select(caseItem => caseItem with
                {
                    PatternType = caseItem.PatternType is null ? null :
                        RewriteTypeSyntax(caseItem.PatternType, substitutions, aggregateNames),
                    Value = caseItem.Value is null ? null :
                        CloneExpression(caseItem.Value, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                    Body = CloneStatement(caseItem.Body, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
                }).ToArray()
            },
            TryStatement item => item with
            {
                Body = CloneBlock(item.Body, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Catches = item.Catches.Select(clause => clause with
                {
                    ExceptionType = clause.ExceptionType is null ? null :
                        RewriteTypeSyntax(clause.ExceptionType, substitutions, aggregateNames),
                    Body = CloneBlock(clause.Body, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
                }).ToArray(),
                Finally = item.Finally is null ? null :
                    CloneBlock(item.Finally, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            _ => source
        };
        // Facts recorded for the source statement belong to the clone as well.
        if (source is VariableDeclarationStatement local && _escapingClassLocals.Contains(local))
            _escapingClassLocals.Add((VariableDeclarationStatement)result);
        if (source is ForInStatement loop && _iteratorProtocols.TryGetValue(loop, out var protocol))
            _iteratorProtocols[(ForInStatement)result] = protocol;
        return result;
    }

    private Expression CloneExpression(
        Expression source,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames,
        Dictionary<Expression, SemanticType> expressionTypes,
        object owner,
        Dictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        Expression result = source switch
        {
            UnaryExpression item => item with { Operand = CloneExpression(item.Operand, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls) },
            BinaryExpression item => item with
            {
                Left = CloneExpression(item.Left, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Right = CloneExpression(item.Right, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            ConditionalExpression item => item with
            {
                Condition = CloneExpression(item.Condition, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                WhenTrue = CloneExpression(item.WhenTrue, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                WhenFalse = CloneExpression(item.WhenFalse, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            IsExpression item => item with
            {
                TargetType = RewriteTypeSyntax(item.TargetType, substitutions, aggregateNames),
                Operand = CloneExpression(item.Operand, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            CallExpression item => item with
            {
                Arguments = item.Arguments.Select(argument =>
                    CloneExpression(argument, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)).ToArray(),
                TypeArguments = item.TypeArguments?.Select(type => RewriteTypeSyntax(type, substitutions, aggregateNames)).ToArray()
            },
            ClassConstructionExpression item => item with
            {
                TypeName = RewriteNamedAggregate(item.TypeName, SemanticTypeKind.Class, substitutions, aggregateNames),
                Arguments = item.Arguments.Select(argument =>
                    CloneExpression(argument, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)).ToArray(),
                TypeArguments = item.TypeArguments?.Select(type => RewriteTypeSyntax(type, substitutions, aggregateNames)).ToArray()
            },
            NewExpression item => item with
            {
                Type = RewriteTypeSyntax(item.Type, substitutions, aggregateNames),
                Arguments = item.Arguments.Select(argument =>
                    CloneExpression(argument, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)).ToArray()
            },
            CastExpression item => item with
            {
                TargetType = RewriteTypeSyntax(item.TargetType, substitutions, aggregateNames),
                Operand = CloneExpression(item.Operand, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            SizeOfExpression item => item with
            {
                TargetType = item.TargetType is null ? null : RewriteTypeSyntax(item.TargetType, substitutions, aggregateNames),
                Operand = item.Operand is null ? null :
                    CloneExpression(item.Operand, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            MemberAccessExpression item => item with
            {
                Target = CloneExpression(item.Target, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            IndexExpression item => item with
            {
                Target = CloneExpression(item.Target, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Index = CloneExpression(item.Index, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            ArrayLiteralExpression item => item with
            {
                Elements = item.Elements.Select(element =>
                    CloneExpression(element, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)).ToArray()
            },
            StructLiteralExpression item => item with
            {
                TypeName = RewriteNamedAggregate(item.TypeName, SemanticTypeKind.Struct, substitutions, aggregateNames),
                Fields = item.Fields.Select(field => field with
                {
                    Value = CloneExpression(field.Value, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
                }).ToArray()
            },
            UnionConstructionExpression item => item with
            {
                UnionName = RewriteNamedAggregate(item.UnionName, SemanticTypeKind.Union, substitutions, aggregateNames),
                Arguments = item.Arguments.Select(argument =>
                    CloneExpression(argument, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)).ToArray()
            },
            _ => source
        };
        if (expressionTypes.TryGetValue(source, out var type))
            expressionTypes[result] = SubstituteAggregate(type, substitutions, aggregateNames);
        if (source is CallExpression call && result is CallExpression cloned)
        {
            if (resolvedCalls.TryGetValue(call, out var function))
                resolvedCalls[cloned] = function;
            if (_callReceiverTypes.TryGetValue(call, out var receiverType))
                _callReceiverTypes[cloned] = SubstituteAggregate(receiverType, substitutions, aggregateNames);
            if (_receiverArgumentCalls.Contains(call))
                _receiverArgumentCalls.Add(cloned);
        }
        return result;
    }

    private PropertyDeclaration CloneProperty(
        PropertyDeclaration source,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames,
        Dictionary<Expression, SemanticType> expressionTypes,
        object owner,
        Dictionary<CallExpression, FunctionSymbol> resolvedCalls) =>
        source with
        {
            Type = RewriteTypeSyntax(source.Type, substitutions, aggregateNames),
            Accessors = source.Accessors.Select(accessor => accessor with
            {
                Body = accessor.Body is null ? null :
                    CloneBlock(accessor.Body, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            }).ToArray()
        };

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
    // depth; the balanced angle brackets keep distinct argument groupings distinct.
    private static string AggregateKey(SemanticType type) =>
        $"{type.Kind}:{AggregateBaseName(type)}<{string.Join(", ", type.TypeArguments ?? [])}>";

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
                return argument.ToString()!.Replace(" ", string.Empty);
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

internal static class MonomorphizationExtensions
{
    public static IReadOnlyList<T>? ToArrayOrNull<T>(this IEnumerable<T> values)
    {
        var result = values.ToArray();
        return result.Length == 0 ? null : result;
    }
}
