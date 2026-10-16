using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Diagnostics;

public sealed record TokenizationResult(
    IReadOnlyList<Token> Tokens,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ParseResult(
    CompilationUnit Unit,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record SemanticResult(
    Semantic.SemanticModel? Model,
    IReadOnlyList<Diagnostic> Diagnostics);
