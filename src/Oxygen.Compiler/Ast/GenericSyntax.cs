namespace Oxygen.Compiler.Ast;

// The declarations of a unit that a library carries in the binary form, for an importing program. These are the generic classes, unions and
// functions, which the program instantiates, with their bodies; the structs, which are data, generic or not; the interfaces, which declare
// signatures only; the non-generic classes, without their bodies, which the program declares and whose code is in the library; and the
// module-level variables, with their constant initializers, since a generic body of the module may use them. Each declaration is extracted
// on its own. The language has no type parameters on methods, so a generic declaration is always top-level. Enums are not carried yet.
public static class GenericSyntax
{
    // The unit with its module header, its imports and the declarations above. Enums are dropped.
    public static CompilationUnit Extract(CompilationUnit unit) => unit with
    {
        Structs = unit.Structs,
        Unions = unit.Unions.Where(IsGeneric).ToArray(),
        Classes = unit.Classes.Select(cls => IsGeneric(cls) ? cls : WithoutBodies(cls)).ToArray(),
        Interfaces = unit.Interfaces,
        Functions = unit.Functions.Where(IsGeneric).ToArray(),
        Enums = [],
        Globals = unit.Globals
    };

    // Whether the unit has a declaration to carry.
    public static bool HasDeclarations(CompilationUnit unit)
    {
        var generic = Extract(unit);
        return generic.Structs.Count > 0 || generic.Unions.Count > 0 || generic.Classes.Count > 0 ||
            generic.Interfaces.Count > 0 || generic.Functions.Count > 0 || generic.Globals.Count > 0;
    }

    // Whether every declaration of the unit is one that the binary form carries: structs, unions and functions that are generic where they
    // have parameters, classes that are generic or have no bodies, any interface, and globals. The unit has no enums. A unit in the
    // binary form of a library must pass this check.
    public static bool IsGenericOnly(CompilationUnit unit) =>
        unit.Enums.Count == 0 &&
        unit.Unions.All(IsGeneric) &&
        unit.Classes.All(cls => IsGeneric(cls) || HasNoBodies(cls)) &&
        unit.Functions.All(IsGeneric);

    // A class's declaration without the bodies of its methods, constructors, destructor and property accessors. Its fields, base and
    // interfaces are kept, since they are its signature.
    public static ClassDeclaration WithoutBodies(ClassDeclaration cls) => cls with
    {
        Properties = cls.Properties?.Select(property => property with
        {
            Accessors = property.Accessors.Select(accessor => accessor with { Body = accessor.Body is null ? null : Empty(accessor.Body) }).ToArray()
        }).ToArray(),
        Methods = cls.Methods.Select(method => method with { Body = Empty(method.Body) }).ToArray(),
        Constructors = cls.Constructors.Select(constructor => constructor with { Body = Empty(constructor.Body) }).ToArray(),
        Destructor = cls.Destructor is null ? null : cls.Destructor with { Body = Empty(cls.Destructor.Body) }
    };

    private static bool HasNoBodies(ClassDeclaration cls) =>
        cls.Methods.All(method => method.Body.Statements.Count == 0) &&
        cls.Constructors.All(constructor => constructor.Body.Statements.Count == 0) &&
        (cls.Destructor is null || cls.Destructor.Body.Statements.Count == 0) &&
        cls.DeclaredProperties.All(property => property.Accessors.All(accessor => accessor.Body is null || accessor.Body.Statements.Count == 0));

    private static BlockStatement Empty(BlockStatement body) => body with { Statements = [] };

    private static bool IsGeneric(UnionDeclaration declaration) => declaration.DeclaredTypeParameters.Count > 0;
    private static bool IsGeneric(ClassDeclaration declaration) => declaration.DeclaredTypeParameters.Count > 0;
    private static bool IsGeneric(FunctionDeclaration declaration) => declaration.DeclaredTypeParameters.Count > 0;
}
