using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

// Access: visibility of members, constructors and accessors.
public sealed partial class SemanticAnalyzer
{
    // Reports a constructor that matches the arguments but is not visible from here, instead of the generic no-match error.
    private void EnsureConstructorAccessible(ClassSymbol cls, int argumentCount, string moduleName, SourceLocation location)
    {
        var hidden = cls.Constructors.FirstOrDefault(constructor =>
            constructor.Parameters.Count == argumentCount
            && !IsMemberAccessible(constructor.Visibility, cls.Name, cls.ModuleName, moduleName));
        if (hidden is not null)
            throw Error(
                $"No accessible constructor of '{cls.Name}' matches the supplied arguments; the matching constructor is " +
                $"{hidden.Visibility.ToString().ToLowerInvariant()}",
                location, cls.Name.Length);
    }

    // A method's module name ends with its owner class: `module.Class`.
    private static string MethodOwnerName(FunctionSymbol method) =>
        method.ModuleName[(method.ModuleName.LastIndexOf('.') + 1)..];

    private static string MethodOwnerModule(FunctionSymbol method) =>
        method.ModuleName[..Math.Max(0, method.ModuleName.LastIndexOf('.'))];

    // Whether a member of `ownerClassName` (declared in `ownerModuleName` with `visibility`) can be used from the code being
    // analyzed in `moduleName`. Public is open; internal needs the same module; protected needs the owner class or a
    // class derived from it; private needs the owner class itself.
    // A protected member is also limited by the receiver: through an expression of type R, the current class must be R
    // or a base of R, as in C#. A null receiver (a constructor) is not limited that way.
    // Whether a field of a class is declared by that class, rather than inherited from a base class.
    private static bool IsDeclaredBy(ClassFieldSymbol field, ClassSymbol cls) =>
        cls.Declaration.Fields.Any(declared => ReferenceEquals(declared, field.Declaration))
        || cls.DeclaredProperties.Any(property => property.Name == field.Name);

    // The class that declares a field, found from a class or one of its base classes.
    private ClassSymbol DeclaringClass(ClassFieldSymbol field, ClassSymbol cls)
    {
        for (var current = cls; ; current = _classesByName[current.BaseTypeName!])
        {
            if (IsDeclaredBy(field, current) || current.BaseTypeName is null || !_classesByName.ContainsKey(current.BaseTypeName))
                return current;
        }
    }

    // The base class that declares a private field which the current class inherits, or null. Such a field cannot be named here.
    private ClassSymbol? PrivateInheritedFieldOwner(string name)
    {
        for (var current = _currentOwnerClass?.BaseTypeName is not null && _classesByName.ContainsKey(_currentOwnerClass.BaseTypeName)
                ? _classesByName[_currentOwnerClass.BaseTypeName] : null;
             current is not null;
             current = current.BaseTypeName is not null && _classesByName.ContainsKey(current.BaseTypeName)
                 ? _classesByName[current.BaseTypeName] : null)
        {
            var field = current.Fields.FirstOrDefault(candidate => candidate.Name == name);
            if (field is { Visibility: Visibility.Private } && IsDeclaredBy(field, current))
                return current;
        }
        return null;
    }

    // A class with a private destructor is destroyed only by its own members, so its objects are created only inside it.
    private void RequireConstructibleHere(ClassSymbol cls, SourceLocation location)
    {
        if (cls.Destructor is { Visibility: Visibility.Private } && _currentOwnerClass?.Name != cls.Name)
            throw Error(
                $"Class '{cls.Name}' has a private destructor, so its objects can only be created inside the class",
                location, cls.Name.Length);
    }

    private bool IsMemberAccessible(Visibility visibility, string ownerClassName, string ownerModuleName, string moduleName,
        string? receiverClassName = null) =>
        visibility switch
        {
            Visibility.Public => true,
            // Inside a class body the module name can carry the class name, so the class's own module is used there.
            Visibility.Internal => string.Equals(ownerModuleName, _currentOwnerClass?.ModuleName ?? moduleName, StringComparison.Ordinal),
            Visibility.Protected => _currentOwnerClass is not null
                && (_currentOwnerClass.Name == ownerClassName || IsClassDerivedFrom(_currentOwnerClass.Name, ownerClassName))
                && (receiverClassName is null || IsClassDerivedFrom(receiverClassName, _currentOwnerClass.Name)),
            _ => _currentOwnerClass is not null && _currentOwnerClass.Name == ownerClassName
        };

    private void EnsureMemberAccessible(Visibility visibility, string memberKind, string memberName, ClassSymbol owner,
        string moduleName, SourceLocation location, string? receiverClassName = null)
    {
        if (!IsMemberAccessible(visibility, owner.Name, owner.ModuleName, moduleName, receiverClassName))
            throw Error(
                $"{memberKind} '{memberName}' of class '{owner.Name}' is {visibility.ToString().ToLowerInvariant()} and cannot be used here",
                location, memberName.Length);
    }

    private void EnsureAccessorVisible(PropertySymbol property, PropertyAccessorDeclaration accessor,
        SourceLocation location)
    {
        var owner = _classesByName.Values.FirstOrDefault(candidate =>
            candidate.DeclaredProperties.Contains(property));
        if (owner is null)
            return;
        var visible = accessor.Visibility switch
        {
            Visibility.Public => true,
            Visibility.Internal => _currentOwnerClass is null
                || string.Equals(_currentOwnerClass.ModuleName, owner.ModuleName, StringComparison.Ordinal),
            Visibility.Protected => _currentOwnerClass is not null
                && (_currentOwnerClass.Name == owner.Name || IsClassDerivedFrom(_currentOwnerClass.Name, owner.Name)),
            _ => _currentOwnerClass is not null && _currentOwnerClass.Name == owner.Name
        };
        if (!visible)
            throw Error($"Accessor for property '{property.Name}' is not accessible", location, property.Name.Length);
    }

}
