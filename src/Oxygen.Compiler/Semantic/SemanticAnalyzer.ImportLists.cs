using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;

namespace Oxygen.Compiler.Semantic;

public sealed partial class SemanticAnalyzer
{
    private enum DeclarationKind
    {
        Function,
        Type
    }

    // Checks each name of a symbol list where it is imported. The imported module must declare the name as a public declaration.
    // A name that the importing module already declares as a declaration of the same kind clashes, and the clash is reported here,
    // at the import, not at a use.
    private void ValidateSymbolImport(
        ImportDeclaration import,
        string importingModule,
        IReadOnlyList<CompilationUnit> units,
        IReadOnlyList<ExternalModuleDeclaration>? externalModules)
    {
        foreach (var symbol in import.Symbols ?? [])
        {
            var exported = DeclarationsNamed(import.Name, symbol.Name, units, externalModules)
                .Where(declaration => declaration.Visibility == Visibility.Public)
                .Select(declaration => declaration.Kind)
                .ToList();
            if (exported.Count == 0)
                throw ErrorWithCode(DiagnosticCode.NotDeclared,
                    $"Module '{import.Name}' does not declare a public '{symbol.Name}'", symbol.Location, symbol.Name.Length);

            var local = DeclarationsNamed(importingModule, symbol.LocalName, units, externalModules)
                .Select(declaration => declaration.Kind)
                .ToHashSet();
            foreach (var kind in exported)
            {
                if (local.Contains(kind))
                    throw ErrorWithCode(DiagnosticCode.AlreadyDeclared,
                        $"The import of '{symbol.Name}' clashes with the {DescribeKind(kind)} '{symbol.LocalName}' that module " +
                        $"'{importingModule}' declares",
                        symbol.Location, symbol.LocalName.Length);
            }
        }
    }

    // The declarations named `name` in `moduleName`, from its source units and from native metadata. A module that spans
    // several files is one module, so every unit with its name counts. Module-level variables are not listed: they are
    // private to their module, so they cannot be imported.
    private static List<(DeclarationKind Kind, Visibility Visibility)> DeclarationsNamed(
        string moduleName,
        string name,
        IReadOnlyList<CompilationUnit> units,
        IReadOnlyList<ExternalModuleDeclaration>? externalModules)
    {
        var found = new List<(DeclarationKind, Visibility)>();
        foreach (var unit in units.Where(unit => (unit.ModuleName ?? "program") == moduleName))
        {
            found.AddRange(unit.Functions.Where(item => item.Name == name)
                .Select(item => (DeclarationKind.Function, item.Visibility)));
            found.AddRange(unit.Structs.Where(item => item.Name == name)
                .Select(item => (DeclarationKind.Type, item.Visibility)));
            found.AddRange(unit.Enums.Where(item => item.Name == name)
                .Select(item => (DeclarationKind.Type, item.Visibility)));
            found.AddRange(unit.Unions.Where(item => item.Name == name)
                .Select(item => (DeclarationKind.Type, item.Visibility)));
            found.AddRange(unit.Classes.Where(item => item.Name == name)
                .Select(item => (DeclarationKind.Type, item.Visibility)));
            found.AddRange(unit.Interfaces.Where(item => item.Name == name)
                .Select(item => (DeclarationKind.Type, item.Visibility)));
        }

        foreach (var module in (externalModules ?? []).Where(module => module.ModuleName == moduleName))
            found.AddRange(module.Functions.Where(item => item.Name == name)
                .Select(item => (DeclarationKind.Function, item.Visibility)));
        return found;
    }

    private static string DescribeKind(DeclarationKind kind) => kind switch
    {
        DeclarationKind.Function => "function",
        _ => "type"
    };
}
