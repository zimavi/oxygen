using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

// Type aliases, `type Name = Target;`. An alias is another name for its target type, so the alias and the target are the same type and
// no conversion is needed between them. An alias is resolved in the scope of its declaration, on first use, and every alias is resolved
// once after the types are registered, so that an error in an alias is reported at its declaration.
public sealed partial class SemanticAnalyzer
{
    // The aliases of the compilation by qualified name, and the type that each one names once it is resolved.
    private readonly Dictionary<string, TypeAliasDeclaration> _aliasDeclarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemanticType> _aliasTypes = new(StringComparer.Ordinal);

    // The aliases whose targets are being resolved. An alias that is reached again while it is in progress is part of a cycle.
    private readonly HashSet<string> _aliasesInProgress = new(StringComparer.Ordinal);

    // Records the aliases of every unit, before any type is registered, so that a type declared earlier can name an alias declared later.
    private void RegisterAliasNames(IReadOnlyList<CompilationUnit> compilationUnits, Func<int, string?> sourceNameAt)
    {
        for (var unitIndex = 0; unitIndex < compilationUnits.Count; unitIndex++)
        {
            var unit = compilationUnits[unitIndex];
            var moduleName = unit.ModuleName ?? "program";
            foreach (var declaration in unit.Aliases)
            {
                var key = QualifiedTypeName(moduleName, declaration.Name);
                _aliasDeclarations[key] = declaration;
                _typeContexts[key] = new DeclarationContext(moduleName, unit.Imports, sourceNameAt(unitIndex));
            }
        }
    }

    // Once the types are registered: an alias may not share its name with a type of its module, and every alias of the program is resolved, so
    // an error in an alias, or a cycle of aliases, is reported at its declaration. An alias of a native library is resolved only when the
    // program uses it, since the library may name a type that it does not carry (an enum).
    private void ResolveAliases(IReadOnlyList<CompilationUnit> compilationUnits, Func<int, string?> sourceNameAt,
        IReadOnlySet<string> libraryModules)
    {
        for (var unitIndex = 0; unitIndex < compilationUnits.Count; unitIndex++)
        {
            var unit = compilationUnits[unitIndex];
            var moduleName = unit.ModuleName ?? "program";
            if (libraryModules.Contains(moduleName))
                continue;
            foreach (var declaration in unit.Aliases)
            {
                var key = QualifiedTypeName(moduleName, declaration.Name);
                _sourceName = sourceNameAt(unitIndex);
                if (IsTypeDeclaredExceptAlias(key))
                    throw ErrorWithCode(DiagnosticCode.AlreadyDeclared, $"A type named '{declaration.Name}' is already declared",
                        declaration.Location, declaration.Name.Length);
                AliasType(key);
            }
        }
        _sourceName = null;
    }

    private bool IsTypeDeclaredExceptAlias(string qualifiedName) =>
        _classesByName.ContainsKey(qualifiedName) || _interfacesByName.ContainsKey(qualifiedName)
        || _structsByName.ContainsKey(qualifiedName) || _enumsByName.ContainsKey(qualifiedName)
        || _unionsByName.ContainsKey(qualifiedName);

    // The type that an alias names, resolved in the scope of its declaration and cached. Reaching an alias that is being resolved means
    // the aliases form a cycle, which is reported at the alias that was reached first, since that is where the cycle starts.
    private SemanticType AliasType(string qualifiedName)
    {
        if (_aliasTypes.TryGetValue(qualifiedName, out var resolved))
            return resolved;
        var declaration = _aliasDeclarations[qualifiedName];
        if (!_aliasesInProgress.Add(qualifiedName))
            throw Error($"Type alias '{declaration.Name}' is part of a cycle of type aliases", declaration.Location, declaration.Name.Length);
        try
        {
            resolved = InTypeScopeOf(qualifiedName, () => ResolveType(declaration.Target));
        }
        finally
        {
            _aliasesInProgress.Remove(qualifiedName);
        }
        _aliasTypes[qualifiedName] = resolved;
        return resolved;
    }

    // The qualified name of the nominal type that a resolved type is named by, or null when the type is not a class, struct, union, enum or
    // interface, or is an instance of a generic type (which an alias cannot name).
    private static string? NominalNameOf(SemanticType type) =>
        type.TypeArguments is null && (type.IsClass || type.IsStruct || type.IsInterface || type.IsUnion || type.IsEnum)
            ? type.StructName ?? type.EnumName
            : null;

    // The nominal type that a visible type name denotes, for uses that need a declared type (a construction, a static member, a base type).
    // A type name denotes itself. An alias denotes the type it names, which must be nominal, so any other alias is an error at the use.
    private string? NominalTypeName(string? key, string name, SourceLocation location)
    {
        if (key is null || !_aliasDeclarations.ContainsKey(key))
            return key;
        var aliased = AliasType(key);
        if (aliased.TypeArguments is not null)
            throw Error($"Type alias '{name}' names an instance of a generic type, which can be used only as a type", location, name.Length);
        return NominalNameOf(aliased)
            ?? throw Error($"Type alias '{name}' names {aliased}, which is not a class, struct, union, enum or interface, so it cannot be used here",
                location, name.Length);
    }
}
