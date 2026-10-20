using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

// Class and interface hierarchy: validation, inherited members and the subtype queries.
public sealed partial class SemanticAnalyzer
{
    private void ValidatePropertyDeclaration(PropertyDeclaration property)
    {
        if (!property.HasGetter && !property.HasSetter && !property.HasInitializer)
            throw Error($"Property '{property.Name}' must declare a getter, setter, or initializer",
                property.Location, property.Name.Length);
        if (property.HasSetter && property.HasInitializer)
            throw Error($"Property '{property.Name}' cannot declare both 'set' and 'init'",
                property.Location, property.Name.Length);
        foreach (var accessor in property.Accessors)
        {
            if (VisibilityRank(accessor.Visibility) > VisibilityRank(property.Visibility))
                throw Error($"Accessor for property '{property.Name}' cannot be more visible than the property",
                    accessor.Location, property.Name.Length);
        }
    }

    private static int VisibilityRank(Visibility visibility) =>
        visibility switch
        {
            Visibility.Private => 0,
            Visibility.Protected => 1,
            Visibility.Internal => 1,
            Visibility.Public => 2,
            _ => 0
        };

    // Checks every class. With a diagnostics bag, each class's errors are reported and the others are still checked; the result
    // says whether all classes passed. Without one, the first error throws, as before.
    private bool ValidateClassHierarchy()
    {
        var valid = true;
        foreach (var classSymbol in _classesByName.Values.ToArray())
        {
            try
            {
                ValidateClassHierarchyOf(classSymbol);
            }
            catch (SemanticException exception) when (_diagnostics is not null)
            {
                _diagnostics?.Add(DiagnosticPhase.Semantic, exception.Message, exception.Location, exception.Length,
                    exception.IsSourceAttributed ? exception.SourceName : _sourceName);
                valid = false;
            }
        }

        return valid;
    }

    private void ValidateClassHierarchyOf(ClassSymbol classSymbol)
    {
        {
            if (classSymbol.IsValue)
            {
                if (classSymbol.BaseTypeName is not null)
                    throw DeclarationError(classSymbol.Name, $"Value class '{classSymbol.Name}' cannot inherit from another class",
                        classSymbol.Declaration.Location);
                if (classSymbol.InterfaceNames.Count != 0)
                    throw DeclarationError(classSymbol.Name, $"Value class '{classSymbol.Name}' cannot implement interfaces",
                        classSymbol.Declaration.Location);
                if (classSymbol.Methods.Any(method => method.Declaration.IsVirtual || method.Declaration.IsOverride))
                    throw DeclarationError(classSymbol.Name, $"Value class '{classSymbol.Name}' cannot declare virtual or override methods",
                        classSymbol.Declaration.Location);
                if (classSymbol.DeclaredProperties.Any(property =>
                        property.Declaration.IsVirtual || property.Declaration.IsOverride))
                    throw DeclarationError(classSymbol.Name, $"Value class '{classSymbol.Name}' cannot declare virtual or override properties",
                        classSymbol.Declaration.Location);
            }
            // A base name that is an interface is already part of InterfaceNames.
            if (classSymbol.BaseTypeName is not null
                && !_classesByName.ContainsKey(classSymbol.BaseTypeName)
                && !_interfacesByName.ContainsKey(classSymbol.BaseTypeName))
                throw DeclarationError(classSymbol.Name, $"Base class '{classSymbol.BaseTypeName}' is not declared", classSymbol.Declaration.Location);
            foreach (var interfaceName in classSymbol.InterfaceNames)
                if (!_interfacesByName.ContainsKey(interfaceName))
                    throw DeclarationError(classSymbol.Name, $"Interface '{interfaceName}' is not declared", classSymbol.Declaration.Location);
            var inherited = classSymbol.BaseTypeName is null || !_classesByName.ContainsKey(classSymbol.BaseTypeName)
                ? [] : GetClassMethods(classSymbol.BaseTypeName, []);
            var inheritedProperties = classSymbol.BaseTypeName is null || !_classesByName.ContainsKey(classSymbol.BaseTypeName)
                ? [] : GetClassProperties(classSymbol.BaseTypeName, []);
            foreach (var method in classSymbol.Methods)
            {
                var overridden = inherited.FirstOrDefault(candidate => MethodSignature(candidate) == MethodSignature(method));
                if (method.Declaration.IsOverride)
                {
                    if (overridden is null)
                        throw DeclarationError(classSymbol.Name, $"Method '{method.Name}' is marked 'override' but does not override a base method",
                            method.Declaration.Location, method.Name.Length);
                    if (!overridden.Declaration.IsVirtual && !overridden.Declaration.IsOverride)
                        throw DeclarationError(classSymbol.Name, $"Method '{method.Name}' cannot override non-virtual method '{overridden.Name}'",
                            method.Declaration.Location, method.Name.Length);
                }
                else if (overridden is not null && (overridden.Declaration.IsVirtual || overridden.Declaration.IsOverride))
                {
                    throw DeclarationError(classSymbol.Name, $"Method '{method.Name}' overrides a virtual method and must be marked 'override'",
                        method.Declaration.Location, method.Name.Length);
                }
            }
            foreach (var property in classSymbol.DeclaredProperties)
            {
                var overridden = inheritedProperties.FirstOrDefault(candidate =>
                    candidate.Name == property.Name && candidate.Type == property.Type);
                if (property.Declaration.IsOverride)
                {
                    if (overridden is null)
                        throw DeclarationError(classSymbol.Name, $"Property '{property.Name}' is marked 'override' but does not override a base property",
                            property.Declaration.Location, property.Name.Length);
                    if (!overridden.Declaration.IsVirtual && !overridden.Declaration.IsOverride)
                        throw DeclarationError(classSymbol.Name, $"Property '{property.Name}' cannot override a non-virtual property",
                            property.Declaration.Location, property.Name.Length);
                }
                else if (overridden is not null
                         && (overridden.Declaration.IsVirtual || overridden.Declaration.IsOverride))
                {
                    throw DeclarationError(classSymbol.Name, $"Property '{property.Name}' overrides a virtual property and must be marked 'override'",
                        property.Declaration.Location, property.Name.Length);
                }
            }
            foreach (var contract in GetInterfaceContracts(classSymbol, classSymbol.InterfaceNames))
            {
                var implementations = classSymbol.Methods.Concat(inherited).Where(method =>
                    method.Name == contract.Name && method.Parameters.Select(parameter => parameter.Type)
                        .SequenceEqual(contract.Parameters.Select(parameter => parameter.Type), SemanticTypeComparer.Instance)
                    && SemanticTypeComparer.Instance.Equals(method.ReturnType, contract.ReturnType)).ToList();
                if (implementations.Count == 0)
                    throw DeclarationError(classSymbol.Name, $"Class '{classSymbol.Name}' does not implement interface method '{contract.Name}'",
                        classSymbol.Declaration.Location);
                // Interface members are public, so the implementation that satisfies the contract must be public too.
                if (!implementations.Any(method => method.Visibility == Visibility.Public))
                    throw DeclarationError(classSymbol.Name, $"Interface method '{contract.Name}' must be implemented as public in class '{classSymbol.Name}'",
                        classSymbol.Declaration.Location);
            }
            foreach (var contract in classSymbol.InterfaceNames.SelectMany(name => _interfacesByName[name].DeclaredProperties))
            {
                var implementation = classSymbol.Fields.FirstOrDefault(field => field.Name == contract.Name);
                if (implementation is null || !SemanticTypeComparer.Instance.Equals(implementation.Type, contract.Type))
                    throw DeclarationError(classSymbol.Name, $"Class '{classSymbol.Name}' does not implement interface property '{contract.Name}'",
                        classSymbol.Declaration.Location);
                if (contract.Declaration.HasGetter
                    && !classSymbol.DeclaredProperties.Concat(
                            classSymbol.BaseTypeName is not null && _classesByName.TryGetValue(classSymbol.BaseTypeName, out var baseClass)
                                ? baseClass.DeclaredProperties
                                : [])
                        .Any(property => property.Name == contract.Name && property.Declaration.HasGetter))
                    throw DeclarationError(classSymbol.Name, $"Class '{classSymbol.Name}' does not provide a getter for interface property '{contract.Name}'",
                        classSymbol.Declaration.Location);
                if ((contract.Declaration.HasSetter || contract.Declaration.HasInitializer)
                    && !classSymbol.DeclaredProperties.Any(property =>
                        property.Name == contract.Name
                        && (property.Declaration.HasSetter || property.Declaration.HasInitializer)))
                    throw DeclarationError(classSymbol.Name, $"Class '{classSymbol.Name}' does not provide a setter for interface property '{contract.Name}'",
                        classSymbol.Declaration.Location);
            }
        }
    }

    private IReadOnlyList<FunctionSymbol> GetClassMethods(string name, HashSet<string> active)
    {
        if (!active.Add(name))
            throw Error($"Class inheritance cycle involving '{name}'", _classesByName[name].Declaration.Location);
        var cls = _classesByName[name];
        var methods = cls.BaseTypeName is not null && _classesByName.ContainsKey(cls.BaseTypeName)
            ? GetClassMethods(cls.BaseTypeName, active)
            : [];
        active.Remove(name);
        var result = methods.ToList();
        foreach (var method in cls.Methods)
        {
            var index = result.FindIndex(candidate => MethodSignature(candidate) == MethodSignature(method));
            if (index >= 0) result[index] = method;
            else result.Add(method);
        }
        return result;
    }

    private IReadOnlyList<PropertySymbol> GetClassProperties(string name, HashSet<string> active)
    {
        if (!active.Add(name))
            throw Error($"Class inheritance cycle involving '{name}'", _classesByName[name].Declaration.Location);
        var cls = _classesByName[name];
        var properties = cls.BaseTypeName is not null && _classesByName.ContainsKey(cls.BaseTypeName)
            ? GetClassProperties(cls.BaseTypeName, active).ToList()
            : [];
        active.Remove(name);
        foreach (var property in cls.DeclaredProperties)
        {
            var index = properties.FindIndex(candidate => candidate.Name == property.Name);
            if (index >= 0) properties[index] = property;
            else properties.Add(property);
        }
        return properties;
    }

    private IReadOnlyList<ClassFieldSymbol> GetClassFields(string name, HashSet<string> active)
    {
        if (!active.Add(name))
            throw DeclarationError(name, $"Class inheritance cycle involving '{name}'", _classesByName[name].Declaration.Location);
        var cls = _classesByName[name];
        var inherited = cls.BaseTypeName is not null && _classesByName.ContainsKey(cls.BaseTypeName)
            ? GetClassFields(cls.BaseTypeName, active)
            : [];
        active.Remove(name);
        var fields = inherited.ToList();
        var own = _ownClassFields.TryGetValue(name, out var declared) ? declared : cls.Fields;
        foreach (var field in own)
        {
            var inheritedIndex = fields.FindIndex(candidate => candidate.Name == field.Name);
            if (inheritedIndex < 0)
            {
                fields.Add(field);
                continue;
            }
            var overridingProperty = cls.DeclaredProperties.FirstOrDefault(property =>
                property.Name == field.Name && property.Declaration.IsOverride);
            if (overridingProperty is null)
                fields.Add(field);
            else
                fields[inheritedIndex] = field;
        }
        if (fields.GroupBy(field => field.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw DeclarationError(name, $"Class '{name}' contains a field that hides an inherited field", cls.Declaration.Location);
        return fields.ToArray();
    }

    private IReadOnlyList<FunctionSymbol> GetInterfaceMethods(string name, HashSet<string> active)
    {
        if (!active.Add(name))
            throw Error($"Interface inheritance cycle involving '{name}'", _interfacesByName[name].Declaration.Location);
        active.Remove(name);
        return _interfacesByName[name].Methods;
    }

    private IReadOnlyList<FunctionSymbol> GetInterfaceContracts(ClassSymbol classSymbol, IReadOnlyList<string> interfaceNames)
    {
        var contracts = new List<FunctionSymbol>();
        foreach (var interfaceType in classSymbol.DeclaredInterfaceTypes)
        {
            if (!_interfacesByName.TryGetValue(interfaceType.StructName!, out var interfaceSymbol))
                continue;

            var substitutions = interfaceSymbol.DeclaredTypeParameters
                .Zip(interfaceType.TypeArguments ?? [], (name, type) => (name, type))
                .ToDictionary(pair => pair.name, pair => pair.type, StringComparer.Ordinal);
            contracts.AddRange(interfaceSymbol.Methods.Select(method => SubstituteFunctionTypes(method, substitutions)));
        }

        foreach (var name in interfaceNames)
        {
            if (!classSymbol.DeclaredInterfaceTypes.Any(type => type.StructName == name))
                contracts.AddRange(GetInterfaceMethods(name, []));
        }

        return contracts;
    }

    private PropertySymbol? FindProperty(ClassSymbol cls, string name)
    {
        var property = cls.DeclaredProperties.FirstOrDefault(candidate => candidate.Name == name);
        if (property is not null)
            return property;
        return cls.BaseTypeName is not null && _classesByName.TryGetValue(cls.BaseTypeName, out var baseClass)
            ? FindProperty(baseClass, name)
            : null;
    }

    private bool IsOwnedClass(SemanticType type) =>
        type.IsClass
        && (!_classesByName.TryGetValue(type.StructName!, out var cls) || !cls.IsValue);

    private bool IsClassDerivedFrom(string sourceName, string targetName)
    {
        if (string.Equals(sourceName, targetName, StringComparison.Ordinal))
            return true;
        if (!_classesByName.TryGetValue(sourceName, out var source))
            return false;
        return source.BaseTypeName is not null
            && _classesByName.ContainsKey(source.BaseTypeName)
            && IsClassDerivedFrom(source.BaseTypeName, targetName);
    }

    private bool ClassImplementsInterface(string className, string interfaceName)
    {
        if (!_classesByName.TryGetValue(className, out var cls))
            return false;

        if (cls.InterfaceNames.Contains(interfaceName, StringComparer.Ordinal))
            return true;
        if (cls.BaseTypeName is not null)
        {
            if (_interfacesByName.ContainsKey(cls.BaseTypeName)
                && string.Equals(cls.BaseTypeName, interfaceName, StringComparison.Ordinal))
                return true;
            if (_classesByName.ContainsKey(cls.BaseTypeName)
                && ClassImplementsInterface(cls.BaseTypeName, interfaceName))
                return true;
        }

        return false;
    }

}
