using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

// `@nothrow` and the no-exceptions mode (docs/exceptions.md). While each body is analysed, its facts are recorded: the operations that
// may throw, the runtime checks that may throw, and the calls it makes. After every body is analysed, CheckNoThrowFunctions decides:
// - a declared @nothrow body is an error when a fact may throw. Its runtime checks are allowed, since they stop the program there;
// - a body that is not declared @nothrow is inferred @nothrow when it has no fact that may throw, no runtime check, and only calls to
//   functions that are nothrow. The inference is a least fixed point, so a recursive cycle is never inferred.
public sealed partial class SemanticAnalyzer
{
    // False for a program built with --no-exceptions. Its catch clauses can never run, which is reported as a warning.
    public bool ExceptionsEnabled { get; set; } = true;

    private enum NoThrowFactKind
    {
        // An operation that may throw on its own: a throw, delete, for-in, a property access, or a call the compiler cannot check.
        Throws,
        // A check that the backend emits and that may throw: an index, a division or shift, a null reference, a cast.
        RuntimeCheck,
        // A call of a function, or of the constructors and destructors that run. The callee must be nothrow.
        Call,
    }

    // One fact of a body. Callee is the declaration of a Call fact's callee. ExplicitOnly marks a dispatched call (a virtual, override or
    // interface method): an override may throw, so only a declared @nothrow callee is nothrow.
    private sealed record NoThrowFact(
        NoThrowFactKind Kind,
        string Message,
        SourceLocation Location,
        int Length = 1,
        SyntaxNode? Callee = null,
        bool ExplicitOnly = false);

    // A recorded body. Key is its declaration, which a call names; it is null when the body cannot be inferred nothrow.
    // Declared is @nothrow, Throws is @throws. A @throws body has no key: it is never inferred nothrow.
    private sealed record NoThrowBody(
        SyntaxNode? Key,
        bool Declared,
        bool Throws,
        string Name,
        SourceLocation Location,
        IReadOnlyList<NoThrowFact> Facts,
        string? SourceName);

    // The expression types and resolved calls of the body being recorded.
    private sealed record NoThrowScope(
        IDictionary<Expression, SemanticType> ExpressionTypes,
        IDictionary<CallExpression, FunctionSymbol> ResolvedCalls);

    private readonly List<NoThrowBody> _noThrowBodies = [];

    // The declarations of the bodies that were inferred @nothrow. Set by CheckNoThrowFunctions and read by the backend.
    private HashSet<SyntaxNode> _inferredNoThrow = new(ReferenceEqualityComparer.Instance);

    // Records the facts of an analysed body. A body is recorded when it is declared @nothrow, or when it is a candidate for inference
    // (candidateKey is not null). A constructor with a `this(...)` or `base(...)` initializer is never inferred.
    private void RecordNoThrowBody(
        FunctionSymbol function,
        SyntaxNode? candidateKey,
        ConstructorDeclaration? constructor,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var declared = function.DeclaresNoThrow;
        var throws = function.DeclaresThrows;
        if (declared && throws)
            throw Error($"Function '{function.Name}' cannot be both @nothrow and @throws", function.Declaration.Location, function.Name.Length);
        // A @throws body is never inferred, since its annotation says that it may throw.
        var key = constructor?.Initializer is null && !throws ? candidateKey : null;
        if (!declared && !throws && key is null)
            return;

        var facts = new List<NoThrowFact>();
        var scope = new NoThrowScope(expressionTypes, resolvedCalls);
        if (constructor is not null && _currentOwnerClass is not null)
            AddConstructorFacts(constructor, _currentOwnerClass, declared, scope, facts);
        AddBodyFacts(function.Declaration.Body, declared, scope, facts);
        _noThrowBodies.Add(new NoThrowBody(key, declared, throws, function.Name, function.Declaration.Location, facts, _sourceName));
    }

    // A constructor runs its field initializers, its body, and the constructors of its base classes. A declared @nothrow constructor may not
    // chain to another constructor, since that one may throw.
    private void AddConstructorFacts(
        ConstructorDeclaration constructor,
        ClassSymbol owner,
        bool declared,
        NoThrowScope scope,
        List<NoThrowFact> facts)
    {
        if (constructor.Initializer is not null && declared)
            facts.Add(new NoThrowFact(NoThrowFactKind.Throws,
                "A @nothrow constructor cannot chain to another constructor, since that one may throw", constructor.Initializer.Location));
        if (owner.BaseTypeName is not null && BaseClassOf(owner) is { } baseClass)
        {
            AddConstructionFacts(baseClass,
                baseClassOf => $"A @nothrow constructor cannot run the constructor of its base class '{baseClassOf.Name}', which may throw",
                constructor.Location, facts);
        }
        foreach (var field in owner.OwnFields)
        {
            if (FieldInitializer(field) is not { } initializer)
                continue;
            foreach (var expression in EnumerateExpressions(initializer))
                AddExpressionFacts(expression, scope, facts);
        }
    }

    // The facts of a body's statements, and of the expressions in them. A nested function literal is a body of its own.
    private void AddBodyFacts(BlockStatement body, bool declared, NoThrowScope scope, List<NoThrowFact> facts)
    {
        foreach (var statement in StatementsWithin(body))
        {
            switch (statement)
            {
                case ThrowStatement throwStatement:
                    facts.Add(new NoThrowFact(NoThrowFactKind.Throws, "A @nothrow function cannot throw",
                        throwStatement.Location, "throw".Length));
                    break;
                case DeleteStatement deleteStatement:
                    facts.Add(new NoThrowFact(NoThrowFactKind.Throws,
                        "A @nothrow function cannot delete an object, since its destructor may throw",
                        deleteStatement.Location, "delete".Length));
                    break;
                case ForInStatement forIn:
                    facts.Add(new NoThrowFact(NoThrowFactKind.Throws,
                        "A @nothrow function cannot use for-in, since it calls iterator methods that may throw",
                        forIn.Location, "for".Length));
                    break;
                case VariableDeclarationStatement declaration:
                    AddReleaseFacts(declaration, scope, facts);
                    break;
                case TryStatement tryStatement when declared:
                    ReportWarning("This try statement catches nothing, since no statement of a @nothrow function can throw",
                        tryStatement.Location, "try".Length);
                    break;
                case AssignmentStatement assignment when IsCheckedArithmetic(assignment.Operator, assignment.Target, scope):
                    facts.Add(new NoThrowFact(NoThrowFactKind.RuntimeCheck, "division, remainder or shift", assignment.Location));
                    break;
            }
            foreach (var expression in OwnExpressions(statement).SelectMany(EnumerateExpressions))
                AddExpressionFacts(expression, scope, facts);
        }
    }

    // The facts of one expression, which the body evaluates itself (its subexpressions are visited by the caller).
    private void AddExpressionFacts(Expression expression, NoThrowScope scope, List<NoThrowFact> facts)
    {
        switch (expression)
        {
            case IndirectCallExpression call:
                facts.Add(new NoThrowFact(NoThrowFactKind.Throws,
                    "A @nothrow function cannot call through a function pointer, since its target is not known", call.Location));
                break;
            case CallExpression call:
                AddCallFacts(call, scope, facts);
                if (scope.ExpressionTypes.TryGetValue(call, out var returned))
                    AddTemporaryFacts(returned, call.Location, call.Name.Length, facts);
                break;
            case NewExpression or ClassConstructionExpression:
                if (scope.ExpressionTypes.TryGetValue(expression, out var created) && created.IsClass
                    && created.StructName is { } className && _classesByName.TryGetValue(className, out var cls))
                {
                    AddConstructionFacts(cls, _ => $"Creating '{created.StructName}' runs a constructor that may throw",
                        expression.Location, facts);
                    AddTemporaryFacts(created, expression.Location, 1, facts);
                }
                break;
            case MemberAccessExpression access:
                AddMemberAccessFacts(access, scope, facts);
                break;
            case IndexExpression index:
                facts.Add(new NoThrowFact(NoThrowFactKind.RuntimeCheck, "an index", index.Location));
                break;
            case UnaryExpression { Operator: TokenKind.Star } dereference:
                facts.Add(new NoThrowFact(NoThrowFactKind.RuntimeCheck, "a pointer dereference", dereference.Location));
                break;
            case BinaryExpression binary when IsCheckedArithmetic(binary.Operator, binary.Left, scope):
                facts.Add(new NoThrowFact(NoThrowFactKind.RuntimeCheck, "division, remainder or shift", binary.Location));
                break;
            case CastExpression cast:
                // A numeric conversion cannot fail. Any other cast may: an enum value, a class, a pointer, or a union.
                if (!(scope.ExpressionTypes.TryGetValue(cast, out var castType) && castType.IsNumeric
                    && scope.ExpressionTypes.TryGetValue(cast.Operand, out var operandType) && operandType.IsNumeric))
                {
                    facts.Add(new NoThrowFact(NoThrowFactKind.RuntimeCheck, "a cast", cast.Location));
                }
                break;
            case IsExpression typeTest:
                facts.Add(new NoThrowFact(NoThrowFactKind.RuntimeCheck, "a type test", typeTest.Location));
                break;
            case UnionConstructionExpression union:
                facts.Add(new NoThrowFact(NoThrowFactKind.Throws,
                    $"A @nothrow function cannot build the union case '{union.CaseName}', since the compiler cannot check it",
                    union.Location, union.CaseName.Length));
                break;
        }
    }

    private void AddCallFacts(CallExpression call, NoThrowScope scope, List<NoThrowFact> facts)
    {
        if (!scope.ResolvedCalls.TryGetValue(call, out var callee))
        {
            // A call that the analysis did not resolve to a function is a construction, a union case or a call through a function pointer.
            facts.Add(new NoThrowFact(NoThrowFactKind.Throws,
                $"'{call.Name}' is not a function the compiler can check, so a @nothrow function cannot call it",
                call.Location, call.Name.Length));
        }
        else if (callee.CInterop?.ExternalName is null)
        {
            // A C function does not throw Oxygen exceptions, so only Oxygen functions are facts.
            var dispatched = !_baseCalls.Contains(call)
                && (callee.Declaration.IsVirtual || callee.Declaration.IsOverride || IsInterfaceMethod(callee));
            facts.Add(new NoThrowFact(NoThrowFactKind.Call,
                $"'{callee.Name}' may throw, so a @nothrow function cannot call it",
                call.Location, call.Name.Length, callee.Declaration, dispatched));
        }

        // A method called on a class or interface value fails when the value is null.
        if (call.Receiver is not null && scope.ExpressionTypes.TryGetValue(call.Receiver, out var receiverType)
            && (receiverType.IsClass || receiverType.IsInterface))
        {
            facts.Add(new NoThrowFact(NoThrowFactKind.RuntimeCheck, "a call on a null reference", call.Location, call.Name.Length));
        }
    }

    // An object that is not bound to a local is a temporary, whose destructor runs at the end of its statement. The compiler does not
    // destroy temporaries yet, but a @nothrow function is checked as if it did, so that the rule holds once it does.
    private void AddTemporaryFacts(SemanticType type, SourceLocation location, int length, List<NoThrowFact> facts)
    {
        if (!type.IsClass || type.StructName is not { } name || !_classesByName.TryGetValue(name, out var cls))
            return;
        foreach (var destructor in DestructorsReleasedBy(cls))
        {
            facts.Add(new NoThrowFact(NoThrowFactKind.Call,
                $"A @nothrow function cannot create a temporary of '{cls.Name}', since its destructor may throw",
                location, length, destructor));
        }
    }

    private void AddMemberAccessFacts(MemberAccessExpression access, NoThrowScope scope, List<NoThrowFact> facts)
    {
        if (!scope.ExpressionTypes.TryGetValue(access.Target, out var target))
            return;
        if (IsPropertyOf(target, access.MemberName))
        {
            facts.Add(new NoThrowFact(NoThrowFactKind.Throws,
                $"'{access.MemberName}' is a property, and reading or writing it runs an accessor that may throw",
                access.Location, access.MemberName.Length));
        }
        else if (target.IsClass && access.Target is not ThisExpression)
        {
            facts.Add(new NoThrowFact(NoThrowFactKind.RuntimeCheck, "a member of a null reference", access.Location, access.MemberName.Length));
        }
        else if (target.IsPointer)
        {
            facts.Add(new NoThrowFact(NoThrowFactKind.RuntimeCheck, "a member of a null pointer", access.Location, access.MemberName.Length));
        }
    }

    // A local whose class has a destructor, or whose subclass has one, releases an object that the destructor may throw from.
    private void AddReleaseFacts(VariableDeclarationStatement declaration, NoThrowScope scope, List<NoThrowFact> facts)
    {
        // The type is the one the analysis gave the declaration: an inferred (var) local has the type of its initializer.
        var declared = declaration.Initializer is not null && scope.ExpressionTypes.TryGetValue(declaration.Initializer, out var initialized)
            ? initialized
            : _resolvedTypeSyntax.GetValueOrDefault(declaration.Type);
        if (declared is null)
            return;
        if (!declared.IsClass || declared.StructName is not { } name || !_classesByName.TryGetValue(name, out var cls))
            return;
        foreach (var destructor in DestructorsReleasedBy(cls))
        {
            facts.Add(new NoThrowFact(NoThrowFactKind.Call,
                $"A @nothrow function cannot declare '{declaration.Name}', since its destructor runs at the end of the scope and may throw",
                declaration.Location, declaration.Name.Length, destructor));
        }
    }

    // The destructors that run when an object of the class is released. The object's runtime class may be the class or any subclass, and
    // each releases the nearest destructor in its chain of bases.
    private IEnumerable<DestructorDeclaration> DestructorsReleasedBy(ClassSymbol cls)
    {
        var destructors = new List<DestructorDeclaration>();
        foreach (var candidate in _classesByName.Values.Where(item => ReferenceEquals(item, cls) || IsSubclassOf(item, cls)))
        {
            for (var current = candidate; current is not null; current = BaseClassOf(current))
            {
                if (current.Destructor is not { } destructor)
                    continue;
                if (!destructors.Contains(destructor))
                    destructors.Add(destructor);
                break;
            }
        }
        return destructors;
    }

    private bool IsSubclassOf(ClassSymbol cls, ClassSymbol baseClass)
    {
        for (var current = BaseClassOf(cls); current is not null; current = BaseClassOf(current))
        {
            if (ReferenceEquals(current, baseClass))
                return true;
        }
        return false;
    }

    // Creating an object runs the constructors of its class and of every base class. A constructor that is synthesized runs the field
    // initializers, so it may throw when one of them is not a literal.
    private void AddConstructionFacts(
        ClassSymbol start,
        Func<ClassSymbol, string> message,
        SourceLocation location,
        List<NoThrowFact> facts)
    {
        for (var current = start; current is not null; current = BaseClassOf(current))
        {
            foreach (var constructor in current.Constructors)
            {
                if (!constructor.IsSynthesized)
                    facts.Add(new NoThrowFact(NoThrowFactKind.Call, message(current), location, 1, constructor.Declaration));
                else if (HasNonLiteralFieldInitializer(current))
                    facts.Add(new NoThrowFact(NoThrowFactKind.Throws, message(current), location));
            }
        }
    }

    private bool HasNonLiteralFieldInitializer(ClassSymbol cls) =>
        cls.OwnFields.Any(field => FieldInitializer(field) is { } initializer
            && initializer is not (IntegerLiteralExpression or FloatingPointLiteralExpression or StringLiteralExpression
                or BooleanLiteralExpression or NullLiteralExpression));

    // A property access runs an accessor, and a class member access is checked for a null reference. Both are facts of the access.
    private bool IsPropertyOf(SemanticType type, string name) =>
        type.IsClass && type.StructName is { } className && _classesByName.ContainsKey(className)
        && GetClassProperties(className, []).Any(property => property.Name == name);

    // Division, remainder and shifts are checked for integers. Floating-point operations do not fail.
    private static bool IsCheckedArithmetic(TokenKind kind, Expression operand, NoThrowScope scope)
    {
        if (kind is not (TokenKind.Slash or TokenKind.Percent or TokenKind.LessThanLessThan or TokenKind.GreaterThanGreaterThan
            or TokenKind.SlashEquals or TokenKind.PercentEquals or TokenKind.LessThanLessThanEquals or TokenKind.GreaterThanGreaterThanEquals))
        {
            return false;
        }
        return !(scope.ExpressionTypes.TryGetValue(operand, out var type)
            && type.Kind == SemanticTypeKind.Primitive && type.Primitive is PrimitiveType.F32 or PrimitiveType.F64);
    }

    private bool IsInterfaceMethod(FunctionSymbol callee) =>
        _interfacesByName.Values.Any(iface => iface.Methods.Any(method => ReferenceEquals(method, callee)));

    // Decides which bodies are @nothrow, and reports the declared ones that break the rules. It runs after every body is analysed, since a
    // body's calls may depend on bodies analysed later.
    private void CheckNoThrowFunctions(DiagnosticBag? diagnostics)
    {
        // The least fixed point: a body joins the set when every fact is a call to a body that is already nothrow. A body with a fact that
        // may throw, or with a runtime check, never joins, so a cycle of calls never joins either.
        var inferred = new HashSet<SyntaxNode>(ReferenceEqualityComparer.Instance);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var body in _noThrowBodies)
            {
                if (body.Key is null || body.Declared || inferred.Contains(body.Key))
                    continue;
                if (body.Facts.All(fact => fact.Kind == NoThrowFactKind.Call && IsKnownNoThrow(fact, inferred)))
                {
                    inferred.Add(body.Key);
                    changed = true;
                }
            }
        }

        foreach (var body in _noThrowBodies.Where(body => body.Declared))
        {
            var failure = body.Facts.FirstOrDefault(fact =>
                fact.Kind == NoThrowFactKind.Throws || fact.Kind == NoThrowFactKind.Call && !IsKnownNoThrow(fact, inferred));
            if (failure is not null)
                ReportOrThrow(diagnostics, failure.Message, failure.Location, failure.Length, body.SourceName);
        }

        // A @throws body must have a fact that may throw, so that the annotation is checked rather than taken on trust.
        foreach (var body in _noThrowBodies.Where(body => body.Throws))
        {
            if (body.Facts.All(fact => fact.Kind == NoThrowFactKind.Call && IsKnownNoThrow(fact, inferred)))
            {
                ReportOrThrow(diagnostics,
                    $"Function '{body.Name}' is @throws, but nothing in it can throw: remove the annotation, or add the operation that throws",
                    body.Location, body.Name.Length, body.SourceName);
            }
        }

        _inferredNoThrow = inferred;
    }

    // A call is nothrow when the callee is declared @nothrow, or inferred so. A dispatched call relies on the declaration only.
    private static bool IsKnownNoThrow(NoThrowFact call, ISet<SyntaxNode> inferred)
    {
        var callee = call.Callee!;
        return DeclaresNoThrow(callee) || !call.ExplicitOnly && inferred.Contains(callee);
    }

    private static bool DeclaresNoThrow(SyntaxNode declaration) => declaration switch
    {
        FunctionDeclaration function => function.DeclaredAnnotations.Any(annotation => annotation.Name == "nothrow"),
        ConstructorDeclaration constructor => constructor.DeclaredAnnotations.Any(annotation => annotation.Name == "nothrow"),
        _ => false
    };

    // The annotations of a function of a library, which its metadata records, as the source annotations of a function would be. They
    // have no source location, since no source declares them.
    private static IReadOnlyList<AnnotationSyntax> ImportedAnnotations(ExternalFunctionDeclaration external)
    {
        var annotations = new List<AnnotationSyntax>();
        if (external.NoThrow)
            annotations.Add(new AnnotationSyntax("nothrow", [], new SourceLocation(1, 1)));
        if (external.NoReturn)
            annotations.Add(new AnnotationSyntax("noreturn", [], new SourceLocation(1, 1)));
        return annotations;
    }

    // @nothrow and @noreturn apply to functions and methods. The other declarations that carry annotations are checked here; classes are
    // checked with their own annotations.
    private static void ValidateFunctionOnlyAnnotations(CompilationUnit unit)
    {
        var annotations = unit.Structs.SelectMany(item => item.DeclaredAnnotations)
            .Concat(unit.Enums.SelectMany(item => item.DeclaredAnnotations))
            .Concat(unit.Unions.SelectMany(item => item.DeclaredAnnotations))
            .Concat(unit.Interfaces.SelectMany(item => item.DeclaredAnnotations));
        foreach (var annotation in annotations)
        {
            if (annotation.Name is "nothrow" or "noreturn" or "throws")
                throw Error($"Annotation '@{annotation.Name}' applies to functions and methods", annotation.Location,
                    annotation.Name.Length + 1);
        }
    }
}
