using System.Globalization;
using System.Text.Json;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    private static LLVMValueRef GetWindowsExceptionPointer(LLVMValueRef catchPad)
    {
        if (_exceptionPointer.Handle == IntPtr.Zero)
        {
            _exceptionPointer = _module.AddFunction(
                "llvm.eh.exceptionpointer.p0",
                LLVMTypeRef.CreateFunction(
                    LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                    [catchPad.TypeOf], false));
        }

        return _exceptionPointer;
    }

    private static bool EmitStatement(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMValueRef function,
        Statement statement,
        Dictionary<string, LLVMValueRef> values,
        Dictionary<string, LLVMValueRef> storage,
        IReadOnlySet<string> storageRequirements,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions,
        SemanticType emittedReturnType,
        Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)> loops)
    {
        switch (statement)
        {
            case VariableDeclarationStatement declaration:
                var declaredType = declaration.Type.IsInferred && declaration.Initializer is not null
                    ? model.ExpressionTypes[declaration.Initializer]
                    : FromTypeSyntax(declaration.Type, model);
                if (declaration.Initializer is not null)
                {
                    var initializer = EmitExpression(
                        context,
                        builder,
                        declaration.Initializer,
                        values,
                        storage,
                        model,
                        llvmFunctions);
                    if (storageRequirements.Contains(declaration.Name))
                    {
                        var declarationStorage = builder.BuildAlloca(
                            ToLlvmType(declaredType, model),
                            declaration.Name + ".addr");
                        builder.BuildStore(CoerceValue(builder, initializer,
                            model.ExpressionTypes[declaration.Initializer], declaredType, model), declarationStorage);
                        storage[declaration.Name] = declarationStorage;
                    }
                    else
                    {
                        values[declaration.Name] = CoerceValue(builder, initializer,
                            model.ExpressionTypes[declaration.Initializer], declaredType, model);
                    }
                }
                else
                {
                    var declarationStorage = builder.BuildAlloca(
                        ToLlvmType(declaredType, model),
                        declaration.Name + ".addr");
                    storage[declaration.Name] = declarationStorage;
                }
                if (declaredType.IsClass)
                    _ownedScopes.Peek().Add(new OwnedLocal(declaration, declaration.Name, declaredType));
                return false;
            case AssignmentStatement assignment:
                var assigned = EmitExpression(context, builder, assignment.Value, values, storage, model, llvmFunctions);
                if (assignment.Target is IdentifierExpression assignmentIdentifier
                    && assignment.Value is IdentifierExpression sourceIdentifier
                    && model.ExpressionTypes[assignment.Target].IsClass
                    && model.ExpressionTypes[assignment.Value].IsClass
                    && !IsValueClass(model.ExpressionTypes[assignment.Target], model)
                    && sourceIdentifier.Name != assignmentIdentifier.Name)
                    TransferOwnership(sourceIdentifier.Name, assignmentIdentifier.Name);
                if (assignment.Target is IdentifierExpression identifier)
                    builder.BuildStore(CoerceValue(builder, assigned, model.ExpressionTypes[assignment.Value],
                        model.ExpressionTypes[assignment.Target], model), storage[identifier.Name]);
                else if (assignment.Target is MemberAccessExpression member
                    && member.Target is IdentifierExpression targetIdentifier)
                {
                    var targetType = model.ExpressionTypes[member.Target];
                    if (targetType.IsInterface)
                    {
                        var iface = model.Interfaces[targetType.StructName!];
                        var setter = _propertyAccessors[(iface.Name, member.MemberName, PropertyAccessorKind.Set)];
                        var receiver = values.TryGetValue(targetIdentifier.Name, out var interfaceValue)
                            ? interfaceValue
                            : builder.BuildLoad2(ToLlvmType(targetType, model), storage[targetIdentifier.Name], targetIdentifier.Name);
                        var table = builder.BuildExtractValue(receiver, 1, "interface.table");
                        var objectValue = builder.BuildExtractValue(receiver, 0, "interface.data");
                        var slot = _interfaceSlots[iface.Name].ToList().IndexOf(setter);
                        var tableEntry = builder.BuildGEP2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), table,
                            [LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)slot, false)],
                            (ReadOnlySpan<char>)"interface.property.slot");
                        var rawTarget = builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                            tableEntry, "interface.property.target");
                        var setterType = ToLlvmFunctionType(setter, model);
                        var setterTarget = builder.BuildBitCast(rawTarget, setterType, "interface.property.call");
                        EmitCallOrInvoke(builder, setterType, setterTarget,
                            [objectValue, CoerceValue(builder, assigned, model.ExpressionTypes[assignment.Value],
                                setter.Parameters[0].Type, model)], string.Empty);
                        return false;
                    }
                    if (targetType.IsClass)
                    {
                        var cls = model.Classes[targetType.StructName!];
                        var property = cls.DeclaredProperties.FirstOrDefault(candidate => candidate.Name == member.MemberName);
                        if (property is not null)
                        {
                            var setter = _propertyAccessors[(cls.Name, property.Name, PropertyAccessorKind.Set)];
                            var setterType = ToLlvmFunctionType(setter, model);
                            var receiver = values.TryGetValue(targetIdentifier.Name, out var directReceiver)
                                ? directReceiver
                                : builder.BuildLoad2(ToLlvmType(targetType, model), storage[targetIdentifier.Name], targetIdentifier.Name);
                            var targetFunction = llvmFunctions[setter];
                            if (property.Declaration.IsVirtual || property.Declaration.IsOverride)
                            {
                                var slot = GetVirtualMethods(cls, model).Select((candidate, index) => (candidate, index))
                                    .First(pair => pair.candidate == setter).index;
                                var slotAddress = builder.BuildStructGEP2(_classTypes[cls.Name], receiver,
                                    (uint)slot + 1, "property.vtable.slot");
                                var raw = builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                                    slotAddress, "property.vtable.target");
                                targetFunction = builder.BuildBitCast(raw, setterType, "property.vtable.call");
                            }
                            EmitCallOrInvoke(builder, setterType, targetFunction,
                                [receiver, CoerceValue(builder, assigned, model.ExpressionTypes[assignment.Value],
                                    property.Type, model)], string.Empty);
                            return false;
                        }
                        var fieldIndex = cls.Fields
                            .Select((field, index) => (field, index))
                            .First(pair => pair.field.Name == member.MemberName).index;
                        var coerced = CoerceValue(builder, assigned, model.ExpressionTypes[assignment.Value],
                            model.ExpressionTypes[assignment.Target], model);
                        if (cls.IsValue)
                        {
                            var current = builder.BuildLoad2(ToLlvmType(targetType, model),
                                storage[targetIdentifier.Name], targetIdentifier.Name);
                            var updated = builder.BuildInsertValue(current, coerced, (uint)fieldIndex, "value.update");
                            builder.BuildStore(updated, storage[targetIdentifier.Name]);
                        }
                        else
                        {
                            var objectValue = values.TryGetValue(targetIdentifier.Name, out var direct)
                                ? direct
                                : builder.BuildLoad2(ToLlvmType(targetType, model), storage[targetIdentifier.Name], targetIdentifier.Name);
                            var address = builder.BuildStructGEP2(_classTypes[targetType.StructName!], objectValue,
                                (uint)FieldOffset(cls, model, fieldIndex), $"field.{member.MemberName}");
                            builder.BuildStore(coerced, address);
                        }
                    }
                    else
                    {
                        var current = builder.BuildLoad2(
                            ToLlvmType(targetType, model),
                            storage[targetIdentifier.Name],
                            targetIdentifier.Name);
                        var fieldIndex = model.Structs[targetType.StructName!].Fields
                            .Select((field, index) => (field, index))
                            .First(pair => pair.field.Name == member.MemberName).index;
                        var updated = builder.BuildInsertValue(current, assigned, (uint)fieldIndex, "struct.update");
                        builder.BuildStore(updated, storage[targetIdentifier.Name]);
                    }
                }
                else if (assignment.Target is MemberAccessExpression { Target: ThisExpression } thisMember)
                {
                    var owner = model.Classes.Values.FirstOrDefault(cls =>
                        cls.DeclaredProperties.Any(property => property.Name == thisMember.MemberName)
                        || cls.Fields.Any(field => field.Name == thisMember.MemberName))
                        ?? throw new InvalidOperationException(
                            $"Class member '{thisMember.MemberName}' could not be resolved for this assignment.");
                    var thisProperty = owner.DeclaredProperties.FirstOrDefault(property => property.Name == thisMember.MemberName);
                    if (thisProperty is not null)
                    {
                        var setter = _propertyAccessors[(owner.Name, thisProperty.Name, PropertyAccessorKind.Set)];
                        var setterType = ToLlvmFunctionType(setter, model);
                        var setterTarget = llvmFunctions[setter];
                        if (thisProperty.Declaration.IsVirtual || thisProperty.Declaration.IsOverride)
                        {
                            var slot = GetVirtualMethods(owner, model).Select((candidate, index) => (candidate, index))
                                .First(pair => pair.candidate == setter).index;
                            var slotAddress = builder.BuildStructGEP2(_classTypes[owner.Name], values["this"],
                                (uint)slot + 1, "property.vtable.slot");
                            var raw = builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                                slotAddress, "property.vtable.target");
                            setterTarget = builder.BuildBitCast(raw, setterType, "property.vtable.call");
                        }
                        EmitCallOrInvoke(builder, setterType, setterTarget,
                            [values["this"], CoerceValue(builder, assigned, model.ExpressionTypes[assignment.Value],
                                thisProperty.Type, model)], string.Empty);
                        return false;
                    }
                    var address = storage["this." + thisMember.MemberName];
                    builder.BuildStore(CoerceValue(builder, assigned, model.ExpressionTypes[assignment.Value],
                        model.ExpressionTypes[assignment.Target], model), address);
                }
                else if (assignment.Target is UnaryExpression { Operator: TokenKind.Star, Operand: var operand })
                    builder.BuildStore(assigned, EmitExpression(context, builder, operand, values, storage, model, llvmFunctions));
                else if (assignment.Target is IndexExpression index)
                    builder.BuildStore(assigned, EmitArrayAddress(context, builder, index, values, storage, model, llvmFunctions));
                else
                    throw new InvalidOperationException("Unsupported assignment target.");
                return false;
            case ReturnStatement returnStatement:
                LLVMValueRef? returnValue = null;
                if (returnStatement.Value is null)
                {
                    EmitOwnedScopeDestructors(builder, values, storage, model);
                    if (emittedReturnType.IsVoid)
                    {
                        EmitTracePop(builder);
                        builder.BuildRetVoid();
                    }
                    else
                    {
                        EmitTracePop(builder);
                        builder.BuildRet(LLVMValueRef.CreateConstInt(ToLlvmType(emittedReturnType, model), 0, false));
                    }
                }
                else
                {
                    returnValue = CoerceValue(builder,
                        EmitExpression(context, builder, returnStatement.Value, values, storage, model, llvmFunctions),
                        model.ExpressionTypes[returnStatement.Value], emittedReturnType, model);
                    if (returnStatement.Value is IdentifierExpression returnedIdentifier
                        && model.ExpressionTypes[returnStatement.Value].IsClass)
                        RemoveOwned(returnedIdentifier.Name);
                    EmitOwnedScopeDestructors(builder, values, storage, model);
                    EmitTracePop(builder);
                    builder.BuildRet(returnValue.Value);
                }
                return true;
            case ExpressionStatement expression:
                EmitExpression(context, builder, expression.Expression, values, storage, model, llvmFunctions);
                return false;
            case BlockStatement block:
                var blockParentDepth = _ownedScopes.Count;
                _ownedScopes.Push([]);
                foreach (var nested in block.Statements)
                    if (EmitStatement(
                        context,
                        builder,
                        function,
                        nested,
                        values,
                        storage,
                        storageRequirements,
                        model,
                        llvmFunctions,
                        emittedReturnType,
                        loops))
                    {
                        if (_ownedScopes.Count > blockParentDepth)
                            _ownedScopes.Pop();
                        return true;
                    }
                CleanupOwnedScope(builder, values, storage, model);
                _ownedScopes.Pop();
                return false;
            case IfStatement conditional:
                {
                    var thenBlock = function.AppendBasicBlock("if.then");
                    var elseBlock = function.AppendBasicBlock("if.else");
                    var mergeBlock = function.AppendBasicBlock("if.end");
                    LLVMValueRef? patternOperand = null;
                    LLVMValueRef condition;
                    if (conditional.Condition is IsExpression pattern)
                    {
                        patternOperand = EmitExpression(context, builder, pattern.Operand, values, storage, model, llvmFunctions);
                        condition = EmitRuntimeTypeCheck(builder, patternOperand.Value, model.ExpressionTypes[pattern.Operand],
                            FromTypeSyntax(pattern.TargetType, model), model);
                    }
                    else
                    {
                        condition = EmitExpression(context, builder, conditional.Condition, values, storage, model, llvmFunctions);
                    }
                    builder.BuildCondBr(condition, thenBlock, elseBlock);
                    builder.PositionAtEnd(thenBlock);
                    if (conditional.Condition is IsExpression { BindingName: { } binding } boundPattern && patternOperand is not null)
                        ((IDictionary<string, LLVMValueRef>)values)[binding] =
                            EmitPatternBindingValue(builder, patternOperand.Value, model.ExpressionTypes[boundPattern.Operand],
                                FromTypeSyntax(boundPattern.TargetType, model), model);
                    var thenTerminated = EmitStatement(context, builder, function, conditional.Then, values, storage, storageRequirements, model, llvmFunctions, emittedReturnType, loops);
                    if (!thenTerminated) builder.BuildBr(mergeBlock);
                    builder.PositionAtEnd(elseBlock);
                    var elseTerminated = conditional.Else is not null
                        && EmitStatement(context, builder, function, conditional.Else, values, storage, storageRequirements, model, llvmFunctions, emittedReturnType, loops);
                    if (!elseTerminated) builder.BuildBr(mergeBlock);
                    if (thenTerminated && elseTerminated) return true;
                    builder.PositionAtEnd(mergeBlock);
                    return false;
                }
            case SwitchStatement switchStatement:
                {
                    var selectorType = model.ExpressionTypes[switchStatement.Value];
                    var selector = EmitExpression(context, builder, switchStatement.Value, values, storage, model, llvmFunctions);
                    var end = function.AppendBasicBlock("switch.end");
                    var bodies = switchStatement.Cases
                        .Select((_, index) => function.AppendBasicBlock($"switch.body{index}"))
                        .ToArray();
                    var defaultIndex = switchStatement.Cases
                        .Select((item, index) => (item, index))
                        .FirstOrDefault(pair => pair.item.IsDefault).index;
                    var defaultTarget = defaultIndex == 0 && !switchStatement.Cases[0].IsDefault
                        ? end
                        : defaultIndex < switchStatement.Cases.Count ? bodies[defaultIndex] : end;

                    if (switchStatement.Cases.Any(item => item.PatternType is not null))
                    {
                        var dispatch = function.AppendBasicBlock("switch.dispatch");
                        builder.BuildBr(dispatch);
                        builder.PositionAtEnd(dispatch);
                        var next = dispatch;
                        foreach (var (item, index) in switchStatement.Cases.Select((item, index) => (item, index)))
                        {
                            if (item.IsDefault)
                            {
                                builder.BuildBr(bodies[index]);
                                break;
                            }
                            var targetType = FromTypeSyntax(item.PatternType!, model);
                            var match = EmitRuntimeTypeCheck(builder, selector, selectorType, targetType, model);
                            var miss = index + 1 < switchStatement.Cases.Count
                                ? function.AppendBasicBlock($"switch.next{index}")
                                : defaultTarget;
                            builder.BuildCondBr(match, bodies[index], miss);
                            builder.PositionAtEnd(miss);
                            next = miss;
                        }
                    }
                    else if (selectorType.IsUnion)
                    {
                        var union = model.Unions[selectorType.EnumName!];
                        var tag = builder.BuildExtractValue(selector, 0, "union.tag");
                        var dispatch = function.AppendBasicBlock("switch.dispatch");
                        builder.BuildBr(dispatch);
                        builder.PositionAtEnd(dispatch);
                        var next = dispatch;
                        foreach (var (item, index) in switchStatement.Cases.Select((item, index) => (item, index)))
                        {
                            var caseIndex = union.Cases.Select((candidate, candidateIndex) => (candidate, candidateIndex))
                                .First(pair => pair.candidate.Name == item.CaseName).candidateIndex;
                            var match = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, tag,
                                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)caseIndex, false), "union.match");
                            var miss = index + 1 < switchStatement.Cases.Count
                                ? function.AppendBasicBlock($"switch.next{index}")
                                : defaultTarget;
                            builder.BuildCondBr(match, bodies[index], miss);
                            builder.PositionAtEnd(miss);
                        }
                    }
                    else if (IsNativeSwitchType(selectorType))
                    {
                        var nativeSwitch = builder.BuildSwitch(selector, defaultTarget, (uint)switchStatement.Cases.Count);
                        foreach (var (item, index) in switchStatement.Cases.Select((item, index) => (item, index)))
                        {
                            if (item.IsDefault)
                                continue;
                            nativeSwitch.AddCase(EmitSwitchConstant(item.Value!, selectorType, context, builder,
                                values, storage, model, llvmFunctions), bodies[index]);
                        }
                    }
                    else
                    {
                        var next = function.AppendBasicBlock("switch.dispatch");
                        builder.BuildBr(next);
                        foreach (var (item, index) in switchStatement.Cases.Select((item, index) => (item, index)))
                        {
                            if (item.IsDefault)
                                break;
                            builder.PositionAtEnd(next);
                            var label = EmitExpression(context, builder, item.Value!, values, storage, model, llvmFunctions);
                            var match = EmitSwitchEquality(builder, selector, label, selectorType, model);
                            var miss = index + 1 < switchStatement.Cases.Count
                                ? function.AppendBasicBlock($"switch.next{index}")
                                : defaultTarget;
                            builder.BuildCondBr(match, bodies[index], miss);
                            next = miss;
                        }
                    }

                    var terminated = true;
                    loops.Push((end, end, _ownedScopes.Count, false));
                    for (var index = 0; index < bodies.Length; index++)
                    {
                        builder.PositionAtEnd(bodies[index]);
                        var item = switchStatement.Cases[index];
                        if (item.BindingName is not null)
                            ((IDictionary<string, LLVMValueRef>)values)[item.BindingName] = item.PatternType is not null
                                ? EmitPatternBindingValue(builder, selector, selectorType,
                                    FromTypeSyntax(item.PatternType, model), model)
                                : builder.BuildExtractValue(selector, 1, "union.payload");
                        var bodyTerminated = EmitStatement(context, builder, function, item.Body, values, storage,
                            storageRequirements, model, llvmFunctions, emittedReturnType, loops);
                        terminated &= bodyTerminated;
                        if (!bodyTerminated)
                            builder.BuildBr(index + 1 < bodies.Length ? bodies[index + 1] : end);
                    }
                    loops.Pop();
                    builder.PositionAtEnd(end);
                    return terminated;
                }
            case WhileStatement whileStatement:
                {
                    var conditionBlock = function.AppendBasicBlock("while.cond");
                    var bodyBlock = function.AppendBasicBlock("while.body");
                    var endBlock = function.AppendBasicBlock("while.end");
                    builder.BuildBr(conditionBlock);
                    builder.PositionAtEnd(conditionBlock);
                    var condition = EmitExpression(context, builder, whileStatement.Condition, values, storage, model, llvmFunctions);
                    builder.BuildCondBr(condition, bodyBlock, endBlock);
                    builder.PositionAtEnd(bodyBlock);
                    loops.Push((endBlock, conditionBlock, _ownedScopes.Count, true));
                    var bodyTerminated = EmitStatement(context, builder, function, whileStatement.Body, values, storage, storageRequirements, model, llvmFunctions, emittedReturnType, loops);
                    loops.Pop();
                    if (!bodyTerminated) builder.BuildBr(conditionBlock);
                    builder.PositionAtEnd(endBlock);
                    return false;
                }
            case ForInStatement forInStatement:
                {
                    if (model.IteratorProtocols.TryGetValue(forInStatement, out var protocol))
                    {
                        var iteratorName = "__forin_iterator";
                        var protocolCollectionValue = EmitExpression(context, builder, protocol.Collection, values, storage, model, llvmFunctions);
                        var iteratorValue = builder.BuildCall2(llvmFunctions[protocol.Iter].FunctionType,
                            llvmFunctions[protocol.Iter], (ReadOnlySpan<LLVMValueRef>)new[] { protocolCollectionValue },
                            (ReadOnlySpan<char>)"for.in.iter");
                        values[iteratorName] = iteratorValue;
                        var protocolValueStorage = builder.BuildAlloca(ToLlvmType(protocol.ElementType, model),
                            forInStatement.VariableName + ".addr");
                        storage[forInStatement.VariableName] = protocolValueStorage;
                        var protocolConditionBlock = function.AppendBasicBlock("for.in.protocol.cond");
                        var protocolBodyBlock = function.AppendBasicBlock("for.in.protocol.body");
                        var protocolEndBlock = function.AppendBasicBlock("for.in.protocol.end");
                        builder.BuildBr(protocolConditionBlock);
                        builder.PositionAtEnd(protocolConditionBlock);
                        var nextResult = builder.BuildCall2(
                            llvmFunctions[protocol.Next].FunctionType,
                            llvmFunctions[protocol.Next],
                            (ReadOnlySpan<LLVMValueRef>)new[] { iteratorValue }, (ReadOnlySpan<char>)"for.in.next");
                        var tag = builder.BuildExtractValue(nextResult, 0, "for.in.option.tag");
                        var hasValue = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, tag,
                            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false), "for.in.has_value");
                        builder.BuildCondBr(hasValue, protocolBodyBlock, protocolEndBlock);
                        builder.PositionAtEnd(protocolBodyBlock);
                        builder.BuildStore(builder.BuildExtractValue(nextResult, 1, "for.in.option.value"),
                            protocolValueStorage);
                        var protocolScopeDepth = _ownedScopes.Count;
                        _ownedScopes.Push([]);
                        loops.Push((protocolEndBlock, protocolConditionBlock, protocolScopeDepth, true));
                        var protocolBodyTerminated = EmitStatement(context, builder, function, forInStatement.Body,
                            values, storage, storageRequirements, model, llvmFunctions, emittedReturnType, loops);
                        loops.Pop();
                        if (!protocolBodyTerminated) builder.BuildBr(protocolConditionBlock);
                        builder.PositionAtEnd(protocolEndBlock);
                        if (_ownedScopes.Count > protocolScopeDepth)
                        {
                            CleanupOwnedScope(builder, values, storage, model);
                            _ownedScopes.Pop();
                        }
                        storage.Remove(forInStatement.VariableName);
                        values.Remove(iteratorName);
                        return false;
                    }
                    var collectionType = model.ExpressionTypes[forInStatement.Collection];
                    var elementType = collectionType.ElementType!;
                    var collectionStorage = builder.BuildAlloca(ToLlvmType(collectionType, model), "for.in.collection");
                    var collectionValue = EmitExpression(context, builder, forInStatement.Collection, values, storage, model, llvmFunctions);
                    builder.BuildStore(collectionValue, collectionStorage);
                    var counter = builder.BuildAlloca(LLVMTypeRef.Int64, "for.in.index");
                    builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false), counter);
                    var valueStorage = builder.BuildAlloca(ToLlvmType(elementType, model), forInStatement.VariableName + ".addr");
                    var hadPreviousStorage = storage.TryGetValue(forInStatement.VariableName, out var previousStorage);
                    storage[forInStatement.VariableName] = valueStorage;
                    var conditionBlock = function.AppendBasicBlock("for.in.cond");
                    var bodyBlock = function.AppendBasicBlock("for.in.body");
                    var incrementBlock = function.AppendBasicBlock("for.in.increment");
                    var endBlock = function.AppendBasicBlock("for.in.end");
                    builder.BuildBr(conditionBlock);
                    builder.PositionAtEnd(conditionBlock);
                    var currentIndex = builder.BuildLoad2(LLVMTypeRef.Int64, counter, "for.in.index");
                    var length = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)collectionType.ArrayLength, false);
                    var hasNext = builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, currentIndex, length, "for.in.has_next");
                    builder.BuildCondBr(hasNext, bodyBlock, endBlock);
                    builder.PositionAtEnd(bodyBlock);
                    var elementAddress = builder.BuildGEP2(ToLlvmType(collectionType, model), collectionStorage,
                        [LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false), currentIndex],
                        (ReadOnlySpan<char>)"for.in.element");
                    var element = builder.BuildLoad2(ToLlvmType(elementType, model), elementAddress, "for.in.value");
                    builder.BuildStore(element, valueStorage);
                    var forInScopeDepth = _ownedScopes.Count;
                    _ownedScopes.Push([]);
                    loops.Push((endBlock, incrementBlock, forInScopeDepth, true));
                    var bodyTerminated = EmitStatement(context, builder, function, forInStatement.Body, values, storage,
                        storageRequirements, model, llvmFunctions, emittedReturnType, loops);
                    loops.Pop();
                    if (!bodyTerminated) builder.BuildBr(incrementBlock);
                    builder.PositionAtEnd(incrementBlock);
                    currentIndex = builder.BuildLoad2(LLVMTypeRef.Int64, counter, "for.in.index");
                    builder.BuildStore(builder.BuildAdd(currentIndex,
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 1, false), "for.in.next"), counter);
                    builder.BuildBr(conditionBlock);
                    builder.PositionAtEnd(endBlock);
                    if (_ownedScopes.Count > forInScopeDepth)
                    {
                        CleanupOwnedScope(builder, values, storage, model);
                        _ownedScopes.Pop();
                    }
                    if (hadPreviousStorage)
                        storage[forInStatement.VariableName] = previousStorage!;
                    else
                        storage.Remove(forInStatement.VariableName);
                    return false;
                }
            case ForStatement forStatement:
                {
                    if (forStatement.Initializer is not null
                        && EmitStatement(context, builder, function, forStatement.Initializer, values, storage, storageRequirements, model, llvmFunctions, emittedReturnType, loops))
                        return true;
                    var conditionBlock = function.AppendBasicBlock("for.cond");
                    var bodyBlock = function.AppendBasicBlock("for.body");
                    var incrementBlock = function.AppendBasicBlock("for.increment");
                    var endBlock = function.AppendBasicBlock("for.end");
                    builder.BuildBr(conditionBlock);
                    builder.PositionAtEnd(conditionBlock);
                    var condition = forStatement.Condition is null
                        ? LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 1, false)
                        : EmitExpression(context, builder, forStatement.Condition, values, storage, model, llvmFunctions);
                    builder.BuildCondBr(condition, bodyBlock, endBlock);
                    builder.PositionAtEnd(bodyBlock);
                    var forScopeDepth = _ownedScopes.Count;
                    _ownedScopes.Push([]);
                    loops.Push((endBlock, incrementBlock, forScopeDepth, true));
                    var bodyTerminated = EmitStatement(context, builder, function, forStatement.Body, values, storage, storageRequirements, model, llvmFunctions, emittedReturnType, loops);
                    loops.Pop();
                    if (!bodyTerminated) builder.BuildBr(incrementBlock);
                    builder.PositionAtEnd(incrementBlock);
                    var incrementTerminated = forStatement.Increment is not null
                        && EmitStatement(context, builder, function, forStatement.Increment, values, storage, storageRequirements, model, llvmFunctions, emittedReturnType, loops);
                    if (!incrementTerminated) builder.BuildBr(conditionBlock);
                    builder.PositionAtEnd(endBlock);
                    if (_ownedScopes.Count > forScopeDepth)
                    {
                        CleanupOwnedScope(builder, values, storage, model);
                        _ownedScopes.Pop();
                    }
                    return false;
                }
            case BreakStatement:
                CleanupOwnedScopesTo(builder, values, storage, model, loops.Peek().ScopeDepth);
                builder.BuildBr(loops.Peek().Break);
                return true;
            case ContinueStatement:
                var loop = loops.First(item => item.IsLoop);
                CleanupOwnedScopesTo(builder, values, storage, model, loop.ScopeDepth);
                builder.BuildBr(loop.Continue);
                return true;
            case ThrowStatement throwStatement:
                {
                    var thrown = EmitExpression(context, builder, throwStatement.Value, values, storage, model, llvmFunctions);
                    var raw = builder.BuildBitCast(thrown,
                        LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "exception.object");
                    if (_exceptionContexts.Count == 0)
                    {
                        builder.BuildCall2(_exceptionThrow.FunctionType, _exceptionThrow,
                            new[] { raw }, (ReadOnlySpan<char>)string.Empty);
                        builder.BuildUnreachable();
                        return true;
                    }

                    var normal = function.AppendBasicBlock("throw.returned");
                    var exceptionContext = _exceptionContexts.Peek();
                    if (_usesWindowsSeh && exceptionContext.Pad.Handle != IntPtr.Zero)
                    {
                        using var funclet = LLVMOperandBundleRef.Create("funclet", new LLVMValueRef[] { exceptionContext.Pad });
                        builder.BuildInvokeWithOperandBundles(_exceptionThrow.FunctionType, _exceptionThrow,
                            new[] { raw }, normal, exceptionContext.Unwind, new LLVMOperandBundleRef[] { funclet },
                            (ReadOnlySpan<char>)string.Empty);
                    }
                    else
                    {
                        builder.BuildInvoke2(_exceptionThrow.FunctionType, _exceptionThrow,
                            new[] { raw }, normal, exceptionContext.Unwind, (ReadOnlySpan<char>)string.Empty);
                    }
                    builder.PositionAtEnd(normal);
                    builder.BuildUnreachable();
                    return true;
                }
            case TryStatement tryStatement:
                return EmitTryStatement(context, builder, function, tryStatement, values, storage,
                    storageRequirements, model, llvmFunctions, emittedReturnType, loops);
            default:
                throw new InvalidOperationException($"Unsupported statement '{statement.GetType().Name}'.");
        }
    }

    private static bool EmitTryStatement(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMValueRef function,
        TryStatement statement,
        Dictionary<string, LLVMValueRef> values,
        Dictionary<string, LLVMValueRef> storage,
        IReadOnlySet<string> storageRequirements,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions,
        SemanticType emittedReturnType,
        Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)> loops)
    {
        if (_usesWindowsSeh)
            return EmitWindowsSehTryStatement(context, builder, function, statement, values, storage,
                storageRequirements, model, llvmFunctions, emittedReturnType, loops);

        var body = function.AppendBasicBlock("try.body");
        var normal = function.AppendBasicBlock("try.normal");
        var landing = function.AppendBasicBlock("try.landingpad");
        builder.BuildBr(body);
        builder.PositionAtEnd(body);
        var outerExceptionContext = _exceptionContexts.Count == 0 ? null : _exceptionContexts.Peek();
        _exceptionContexts.Push(new ExceptionContext(landing, default));
        var bodyReturned = EmitStatement(context, builder, function, statement.Body, values, storage,
            storageRequirements, model, llvmFunctions, emittedReturnType, loops);
        _exceptionContexts.Pop();
        if (!bodyReturned)
            builder.BuildBr(normal);

        builder.PositionAtEnd(landing);
        if (statement.Finally is not null && outerExceptionContext is not null)
            _exceptionContexts.Push(outerExceptionContext);
        var landingType = LLVMTypeRef.CreateStruct(
            [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.Int32], false);
        var landingPad = builder.BuildLandingPad(landingType, _personality, 1, "exception.landingpad");
        landingPad.AddClause(LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)));
        var exception = builder.BuildExtractValue(landingPad, 0, "exception.handle");
        var exceptionalFinallyReturned = statement.Finally is not null
            && EmitStatement(context, builder, function, statement.Finally, values, storage,
                storageRequirements, model, llvmFunctions, emittedReturnType, loops);
        if (statement.Finally is not null && outerExceptionContext is not null)
            _exceptionContexts.Pop();
        if (exceptionalFinallyReturned)
            return true;
        var objectValue = builder.BuildCall2(_exceptionObject.FunctionType, _exceptionObject,
            new[] { exception }, "exception.object");

        var end = function.AppendBasicBlock("try.end");
        var catchBlocks = statement.Catches.Select((_, index) =>
            function.AppendBasicBlock($"catch.{index}")).ToArray();
        var next = function.AppendBasicBlock("catch.unmatched");
        var dispatchBlocks = statement.Catches
            .Select((_, index) => index == 0 ? landing : function.AppendBasicBlock($"catch.next{index - 1}"))
            .ToArray();
        for (var index = 0; index < statement.Catches.Count; index++)
        {
            builder.PositionAtEnd(dispatchBlocks[index]);
            var clause = statement.Catches[index];
            if (clause.ExceptionType is null)
            {
                builder.BuildBr(catchBlocks[index]);
            }
            else
            {
                var target = FromTypeSyntax(clause.ExceptionType, model);
                var match = EmitExceptionTypeCheck(builder, objectValue, target, model);
                builder.BuildCondBr(match, catchBlocks[index], index + 1 < catchBlocks.Length
                    ? dispatchBlocks[index + 1] : next);
            }
            if (clause.ExceptionType is null)
                break;
        }
        if (statement.Catches.Count == 0)
        {
            builder.PositionAtEnd(landing);
            builder.BuildBr(next);
        }

        builder.PositionAtEnd(next);
        builder.BuildCall2(_exceptionRethrow.FunctionType, _exceptionRethrow,
            Array.Empty<LLVMValueRef>(), (ReadOnlySpan<char>)string.Empty);
        builder.BuildUnreachable();
        for (var index = 0; index < statement.Catches.Count; index++)
        {
            builder.PositionAtEnd(catchBlocks[index]);
            var clause = statement.Catches[index];
            if (clause.BindingName is not null && clause.ExceptionType is not null)
                values[clause.BindingName] = builder.BuildBitCast(objectValue,
                    ToLlvmType(FromTypeSyntax(clause.ExceptionType, model), model), "caught.exception");
            builder.BuildCall2(_exceptionEndCatch.FunctionType, _exceptionEndCatch,
                Array.Empty<LLVMValueRef>(), (ReadOnlySpan<char>)string.Empty);
            if (outerExceptionContext is not null)
                _exceptionContexts.Push(outerExceptionContext);
            var returned = EmitStatement(context, builder, function, clause.Body, values, storage,
                storageRequirements, model, llvmFunctions, emittedReturnType, loops);
            if (outerExceptionContext is not null)
                _exceptionContexts.Pop();
            if (!returned)
                builder.BuildBr(end);
        }

        builder.PositionAtEnd(normal);
        if (statement.Finally is not null && outerExceptionContext is not null)
            _exceptionContexts.Push(outerExceptionContext);
        var normalFinallyReturned = statement.Finally is not null
            && EmitStatement(context, builder, function, statement.Finally, values, storage,
                storageRequirements, model, llvmFunctions, emittedReturnType, loops);
        if (statement.Finally is not null && outerExceptionContext is not null)
            _exceptionContexts.Pop();
        if (!normalFinallyReturned)
            builder.BuildBr(end);
        builder.PositionAtEnd(end);
        return normalFinallyReturned;
    }

    private static LLVMValueRef EmitExceptionTypeCheck(
        LLVMBuilderRef builder, LLVMValueRef objectValue, SemanticType target, SemanticModel model)
    {
        var match = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0, false);
        foreach (var possible in model.Classes.Keys.Where(candidate =>
                     IsClassDerivedFrom(candidate, target.StructName!, model)))
        {
            var candidate = builder.BuildCall2(_isInstance.FunctionType, _isInstance,
                new[] { objectValue, GetTypeName(builder, possible) }, (ReadOnlySpan<char>)"catch.match");
            match = builder.BuildOr(match, candidate, "catch.match");
        }

        return builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, match,
            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0, false), "catch.match");
    }

    private static bool EmitWindowsSehTryStatement(
            LLVMContextRef context,
            LLVMBuilderRef builder,
            LLVMValueRef function,
            TryStatement statement,
            Dictionary<string, LLVMValueRef> values,
            Dictionary<string, LLVMValueRef> storage,
            IReadOnlySet<string> storageRequirements,
            SemanticModel model,
            IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions,
            SemanticType emittedReturnType,
            Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)> loops)
        {
            var body = function.AppendBasicBlock("try.body");
            var normal = function.AppendBasicBlock("try.normal");
            var end = function.AppendBasicBlock("try.end");
            var catchDispatch = function.AppendBasicBlock("try.catchswitch");
            var catchBody = function.AppendBasicBlock("try.catchpad");
            var unmatched = function.AppendBasicBlock("try.unmatched");
            var outer = _exceptionContexts.Count == 0 ? null : _exceptionContexts.Peek();
            var cleanup = statement.Finally is null ? default : function.AppendBasicBlock("try.cleanuppad");

            builder.BuildBr(body);
            builder.PositionAtEnd(body);
            _exceptionContexts.Push(new ExceptionContext(catchDispatch, default));
            var bodyReturned = EmitStatement(context, builder, function, statement.Body, values, storage,
                storageRequirements, model, llvmFunctions, emittedReturnType, loops);
            _exceptionContexts.Pop();
            if (!bodyReturned)
                builder.BuildBr(normal);

            builder.PositionAtEnd(catchDispatch);
            var catchSwitch = builder.BuildCatchSwitch(default,
                outer?.Unwind ?? (statement.Finally is null ? default : cleanup),
                1, "try.catchswitch");
            catchSwitch.AddHandler(catchBody);

            builder.PositionAtEnd(catchBody);
            var catchPad = builder.BuildCatchPad(catchSwitch,
                new LLVMValueRef[]
                {
                    LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)),
                    LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0))
                },
                "try.catchpad");
            _exceptionContexts.Push(new ExceptionContext(
                outer?.Unwind ?? (statement.Finally is null ? default : cleanup), catchPad));
            var exceptionPointer = GetWindowsExceptionPointer(catchPad);
            var nativeException = builder.BuildCall2(
                exceptionPointer.FunctionType, exceptionPointer,
                new LLVMValueRef[] { catchPad }, "native.exception");
            var objectValue = builder.BuildCallWithOperandBundles(
                _exceptionObject.FunctionType, _exceptionObject,
                new LLVMValueRef[] { nativeException },
                new LLVMOperandBundleRef[] { LLVMOperandBundleRef.Create("funclet", new LLVMValueRef[] { catchPad }) },
                "exception.object");
            var catchBlocks = statement.Catches.Select((_, index) =>
                function.AppendBasicBlock($"catch.{index}")).ToArray();
            var next = unmatched;
            var dispatchBlocks = statement.Catches.Select((_, index) =>
                index == 0 ? catchBody : function.AppendBasicBlock($"catch.next{index}")).ToArray();
            for (var index = 0; index < statement.Catches.Count; index++)
            {
                builder.PositionAtEnd(dispatchBlocks[index]);
                var clause = statement.Catches[index];
                if (clause.ExceptionType is null)
                    builder.BuildBr(catchBlocks[index]);
                else
                {
                    var target = FromTypeSyntax(clause.ExceptionType, model);
                    var match = EmitExceptionTypeCheck(builder, objectValue, target, model);
                    builder.BuildCondBr(match, catchBlocks[index],
                        index + 1 < catchBlocks.Length ? dispatchBlocks[index + 1] : next);
                }
                if (clause.ExceptionType is null)
                    break;
            }
            if (statement.Catches.Count == 0)
                builder.BuildBr(unmatched);

            builder.PositionAtEnd(unmatched);
            using (var funclet = LLVMOperandBundleRef.Create("funclet", new LLVMValueRef[] { catchPad }))
                builder.BuildCallWithOperandBundles(_exceptionRethrow.FunctionType, _exceptionRethrow,
                    Array.Empty<LLVMValueRef>(), new LLVMOperandBundleRef[] { funclet });
            builder.BuildUnreachable();

            for (var index = 0; index < statement.Catches.Count; index++)
            {
                builder.PositionAtEnd(catchBlocks[index]);
                var clause = statement.Catches[index];
                if (clause.BindingName is not null && clause.ExceptionType is not null)
                    values[clause.BindingName] = builder.BuildBitCast(objectValue,
                        ToLlvmType(FromTypeSyntax(clause.ExceptionType, model), model), "caught.exception");
                using (var funclet = LLVMOperandBundleRef.Create("funclet", new LLVMValueRef[] { catchPad }))
                    builder.BuildCallWithOperandBundles(_exceptionEndCatch.FunctionType, _exceptionEndCatch,
                        Array.Empty<LLVMValueRef>(), new LLVMOperandBundleRef[] { funclet });
                var returned = EmitStatement(context, builder, function, clause.Body, values, storage,
                    storageRequirements, model, llvmFunctions, emittedReturnType, loops);
                if (!returned)
                    builder.BuildCatchRet(catchPad, end);
            }
            _exceptionContexts.Pop();

            builder.PositionAtEnd(normal);
            var normalFinallyReturned = statement.Finally is not null
                && EmitStatement(context, builder, function, statement.Finally, values, storage,
                    storageRequirements, model, llvmFunctions, emittedReturnType, loops);
            if (!normalFinallyReturned)
                builder.BuildBr(end);

            if (statement.Finally is not null)
            {
                builder.PositionAtEnd(cleanup);
                var cleanupPad = builder.BuildCleanupPad(default, Array.Empty<LLVMValueRef>(), "try.cleanuppad");
                _exceptionContexts.Push(new ExceptionContext(outer?.Unwind ?? default, cleanupPad));
                var cleanupReturned = EmitStatement(context, builder, function, statement.Finally, values, storage,
                    storageRequirements, model, llvmFunctions, emittedReturnType, loops);
                _exceptionContexts.Pop();
                if (!cleanupReturned)
                    builder.BuildCleanupRet(cleanupPad, outer?.Unwind ?? default);
            }
            builder.PositionAtEnd(end);
            return normalFinallyReturned;
    }

}
