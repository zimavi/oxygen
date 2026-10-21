namespace Oxygen.Compiler.Ast;

// The generic declarations of a unit: the classes, structs, unions, interfaces and functions with type parameters. A library
// carries these in the binary form, since an importing program instantiates them. The other declarations of the unit are not
// generic, so they are not part of this form. A generic method inside a non-generic class is not covered yet.
public static class GenericSyntax
{
    // The unit with its module header, its imports and its generic declarations. Enums and globals are never generic, so they are dropped.
    public static CompilationUnit Extract(CompilationUnit unit) => unit with
    {
        Structs = unit.Structs.Where(IsGeneric).ToArray(),
        Unions = unit.Unions.Where(IsGeneric).ToArray(),
        Classes = unit.Classes.Where(IsGeneric).ToArray(),
        Interfaces = unit.Interfaces.Where(IsGeneric).ToArray(),
        Functions = unit.Functions.Where(IsGeneric).ToArray(),
        Enums = [],
        Globals = []
    };

    // Whether every declaration of the unit is generic, and the unit has no enums and no globals. A unit in the binary form of
    // a library must pass this check.
    public static bool IsGenericOnly(CompilationUnit unit) =>
        unit.Enums.Count == 0 &&
        unit.Globals.Count == 0 &&
        unit.Structs.All(IsGeneric) &&
        unit.Unions.All(IsGeneric) &&
        unit.Classes.All(IsGeneric) &&
        unit.Interfaces.All(IsGeneric) &&
        unit.Functions.All(IsGeneric);

    private static bool IsGeneric(StructDeclaration declaration) => declaration.DeclaredTypeParameters.Count > 0;
    private static bool IsGeneric(UnionDeclaration declaration) => declaration.DeclaredTypeParameters.Count > 0;
    private static bool IsGeneric(ClassDeclaration declaration) => declaration.DeclaredTypeParameters.Count > 0;
    private static bool IsGeneric(InterfaceDeclaration declaration) => declaration.DeclaredTypeParameters.Count > 0;
    private static bool IsGeneric(FunctionDeclaration declaration) => declaration.DeclaredTypeParameters.Count > 0;
}
