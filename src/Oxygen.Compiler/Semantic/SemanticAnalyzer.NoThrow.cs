using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

// `@nothrow` and the no-exceptions mode (docs/exceptions.md). A @nothrow function promises never to throw, so its body is checked for
// every construct that can throw. The check is conservative: a construct that the compiler cannot prove nothrow is an error.
public sealed partial class SemanticAnalyzer
{
    // False for a program built with --no-exceptions. Its catch clauses can never run, which is reported as a warning.
    public bool ExceptionsEnabled { get; set; } = true;

    // Checks the body of a @nothrow function, after its calls are resolved. Nested function literals are checked as functions of their own.
    private void CheckNoThrowBody(
        BlockStatement body,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        foreach (var statement in StatementsWithin(body))
        {
            switch (statement)
            {
                case ThrowStatement throwStatement:
                    throw Error("A @nothrow function cannot throw", throwStatement.Location, "throw".Length);
                case DeleteStatement deleteStatement:
                    throw Error("A @nothrow function cannot delete an object, since its destructor may throw",
                        deleteStatement.Location, "delete".Length);
                case ForInStatement forIn:
                    throw Error("A @nothrow function cannot use for-in, since it calls iterator methods that may throw",
                        forIn.Location, "for".Length);
                case VariableDeclarationStatement declaration when HasDestructibleType(declaration, expressionTypes):
                    throw Error($"A @nothrow function cannot declare '{declaration.Name}', since its destructor runs at the end of " +
                        "the scope and may throw", declaration.Location, declaration.Name.Length);
                case TryStatement tryStatement:
                    ReportWarning("This try statement catches nothing, since no statement of a @nothrow function can throw",
                        tryStatement.Location, "try".Length);
                    break;
            }
            foreach (var expression in OwnExpressions(statement).SelectMany(EnumerateExpressions))
                CheckNoThrowExpression(expression, expressionTypes, resolvedCalls);
        }
    }

    private void CheckNoThrowExpression(
        Expression expression,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        switch (expression)
        {
            case CallExpression call:
                // A call that the analysis did not resolve to a function is a construction, a union case or a call through a function pointer.
                if (!resolvedCalls.TryGetValue(call, out var callee))
                    throw Error($"'{call.Name}' is not a function the compiler can check, so a @nothrow function cannot call it",
                        call.Location, call.Name.Length);
                if (!callee.NeverThrows)
                    throw Error($"'{callee.Name}' may throw, so a @nothrow function cannot call it", call.Location, call.Name.Length);
                break;
            case NewExpression or ClassConstructionExpression:
                if (expressionTypes.TryGetValue(expression, out var created) && MayThrowOnConstruction(created))
                    throw Error($"Creating '{created.StructName}' runs a constructor that may throw", expression.Location);
                break;
            case MemberAccessExpression access:
                if (expressionTypes.TryGetValue(access.Target, out var target) && IsPropertyOf(target, access.MemberName))
                    throw Error($"'{access.MemberName}' is a property, and reading or writing it runs an accessor that may throw",
                        access.Location, access.MemberName.Length);
                break;
        }
    }

    // The annotations of a function of a library, which its metadata records, as the source annotations of a function would be.
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
            if (annotation.Name is "nothrow" or "noreturn")
                throw Error($"Annotation '@{annotation.Name}' applies to functions and methods", annotation.Location,
                    annotation.Name.Length + 1);
        }
    }

    // A local whose class has a destructor is destroyed at the end of its scope, and the destructor may throw.
    private bool HasDestructibleType(VariableDeclarationStatement declaration, IDictionary<Expression, SemanticType> expressionTypes)
    {
        var declared = declaration.Initializer is not null && expressionTypes.TryGetValue(declaration.Initializer, out var initialized)
            ? initialized
            : ResolveType(declaration.Type);
        return declared.IsClass && declared.StructName is { } name && _classesByName.TryGetValue(name, out var cls)
            && cls.Destructor is not null;
    }

    // Creating an object runs the constructors of its class and of its bases, so it may throw unless every one of them is @nothrow.
    private bool MayThrowOnConstruction(SemanticType type)
    {
        if (!type.IsClass || type.StructName is not { } name || !_classesByName.TryGetValue(name, out var cls))
            return false;
        return ChainMayThrow(cls);
    }

    // Creating an object of the class runs the constructors of the whole chain of its bases.
    private bool ChainMayThrow(ClassSymbol cls)
    {
        for (var current = cls; current is not null; current = BaseClassOf(current))
        {
            if (ConstructionMayThrow(current))
                return true;
        }
        return false;
    }

    // The construction of one class by its own constructors may throw when a declared constructor is not @nothrow. A class without declared
    // constructors has a synthesized one, which runs the field initializers, so it may throw when one of them is not a literal.
    private bool ConstructionMayThrow(ClassSymbol cls)
    {
        if (cls.Constructors.Any(constructor => !constructor.IsSynthesized && !constructor.DeclaresNoThrow))
            return true;
        return cls.Constructors.Any(constructor => constructor.IsSynthesized) && HasNonLiteralFieldInitializer(cls);
    }

    private bool HasNonLiteralFieldInitializer(ClassSymbol cls) =>
        cls.Fields.Where(field => cls.DeclaresField(field.Name)).Any(field =>
            (_fieldInitializerOverrides?.GetValueOrDefault(field.Declaration) ?? field.Declaration.Initializer) is { } initializer
            && initializer is not (IntegerLiteralExpression or FloatingPointLiteralExpression or StringLiteralExpression
                or BooleanLiteralExpression or NullLiteralExpression));

    // A @nothrow constructor runs its field initializers, which are checked as expressions, and its body, which is checked as a function.
    // It may not chain to another constructor, and it may not run a base class's constructor, which may throw.
    private void CheckNoThrowConstructor(
        ConstructorDeclaration constructor,
        ClassSymbol owner,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        if (constructor.Initializer is not null)
            throw Error("A @nothrow constructor cannot chain to another constructor, since that one may throw",
                constructor.Initializer.Location, 1);
        if (owner.BaseTypeName is not null && BaseClassOf(owner) is { } baseClass && ChainMayThrow(baseClass))
            throw Error($"A @nothrow constructor cannot run the constructor of its base class '{baseClass.Name}', which may throw",
                constructor.Location, 1);
        foreach (var field in owner.Fields.Where(field => owner.DeclaresField(field.Name)))
        {
            var initializer = _fieldInitializerOverrides?.GetValueOrDefault(field.Declaration) ?? field.Declaration.Initializer;
            if (initializer is null)
                continue;
            foreach (var expression in EnumerateExpressions(initializer))
                CheckNoThrowExpression(expression, expressionTypes, resolvedCalls);
        }
    }



    private bool IsPropertyOf(SemanticType type, string name) =>
        type.IsClass && type.StructName is { } className && _classesByName.ContainsKey(className)
        && GetClassProperties(className, []).Any(property => property.Name == name);

    // The statement and every statement nested in it, in source order.
    private static IEnumerable<Statement> StatementsWithin(Statement statement)
    {
        yield return statement;
        foreach (var child in ChildStatements(statement))
        foreach (var nested in StatementsWithin(child))
            yield return nested;
    }

    private static IEnumerable<Statement> StatementsWithin(BlockStatement block) => StatementsWithin((Statement)block);

    private static IEnumerable<Statement> ChildStatements(Statement statement) => statement switch
    {
        BlockStatement block => block.Statements,
        IfStatement conditional => conditional.Else is null ? [conditional.Then] : [conditional.Then, conditional.Else],
        WhileStatement loop => [loop.Body],
        ForStatement loop => new[] { loop.Initializer, loop.Increment, loop.Body }.OfType<Statement>(),
        ForInStatement loop => [loop.Body],
        TryStatement tryStatement => new Statement[] { tryStatement.Body }
            .Concat(tryStatement.Catches.Select(clause => clause.Body))
            .Concat(tryStatement.Finally is null ? [] : [tryStatement.Finally]),
        SwitchStatement switchStatement => switchStatement.Cases.Select(item => item.Body),
        _ => []
    };

    // The expressions that a statement evaluates itself. The expressions of its nested statements belong to those statements.
    private static IEnumerable<Expression> OwnExpressions(Statement statement) => statement switch
    {
        VariableDeclarationStatement declaration => declaration.Initializer is null ? [] : [declaration.Initializer],
        AssignmentStatement assignment => [assignment.Target, assignment.Value],
        ReturnStatement item => item.Value is null ? [] : [item.Value],
        ExpressionStatement item => [item.Expression],
        IfStatement item => [item.Condition],
        WhileStatement item => [item.Condition],
        ForStatement item => item.Condition is null ? [] : [item.Condition],
        ForInStatement item => [item.Collection],
        ThrowStatement item => item.Value is null ? [] : [item.Value],
        DeleteStatement item => [item.Target],
        SwitchStatement item => new[] { item.Value }.Concat(item.Cases.Where(@case => @case.Value is not null).Select(@case => @case.Value!)),
        _ => []
    };
}
