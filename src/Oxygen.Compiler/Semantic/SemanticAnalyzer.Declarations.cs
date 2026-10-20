using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

// Declarations: function symbols, C interop and memory annotations, and the main-function rules.
public sealed partial class SemanticAnalyzer
{
    private static FunctionSymbol CreateConstructorFunctionSymbol(string moduleName, ConstructorSymbol constructor)
    {
        var declaration = new FunctionDeclaration(
            new TypeSyntax(PrimitiveType.Void, false, constructor.Declaration.Location),
            constructor.ClassName,
            constructor.Declaration.Parameters,
            constructor.Declaration.Body,
            constructor.Visibility,
            constructor.Declaration.Location);
        return new FunctionSymbol(
            constructor.ClassName,
            SemanticType.FromPrimitive(PrimitiveType.Void),
            constructor.Parameters,
            declaration,
            $"{moduleName}.{constructor.ClassName}",
            constructor.Visibility);
    }

    private static FunctionSymbol CreateDestructorFunctionSymbol(string moduleName, DestructorDeclaration destructor)
    {
        var declaration = new FunctionDeclaration(
            new TypeSyntax(PrimitiveType.Void, false, destructor.Location),
            destructor.ClassName,
            [],
            destructor.Body,
            destructor.Visibility,
            destructor.Location);
        return new FunctionSymbol(
            destructor.ClassName,
            SemanticType.FromPrimitive(PrimitiveType.Void),
            [],
            declaration,
            $"{moduleName}.{destructor.ClassName}",
            destructor.Visibility);
    }

    private void EnsureTypeNameAvailable(string name, SourceLocation location)
    {
        if (_enumsByName.ContainsKey(name) || _structsByName.ContainsKey(name) || _unionsByName.ContainsKey(name)
            || _classesByName.ContainsKey(name) || _interfacesByName.ContainsKey(name))
            throw Error($"A type named '{name}' is already declared", location, name.Length);
    }

    private static string MethodSignature(FunctionSymbol method) =>
        $"{method.Name}({string.Join(",", method.Parameters.Select(parameter => parameter.Type))})";

    private FunctionSymbol CreateFunctionSymbol(FunctionDeclaration declaration, string moduleName)
    {
        var returnType = ResolveType(declaration.ReturnType);
        var parameters = new List<ParameterSymbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var parameter in declaration.Parameters)
        {
            if (!names.Add(parameter.Name))
            {
                throw Error($"Parameter '{parameter.Name}' is declared more than once", parameter.Location, parameter.Name.Length);
            }

            parameters.Add(new ParameterSymbol(parameter.Name,
                ResolveValueType(parameter.Type, $"Parameter '{parameter.Name}'", parameter.Location), parameter));
        }

        return new FunctionSymbol(
            declaration.Name,
            returnType,
            parameters,
            declaration,
            moduleName,
            declaration.Visibility,
            CInterop: ParseCInterop(declaration),
            GenericParameters: declaration.DeclaredTypeParameters.Select(parameter => parameter.Name).ToArray(),
            IsExternal: declaration.IsExternalDeclaration,
            Memory: ResolveMemoryMode(declaration));
    }

    // `@manual_memory` and `@auto_memory` select a function's memory mode; without either, the compiler default applies.
    private MemoryMode ResolveMemoryMode(FunctionDeclaration declaration)
    {
        var manual = declaration.DeclaredAnnotations.Any(annotation => annotation.Name == "manual_memory");
        var automatic = declaration.DeclaredAnnotations.Any(annotation => annotation.Name == "auto_memory");
        if (manual && automatic)
            throw Error($"Function '{declaration.Name}' cannot be both @manual_memory and @auto_memory", declaration.Location,
                declaration.Name.Length);
        if (manual)
            return MemoryMode.Manual;
        return automatic ? MemoryMode.Automatic : DefaultMemoryMode;
    }

    private static CInteropFunctionMetadata? ParseCInterop(FunctionDeclaration declaration)
    {
        string? externalName = null;
        var callingConvention = "c";
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var annotation in declaration.DeclaredAnnotations)
        {
            if (!seen.Add(annotation.Name))
                throw Error($"Annotation '@{annotation.Name}' may only appear once", annotation.Location, annotation.Name.Length + 1);

            switch (annotation.Name)
            {
                case "extern":
                    if (annotation.Arguments.Count != 1 ||
                        !TryUnquote(annotation.Arguments[0], out externalName) ||
                        string.IsNullOrWhiteSpace(externalName))
                        throw Error("Annotation '@extern' expects one string argument", annotation.Location);
                    break;
                case "calling_convention":
                    if (annotation.Arguments.Count != 1)
                        throw Error("Annotation '@calling_convention' expects one identifier or string argument", annotation.Location);
                    if (!TryUnquote(annotation.Arguments[0], out callingConvention))
                    {
                        if (!string.Equals(annotation.Arguments[0], "c", StringComparison.Ordinal))
                            throw Error("Annotation '@calling_convention' expects one identifier or string argument", annotation.Location);
                        callingConvention = annotation.Arguments[0];
                    }
                    if (!string.Equals(callingConvention, "c", StringComparison.Ordinal))
                        throw Error($"Unsupported calling convention '{callingConvention}'", annotation.Location);
                    break;
                case "manual_memory":
                case "auto_memory":
                    if (annotation.Arguments.Count != 0)
                        throw Error($"Annotation '@{annotation.Name}' does not accept arguments", annotation.Location);
                    break;
                case "packed":
                    if (annotation.Arguments.Count != 0)
                        throw Error("Annotation '@packed' does not accept arguments", annotation.Location);
                    throw Error("Annotation '@packed' is reserved for struct declarations, which are not implemented", annotation.Location);
                case "align":
                    if (annotation.Arguments.Count != 1 ||
                        !uint.TryParse(annotation.Arguments[0], out var alignment) ||
                        alignment == 0 ||
                        (alignment & (alignment - 1)) != 0 ||
                        alignment > 4096)
                        throw Error("Annotation '@align' expects one power-of-two byte alignment from 1 through 4096", annotation.Location);
                    throw Error($"Annotation '@{annotation.Name}' is reserved for struct declarations, which are not implemented", annotation.Location);
                default:
                    throw Error($"Unknown annotation '@{annotation.Name}'", annotation.Location, annotation.Name.Length + 1);
            }
        }

        return externalName is null && callingConvention == "c"
            ? null
            : new CInteropFunctionMetadata(externalName, callingConvention);
    }

    private static void ValidateClassAnnotations(ClassDeclaration declaration)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var annotation in declaration.DeclaredAnnotations)
        {
            if (!seen.Add(annotation.Name))
                throw Error($"Annotation '@{annotation.Name}' may only appear once", annotation.Location, annotation.Name.Length + 1);
            if (annotation.Name == "value" && annotation.Arguments.Count == 0)
                continue;
            throw Error(annotation.Name == "value"
                ? "Annotation '@value' does not accept arguments"
                : $"Unknown class annotation '@{annotation.Name}'", annotation.Location);
        }
    }

    private static bool TryUnquote(string value, out string unquoted)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            unquoted = value[1..^1];
            return true;
        }

        unquoted = string.Empty;
        return false;
    }

    private static void ValidateMainFunction(
        FunctionSymbol mainFunction,
        DiagnosticBag? diagnostics,
        string? sourceName)
    {
        if (mainFunction.Parameters.Count != 0)
        {
            ReportOrThrow(
                diagnostics,
                "The 'main' function cannot have parameters",
                mainFunction.Declaration.Location,
                mainFunction.Name.Length,
                sourceName);
        }

        if (mainFunction.IsExternal)
        {
            ReportOrThrow(
                diagnostics,
                "The 'main' function must be defined in Oxygen and cannot be extern",
                mainFunction.Declaration.Location,
                mainFunction.Name.Length,
                sourceName);
        }

        if (mainFunction.ReturnType != SemanticType.FromPrimitive(PrimitiveType.I32)
            && !mainFunction.ReturnType.IsVoid)
        {
            ReportOrThrow(
                diagnostics,
                $"The 'main' function must return i32 or void, got {mainFunction.ReturnType}",
                mainFunction.Declaration.Location,
                mainFunction.Name.Length,
                sourceName);
        }
    }

}
