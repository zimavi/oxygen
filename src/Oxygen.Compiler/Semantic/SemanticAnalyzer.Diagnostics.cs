using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

// Diagnostics: error and warning construction.
public sealed partial class SemanticAnalyzer
{
    private static void ReportOrThrow(
        DiagnosticBag? diagnostics,
        string message,
        SourceLocation location,
        int length = 1,
        string? sourceName = null)
    {
        if (diagnostics is null)
        {
            throw Error(message, location, length);
        }

        diagnostics.Add(DiagnosticPhase.Semantic, message, location, length, sourceName);
    }

    private static void RequireType(SemanticType actual, SemanticType expected, SourceLocation location, string context)
    {
        if (actual != expected)
        {
            throw Error($"{context} require {expected} operands, got {actual}", location);
        }
    }

    private static SemanticException Error(string message, SourceLocation location, int length = 1) =>
        new(message, location, length);

    // An error about a class or struct belongs to the file that declares it, even when it is found in a phase that has no
    // current source, such as the class hierarchy checks that run after all declarations are registered.
    private SemanticException DeclarationError(string declarationName, string message, SourceLocation location, int length = 1) =>
        new(message, location, length)
        {
            IsSourceAttributed = true,
            SourceName = _declarationSourceNames.GetValueOrDefault(declarationName)
        };

    private void MarkVariableUsed(VariableSymbol variable) => _usedVariables.Add(variable);

    private void ReportWarning(
        string message,
        SourceLocation location,
        int length = 1,
        string? sourceName = null)
    {
        // A warning is about the file being analysed unless the caller names another one.
        _diagnostics?.AddWarning(
            DiagnosticPhase.Semantic,
            message,
            location,
            length,
            sourceName ?? _sourceName);
    }

}
