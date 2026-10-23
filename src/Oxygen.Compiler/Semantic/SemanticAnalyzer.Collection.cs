using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

// Collection: walks syntax to find the generic uses that need a specialized copy.
public sealed partial class SemanticAnalyzer
{
    // The expressions of a statement, and of every statement nested in it.
    private static IEnumerable<Expression> EnumerateExpressions(Statement? statement) =>
        statement is null ? [] : StatementsWithin(statement).SelectMany(OwnExpressions).SelectMany(EnumerateExpressions);

    // The statement and every statement nested in it, in source order.
    private static IEnumerable<Statement> StatementsWithin(Statement statement)
    {
        yield return statement;
        foreach (var child in ChildStatements(statement))
        foreach (var nested in StatementsWithin(child))
            yield return nested;
    }

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

    private static IEnumerable<Expression> EnumerateExpressions(Expression? expression)
    {
        if (expression is null)
            return [];
        var nested = expression switch
        {
            UnaryExpression item => EnumerateExpressions(item.Operand),
            BinaryExpression item => EnumerateExpressions(item.Left).Concat(EnumerateExpressions(item.Right)),
            ConditionalExpression item => EnumerateExpressions(item.Condition)
                .Concat(EnumerateExpressions(item.WhenTrue)).Concat(EnumerateExpressions(item.WhenFalse)),
            IsExpression item => EnumerateExpressions(item.Operand),
            NamedArgumentExpression item => EnumerateExpressions(item.Value),
            CallExpression item => (item.Receiver is null ? [] : EnumerateExpressions(item.Receiver)).Concat(item.Arguments.SelectMany(EnumerateExpressions)),
            ClassConstructionExpression item => item.Arguments.SelectMany(EnumerateExpressions),
            NewExpression item => item.Arguments.SelectMany(EnumerateExpressions),
            CastExpression item => EnumerateExpressions(item.Operand),
            SizeOfExpression item => EnumerateExpressions(item.Operand),
            LengthExpression item => EnumerateExpressions(item.Target),
            MemberAccessExpression item => EnumerateExpressions(item.Target),
            IndexExpression item => EnumerateExpressions(item.Target).Concat(EnumerateExpressions(item.Index)),
            ArrayLiteralExpression item => item.Elements.SelectMany(EnumerateExpressions),
            StructLiteralExpression item => item.Fields.SelectMany(field => EnumerateExpressions(field.Value)),
            UnionConstructionExpression item => item.Arguments.SelectMany(EnumerateExpressions),
            IndirectCallExpression item => EnumerateExpressions(item.Callee).Concat(item.Arguments.SelectMany(EnumerateExpressions)),
            _ => []
        };
        return nested.Prepend(expression);
    }

    private static void CollectFunctionSyntax(FunctionDeclaration function, Action<TypeSyntax, IReadOnlySet<string>> add)
    {
        var parameters = function.DeclaredTypeParameters.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        add(function.ReturnType, parameters);
        foreach (var parameter in function.Parameters)
            add(parameter.Type, parameters);
        CollectBlockSyntax(function.Body, parameters, add);
    }

    private static void CollectClassSyntax(ClassDeclaration declaration, Action<TypeSyntax, IReadOnlySet<string>> add)
    {
        var parameters = declaration.DeclaredTypeParameters.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var field in declaration.Fields)
        {
            add(field.Type, parameters);
            CollectExpressionSyntax(field.Initializer, parameters, add);
        }
        foreach (var property in declaration.DeclaredProperties)
        {
            add(property.Type, parameters);
            foreach (var accessor in property.Accessors)
                CollectBlockSyntax(accessor.Body, parameters, add);
        }
        foreach (var method in declaration.Methods)
            CollectFunctionSyntax(method, add);
        foreach (var constructor in declaration.Constructors)
        {
            foreach (var parameter in constructor.Parameters)
                add(parameter.Type, parameters);
            foreach (var argument in constructor.Initializer?.Arguments ?? [])
                CollectExpressionSyntax(argument, parameters, add);
            CollectBlockSyntax(constructor.Body, parameters, add);
        }
        if (declaration.Destructor is not null)
            CollectBlockSyntax(declaration.Destructor.Body, parameters, add);
    }

    private static void CollectInterfaceSyntax(InterfaceDeclaration declaration, Action<TypeSyntax, IReadOnlySet<string>> add)
    {
        var parameters = declaration.DeclaredTypeParameters.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var property in declaration.DeclaredProperties)
            add(property.Type, parameters);
        foreach (var method in declaration.Methods)
            CollectFunctionSyntax(method, add);
    }

    private static void CollectBlockSyntax(BlockStatement? block, IReadOnlySet<string> parameters,
        Action<TypeSyntax, IReadOnlySet<string>> add)
    {
        if (block is null)
            return;
        foreach (var statement in block.Statements)
            CollectStatementSyntax(statement, parameters, add);
    }

    private static void CollectStatementSyntax(Statement statement, IReadOnlySet<string> parameters,
        Action<TypeSyntax, IReadOnlySet<string>> add)
    {
        switch (statement)
        {
            case BlockStatement block:
                CollectBlockSyntax(block, parameters, add);
                break;
            case VariableDeclarationStatement variable:
                add(variable.Type, parameters);
                CollectExpressionSyntax(variable.Initializer, parameters, add);
                break;
            case AssignmentStatement assignment:
                CollectExpressionSyntax(assignment.Target, parameters, add);
                CollectExpressionSyntax(assignment.Value, parameters, add);
                break;
            case ReturnStatement @return:
                CollectExpressionSyntax(@return.Value, parameters, add);
                break;
            case ExpressionStatement expression:
                CollectExpressionSyntax(expression.Expression, parameters, add);
                break;
            case IfStatement conditional:
                CollectExpressionSyntax(conditional.Condition, parameters, add);
                CollectStatementSyntax(conditional.Then, parameters, add);
                if (conditional.Else is not null) CollectStatementSyntax(conditional.Else, parameters, add);
                break;
            case WhileStatement loop:
                CollectExpressionSyntax(loop.Condition, parameters, add);
                CollectStatementSyntax(loop.Body, parameters, add);
                break;
            case ForStatement loop:
                if (loop.Initializer is not null) CollectStatementSyntax(loop.Initializer, parameters, add);
                CollectExpressionSyntax(loop.Condition, parameters, add);
                if (loop.Increment is not null) CollectStatementSyntax(loop.Increment, parameters, add);
                CollectStatementSyntax(loop.Body, parameters, add);
                break;
            case ForInStatement loop:
                add(loop.VariableType, parameters);
                CollectExpressionSyntax(loop.Collection, parameters, add);
                CollectStatementSyntax(loop.Body, parameters, add);
                break;
            case ThrowStatement @throw:
                CollectExpressionSyntax(@throw.Value, parameters, add);
                break;
            case DeleteStatement @delete:
                CollectExpressionSyntax(@delete.Target, parameters, add);
                break;
            case TryStatement @try:
                CollectBlockSyntax(@try.Body, parameters, add);
                foreach (var item in @try.Catches)
                {
                    if (item.ExceptionType is not null) add(item.ExceptionType, parameters);
                    CollectBlockSyntax(item.Body, parameters, add);
                }
                CollectBlockSyntax(@try.Finally, parameters, add);
                break;
            case SwitchStatement @switch:
                CollectExpressionSyntax(@switch.Value, parameters, add);
                foreach (var item in @switch.Cases)
                {
                    if (item.PatternType is not null) add(item.PatternType, parameters);
                    CollectExpressionSyntax(item.Value, parameters, add);
                    CollectStatementSyntax(item.Body, parameters, add);
                }
                break;
        }
    }

    private static void CollectExpressionSyntax(Expression? expression, IReadOnlySet<string> parameters,
        Action<TypeSyntax, IReadOnlySet<string>> add)
    {
        if (expression is null)
            return;
        switch (expression)
        {
            case IsExpression item:
                add(item.TargetType, parameters);
                CollectExpressionSyntax(item.Operand, parameters, add);
                break;
            case UnaryExpression item:
                CollectExpressionSyntax(item.Operand, parameters, add);
                break;
            case BinaryExpression item:
                CollectExpressionSyntax(item.Left, parameters, add);
                CollectExpressionSyntax(item.Right, parameters, add);
                break;
            case ConditionalExpression item:
                CollectExpressionSyntax(item.Condition, parameters, add);
                CollectExpressionSyntax(item.WhenTrue, parameters, add);
                CollectExpressionSyntax(item.WhenFalse, parameters, add);
                break;
            case NamedArgumentExpression item:
                CollectExpressionSyntax(item.Value, parameters, add);
                break;
            case CallExpression item:
                foreach (var type in item.ExplicitTypeArguments) add(type, parameters);
                if (item.Receiver is not null) CollectExpressionSyntax(item.Receiver, parameters, add);
                foreach (var argument in item.Arguments) CollectExpressionSyntax(argument, parameters, add);
                break;
            case ClassConstructionExpression item:
                foreach (var type in item.ExplicitTypeArguments) add(type, parameters);
                foreach (var argument in item.Arguments) CollectExpressionSyntax(argument, parameters, add);
                break;
            case NewExpression item:
                add(item.Type, parameters);
                foreach (var argument in item.Arguments) CollectExpressionSyntax(argument, parameters, add);
                break;
            case CastExpression item:
                add(item.TargetType, parameters);
                CollectExpressionSyntax(item.Operand, parameters, add);
                break;
            case SizeOfExpression item:
                if (item.TargetType is not null) add(item.TargetType, parameters);
                CollectExpressionSyntax(item.Operand, parameters, add);
                break;
            case MemberAccessExpression item:
                CollectExpressionSyntax(item.Target, parameters, add);
                break;
            case IndexExpression item:
                CollectExpressionSyntax(item.Target, parameters, add);
                CollectExpressionSyntax(item.Index, parameters, add);
                break;
            case ArrayLiteralExpression item:
                foreach (var element in item.Elements) CollectExpressionSyntax(element, parameters, add);
                break;
            case StructLiteralExpression item:
                foreach (var field in item.Fields) CollectExpressionSyntax(field.Value, parameters, add);
                break;
            case UnionConstructionExpression item:
                foreach (var argument in item.Arguments) CollectExpressionSyntax(argument, parameters, add);
                break;
        }
    }

}
