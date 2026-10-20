using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

// Cloning: deep-copies declarations and statements with generic type parameters replaced.
public sealed partial class SemanticAnalyzer
{
    private FunctionDeclaration CloneFunctionAggregate(
        FunctionDeclaration source,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames,
        Dictionary<Expression, SemanticType> expressionTypes,
        object owner,
        Dictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var parameterTypes = source.Parameters.Select(parameter => parameter with
        {
            Type = RewriteTypeSyntax(parameter.Type, substitutions, aggregateNames)
        }).ToArray();
        var result = source with
        {
            ReturnType = RewriteTypeSyntax(source.ReturnType, substitutions, aggregateNames),
            Parameters = parameterTypes,
            Body = CloneBlock(source.Body, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
            TypeParameters = []
        };
        return result;
    }

    private BlockStatement CloneBlock(
        BlockStatement source,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames,
        Dictionary<Expression, SemanticType> expressionTypes,
        object owner,
        Dictionary<CallExpression, FunctionSymbol> resolvedCalls) =>
        source with { Statements = source.Statements.Select(statement =>
            CloneStatement(statement, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)).ToArray() };

    private Statement CloneStatement(
        Statement source,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames,
        Dictionary<Expression, SemanticType> expressionTypes,
        object owner,
        Dictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var result = source switch
        {
            BlockStatement item => CloneBlock(item, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
            VariableDeclarationStatement item => item with
            {
                Type = RewriteTypeSyntax(item.Type, substitutions, aggregateNames),
                Initializer = item.Initializer is null ? null :
                    CloneExpression(item.Initializer, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            AssignmentStatement item => item with
            {
                Target = CloneExpression(item.Target, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Value = CloneExpression(item.Value, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            ReturnStatement item => item with
            {
                Value = item.Value is null ? null :
                    CloneExpression(item.Value, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            ExpressionStatement item => item with
            {
                Expression = CloneExpression(item.Expression, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            IfStatement item => item with
            {
                Condition = CloneExpression(item.Condition, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Then = CloneStatement(item.Then, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Else = item.Else is null ? null :
                    CloneStatement(item.Else, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            WhileStatement item => item with
            {
                Condition = CloneExpression(item.Condition, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Body = CloneStatement(item.Body, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            ForStatement item => item with
            {
                Initializer = item.Initializer is null ? null :
                    CloneStatement(item.Initializer, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Condition = item.Condition is null ? null :
                    CloneExpression(item.Condition, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Increment = item.Increment is null ? null :
                    CloneStatement(item.Increment, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Body = CloneStatement(item.Body, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            ForInStatement item => item with
            {
                VariableType = RewriteTypeSyntax(item.VariableType, substitutions, aggregateNames),
                Collection = CloneExpression(item.Collection, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Body = CloneStatement(item.Body, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            DeleteStatement item => item with
            {
                Target = CloneExpression(item.Target, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            ThrowStatement item => item with
            {
                Value = item.Value is null
                    ? null
                    : CloneExpression(item.Value, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            SwitchStatement item => item with
            {
                Value = CloneExpression(item.Value, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Cases = item.Cases.Select(caseItem => caseItem with
                {
                    PatternType = caseItem.PatternType is null ? null :
                        RewriteTypeSyntax(caseItem.PatternType, substitutions, aggregateNames),
                    Value = caseItem.Value is null ? null :
                        CloneExpression(caseItem.Value, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                    Body = CloneStatement(caseItem.Body, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
                }).ToArray()
            },
            TryStatement item => item with
            {
                Body = CloneBlock(item.Body, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Catches = item.Catches.Select(clause => clause with
                {
                    ExceptionType = clause.ExceptionType is null ? null :
                        RewriteTypeSyntax(clause.ExceptionType, substitutions, aggregateNames),
                    Body = CloneBlock(clause.Body, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
                }).ToArray(),
                Finally = item.Finally is null ? null :
                    CloneBlock(item.Finally, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            _ => source
        };
        // Facts recorded for the source statement belong to the clone as well.
        if (source is VariableDeclarationStatement local && _escapingClassLocals.Contains(local))
            _escapingClassLocals.Add((VariableDeclarationStatement)result);
        if (source is ForInStatement loop && _iteratorProtocols.TryGetValue(loop, out var protocol))
            _iteratorProtocols[(ForInStatement)result] = protocol;
        return result;
    }

    private Expression CloneExpression(
        Expression source,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames,
        Dictionary<Expression, SemanticType> expressionTypes,
        object owner,
        Dictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        Expression result = source switch
        {
            UnaryExpression item => item with { Operand = CloneExpression(item.Operand, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls) },
            BinaryExpression item => item with
            {
                Left = CloneExpression(item.Left, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Right = CloneExpression(item.Right, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            ConditionalExpression item => item with
            {
                Condition = CloneExpression(item.Condition, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                WhenTrue = CloneExpression(item.WhenTrue, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                WhenFalse = CloneExpression(item.WhenFalse, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            IsExpression item => item with
            {
                TargetType = RewriteTypeSyntax(item.TargetType, substitutions, aggregateNames),
                Operand = CloneExpression(item.Operand, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            CallExpression item => item with
            {
                Arguments = item.Arguments.Select(argument =>
                    CloneExpression(argument, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)).ToArray(),
                TypeArguments = item.TypeArguments?.Select(type => RewriteTypeSyntax(type, substitutions, aggregateNames)).ToArray()
            },
            ClassConstructionExpression item => item with
            {
                TypeName = RewriteNamedAggregate(item.TypeName, SemanticTypeKind.Class, substitutions, aggregateNames),
                Arguments = item.Arguments.Select(argument =>
                    CloneExpression(argument, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)).ToArray(),
                TypeArguments = item.TypeArguments?.Select(type => RewriteTypeSyntax(type, substitutions, aggregateNames)).ToArray()
            },
            NewExpression item => item with
            {
                Type = RewriteTypeSyntax(item.Type, substitutions, aggregateNames),
                Arguments = item.Arguments.Select(argument =>
                    CloneExpression(argument, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)).ToArray()
            },
            CastExpression item => item with
            {
                TargetType = RewriteTypeSyntax(item.TargetType, substitutions, aggregateNames),
                Operand = CloneExpression(item.Operand, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            SizeOfExpression item => item with
            {
                TargetType = item.TargetType is null ? null : RewriteTypeSyntax(item.TargetType, substitutions, aggregateNames),
                Operand = item.Operand is null ? null :
                    CloneExpression(item.Operand, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            MemberAccessExpression item => item with
            {
                Target = CloneExpression(item.Target, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            IndexExpression item => item with
            {
                Target = CloneExpression(item.Target, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls),
                Index = CloneExpression(item.Index, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            },
            ArrayLiteralExpression item => item with
            {
                Elements = item.Elements.Select(element =>
                    CloneExpression(element, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)).ToArray()
            },
            StructLiteralExpression item => item with
            {
                TypeName = RewriteNamedAggregate(item.TypeName, SemanticTypeKind.Struct, substitutions, aggregateNames),
                Fields = item.Fields.Select(field => field with
                {
                    Value = CloneExpression(field.Value, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
                }).ToArray()
            },
            UnionConstructionExpression item => item with
            {
                UnionName = RewriteNamedAggregate(item.UnionName, SemanticTypeKind.Union, substitutions, aggregateNames),
                Arguments = item.Arguments.Select(argument =>
                    CloneExpression(argument, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)).ToArray()
            },
            _ => source
        };
        if (expressionTypes.TryGetValue(source, out var type))
            expressionTypes[result] = SubstituteAggregate(type, substitutions, aggregateNames);
        if (source is CallExpression call && result is CallExpression cloned)
        {
            if (resolvedCalls.TryGetValue(call, out var function))
                resolvedCalls[cloned] = function;
            if (_callReceiverTypes.TryGetValue(call, out var receiverType))
                _callReceiverTypes[cloned] = SubstituteAggregate(receiverType, substitutions, aggregateNames);
            if (_receiverArgumentCalls.Contains(call))
                _receiverArgumentCalls.Add(cloned);
        }
        if (_constructorSelections.TryGetValue(source, out var selection))
            _constructorSelections[result] = selection;
        return result;
    }

    private ConstructorInitializer CloneInitializer(
        ConstructorInitializer source,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames,
        Dictionary<Expression, SemanticType> expressionTypes,
        object owner,
        Dictionary<CallExpression, FunctionSymbol> resolvedCalls) =>
        source with
        {
            Arguments = source.Arguments.Select(argument =>
                CloneExpression(argument, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)).ToArray()
        };

    private PropertyDeclaration CloneProperty(
        PropertyDeclaration source,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, string> aggregateNames,
        Dictionary<Expression, SemanticType> expressionTypes,
        object owner,
        Dictionary<CallExpression, FunctionSymbol> resolvedCalls) =>
        source with
        {
            Type = RewriteTypeSyntax(source.Type, substitutions, aggregateNames),
            Accessors = source.Accessors.Select(accessor => accessor with
            {
                Body = accessor.Body is null ? null :
                    CloneBlock(accessor.Body, substitutions, aggregateNames, expressionTypes, owner, resolvedCalls)
            }).ToArray()
        };

}
