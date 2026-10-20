namespace Oxygen.Compiler.Diagnostics;

// Stable codes for the categories of error that have an explanation and a suggestion. A diagnostic carries its code, and the
// advice is chosen by the code, not by the wording of the message.
public static class DiagnosticCode
{
    public const string UnexpectedCharacter = "OX0001";
    public const string FloatingPointDigits = "OX0002";
    public const string NumericSuffix = "OX0003";
    public const string RadixDigits = "OX0004";
    public const string UnterminatedString = "OX0005";
    public const string UnterminatedComment = "OX0006";
    public const string ExponentDigits = "OX0007";
    public const string CharLiteral = "OX0008";

    public const string ExpectedToken = "OX0100";

    public const string NotDeclared = "OX0200";
    public const string AlreadyDeclared = "OX0201";
    public const string MustReturn = "OX0202";
    public const string ConversionMismatch = "OX0203";
    public const string ArgumentCount = "OX0204";
    public const string OperandRequirement = "OX0205";
    public const string ConstAssignment = "OX0206";
    public const string DefaultParameter = "OX0207";
    public const string OverloadConflict = "OX0208";
}
