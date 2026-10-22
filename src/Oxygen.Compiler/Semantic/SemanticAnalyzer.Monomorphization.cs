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

        foreach (var type in expressionTypes.Values.Concat(_callReceiverTypes.Values))
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
            foreach (var method in cls.Methods.Concat(cls.StaticMethods))
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
            _typeScopeModule = unit.ModuleName ?? "program";
            _typeScopeImports = unit.Imports;
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
                foreach (var method in cls.Methods.Concat(cls.StaticMethods))
                {
                    AddType(SubstituteAggregate(method.ReturnType, substitutions, aggregateNames));
                    foreach (var parameter in method.Parameters)
                        AddType(SubstituteAggregate(parameter.Type, substitutions, aggregateNames));
                }
                foreach (var constructor in cls.Constructors)
                    foreach (var parameter in constructor.Parameters)
                        AddType(SubstituteAggregate(parameter.Type, substitutions, aggregateNames));
                AddType(SubstituteAggregate(cls.DeclaredInterfaceTypes.FirstOrDefault(), substitutions, aggregateNames));
                // A generic base class is specialized for the same arguments, so that its constructor can be called.
                if (cls.Declaration.BaseTypeSyntax is { Count: > 0 } baseSyntax)
                    AddType(SubstituteAggregate(
                        InTypeScopeOf(cls.QualifiedName, () => ConcreteType(baseSyntax[0],
                            cls.DeclaredTypeParameters.ToHashSet(StringComparer.Ordinal))),
                        substitutions, aggregateNames));
                // The member bodies are analyzed for these concrete arguments. That types every
                // expression concretely, specializes the generic functions they call, and
                // reveals aggregates that only appear inside bodies (e.g. `Iterator(this)`).
                var instantiation = InTypeScopeOf(cls.QualifiedName, () => InstantiateClass(cls, aggregate, substitutions,
                    functionsByName, expressionTypes, resolvedCalls));
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

        var specializedMethods = new Dictionary<(FunctionDeclaration Declaration, string Owner), FunctionSymbol>(
            new DeclarationOwnerComparer());
        var specializedDeclarations = new Dictionary<FunctionDeclaration, FunctionDeclaration>(ReferenceEqualityComparer.Instance);
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
            // The specialized type is keyed by its qualified name, `module.Name`, and its symbol carries the simple name.
            var simpleName = specializedName[(baseName.LastIndexOf('.') + 1)..];

            if (aggregate.Kind == SemanticTypeKind.Class && _classesByName.TryGetValue(baseName, out var cls))
            {
                var specialized = InTypeScopeOf(baseName, () => SpecializeClass(
                    cls, instantiations[key], simpleName, substitutions, aggregateNames, specializedMethods,
                    specializedDeclarations, newExpressionTypes, newResolvedCalls));
                _classesByName[specializedName] = specialized;
                AddSpecializedMethods(functions, functionsByName, specialized.Methods.Concat(specialized.StaticMethods).ToArray());
                foreach (var field in specialized.Fields)
                    AddType(field.Type);
                foreach (var method in specialized.Methods.Concat(specialized.StaticMethods))
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
                var specialized = InTypeScopeOf(baseName, () => SpecializeInterface(
                    iface, simpleName, substitutions, aggregateNames, specializedMethods,
                    specializedDeclarations, newExpressionTypes, newResolvedCalls));
                _interfacesByName[specializedName] = specialized;
                AddSpecializedMethods(functions, functionsByName, specialized.Methods);
            }
            else if (aggregate.Kind == SemanticTypeKind.Union && _unionsByName.TryGetValue(baseName, out var union))
            {
                _unionsByName[specializedName] = SpecializeUnion(union, simpleName, substitutions, aggregateNames);
            }
            else if (aggregate.Kind == SemanticTypeKind.Struct && _structsByName.TryGetValue(baseName, out var structure))
            {
                _structsByName[specializedName] = SpecializeStruct(structure, simpleName, substitutions, aggregateNames);
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
        {
            functions[pair.Key.Declaration] = pair.Value;
            // The normalized copy is a different record, so the use recorded for the instance is moved to it.
            if (_usedFunctions.Remove(pair.Key))
                _usedFunctions.Add(pair.Value);
        }
        foreach (var overloads in functionsByName.Values)
            for (var index = 0; index < overloads.Count; index++)
                if (normalizedFunctions.TryGetValue(overloads[index], out var replacement))
                    overloads[index] = replacement;
        // The address of a specialized function names its normalized symbol, as a call does, so the backend finds the declaration.
        foreach (var reference in _functionReferences.ToArray())
            if (normalizedFunctions.TryGetValue(reference.Value, out var addressed))
                _functionReferences[reference.Key] = addressed;
        // Covers both the original calls and the calls cloned into specialized bodies; the latter
        // still point at the generic declaration until they are resolved against their receiver.
        foreach (var pair in newResolvedCalls.ToArray())
        {
            var symbol = normalizedFunctions.TryGetValue(pair.Value, out var replacement) ? replacement : pair.Value;
            newResolvedCalls[pair.Key] = ResolveSpecializedCall(pair.Key, symbol, expressionTypes, aggregateNames,
                specializedMethods);
        }
        // A for-in protocol names the iter() call and the next() method of its iterator. Both are specialized for the concrete
        // iterator type that the specialized iter() returns.
        foreach (var (loop, protocol) in _iteratorProtocols.ToArray())
        {
            var iter = newResolvedCalls[protocol.IterCall];
            // The iterator type is named as its instance (an interface such as Iterator<i32> is Iterator__ox_i32), so its
            // specialized next() is found under that name.
            var iteratorType = NormalizeAggregate(iter.ReturnType, aggregateNames);
            var iteratorOwner = iteratorType.StructName;
            var next = iteratorOwner is not null
                && specializedMethods.TryGetValue((protocol.Next.Declaration, iteratorOwner), out var specializedNext)
                ? specializedNext
                : protocol.Next;
            _iteratorProtocols[loop] = protocol with { Iter = iter, Next = next, IteratorType = iteratorType };
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
        var owner = symbol.ModuleName;
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

    // A specialized method is keyed by its declaration, compared by reference, and by the owner's specialized name.
    private sealed class DeclarationOwnerComparer : IEqualityComparer<(FunctionDeclaration Declaration, string Owner)>
    {
        public bool Equals((FunctionDeclaration Declaration, string Owner) x, (FunctionDeclaration Declaration, string Owner) y) =>
            ReferenceEquals(x.Declaration, y.Declaration) && string.Equals(x.Owner, y.Owner, StringComparison.Ordinal);

        public int GetHashCode((FunctionDeclaration Declaration, string Owner) key) =>
            HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Declaration),
                StringComparer.Ordinal.GetHashCode(key.Owner));
    }

}
