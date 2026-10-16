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
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool HasBlockingDiagnostics(bool warningsAsErrors = false) =>
        Diagnostics.Any(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error
            || warningsAsErrors && diagnostic.Severity == DiagnosticSeverity.Warning);
}
