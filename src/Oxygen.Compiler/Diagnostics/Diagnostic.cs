using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Diagnostics;

public enum DiagnosticPhase
{
    Lexing,
    Parsing,
    Semantic
}

public enum DiagnosticSeverity
{
    Error,
    Warning
}

public readonly record struct SourceSpan(SourceLocation Start, int Length)
{
    public int EndColumn => Start.Column + Math.Max(1, Length) - 1;
}

public sealed record Diagnostic(
    DiagnosticPhase Phase,
    DiagnosticSeverity Severity,
    string Message,
    string? SourceName,
    SourceSpan Span,
    string? Explanation = null,
    string? Suggestion = null);

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _diagnostics = [];

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public bool HasErrors => _diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    public void Add(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    public void Add(
        DiagnosticPhase phase,
        string message,
        SourceLocation location,
        int length = 1,
        string? sourceName = null) =>
        Add(new Diagnostic(
            phase,
            DiagnosticSeverity.Error,
            message,
            sourceName,
            new SourceSpan(location, Math.Max(1, length)),
            DiagnosticAdvice.Explain(phase, message),
            DiagnosticAdvice.Suggest(phase, message)));

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _diagnostics.AddRange(diagnostics);
}

public sealed record SourceText(string Name, string Text);

internal static class DiagnosticAdvice
{
    public static string? Explain(DiagnosticPhase phase, string message) => (phase, message) switch
    {
        (DiagnosticPhase.Lexing, var value) when value.StartsWith("Unexpected character", StringComparison.Ordinal) =>
            "This character is not part of Oxygen's lexical grammar, so the compiler cannot interpret it as a token.",
        (DiagnosticPhase.Lexing, "A floating-point literal requires digits after the decimal point") =>
            "A decimal point starts a floating-point literal and must be followed by at least one digit.",
        (DiagnosticPhase.Lexing, "Invalid character after numeric literal") =>
            "Numeric literals must end before an identifier or another unsupported character begins.",
        (DiagnosticPhase.Lexing, "Unterminated string literal") =>
            "The compiler reached the end of the line or file before finding the closing double quote.",
        (DiagnosticPhase.Lexing, "Unterminated block comment") =>
            "The compiler reached the end of the file before finding the closing */ marker.",
        (DiagnosticPhase.Parsing, var value) when value.StartsWith("Expected", StringComparison.Ordinal) =>
            "The surrounding tokens do not form a valid Oxygen construct at this location.",
        (DiagnosticPhase.Semantic, var value) when value.Contains("not declared", StringComparison.Ordinal) =>
            "Name lookup only considers declarations visible in the current lexical scope and declared functions.",
        (DiagnosticPhase.Semantic, var value) when value.Contains("already declared", StringComparison.Ordinal)
            || value.Contains("more than once", StringComparison.Ordinal) =>
            "Each scope and function signature must give every declared name a single, unambiguous meaning.",
        (DiagnosticPhase.Semantic, var value) when value.Contains("must return", StringComparison.Ordinal) =>
            "Every path through a non-void function must produce a value of its declared return type.",
        (DiagnosticPhase.Semantic, var value) when value.Contains("requires", StringComparison.Ordinal) =>
            "This operation is only defined for the operand types described by the language type rules.",
        (DiagnosticPhase.Semantic, var value) when value.Contains("expected", StringComparison.Ordinal) =>
            "Oxygen uses exact type matching here; it does not insert an implicit conversion to make the types fit.",
        _ => null
    };

    public static string? Suggest(DiagnosticPhase phase, string message) => (phase, message) switch
    {
        (DiagnosticPhase.Lexing, var value) when value.StartsWith("Unexpected character", StringComparison.Ordinal) =>
            "Remove the character or replace it with the Oxygen operator or punctuation you intended.",
        (DiagnosticPhase.Lexing, "A floating-point literal requires digits after the decimal point") =>
            "Write a value such as 3.14, or remove the decimal point if this should be an integer.",
        (DiagnosticPhase.Lexing, "Invalid character after numeric literal") =>
            "Separate the number from the following identifier with whitespace or an operator.",
        (DiagnosticPhase.Lexing, "Unterminated string literal") =>
            "Add a closing \" before the end of the line, and escape embedded quotes when needed.",
        (DiagnosticPhase.Lexing, "Unterminated block comment") =>
            "Add */ to close the block comment, or use // for a single-line comment.",
        (DiagnosticPhase.Parsing, var value) when value.Contains("';'", StringComparison.Ordinal) =>
            "Terminate this statement with a semicolon.",
        (DiagnosticPhase.Parsing, var value) when value.Contains("')'", StringComparison.Ordinal) =>
            "Check the parameter or argument list and add the missing closing parenthesis.",
        (DiagnosticPhase.Parsing, var value) when value.Contains("'}'", StringComparison.Ordinal) =>
            "Close the current block with }.",
        (DiagnosticPhase.Parsing, var value) when value.Contains("expression", StringComparison.Ordinal) =>
            "Provide a literal, identifier, function call, or parenthesized expression.",
        (DiagnosticPhase.Semantic, var value) when value.Contains("const variable", StringComparison.Ordinal) =>
            "Declare the variable without const if it must change, or assign the value only once.",
        (DiagnosticPhase.Semantic, var value) when value.Contains("using 'var'", StringComparison.Ordinal) =>
            "Initialize the variable, for example: var result = expression;",
        (DiagnosticPhase.Semantic, var value) when value.Contains("expects", StringComparison.Ordinal) =>
            "Pass exactly the number of arguments shown in the function declaration.",
        (DiagnosticPhase.Semantic, var value) when value.Contains("already declared", StringComparison.Ordinal)
            || value.Contains("more than once", StringComparison.Ordinal) =>
            "Rename the declaration, or remove the duplicate if it was accidental.",
        (DiagnosticPhase.Semantic, var value) when value.Contains("must return", StringComparison.Ordinal) =>
            "Add a return statement with the declared type, or change the function return type to void.",
        _ => null
    };
}
