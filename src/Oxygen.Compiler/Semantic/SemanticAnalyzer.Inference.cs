namespace Oxygen.Compiler.Semantic;

// Type-argument inference for generic functions, union cases and class constructors. The type arguments come from the types
// of the arguments and, where they do not decide, from the expected type. A type parameter bound to two different types is
// an error, except for classes and interfaces: two of those bind to the wider type when one converts implicitly to the
// other, and otherwise to their nearest common supertype. So `both(new Dog(), new Animal())` binds T to Animal in either
// order. An expected type binds the parameters that no argument reached, and widens a class or interface binding to the
// expected type when that binding converts to it: `Option<Iterable<i32>> it = Option.Some(array)` binds T to Iterable<i32>.
public sealed partial class SemanticAnalyzer
{
    // The type arguments of a generic function from its argument types, then from the expected return type. Returns null when
    // they cannot be inferred, and `problem` then says why: an argument that does not agree with the others, or a parameter that
    // nothing bound.
    private IReadOnlyDictionary<string, SemanticType>? InferGenericSubstitutions(
        FunctionSymbol generic,
        IReadOnlyList<SemanticType> argumentTypes,
        int[] order,
        SemanticType? expectedReturnType,
        out string? problem)
    {
        var parameters = generic.DeclaredGenericParameters;
        var bindings = new Dictionary<string, SemanticType>(StringComparer.Ordinal);
        problem = null;
        // Only the arguments are read: a parameter that is omitted takes its default, which cannot have a generic type.
        for (var position = 0; position < order.Length; position++)
        {
            if (order[position] == DefaultArgument)
                continue;
            if (!InferFromArgument(generic.Parameters[position].Type, argumentTypes[order[position]], parameters, bindings))
            {
                problem = $"The arguments of '{generic.Name}' do not agree on its type arguments: a type parameter would need " +
                    "two types that share no supertype, or an argument does not have the shape of its parameter";
                return null;
            }
        }

        if (expectedReturnType is not null)
            BindFromExpectedType(generic.ReturnType, expectedReturnType, parameters, bindings);
        var unbound = parameters.FirstOrDefault(name => !bindings.ContainsKey(name));
        if (unbound is not null)
        {
            problem = $"Cannot infer type parameter '{unbound}' of '{generic.Name}' from its arguments; write its type arguments explicitly";
            return null;
        }

        return bindings;
    }

    // The type arguments of a class's constructor from its argument types, then from the expected type of the construction.
    private IReadOnlyDictionary<string, SemanticType>? InferConstructorTypeArguments(
        ClassSymbol cls,
        ConstructorSymbol constructor,
        IReadOnlyList<SemanticType> argumentTypes,
        int[] order,
        SemanticType? expectedType)
    {
        var parameters = cls.DeclaredTypeParameters;
        var bindings = new Dictionary<string, SemanticType>(StringComparer.Ordinal);
        for (var position = 0; position < order.Length; position++)
        {
            if (order[position] == DefaultArgument)
                continue;
            if (!InferFromArgument(constructor.Parameters[position].Type, argumentTypes[order[position]], parameters, bindings))
                return null;
        }

        if (expectedType is not null)
        {
            var constructed = SemanticType.Class(cls.Name, parameters.Select(SemanticType.GenericParameter).ToArray());
            BindFromExpectedType(constructed, expectedType, parameters, bindings);
        }

        return parameters.All(bindings.ContainsKey) ? bindings : null;
    }

    // Binds the type parameters that occur in `pattern`, from an argument of type `actual`. Returns false when the argument does
    // not have the pattern's shape, or when a parameter would need two types that do not share a supertype. A part of the pattern
    // without type parameters is not checked here: the overload ranking checks the arguments against the specialized parameters.
    private bool InferFromArgument(
        SemanticType pattern,
        SemanticType actual,
        IReadOnlyList<string> parameters,
        IDictionary<string, SemanticType> bindings)
    {
        if (!ContainsGenericParameter(pattern))
            return true;
        if (pattern.IsGenericParameter)
        {
            var name = pattern.GenericParameterName!;
            return !parameters.Contains(name, StringComparer.Ordinal) || BindArgument(name, actual, bindings);
        }

        if (pattern.IsPointer)
            return actual.IsPointer && actual.Pointee is not null && pattern.Pointee is not null
                && InferFromArgument(pattern.Pointee, actual.Pointee, parameters, bindings);
        if (pattern.IsArray)
            return actual.IsArray && actual.ArrayLength == pattern.ArrayLength && actual.ElementType is not null
                && pattern.ElementType is not null && InferFromArgument(pattern.ElementType, actual.ElementType, parameters, bindings);
        if (pattern.TypeArguments is { Count: > 0 } patternArguments)
        {
            if (!SameNamedType(pattern, actual) || actual.TypeArguments is not { } actualArguments
                || actualArguments.Count != patternArguments.Count)
                return false;
            return patternArguments.Zip(actualArguments).All(pair =>
                InferFromArgument(pair.First, pair.Second, parameters, bindings));
        }

        return false;
    }

    // Binds one type parameter to the type of an argument, reconciling it with an earlier binding.
    private bool BindArgument(string name, SemanticType actual, IDictionary<string, SemanticType> bindings)
    {
        if (!bindings.TryGetValue(name, out var bound))
        {
            bindings[name] = actual;
            return true;
        }

        if (SemanticTypeComparer.Instance.Equals(bound, actual))
            return true;
        if (!IsClassOrInterface(bound) || !IsClassOrInterface(actual))
            return false;
        if (IsImplicitReferenceConversion(actual, bound))
            return true;
        if (IsImplicitReferenceConversion(bound, actual))
        {
            bindings[name] = actual;
            return true;
        }

        var common = NearestCommonSupertype(bound, actual);
        if (common is null)
            return false;
        bindings[name] = common;
        return true;
    }

    // Binds from the expected type. It binds only the parameters that the arguments left open, and it widens a class or
    // interface binding to the expected type when that binding converts to it. An expected type of another shape binds
    // nothing; a mismatch is reported where the value is used, so the expected type never rejects a candidate.
    private void BindFromExpectedType(
        SemanticType pattern,
        SemanticType expected,
        IReadOnlyList<string> parameters,
        IDictionary<string, SemanticType> bindings)
    {
        if (!ContainsGenericParameter(pattern))
            return;
        if (pattern.IsGenericParameter)
        {
            var name = pattern.GenericParameterName!;
            if (!parameters.Contains(name, StringComparer.Ordinal))
                return;
            if (!bindings.TryGetValue(name, out var bound))
                bindings[name] = expected;
            else if (IsClassOrInterface(bound) && IsClassOrInterface(expected) && IsImplicitReferenceConversion(bound, expected))
                bindings[name] = expected;
            return;
        }

        if (pattern.IsPointer && expected.IsPointer && pattern.Pointee is not null && expected.Pointee is not null)
            BindFromExpectedType(pattern.Pointee, expected.Pointee, parameters, bindings);
        else if (pattern.IsArray && expected.IsArray && pattern.ElementType is not null && expected.ElementType is not null)
            BindFromExpectedType(pattern.ElementType, expected.ElementType, parameters, bindings);
        else if (pattern.TypeArguments is { Count: > 0 } patternArguments && SameNamedType(pattern, expected)
            && expected.TypeArguments is { } expectedArguments && expectedArguments.Count == patternArguments.Count)
        {
            foreach (var (patternArgument, expectedArgument) in patternArguments.Zip(expectedArguments))
                BindFromExpectedType(patternArgument, expectedArgument, parameters, bindings);
        }
    }

    // The nearest class or interface that both types reach: the common supertype with the fewest steps from the two types
    // together. Null when none is common, when the nearest is tied, or when it is generic, since its arguments are not known.
    private SemanticType? NearestCommonSupertype(SemanticType left, SemanticType right)
    {
        var leftDistances = SupertypeDistances(left);
        var rightDistances = SupertypeDistances(right);
        var shared = leftDistances
            .Where(pair => rightDistances.ContainsKey(pair.Key))
            .Select(pair => (name: pair.Key, distance: pair.Value + rightDistances[pair.Key]))
            .ToList();
        if (shared.Count == 0)
            return null;

        var nearest = shared.Min(item => item.distance);
        var candidates = shared.Where(item => item.distance == nearest).ToList();
        if (candidates.Count != 1)
            return null;

        var name = candidates[0].name;
        if (_classesByName.TryGetValue(name, out var cls))
            return cls.DeclaredTypeParameters.Count == 0 ? SemanticType.Class(name) : null;
        if (_interfacesByName.TryGetValue(name, out var iface))
            return iface.DeclaredTypeParameters.Count == 0 ? SemanticType.Interface(name) : null;
        return null;
    }

    // The classes and interfaces that a class reaches through its base class and its interfaces, each with its step count. The
    // type itself is at 0.
    private Dictionary<string, int> SupertypeDistances(SemanticType type)
    {
        var distances = new Dictionary<string, int>(StringComparer.Ordinal) { [type.StructName!] = 0 };
        var frontier = new List<string> { type.StructName! };
        for (var depth = 1; frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var name in frontier)
            {
                if (!_classesByName.TryGetValue(name, out var cls))
                    continue;
                IEnumerable<string> parents = cls.BaseTypeName is null
                    ? cls.InterfaceNames
                    : cls.InterfaceNames.Prepend(cls.BaseTypeName);
                foreach (var parent in parents)
                {
                    if (distances.TryAdd(parent, depth))
                        next.Add(parent);
                }
            }

            frontier = next;
        }

        return distances;
    }

    private bool IsImplicitReferenceConversion(SemanticType from, SemanticType to) =>
        IsReferenceCast(from, to, out var implicitConversion) && implicitConversion;

    private static bool IsClassOrInterface(SemanticType type) => type.IsClass || type.IsInterface;

    // Whether two types name the same class, interface, struct, or union, so that their type arguments line up.
    private static bool SameNamedType(SemanticType left, SemanticType right) =>
        left.Kind == right.Kind && left.StructName == right.StructName && left.EnumName == right.EnumName;
}
