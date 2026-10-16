using Oxygen.Compiler.Ast;

namespace Oxygen.Compiler.Semantic;

public sealed record ExternalModuleDeclaration(
    string ModuleName,
    IReadOnlyList<ExternalFunctionDeclaration> Functions);

public sealed record ExternalFunctionDeclaration(
    string Name,
    SemanticType ReturnType,
    IReadOnlyList<SemanticType> ParameterTypes,
    string NativeName,
    Visibility Visibility = Visibility.Public,
    CInteropFunctionMetadata? CInterop = null);
