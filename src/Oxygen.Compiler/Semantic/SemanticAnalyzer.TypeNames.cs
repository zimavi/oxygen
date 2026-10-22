using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

// Type names are module-scoped. Every type is keyed by its qualified name, `module.Name`, so two modules can declare one name.
// A name written in a module denotes the type its module can see: a type the module declares, a type of an imported module, or
// a fully qualified type. Two visible types with one name are ambiguous where the name is used; the qualified name, or the
// import alias with the name, picks one.
public sealed partial class SemanticAnalyzer
{
    // The module and imports whose names are in scope while a type name is resolved. Set for each declaration and body.
    private string _typeScopeModule = "program";
    private IReadOnlyList<ImportDeclaration> _typeScopeImports = [];

    // The type resolved for each type syntax node, recorded for the backend. Each node is resolved in the scope it is written in.
    private readonly Dictionary<TypeSyntax, SemanticType> _resolvedTypeSyntax = new(ReferenceEqualityComparer.Instance);

    private static string QualifiedTypeName(string moduleName, string name) => $"{moduleName}.{name}";

    // The module whose names are in scope for code of `moduleName`. A method's module name is its class's qualified name,
    // so the scope is the module that declares that class.
    private string TypeScopeModuleOf(string moduleName) =>
        _classesByName.TryGetValue(moduleName, out var declaringClass) ? declaringClass.ModuleName : moduleName;

    // Runs an action with the names of one module in scope, then restores the previous scope.
    private T InTypeScope<T>(string moduleName, IReadOnlyList<ImportDeclaration> imports, Func<T> action)
    {
        var previousModule = _typeScopeModule;
        var previousImports = _typeScopeImports;
        _typeScopeModule = moduleName;
        _typeScopeImports = imports;
        try
        {
            return action();
        }
        finally
        {
            _typeScopeModule = previousModule;
            _typeScopeImports = previousImports;
        }
    }

    // Runs an action in the scope of the declaration of a type, found by the type's qualified name. A type without a
    // recorded declaration runs in the current scope.
    private void InTypeScopeOf(string qualifiedTypeName, Action action)
    {
        if (_typeContexts.TryGetValue(qualifiedTypeName, out var context))
            InTypeScope(context.ModuleName, context.Imports, () => { action(); return 0; });
        else
            action();
    }

    private T InTypeScopeOf<T>(string qualifiedTypeName, Func<T> action) =>
        _typeContexts.TryGetValue(qualifiedTypeName, out var context)
            ? InTypeScope(context.ModuleName, context.Imports, action)
            : action();

    // Runs an action in the scope of a declared function, so the names it writes resolve where it is declared.
    private T InFunctionScopeOf<T>(FunctionDeclaration declaration, Func<T> action) =>
        _functionContexts.TryGetValue(declaration, out var context)
            ? InTypeScope(context.ModuleName, context.Imports, action)
            : action();

    private void InFunctionScopeOf(FunctionDeclaration declaration, Action action) =>
        InFunctionScopeOf(declaration, () => { action(); return 0; });

    // Whether a qualified name is a type or an alias. An alias is a type name too, so it is ambiguous with a type of the same name.
    private bool IsDeclaredType(string qualifiedName) =>
        _classesByName.ContainsKey(qualifiedName) || _interfacesByName.ContainsKey(qualifiedName)
        || _structsByName.ContainsKey(qualifiedName) || _enumsByName.ContainsKey(qualifiedName)
        || _unionsByName.ContainsKey(qualifiedName) || _aliasDeclarations.ContainsKey(qualifiedName);

    // The qualified name of the single type or alias that `name` denotes in the current scope, or null when none or several are visible.
    // Used where the declaration that names the type has already been checked, so ambiguity is not reported again.
    private string? VisibleTypeKeyOrNull(string name)
    {
        var matches = NameCandidates(name, _typeScopeModule, _typeScopeImports).Where(IsDeclaredType).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    // The nominal type that `name` denotes in the current scope, or null when no type of that name is visible. An alias is replaced by the
    // type it names. Used where the declaration that names the type has already been checked, so ambiguity is not reported again.
    private string? TypeNameOrNull(string name)
    {
        var key = VisibleTypeKeyOrNull(name);
        if (key is null)
            return null;
        return _aliasDeclarations.ContainsKey(key) ? NominalNameOf(AliasType(key)) : key;
    }

    // The qualified name or alias that `name` denotes in the current scope, or null when no type of that name is visible.
    // Two visible types are an error: the name is ambiguous at this use.
    private string? FindVisibleTypeKey(string name, SourceLocation location)
    {
        var matches = NameCandidates(name, _typeScopeModule, _typeScopeImports)
            .Where(IsDeclaredType)
            .ToList();
        if (matches.Count > 1)
            throw ErrorWithCode(DiagnosticCode.AmbiguousType,
                $"Type '{name}' is ambiguous: it names {string.Join(" and ", matches.Select(match => $"'{match}'"))}; " +
                $"write the qualified name, such as '{matches[0]}', or use the import alias",
                location, name.Length);
        return matches.Count == 1 ? matches[0] : null;
    }

    // The qualified name of the nominal type that `name` denotes in the current scope, or null when no type of that name is visible. An alias
    // denotes the type it names (see NominalTypeName).
    private string? FindTypeName(string name, SourceLocation location) =>
        NominalTypeName(FindVisibleTypeKey(name, location), name, location);
}
