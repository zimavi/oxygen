using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

// Pointer nullness. The analysis decides, for each dereference, whether its pointer operand is proven non-null. A proven operand needs no
// runtime check (SemanticModel.NonNullDereferences). A pointer that is null on every path is an error at the dereference. Everything else
// is checked at run time.
public sealed partial class SemanticAnalyzer
{
    // What is known about a pointer at a point. A pointer that is not in the flow state is Unknown.
    private enum PointerNullness
    {
        Unknown,
        Null,
        NonNull
    }

    // The function whose body declared each address-taken name is needed, so the names are found once per body.
    private readonly Dictionary<FunctionDeclaration, HashSet<string>> _addressTakenNames = new(ReferenceEqualityComparer.Instance);

    // The nullness of a pointer operand. The address of anything is non-null, since taking it does not read through the pointer. A local
    // pointer has the nullness it was last given, and anything else (a parameter, a field, a call, a cast or arithmetic) is unknown.
    private PointerNullness PointerNullnessOf(Expression pointer, Scope scope) => pointer switch
    {
        NullLiteralExpression => PointerNullness.Null,
        UnaryExpression { Operator: TokenKind.Ampersand } => PointerNullness.NonNull,
        LambdaExpression => PointerNullness.NonNull,
        IdentifierExpression identifier when _pointerStates.TryGetValue(scope.Lookup(identifier.Name, identifier.Location), out var nullness) =>
            nullness,
        _ => PointerNullness.Unknown
    };

    // Records the nullness of a pointer local after an assignment or a declaration. An unknown nullness forgets the local.
    private void SetPointerNullness(VariableSymbol variable, PointerNullness nullness)
    {
        if (nullness == PointerNullness.Unknown)
            _pointerStates.Remove(variable);
        else
            _pointerStates[variable] = nullness;
    }

    // A pointer whose address is taken anywhere in its function can be written through another pointer, so its nullness is never tracked.
    // Locals belong to one function, so the names are the names of that function's body.
    private bool IsAddressTaken(FunctionSymbol function, string name)
    {
        if (!_addressTakenNames.TryGetValue(function.Declaration, out var names))
        {
            names = EnumerateExpressions(function.Declaration.Body)
                .OfType<UnaryExpression>()
                .Where(item => item.Operator == TokenKind.Ampersand && item.Operand is IdentifierExpression)
                .Select(item => ((IdentifierExpression)item.Operand).Name)
                .ToHashSet(StringComparer.Ordinal);
            _addressTakenNames[function.Declaration] = names;
        }
        return names.Contains(name);
    }

    // Classifies a dereference of a pointer operand. A pointer that is null on every path is an error. A pointer proven non-null is
    // recorded as such. Any other pointer is recorded as unproven, so the backend checks it at run time.
    private void ClassifyDereference(Expression pointer, Scope scope, SourceLocation location)
    {
        switch (PointerNullnessOf(pointer, scope))
        {
            case PointerNullness.Null:
                throw Error("Cannot dereference a null pointer", location);
            case PointerNullness.NonNull:
                _provenNonNullDereferences.Add(pointer);
                break;
            default:
                _unprovenDereferences.Add(pointer);
                break;
        }
    }
}
