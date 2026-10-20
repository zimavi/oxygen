using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Diagnostics;

public enum DiagnosticPhase
{
    Lexing,
    Parsing,
    Semantic,
    Backend,
    Toolchain,
    Metadata
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
    string? Suggestion = null,
    string? Code = null);

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _diagnostics = [];

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public bool HasErrors => _diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    public bool HasWarnings => _diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);

    public bool HasBlockingDiagnostics(bool warningsAsErrors = false) =>
        HasErrors || warningsAsErrors && HasWarnings;

    public void Add(Diagnostic diagnostic)
    {
        // A generic body is checked in its declared form and again for each instantiation, so one mistake can be reported
        // several times at the same place. An error repeats an earlier one when phase, file and location match, and the
        // first report is kept.
        if (diagnostic.Severity == DiagnosticSeverity.Error && _diagnostics.Any(existing =>
                existing.Severity == DiagnosticSeverity.Error
                && existing.Phase == diagnostic.Phase
                && existing.SourceName == diagnostic.SourceName
                && existing.Span.Start == diagnostic.Span.Start))
            return;
        _diagnostics.Add(diagnostic);
    }

    public void Add(
        DiagnosticPhase phase,
        string message,
        SourceLocation location,
        int length = 1,
        string? sourceName = null,
        string? code = null) =>
        Add(phase, DiagnosticSeverity.Error, message, location, length, sourceName, code: code);

    public void AddWarning(
        DiagnosticPhase phase,
        string message,
        SourceLocation location,
        int length = 1,
        string? sourceName = null,
        string? code = null) =>
        Add(phase, DiagnosticSeverity.Warning, message, location, length, sourceName, code: code);

    public void Add(
        DiagnosticPhase phase,
        DiagnosticSeverity severity,
        string message,
        SourceLocation location,
        int length = 1,
        string? sourceName = null,
        string? explanation = null,
        string? suggestion = null,
        string? code = null) =>
        Add(new Diagnostic(
            phase,
            severity,
            message,
            sourceName,
            new SourceSpan(location, Math.Max(1, length)),
            explanation ?? DiagnosticAdvice.Explain(code),
            suggestion ?? DiagnosticAdvice.Suggest(code),
            code));

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _diagnostics.AddRange(diagnostics);
}

public sealed record SourceText(string Name, string Text);

// The explanation and the suggestion for each diagnostic code. A diagnostic without a code has no advice.
internal static class DiagnosticAdvice
{
    public static string? Explain(string? code) => code switch
    {
        DiagnosticCode.UnexpectedCharacter =>
            "This character is not part of Oxygen's lexical grammar, so the compiler cannot interpret it as a token.",
        DiagnosticCode.FloatingPointDigits =>
            "A decimal point starts a floating-point literal and must be followed by at least one digit.",
        DiagnosticCode.NumericSuffix =>
            "Numeric literals must end before an identifier or another unsupported character begins, and a suffix must be a type suffix.",
        DiagnosticCode.RadixDigits =>
            "A hexadecimal or binary literal is a prefix followed by digits of that base.",
        DiagnosticCode.ExponentDigits =>
            "An exponent is an e, an optional sign, and at least one digit, so the value 1e5 means 1 times 10 to the power 5.",
        DiagnosticCode.CharLiteral =>
            "A character literal is one ASCII character or one escape sequence between single quotes, and its value is that byte.",
        DiagnosticCode.UnterminatedString =>
            "The compiler reached the end of the line or file before finding the closing double quote.",
        DiagnosticCode.UnterminatedComment =>
            "The compiler reached the end of the file before finding the closing */ marker.",
        DiagnosticCode.ExpectedToken =>
            "The surrounding tokens do not form a valid Oxygen construct at this location.",
        DiagnosticCode.NotDeclared =>
            "Name lookup only considers declarations visible in the current lexical scope and declared functions.",
        DiagnosticCode.AlreadyDeclared =>
            "Each scope and function signature must give every declared name a single, unambiguous meaning.",
        DiagnosticCode.MustReturn =>
            "Every path through a non-void function must produce a value of its declared return type.",
        DiagnosticCode.ConversionMismatch =>
            "A value converts implicitly only where the conversion loses nothing, such as a wider integer or an integer constant " +
            "that fits. Other conversions are written explicitly with cast<T>(value).",
        DiagnosticCode.ArgumentCount =>
            "A call passes exactly the number of arguments that the function declares.",
        DiagnosticCode.OperandRequirement =>
            "This operation is only defined for the operand types described by the language type rules.",
        DiagnosticCode.ConstAssignment =>
            "A const variable is assigned only by its declaration, so it cannot change after that.",
        DiagnosticCode.DefaultParameter =>
            "A parameter with a default value may be omitted by a call, so the parameters with defaults come after the required ones, and each default is a constant of the parameter's type.",
        DiagnosticCode.OverloadConflict =>
            "Two overloads conflict when one call could match both: an argument count fits both, and their first parameters of that count have the same types.",
        _ => null
    };

    public static string? Suggest(string? code) => code switch
    {
        DiagnosticCode.UnexpectedCharacter =>
            "Remove the character or replace it with the Oxygen operator or punctuation you intended.",
        DiagnosticCode.FloatingPointDigits =>
            "Write a value such as 3.14, or remove the decimal point if this should be an integer.",
        DiagnosticCode.NumericSuffix =>
            "Separate the number from the following identifier with whitespace or an operator, and use a type suffix such as u8.",
        DiagnosticCode.RadixDigits =>
            "Write digits after the prefix: 0x1F for hexadecimal, or 0b101 for binary.",
        DiagnosticCode.ExponentDigits =>
            "Write digits after the e, such as 1e5, or remove the e.",
        DiagnosticCode.CharLiteral =>
            "Write one ASCII character between single quotes, such as 'a' or '\\n'. For any other byte, write its number with the u8 suffix, such as 233u8.",
        DiagnosticCode.UnterminatedString =>
            "Add a closing \" before the end of the line, and escape embedded quotes when needed.",
        DiagnosticCode.UnterminatedComment =>
            "Add */ to close the block comment, or use // for a single-line comment.",
        DiagnosticCode.ExpectedToken =>
            "Check the tokens around the reported position, and add or correct the one the message names.",
        DiagnosticCode.NotDeclared =>
            "Declare the name before its use, or check its spelling and the imports of the module.",
        DiagnosticCode.AlreadyDeclared =>
            "Rename the declaration, or remove the duplicate if it was accidental.",
        DiagnosticCode.MustReturn =>
            "Add a return statement with the declared type, or change the function return type to void.",
        DiagnosticCode.ConversionMismatch =>
            "Change the declared type to the value's type, or convert the value explicitly with cast<T>(value).",
        DiagnosticCode.ArgumentCount =>
            "Pass exactly the number of arguments shown in the function declaration.",
        DiagnosticCode.OperandRequirement =>
            "Use operands of the types the operator accepts.",
        DiagnosticCode.ConstAssignment =>
            "Declare the variable without const if it must change, or assign the value only once.",
        DiagnosticCode.DefaultParameter =>
            "Move the parameters with defaults to the end, and write a bool literal, a number, or an integer constant of the parameter's type as the default.",
        DiagnosticCode.OverloadConflict =>
            "Give the overloads different parameter types at a position that both can be called with, or remove the default that makes them overlap.",
        _ => null
    };
}
