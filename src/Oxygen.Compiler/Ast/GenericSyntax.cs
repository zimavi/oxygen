namespace Oxygen.Compiler.Ast;

// The generic declarations of a unit: the classes, structs, unions and functions with type parameters. A library carries these in the
// binary form, since an importing program instantiates them. Each one is extracted on its own, since nothing outside it is generic.
// The language has no type parameters on methods, so a generic declaration is always top-level.
// Interfaces are carried whether or not they are generic: they declare signatures only, so they have no body that needs the library's
// code, and a generic function's constraint may name one. Enums and globals are not carried yet.
public static class GenericSyntax
{
    // The unit with its module header, its imports and its generic declarations and interfaces. Enums and globals are dropped.
    public static CompilationUnit Extract(CompilationUnit unit) => unit with
    {
        Structs = unit.Structs.Where(IsGeneric).ToArray(),
        Unions = unit.Unions.Where(IsGeneric).ToArray(),
        Classes = unit.Classes.Where(IsGeneric).ToArray(),
        Interfaces = unit.Interfaces,
        Functions = unit.Functions.Where(IsGeneric).ToArray(),
        Enums = [],
        Globals = []
    };

    // Whether the unit has a declaration to carry.
    public static bool HasDeclarations(CompilationUnit unit)
    {
        var generic = Extract(unit);
        return generic.Structs.Count > 0 || generic.Unions.Count > 0 || generic.Classes.Count > 0 ||
            generic.Interfaces.Count > 0 || generic.Functions.Count > 0;
    }

    // Whether every declaration of the unit is one that the binary form carries: generic classes, structs, unions and functions, and any
    // interface. The unit has no enums and no globals. A unit in the binary form of a library must pass this check.
    public static bool IsGenericOnly(CompilationUnit unit) =>
        unit.Enums.Count == 0 &&
        unit.Globals.Count == 0 &&
        unit.Structs.All(IsGeneric) &&
        unit.Unions.All(IsGeneric) &&
        unit.Classes.All(IsGeneric) &&
        unit.Functions.All(IsGeneric);

    private static bool IsGeneric(StructDeclaration declaration) => declaration.DeclaredTypeParameters.Count > 0;
    private static bool IsGeneric(UnionDeclaration declaration) => declaration.DeclaredTypeParameters.Count > 0;
    private static bool IsGeneric(ClassDeclaration declaration) => declaration.DeclaredTypeParameters.Count > 0;
    private static bool IsGeneric(FunctionDeclaration declaration) => declaration.DeclaredTypeParameters.Count > 0;
}
