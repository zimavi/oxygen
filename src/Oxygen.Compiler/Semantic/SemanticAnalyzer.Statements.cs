using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

// Statements: declarations, assignments, returns, control flow and switch.
public sealed partial class SemanticAnalyzer
{
    // Whether entering a switch case returns on every path. A case that breaks out of the switch does not fall into the
    // next case, so only a case that neither returns nor breaks inherits the next case's answer.
    private bool CaseReturns(Func<bool> analyzeBody, bool nextCaseReturns, int caseIndex)
    {
        var savedExitDepth = _switchExitDepth;
        var savedBreak = _breakReached;
        _switchExitDepth = _breakableDepth;
        _breakReached = false;
        // A case can be entered directly, so every case starts from the state before the switch. Its end is recorded for the join.
        var frame = _switchFlows.Peek();
        RestoreFlow(frame.Start);
        var returns = analyzeBody();
        frame.Ends.Add((caseIndex, SnapshotFlow()));
        var breaks = _breakReached;
        _switchExitDepth = savedExitDepth;
        _breakReached = savedBreak;
        return returns || (!breaks && nextCaseReturns);
    }

    private bool AnalyzeStatement(
        Statement statement,
        Scope scope,
        FunctionSymbol function,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        switch (statement)
        {
            case BlockStatement block:
                return AnalyzeBlock(block, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
            case IfStatement conditional:
                var conditionType = AnalyzeExpression(conditional.Condition, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                RequireBoolean(conditionType, conditional.Condition.Location);
                var thenScope = scope;
                if (conditional.Condition is IsExpression { BindingName: { } binding, TargetType: var targetSyntax })
                {
                    var targetType = ResolveType(targetSyntax);
                    thenScope = new Scope(scope, MarkVariableUsed);
                    thenScope.Declare(binding, new VariableSymbol(targetType, false, conditional.Condition, false));
                }
                var beforeBranches = SnapshotDeleteState();
                var beforeFlow = SnapshotFlow();
                var thenReturns = AnalyzeStatement(conditional.Then, thenScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                var afterThen = SnapshotDeleteState();
                var afterThenFlow = SnapshotFlow();
                RestoreDeleteState(beforeBranches);
                RestoreFlow(beforeFlow);
                var elseReturns = conditional.Else is not null
                    && AnalyzeStatement(conditional.Else, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                var afterElse = SnapshotDeleteState();
                // A branch that returns does not reach the code after the if, so only the other branch's deletions count.
                if (thenReturns && !elseReturns)
                    RestoreDeleteState(afterElse);
                else if (elseReturns && !thenReturns)
                    RestoreDeleteState(afterThen);
                else if (!thenReturns && !elseReturns)
                    RestoreDeleteState(JoinDeleteStates(afterThen, afterElse));
                RestoreFlow(JoinFlows(afterThenFlow, SnapshotFlow()));
                return conditional.Else is not null && thenReturns && elseReturns;
            case WhileStatement whileStatement:
                var whileType = AnalyzeExpression(whileStatement.Condition, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                RequireBoolean(whileType, whileStatement.Condition.Location);
                var beforeWhile = BeginLoopDeletes();
                ForgetNullsAssignedIn(whileStatement, scope);
                var flowBeforeWhile = SnapshotFlow();
                _breakStates.Push([]);
                _loopDepth++;
                _breakableDepth++;
                AnalyzeStatement(whileStatement.Body, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                _breakableDepth--;
                _loopDepth--;
                FinishLoopDeletes(beforeWhile);
                RestoreFlow(AfterLoopFlow(flowBeforeWhile, _breakStates.Pop(),
                    whileStatement.Condition is BooleanLiteralExpression { Value: true }));
                return false;
            case ForStatement forStatement:
                var loopScope = new Scope(scope, MarkVariableUsed);
                if (forStatement.Initializer is not null)
                    AnalyzeStatement(forStatement.Initializer, loopScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                if (forStatement.Condition is not null)
                {
                    var forType = AnalyzeExpression(forStatement.Condition, loopScope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                    RequireBoolean(forType, forStatement.Condition.Location);
                }
                var beforeFor = BeginLoopDeletes();
                ForgetNullsAssignedIn(forStatement, scope);
                var flowBeforeFor = SnapshotFlow();
                _breakStates.Push([]);
                _loopDepth++;
                _breakableDepth++;
                AnalyzeStatement(forStatement.Body, loopScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                if (forStatement.Increment is not null)
                    AnalyzeStatement(forStatement.Increment, loopScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls);
                _breakableDepth--;
                _loopDepth--;
                FinishLoopDeletes(beforeFor);
                RestoreFlow(AfterLoopFlow(flowBeforeFor, _breakStates.Pop(),
                    forStatement.Condition is null or BooleanLiteralExpression { Value: true }));
                return false;
            case ForInStatement forInStatement:
                {
                    if (!forInStatement.VariableType.IsInferred)
                        throw Error("For-in iteration variables must use 'var'", forInStatement.VariableType.Location);
                    var collectionType = AnalyzeExpression(forInStatement.Collection, scope, functions, moduleName,
                        imports, expressionTypes, resolvedCalls);
                    SemanticType iterationElement;
                    if (collectionType.IsArray && collectionType.ElementType is not null)
                    {
                        // Fixed arrays use the intrinsic lowering path for now.
                        // User-defined collections below must satisfy iter/next.
                        iterationElement = collectionType.ElementType;
                    }
                    else if (!collectionType.IsClass
                             || !_classesByName.TryGetValue(collectionType.StructName!, out var collectionClass))
                        throw ErrorWithCode(DiagnosticCode.OperandRequirement, $"For-in requires a fixed array or iterator-protocol collection, got {collectionType}",
                            forInStatement.Collection.Location);
                    else
                    {
                        var iterCandidates = GetClassMethods(collectionClass.QualifiedName, [])
                            .Where(method => method.Name == "iter" && method.Parameters.Count == 0).ToArray();
                        if (iterCandidates.Length != 1)
                            throw Error($"Collection class '{collectionClass.Name}' must declare exactly one iter() method",
                                forInStatement.Collection.Location);
                        var iter = iterCandidates[0];
                        var iterCall = new CallExpression($"{collectionClass.Name}.iter", [],
                            forInStatement.Collection.Location);
                        // The declared signatures use the generic parameters of their classes. The type arguments of this
                        // collection and of its iterator are substituted into them, so the element type is concrete.
                        var iteratorType = SubstituteGenericType(iter.ReturnType,
                            TypeArgumentSubstitutions(collectionClass, collectionType));
                        if (!iteratorType.IsClass || !_classesByName.TryGetValue(iteratorType.StructName!, out var iteratorClass))
                            throw ErrorWithCode(DiagnosticCode.MustReturn, "Iterator 'iter()' must return an iterator class", iterCall.Location);
                        // The receiver for next() is represented by the iterator value
                        // in the lowering; resolve the declared method directly here.
                        var nextCandidates = GetClassMethods(iteratorClass.QualifiedName, [])
                            .Where(method => method.Name == "next" && method.Parameters.Count == 0).ToArray();
                        if (nextCandidates.Length != 1)
                            throw Error($"Iterator class '{iteratorClass.Name}' must declare exactly one next() method",
                                forInStatement.Collection.Location);
                        var next = nextCandidates[0];
                        var option = SubstituteGenericType(next.ReturnType,
                            TypeArgumentSubstitutions(iteratorClass, iteratorType));
                        if (!option.IsUnion || !string.Equals(option.EnumName, OptionUnion, StringComparison.Ordinal)
                            || option.TypeArguments is not { Count: 1 })
                            throw ErrorWithCode(DiagnosticCode.MustReturn, "Iterator next() must return Option<T> with the iteration element type",
                                forInStatement.Collection.Location);
                        iterationElement = option.TypeArguments[0];
                        // Monomorphization specializes the recorded iter() call for the collection's type arguments, and the
                        // protocol's methods with it.
                        resolvedCalls[iterCall] = iter;
                        _callReceiverTypes[iterCall] = collectionType;
                        _iteratorProtocols[forInStatement] = new IteratorProtocol(
                            forInStatement.Collection, iterCall, iter, next, iterationElement, option);
                        _usedFunctions.Add(iter);
                        _usedFunctions.Add(next);
                    }
                    var forInScope = new Scope(scope, MarkVariableUsed);
                    if (forInScope.ContainsInCurrentScope(forInStatement.VariableName))
                        throw ErrorWithCode(DiagnosticCode.AlreadyDeclared, $"Variable '{forInStatement.VariableName}' is already declared in this scope",
                            forInStatement.Location, forInStatement.VariableName.Length);
                    var variable = new VariableSymbol(iterationElement, false,
                        new VariableDeclarationStatement(forInStatement.VariableType, forInStatement.VariableName, false,
                            null, forInStatement.Location), false);
                    forInScope.Declare(forInStatement.VariableName, variable);
                    _declaredVariables.Add(variable);
                    var beforeForIn = BeginLoopDeletes();
                    ForgetNullsAssignedIn(forInStatement, scope);
                    var flowBeforeForIn = SnapshotFlow();
                    _breakStates.Push([]);
                    _loopDepth++;
                    _breakableDepth++;
                    AnalyzeStatement(forInStatement.Body, forInScope, function, functions, moduleName, imports,
                        expressionTypes, resolvedCalls);
                    _breakableDepth--;
                    _loopDepth--;
                    FinishLoopDeletes(beforeForIn);
                    RestoreFlow(AfterLoopFlow(flowBeforeForIn, _breakStates.Pop(), alwaysTrue: false));
                    return false;
                }
            case SwitchStatement switchStatement:
                {
                    var valueType = AnalyzeExpression(switchStatement.Value, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                    _switchFlows.Push((SnapshotFlow(), []));
                    _breakStates.Push([]);
                    _breakableDepth++;
                    try
                    {
                        if (valueType.IsUnion)
                        {
                            var union = _unionsByName[valueType.EnumName!];
                            var unionSubstitutions = union.DeclaredTypeParameters
                                .Zip(valueType.TypeArguments ?? [], (name, type) => (name, type))
                                .ToDictionary(item => item.name, item => item.type, StringComparer.Ordinal);
                            var seen = new HashSet<string>(StringComparer.Ordinal);
                            var caseReturns = new bool[switchStatement.Cases.Count];
                            for (var index = switchStatement.Cases.Count - 1; index >= 0; index--)
                            {
                                var item = switchStatement.Cases[index];
                                if (item.IsDefault || item.Value is not null)
                                    throw Error("Union switches require named union cases", item.Location);
                                if (item.CaseName is null || !seen.Add(item.CaseName)
                                    || !union.CasesByName.TryGetValue(item.CaseName, out var unionCase))
                                    throw Error($"Unknown or duplicate union case '{item.CaseName}'", item.Location);
                                if ((item.BindingName is null) != (unionCase.PayloadType is null))
                                    throw Error($"Union case '{item.CaseName}' {(unionCase.PayloadType is null ? "does not have" : "requires")} a payload binding", item.Location);
                                var caseScope = new Scope(scope, MarkVariableUsed);
                                if (item.BindingName is not null)
                                    caseScope.Declare(item.BindingName, new VariableSymbol(
                                        SubstituteGenericType(unionCase.PayloadType!, unionSubstitutions),
                                        false, item, false));
                                caseReturns[index] = CaseReturns(
                                    () => AnalyzeStatement(item.Body, caseScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls),
                                    index + 1 < caseReturns.Length && caseReturns[index + 1], index);
                            }
                            if (seen.Count != union.Cases.Count)
                                throw Error($"Switch must handle every case of union '{union.Name}'", switchStatement.Location);
                            return caseReturns.Length > 0 && caseReturns[0];
                        }

                        if (switchStatement.Cases.Any(item => item.PatternType is not null))
                        {
                            if (!valueType.IsClass && !valueType.IsInterface)
                                throw Error("Type-pattern switches require a class or interface value", switchStatement.Value.Location);
                            var caseReturns = new bool[switchStatement.Cases.Count];
                            for (var index = switchStatement.Cases.Count - 1; index >= 0; index--)
                            {
                                var item = switchStatement.Cases[index];
                                if (item.IsDefault)
                                {
                                    if (item.PatternType is not null || item.BindingName is not null)
                                        throw Error("Default switch cases cannot bind a value", item.Location);
                                }
                                else
                                {
                                    if (item.PatternType is null || item.Value is not null || item.CaseName is not null)
                                        throw Error("Type-pattern switches require 'case is Type' labels", item.Location);
                                    var targetType = AnalyzeTypePattern(item.PatternType, valueType, item.Location);
                                    if (item.BindingName is not null)
                                    {
                                        var caseScope = new Scope(scope, MarkVariableUsed);
                                        caseScope.Declare(item.BindingName, new VariableSymbol(targetType, false, item, false));
                                        caseReturns[index] = CaseReturns(
                                            () => AnalyzeStatement(item.Body, caseScope, function, functions, moduleName, imports, expressionTypes, resolvedCalls),
                                            index + 1 < caseReturns.Length && caseReturns[index + 1], index);
                                    }
                                    else
                                    {
                                        caseReturns[index] = CaseReturns(
                                            () => AnalyzeStatement(item.Body, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls),
                                            index + 1 < caseReturns.Length && caseReturns[index + 1], index);
                                    }
                                    continue;
                                }
                                caseReturns[index] = CaseReturns(
                                    () => AnalyzeStatement(item.Body, scope, function, functions, moduleName, imports, expressionTypes, resolvedCalls),
                                    index + 1 < caseReturns.Length && caseReturns[index + 1], index);
                            }
                            return caseReturns.Length > 0 && caseReturns[0];
                        }

                        if (!IsSwitchComparable(valueType))
                            throw ErrorWithCode(DiagnosticCode.OperandRequirement, $"Switch requires a comparable value, got {valueType}", switchStatement.Value.Location);
                        var seenLabels = new HashSet<string>(StringComparer.Ordinal);
                        var hasDefault = false;
                        var caseReturnsForValue = new bool[switchStatement.Cases.Count];
                        for (var index = switchStatement.Cases.Count - 1; index >= 0; index--)
                        {
                            var item = switchStatement.Cases[index];
                            if (item.CaseName is not null || item.BindingName is not null)
                                throw Error("Value switches require constant case labels", item.Location);
                            if (item.IsDefault)
                            {
                                if (hasDefault)
                                    throw Error("A switch can contain only one default case", item.Location);
                                hasDefault = true;
                            }
                            else
                            {
                                if (item.Value is null)
                                    throw Error("Switch case is missing a value", item.Location);
                                var labelType = AnalyzeExpression(item.Value, scope, functions, moduleName, imports,
                                    expressionTypes, resolvedCalls);
                                if (!IsConstantSwitchLabel(item.Value, labelType, valueType)
                                    || (labelType != valueType
                                        && !(valueType.IsPointer && item.Value is NullLiteralExpression)
                                        && !CanAssign(valueType, item.Value, labelType)))
                                    throw Error($"Switch case value must be a constant comparable with {valueType}", item.Location);
                                if (!seenLabels.Add(SwitchLabelKey(item.Value)))
                                    throw Error("Duplicate switch case value", item.Location);
                            }

                            caseReturnsForValue[index] = CaseReturns(
                                () => AnalyzeStatement(item.Body, scope, function, functions, moduleName, imports,
                                    expressionTypes, resolvedCalls),
                                index + 1 < caseReturnsForValue.Length && caseReturnsForValue[index + 1], index);
                        }
                        return hasDefault && caseReturnsForValue.Length > 0 && caseReturnsForValue[0];
                    }
                    finally
                    {
                        _breakableDepth--;
                        // After the switch: the end of every case that is reached, every break, and the state where no case
                        // matches. A union switch that names every case has no such path.
                        var (switchStart, caseEnds) = _switchFlows.Pop();
                        var breaks = _breakStates.Pop();
                        var exhaustive = switchStatement.Cases.Any(item => item.IsDefault) || valueType.IsUnion;
                        var afterSwitch = exhaustive ? UnreachableFlow() : switchStart;
                        // Only the last case leaves the switch by falling out of it. An earlier case that ends reachable falls
                        // into the next case, which is already covered.
                        var lastCase = switchStatement.Cases.Count - 1;
                        foreach (var state in caseEnds.Where(entry => entry.Index == lastCase).Select(entry => entry.State).Concat(breaks))
                            afterSwitch = JoinFlows(afterSwitch, state);
                        RestoreFlow(afterSwitch);
                    }
                }
            case BreakStatement breakStatement when _breakableDepth == 0:
                throw Error("'break' is only valid inside a loop or switch", breakStatement.Location);
            case ContinueStatement continueStatement when _loopDepth == 0:
                throw Error("'continue' is only valid inside a loop", continueStatement.Location);
            case BreakStatement:
                // A break that leaves the switch being analyzed ends the case without falling into the next one.
                if (_breakableDepth == _switchExitDepth)
                    _breakReached = true;
                _breakStates.Peek().Add(SnapshotFlow());
                _reachable = false;
                return false;
            case ContinueStatement:
                _reachable = false;
                return false;
            case VariableDeclarationStatement declaration:
                AnalyzeVariableDeclaration(declaration, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                if (declaration.Initializer is null)
                    AddUnassignedParts(declaration, scope.Lookup(declaration.Name, declaration.Location).Type);
                return false;
            case AssignmentStatement assignment:
                AnalyzeAssignment(assignment, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                return false;
            case ReturnStatement returnStatement:
                AnalyzeReturn(returnStatement, function, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                _reachable = false;
                return true;
            case DeleteStatement deleteStatement:
                AnalyzeDelete(deleteStatement, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
                return false;
            case ThrowStatement throwStatement when throwStatement.Value is null:
                {
                    // `throw;` rethrows the exception that the enclosing catch clause caught.
                    if (_catchDepth == 0)
                        throw Error("'throw;' rethrows the exception of a catch clause, so it must be inside one",
                            throwStatement.Location);
                    _reachable = false;
                    return true;
                }
            case ThrowStatement throwStatement:
                {
                    var thrownType = AnalyzeExpression(throwStatement.Value!, scope, functions, moduleName, imports,
                        expressionTypes, resolvedCalls);
                    if (!IsExceptionType(thrownType))
                        throw Error($"Only exception class values can be thrown, got {thrownType}",
                            throwStatement.Value!.Location);
                    _reachable = false;
                    return true;
                }
            case TryStatement tryStatement:
                {
                    ForgetNullsAssignedIn(tryStatement, scope);
                    var beforeTry = SnapshotFlow();
                    var tryReturns = AnalyzeBlock(tryStatement.Body, scope, function, functions, moduleName, imports,
                        expressionTypes, resolvedCalls);
                    var afterCatches = SnapshotFlow();
                    var allCatchesReturn = true;
                    var catchAllSeen = false;
                    foreach (var (catchClause, index) in tryStatement.Catches.Select((item, index) => (item, index)))
                    {
                        SemanticType? catchType = null;
                        if (catchClause.ExceptionType is not null)
                        {
                            if (catchAllSeen)
                                throw Error("A typed catch cannot follow a catch-all clause", catchClause.Location);
                            catchType = ResolveType(catchClause.ExceptionType);
                            if (!IsExceptionType(catchType))
                                throw Error($"Catch type '{catchType}' must be an exception class",
                                    catchClause.ExceptionType.Location);
                        }
                        else
                        {
                            if (catchAllSeen)
                                throw Error("A try statement cannot contain multiple catch-all clauses", catchClause.Location);
                            catchAllSeen = true;
                            if (index != tryStatement.Catches.Count - 1)
                                throw Error("The catch-all clause must be last", catchClause.Location);
                        }

                        var catchScope = scope;
                        if (catchClause.BindingName is not null)
                        {
                            catchScope = new Scope(scope, MarkVariableUsed);
                            catchScope.Declare(catchClause.BindingName,
                                new VariableSymbol(catchType!, false, catchClause, false));
                        }

                        // A catch can be entered from any point in the try body, so it starts from the state before the try.
                        RestoreFlow(beforeTry);
                        bool catchReturns;
                        _catchDepth++;
                        try
                        {
                            catchReturns = AnalyzeBlock(catchClause.Body, catchScope, function, functions, moduleName,
                                imports, expressionTypes, resolvedCalls);
                        }
                        finally
                        {
                            _catchDepth--;
                        }
                        allCatchesReturn &= catchReturns;
                        afterCatches = JoinFlows(afterCatches, SnapshotFlow());
                    }

                    var finallyReturns = false;
                    if (tryStatement.Finally is null)
                    {
                        RestoreFlow(afterCatches);
                    }
                    else
                    {
                        // The finally block also runs on the exceptional paths, so it starts from every state the try can leave.
                        // Code after the try is reached only if the normal paths reach it.
                        var finallyStart = new HashSet<UnassignedSlot>(beforeTry.Unassigned.Concat(afterCatches.Unassigned));
                        RestoreFlow(new FlowState(finallyStart, beforeTry.Reachable, new HashSet<VariableSymbol>(beforeTry.Nulls)));
                        finallyReturns = AnalyzeBlock(tryStatement.Finally, scope, function, functions, moduleName, imports,
                            expressionTypes, resolvedCalls);
                        var afterFinally = SnapshotFlow();
                        // The normal path is the one that leaves the try: the finally block's assignments apply to it.
                        var assignedByFinally = finallyStart.Where(part => !afterFinally.Unassigned.Contains(part));
                        var normalAfter = afterCatches.Unassigned.Where(part => !assignedByFinally.Contains(part));
                        RestoreFlow(new FlowState(
                            new HashSet<UnassignedSlot>(normalAfter),
                            afterCatches.Reachable && afterFinally.Reachable,
                            new HashSet<VariableSymbol>(afterCatches.Nulls.Intersect(afterFinally.Nulls))));
                    }
                    return finallyReturns || (tryReturns && allCatchesReturn);
                }
            case ExpressionStatement expressionStatement:
                AnalyzeExpression(
                    expressionStatement.Expression,
                    scope,
                    functions,
                    moduleName,
                    imports,
                    expressionTypes,
                    resolvedCalls,
                    allowVoid: true);
                return false;
            default:
                throw new InvalidOperationException($"Unsupported statement type {statement.GetType().Name}.");
        }
    }

    private void RequireBoolean(SemanticType type, SourceLocation location)
    {
        if (type.Primitive != PrimitiveType.Bool || type.Kind != SemanticTypeKind.Primitive)
            throw Error($"Loop and conditional conditions must be bool, got {type}", location);
    }

    // The qualified name of the standard library's base exception class, which `throw` accepts.
    private const string ExceptionClass = "std.exception.Exception";

    // The qualified name of the standard library's Option union, which the for-in protocol requires from `next()`.
    private const string OptionUnion = "std.collection.Option";

    private bool IsExceptionType(SemanticType type)
    {
        if (!type.IsClass || type.StructName is null || !_classesByName.ContainsKey(type.StructName))
            return false;
        return string.Equals(type.StructName, ExceptionClass, StringComparison.Ordinal)
               || IsClassDerivedFrom(type.StructName, ExceptionClass);
    }

    private void AnalyzeVariableDeclaration(
        VariableDeclarationStatement declaration,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        if (declaration.Type.IsInferred && declaration.Initializer is null)
        {
            throw ErrorWithCode(DiagnosticCode.OperandRequirement, $"Variable '{declaration.Name}' using 'var' requires an initializer", declaration.Location, declaration.Name.Length);
        }

        if (scope.ContainsInCurrentScope(declaration.Name))
        {
            throw ErrorWithCode(DiagnosticCode.AlreadyDeclared, $"Variable '{declaration.Name}' is already declared in this scope", declaration.Location, declaration.Name.Length);
        }

        if (declaration.IsConst && declaration.Initializer is null)
            throw Error($"Const variable '{declaration.Name}' needs an initializer", declaration.Location, declaration.Name.Length);
        var initializerType = declaration.Initializer is null
            ? (SemanticType?)null
            : AnalyzeExpression(declaration.Initializer, scope, functions, moduleName, imports, expressionTypes, resolvedCalls,
                expectedType: declaration.Type.IsInferred ? null : ResolveType(declaration.Type));
        var type = declaration.Type.IsInferred
            ? initializerType!
            : ResolveType(declaration.Type);

        if (type.IsVoid)
        {
            throw Error($"Variable '{declaration.Name}' cannot have type void", declaration.Location, declaration.Name.Length);
        }

        if (initializerType is { IsArray: true } && type.IsArray
            && initializerType.ArrayLength != type.ArrayLength)
        {
            throw Error(
                $"Array initializer for '{declaration.Name}' expects {type.ArrayLength} element(s), got {initializerType.ArrayLength}",
                declaration.Initializer!.Location);
        }

        if (initializerType is not null && !CanAssign(type, declaration.Initializer!, initializerType))
        {
            throw ErrorWithCode(DiagnosticCode.ConversionMismatch, 
                $"Cannot initialize '{declaration.Name}' with {initializerType}; expected {type}",
                declaration.Initializer!.Location);
        }
        if (declaration.Initializer is NullLiteralExpression && type.IsPointer)
            expressionTypes[declaration.Initializer] = type;

        var variable = new VariableSymbol(type, declaration.IsConst, declaration, false);
        scope.Declare(declaration.Name, variable);
        if (declaration.Initializer is not null && IsStackObject(declaration.Initializer, scope))
            _stackObjectLocals.Add(declaration);
        _declaredVariables.Add(variable);
        if (type.IsPointer && declaration.Initializer is NullLiteralExpression)
            _definitelyNullVariables.Add(variable);
    }

    private void AnalyzeAssignment(
        AssignmentStatement assignment,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        // Assigning to a variable is not a use of it, so a deleted variable can be given a new value.
        // A plain `=` to a member is a write, so a set-only property is named without being read. A compound assignment reads it.
        var targetType = assignment.Target switch
        {
            IdentifierExpression assignedIdentifier => AssignmentTargetType(assignedIdentifier, scope, expressionTypes),
            MemberAccessExpression assignedMember when assignment.Operator == TokenKind.Equals => WrittenMemberType(
                assignedMember, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            _ => AnalyzeExpression(assignment.Target, scope, functions, moduleName, imports, expressionTypes, resolvedCalls)
        };
        // A compound assignment reads its target before it writes it.
        if (assignment.Operator != TokenKind.Equals && assignment.Target is IdentifierExpression compoundTarget
            && scope.Lookup(compoundTarget.Name, compoundTarget.Location).Declaration is VariableDeclarationStatement compoundDeclaration)
            CheckAssigned(compoundDeclaration, compoundTarget.Name, compoundTarget.Location);
        // A compound assignment to a static property reads it through its getter and writes it through its setter.
        if (assignment.Operator != TokenKind.Equals && assignment.Target is MemberAccessExpression { Target: var propertyOwner } compoundProperty
            && _staticPropertyGetters.ContainsKey(compoundProperty) && StaticTargetClass(propertyOwner, scope) is { } propertyClass)
            AnalyzeStaticProperty(compoundProperty, propertyClass, forWrite: true, moduleName);
        var isCompound = CompoundAssignment.IsCompound(assignment.Operator);
        var valueType = AnalyzeExpression(assignment.Value, scope, functions, moduleName, imports, expressionTypes, resolvedCalls,
            expectedType: isCompound ? null : targetType);
        if (assignment.Target is IdentifierExpression reassigned
            && scope.Lookup(reassigned.Name, reassigned.Location).Declaration is VariableDeclarationStatement reassignedDeclaration)
        {
            _deletedLocals.Remove(reassignedDeclaration);
            _maybeDeletedLocals.Remove(reassignedDeclaration);
            ClearAssignedParts(reassignedDeclaration, "");
        }
        else if (assignment.Target is MemberAccessExpression && ResolveStructPlace(assignment.Target, scope) is { } written)
        {
            // A field of a struct local is assigned on its own, and a struct field assigns every field under it.
            ClearAssignedParts(written.Declaration, written.Path);
        }
        if (isCompound)
        {
            // x op= y is x = x op y. The operation is checked with the binary rules, on the target and the value, and its
            // result must convert back to the target. The synthetic expression is not kept: the backend reads the target once.
            if (assignment.Target is MemberAccessExpression compoundMember)
                RequireCompoundMemberSupported(compoundMember, expressionTypes);
            var operation = new BinaryExpression(assignment.Target, CompoundAssignment.BinaryOperator(assignment.Operator),
                assignment.Value, assignment.Location);
            valueType = BinaryResultType(operation, targetType, valueType, expressionTypes);
        }
        if (!CanAssign(targetType, assignment.Value, valueType))
        {
            throw ErrorWithCode(DiagnosticCode.ConversionMismatch, $"Cannot assign {valueType} to target of type {targetType}", assignment.Value.Location);
        }

        ValidateWritableTarget(assignment.Target, scope, expressionTypes);
        RequireStackObjectStaysInFunction(assignment, scope);
        TrackPointerAssignment(assignment, assignment.Value, valueType, scope);
    }

    // A compound assignment to a member needs the member's current value: a field, or a property of a class with a getter.
    // Interface properties and properties of value classes are not supported yet.
    private void RequireCompoundMemberSupported(MemberAccessExpression member, IDictionary<Expression, SemanticType> expressionTypes)
    {
        if (!expressionTypes.TryGetValue(member.Target, out var ownerType))
            return;
        if (ownerType.IsInterface)
            throw Error("Compound assignment to an interface property is not supported", member.Location, member.MemberName.Length);
        if (!ownerType.IsClass || !_classesByName.TryGetValue(ownerType.StructName!, out var owner))
            return;
        var property = owner.DeclaredProperties.FirstOrDefault(candidate => candidate.Name == member.MemberName);
        if (property is null)
            return;
        if (owner.IsValue)
            throw Error("Compound assignment to a property of a value class is not supported", member.Location, member.MemberName.Length);
        if (!property.Declaration.Accessors.Any(accessor => accessor.Kind == PropertyAccessorKind.Get))
            throw Error($"Compound assignment needs a readable property, but '{member.MemberName}' has no getter",
                member.Location, member.MemberName.Length);
    }

    // The substitutions that map a generic class's declared type parameters to the arguments of one of its concrete uses.
    private static Dictionary<string, SemanticType> TypeArgumentSubstitutions(ClassSymbol cls, SemanticType use) =>
        cls.DeclaredTypeParameters
            .Zip(use.TypeArguments ?? [], (name, argument) => (name, argument))
            .ToDictionary(pair => pair.name, pair => pair.argument, StringComparer.Ordinal);

    // The number of catch clauses whose body is being analyzed, which is where `throw;` is valid.
    private int _catchDepth;

    // A const variable is immutable where an assignment reaches it through members and elements. Its value parts (struct and
    // value-class fields, array elements) are part of it, so none of them change. Its reference objects change only through
    // their own class: a field of such an object is assigned only inside that class. A pointer is the explicit way to mutate.
    private void RequireMutableChain(Expression target, Scope scope, IDictionary<Expression, SemanticType> expressionTypes)
    {
        var current = target;
        while (current is MemberAccessExpression or IndexExpression)
        {
            var inner = current is MemberAccessExpression member ? member.Target : ((IndexExpression)current).Target;
            SemanticType? innerType = expressionTypes.TryGetValue(inner, out var found) ? found : null;
            if (innerType is { IsPointer: true })
                return;
            if (current is MemberAccessExpression && innerType is not null && IsOwnedClass(innerType)
                && ConstRootName(inner, scope, expressionTypes) is { } root)
            {
                if (_currentOwnerClass?.QualifiedName != innerType.StructName)
                    throw ErrorWithCode(DiagnosticCode.ConstAssignment, 
                        $"Cannot assign to a field of const variable '{root}' outside class '{innerType}'",
                        target.Location, root.Length);
                return;
            }
            if (inner is IdentifierExpression identifier && TryLookupVariable(scope, identifier.Name, identifier.Location) is { IsConst: true })
                throw ErrorWithCode(DiagnosticCode.ConstAssignment, $"Cannot assign to a part of const variable '{identifier.Name}'", target.Location, identifier.Name.Length);
            current = inner;
        }
    }

    // The name of the const variable that an expression reaches through members and elements, or null. A pointer ends the walk.
    private string? ConstRootName(Expression expression, Scope scope, IDictionary<Expression, SemanticType> expressionTypes)
    {
        var current = expression;
        while (current is MemberAccessExpression or IndexExpression)
        {
            current = current is MemberAccessExpression member ? member.Target : ((IndexExpression)current).Target;
            if (expressionTypes.TryGetValue(current, out var currentType) && currentType.IsPointer)
                return null;
        }
        return current is IdentifierExpression identifier && TryLookupVariable(scope, identifier.Name, identifier.Location) is { IsConst: true }
            ? identifier.Name
            : null;
    }

    private void ValidateWritableTarget(Expression target, Scope scope, IDictionary<Expression, SemanticType> expressionTypes)
    {
        if (target is MemberAccessExpression { MemberName: "length" } lengthMember
            && expressionTypes.TryGetValue(lengthMember.Target, out var lengthOwner)
            && (lengthOwner.IsArray || lengthOwner.Kind == SemanticTypeKind.String))
            throw Error("The length of an array or a string cannot be assigned", target.Location);
        if (target is IndexExpression stringIndex
            && expressionTypes.TryGetValue(stringIndex.Target, out var indexed) && indexed.Kind == SemanticTypeKind.String)
            throw Error("A string is immutable, so its bytes cannot be assigned", target.Location);
        if (target is IdentifierExpression identifier)
        {
            var variable = scope.Lookup(identifier.Name, identifier.Location);
            if (variable.IsConst)
                throw ErrorWithCode(DiagnosticCode.ConstAssignment, $"Cannot assign to const variable '{identifier.Name}'", target.Location, identifier.Name.Length);
        }
        else if (target is MemberAccessExpression member)
        {
            // A class name is not a variable, so a static field written through it is checked by the member access.
            var isStaticTarget = StaticTargetClass(member.Target, scope) is not null;
            var targetClass = member.Target is ThisExpression && _currentOwnerClass is not null
                ? _currentOwnerClass
                : !isStaticTarget && member.Target is IdentifierExpression classIdentifier
                    && scope.Lookup(classIdentifier.Name, classIdentifier.Location).Type.IsClass
                    ? _classesByName[scope.Lookup(classIdentifier.Name, classIdentifier.Location).Type.StructName!]
                    : null;
            var field = targetClass?.Fields.FirstOrDefault(candidate => candidate.Name == member.MemberName);
            if (field is not null && field.IsConst && !_currentIsConstructor)
                throw ErrorWithCode(DiagnosticCode.ConversionMismatch, $"Cannot assign to const field '{field.Name}'", target.Location, field.Name.Length);
            if (!isStaticTarget)
                ValidatePropertyWrite(member, scope);
        }
        else if (target is IndexExpression index
                 && index.Target is IdentifierExpression indexIdentifier
                 && scope.Lookup(indexIdentifier.Name, indexIdentifier.Location).IsConst)
            throw ErrorWithCode(DiagnosticCode.ConstAssignment, $"Cannot assign to an element of const variable '{indexIdentifier.Name}'", target.Location,
                indexIdentifier.Name.Length);
        else if (target is CallExpression { IsBareName: true } bareStatic && _globalReferences.TryGetValue(bareStatic, out var staticField))
        {
            // `Class<T>.field` is a static field of one instance, which is assigned like any static field.
            if (staticField.IsConst)
                throw ErrorWithCode(DiagnosticCode.ConstAssignment, $"Cannot assign to const static field '{staticField.Name}'",
                    target.Location, staticField.Name.Length);
        }
        else if (target is not (UnaryExpression { Operator: TokenKind.Star } or IndexExpression or MemberAccessExpression))
        {
            throw Error("Assignment target is not addressable", target.Location);
        }
        RequireMutableChain(target, scope, expressionTypes);
    }

    private void ValidatePropertyWrite(MemberAccessExpression member, Scope scope)
    {
        var targetClass = member.Target switch
        {
            ThisExpression when _currentOwnerClass is not null => _currentOwnerClass,
            IdentifierExpression identifier when LookupVariableType(scope, identifier.Name, identifier.Location) is { IsClass: true }
                => _classesByName[scope.Lookup(identifier.Name, identifier.Location).Type.StructName!],
            _ => null
        };
        if (targetClass is null)
            return;
        var property = FindProperty(targetClass, member.MemberName);
        if (property is null)
            return;
        var accessor = property.Declaration.Accessors.FirstOrDefault(candidate =>
            candidate.Kind == PropertyAccessorKind.Set)
            ?? property.Declaration.Accessors.FirstOrDefault(candidate =>
                candidate.Kind == PropertyAccessorKind.Init);
        if (accessor is null)
            throw Error($"Property '{property.Name}' is read-only", member.Location, property.Name.Length);
        if (accessor.Kind == PropertyAccessorKind.Init && !_currentIsConstructor)
            throw Error($"Property '{property.Name}' can only be assigned in a constructor", member.Location,
                property.Name.Length);
        EnsureAccessorVisible(property, accessor, member.Location);
    }

    private void TrackPointerAssignment(
        AssignmentStatement assignment,
        Expression value,
        SemanticType valueType,
        Scope scope)
    {
        if (assignment.Target is IdentifierExpression assignedIdentifier)
        {
            var variable = scope.Lookup(assignedIdentifier.Name, assignedIdentifier.Location);
            if (IsOwnedClass(valueType) && assignment.Value is IdentifierExpression sourceIdentifier)
            {
                var source = scope.Lookup(sourceIdentifier.Name, sourceIdentifier.Location);
                if (source.Declaration is VariableDeclarationStatement sourceDeclaration
                    && scope.LookupDepth(assignedIdentifier.Name, assignedIdentifier.Location)
                    > scope.LookupDepth(sourceIdentifier.Name, sourceIdentifier.Location))
                    _escapingClassLocals.Add(sourceDeclaration);
            }
            if (variable.Type.IsPointer && assignment.Operator == TokenKind.Equals)
            {
                if (value is NullLiteralExpression)
                    _definitelyNullVariables.Add(variable);
                else
                    _definitelyNullVariables.Remove(variable);
            }
        }
    }

    private void AnalyzeReturn(
        ReturnStatement returnStatement,
        FunctionSymbol function,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        if (returnStatement.Value is null)
        {
            if (!function.ReturnType.IsVoid)
            {
                throw ErrorWithCode(DiagnosticCode.MustReturn, $"Function '{function.Name}' must return a value of type {function.ReturnType}", returnStatement.Location);
            }

            return;
        }

        if (function.ReturnType.IsVoid)
        {
            throw Error($"Void function '{function.Name}' cannot return a value", returnStatement.Value.Location);
        }

        var valueType = AnalyzeExpression(returnStatement.Value, scope, functions, moduleName, imports, expressionTypes, resolvedCalls,
            expectedType: function.ReturnType);
        RequireStackObjectNotReturned(returnStatement.Value, scope);
        if (IsOwnedClass(valueType) && returnStatement.Value is IdentifierExpression identifier
            && scope.Lookup(identifier.Name, identifier.Location).Declaration is VariableDeclarationStatement declaration)
            _escapingClassLocals.Add(declaration);
        if (!CanAssign(function.ReturnType, returnStatement.Value, valueType))
        {
            throw ErrorWithCode(DiagnosticCode.ConversionMismatch, 
                $"Cannot return {valueType} from function '{function.Name}'; expected {function.ReturnType}",
                returnStatement.Value.Location);
        }
        if (returnStatement.Value is NullLiteralExpression && function.ReturnType.IsPointer)
            expressionTypes[returnStatement.Value] = function.ReturnType;
    }

    // The type of a member that a plain assignment writes to. The member is not read, so a set-only property has a type.
    private SemanticType WrittenMemberType(
        MemberAccessExpression member,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var type = AnalyzeMemberAccess(member, scope, functions, moduleName, imports, expressionTypes, resolvedCalls,
            forWrite: true);
        expressionTypes[member] = type;
        return type;
    }

    private SemanticType AssignmentTargetType(IdentifierExpression target, Scope scope,
        IDictionary<Expression, SemanticType> expressionTypes)
    {
        var symbol = scope.Lookup(target.Name, target.Location);
        RecordGlobalReference(target, symbol);
        var type = symbol.Type;
        expressionTypes[target] = type;
        return type;
    }

    private static bool IsSwitchComparable(SemanticType type) =>
        type.IsInteger || type.IsEnum || type.Kind == SemanticTypeKind.String
        || type.Primitive == PrimitiveType.Bool || type.IsPointer;

    private static bool IsConstantSwitchLabel(Expression expression, SemanticType labelType, SemanticType selectorType)
    {
        if (selectorType.IsEnum)
            return expression is MemberAccessExpression && labelType == selectorType;
        if (selectorType.IsInteger)
            return labelType.IsInteger
                && expression is (IntegerLiteralExpression or UnaryExpression or BinaryExpression);
        if (selectorType.Primitive == PrimitiveType.Bool)
            return labelType.Primitive == PrimitiveType.Bool && expression is BooleanLiteralExpression;
        if (selectorType.Kind == SemanticTypeKind.String)
            return labelType.Kind == SemanticTypeKind.String && expression is StringLiteralExpression;
        return selectorType.IsPointer && expression is NullLiteralExpression;
    }

    private static string SwitchLabelKey(Expression expression)
    {
        return expression switch
        {
            IntegerLiteralExpression integer when TryGetConstantInteger(integer, out var value) => $"i:{value}",
            BooleanLiteralExpression boolean => $"b:{boolean.Value}",
            StringLiteralExpression literal => $"s:{literal.Lexeme}",
            MemberAccessExpression member => $"e:{member.Target}.{member.MemberName}",
            _ => expression.ToString() ?? string.Empty
        };
    }

}
