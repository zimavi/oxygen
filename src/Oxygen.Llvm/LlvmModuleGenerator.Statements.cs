using System.Globalization;
using System.Text.Json;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    private LLVMValueRef GetWindowsExceptionPointer(LLVMValueRef catchPad)
    {
        if (_exceptionPointer.Handle == IntPtr.Zero)
        {
            _exceptionPointer = DeclareNativeFunction(_module, 
                "llvm.eh.exceptionpointer.p0",
                LLVMTypeRef.CreateFunction(
                    LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                    [catchPad.TypeOf], false));
        }

        return _exceptionPointer;
    }

    // Sets the runtime flag of a local that some `delete` releases: true while its object is alive.
    private void SetDropFlag(LLVMBuilderRef builder, LLVMValueRef slot, bool alive) =>
        builder.BuildStore(LLVMValueRef.CreateConstInt(_context.Int1Type, alive ? 1UL : 0UL, false), _dropFlags[slot]);

    // growing the stack on every iteration, and mem2reg can promote the slot.
    private LLVMValueRef BuildEntryAlloca(LLVMBuilderRef builder, LLVMTypeRef type, string name)
    {
        var entry = builder.InsertBlock.Parent.EntryBasicBlock;
        using var entryBuilder = _context.CreateBuilder();
        var first = entry.FirstInstruction;
        if (first.Handle == IntPtr.Zero)
            entryBuilder.PositionAtEnd(entry);
        else
            entryBuilder.PositionBefore(first);
        return entryBuilder.BuildAlloca(type, name);
    }

    // Binds a name to a new slot. A value binding of the same name (for example a pattern binding in an enclosing
    // scope) is removed, because values are looked up before storage.
    private LLVMValueRef DeclareLocalSlot(LLVMBuilderRef builder, Dictionary<string, LLVMValueRef> values,
        Dictionary<string, LLVMValueRef> storage, string name, LLVMTypeRef type, LLVMValueRef? initialValue)
    {
        var slot = BuildEntryAlloca(builder, type, name + ".addr");
        if (initialValue is { } value)
            builder.BuildStore(value, slot);
        values.Remove(name);
        storage[name] = slot;
        return slot;
    }

    // Every local and parameter lives in a stack slot (see BuildEntryAlloca), so the only bindings that need
    // scoping are names. Constructs that introduce names restore the previous bindings when they end, which makes
    // an inner declaration shadow an outer one without changing the outer one's storage.
    private bool EmitStatement(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMValueRef function,
        Statement statement,
        Dictionary<string, LLVMValueRef> values,
        Dictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions,
        SemanticType emittedReturnType,
        Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)> loops)
    {
        if (statement is not (BlockStatement or IfStatement or ForStatement or ForInStatement or SwitchStatement
            or TryStatement))
            return EmitStatementBody(context, builder, function, statement, values, storage, model, llvmFunctions,
                emittedReturnType, loops);

        var savedValues = new Dictionary<string, LLVMValueRef>(values, StringComparer.Ordinal);
        var savedStorage = new Dictionary<string, LLVMValueRef>(storage, StringComparer.Ordinal);
        var terminated = EmitStatementBody(context, builder, function, statement, values, storage, model, llvmFunctions,
            emittedReturnType, loops);
        ReplaceBindings(values, savedValues);
        ReplaceBindings(storage, savedStorage);
        return terminated;
    }

    private void ReplaceBindings(Dictionary<string, LLVMValueRef> bindings,
        Dictionary<string, LLVMValueRef> saved)
    {
        bindings.Clear();
        foreach (var (name, value) in saved)
            bindings.Add(name, value);
    }

    private bool EmitStatementBody(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMValueRef function,
        Statement statement,
        Dictionary<string, LLVMValueRef> values,
        Dictionary<string, LLVMValueRef> storage,
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
                LLVMValueRef? declaredValue = null;
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
                    declaredValue = CoerceValue(builder, initializer,
                        model.ExpressionTypes[declaration.Initializer], declaredType, model);
                }
                else
                {
                    // Definite assignment (M2) will reject reads of an unassigned local. Until then the slot holds the zero
                    // value of its type, so the backend never reads undefined stack memory.
                    declaredValue = CreateZeroValue(declaredType, model);
                }
                var declaredSlot = DeclareLocalSlot(builder, values, storage, declaration.Name, ToLlvmType(declaredType, model), declaredValue);
                if (model.DeletedLocals.Contains(declaration))
                {
                    _dropFlags[declaredSlot] = BuildEntryAlloca(builder, _context.Int1Type, declaration.Name + ".alive");
                    SetDropFlag(builder, declaredSlot, declaration.Initializer is NewExpression);
                }
                // A stack object (or a value class) is destroyed at scope end. An object from `new` is not: its owner
                // releases it with `delete`.
                if (declaredType.IsClass && (IsValueClass(declaredType, model) || declaration.Initializer is ClassConstructionExpression
                    || model.AutomaticallyFreedLocals.Contains(declaration)))
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
                {
                    builder.BuildStore(CoerceValue(builder, assigned, model.ExpressionTypes[assignment.Value],
                        model.ExpressionTypes[assignment.Target], model), storage[identifier.Name]);
                    // Assigning a new object makes the local alive again; any other value leaves it not owning an object.
                    if (_dropFlags.ContainsKey(storage[identifier.Name]))
                        SetDropFlag(builder, storage[identifier.Name], assignment.Value is NewExpression);
                }
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
                        var tableEntry = builder.BuildGEP2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), table,
                            [LLVMValueRef.CreateConstInt(_context.Int64Type, (ulong)slot, false)],
                            (ReadOnlySpan<char>)"interface.property.slot");
                        var rawTarget = builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                            tableEntry, "interface.property.call");
                        var setterType = ToLlvmFunctionType(setter, model);
                        EmitCallOrInvoke(builder, setterType, rawTarget,
                            [objectValue, CoerceValue(builder, assigned, model.ExpressionTypes[assignment.Value],
                                setter.Parameters[0].Type, model)], string.Empty);
                        return false;
                    }
                    if (targetType.IsClass)
                    {
                        var cls = model.Classes[targetType.StructName!];
                        var isProperty = cls.DeclaredProperties.Any(candidate => candidate.Name == member.MemberName);
                        if (cls.IsValue && !isProperty)
                        {
                            // A field of a value class is stored into the variable's own value.
                            var fieldIndex = cls.Fields
                                .Select((field, index) => (field, index))
                                .First(pair => pair.field.Name == member.MemberName).index;
                            var coerced = CoerceValue(builder, assigned, model.ExpressionTypes[assignment.Value],
                                model.ExpressionTypes[assignment.Target], model);
                            var current = builder.BuildLoad2(ToLlvmType(targetType, model),
                                storage[targetIdentifier.Name], targetIdentifier.Name);
                            var updated = builder.BuildInsertValue(current, coerced, (uint)fieldIndex, "value.update");
                            builder.BuildStore(updated, storage[targetIdentifier.Name]);
                        }
                        else
                        {
                            var receiver = values.TryGetValue(targetIdentifier.Name, out var direct)
                                ? direct
                                : builder.BuildLoad2(ToLlvmType(targetType, model), storage[targetIdentifier.Name], targetIdentifier.Name);
                            EmitClassMemberStore(builder, cls, receiver, member, assigned,
                                model.ExpressionTypes[assignment.Value], model.ExpressionTypes[assignment.Target],
                                model, llvmFunctions);
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
                    var owner = ThisPropertyOwner(thisMember, model);
                    var thisProperty = owner?.DeclaredProperties.First(property => property.Name == thisMember.MemberName);
                    if (owner is not null && thisProperty is not null)
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
                            setterTarget = builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                                slotAddress, "property.vtable.target");
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
                else if (assignment.Target is MemberAccessExpression nestedMember
                    && model.ExpressionTypes[nestedMember.Target].IsClass
                    && !IsValueClass(model.ExpressionTypes[nestedMember.Target], model))
                {
                    // The object is any expression of class type, such as holder.inner in holder.inner.value = v. It is
                    // evaluated once, then the member is stored as for a variable.
                    var objectType = model.ExpressionTypes[nestedMember.Target];
                    var receiver = EmitExpression(context, builder, nestedMember.Target, values, storage, model, llvmFunctions);
                    EmitClassMemberStore(builder, model.Classes[objectType.StructName!], receiver, nestedMember, assigned,
                        model.ExpressionTypes[assignment.Value], model.ExpressionTypes[assignment.Target], model, llvmFunctions);
                }
                else if (assignment.Target is UnaryExpression { Operator: TokenKind.Star, Operand: var operand })
                    builder.BuildStore(CoerceValue(builder, assigned, model.ExpressionTypes[assignment.Value],
                        model.ExpressionTypes[assignment.Target], model),
                        EmitExpression(context, builder, operand, values, storage, model, llvmFunctions));
                else if (assignment.Target is IndexExpression index)
                    // The value is converted to the element type: a literal keeps its i32 type otherwise, and storing
                    // that into an i8 element would write past it.
                    builder.BuildStore(CoerceValue(builder, assigned, model.ExpressionTypes[assignment.Value],
                            model.ExpressionTypes[assignment.Target], model),
                        EmitArrayAddress(context, builder, index, values, storage, model, llvmFunctions));
                else
                    throw new InvalidOperationException("Unsupported assignment target.");
                return false;
            case ReturnStatement returnStatement:
                LLVMValueRef? returnValue = null;
                if (returnStatement.Value is null)
                {
                    if (EmitExitFinallies(context, builder, function, values, storage, model, llvmFunctions,
                            emittedReturnType, loops, 0))
                        return true;
                    EmitOwnedScopeDestructors(builder, values, storage, model);
                    if (emittedReturnType.IsVoid)
                    {
                        EmitTracePop(builder);
                        builder.BuildRetVoid();
                    }
                    else
                    {
                        EmitTracePop(builder);
                        builder.BuildRet(CreateZeroValue(emittedReturnType, model));
                    }
                }
                else
                {
                    returnValue = CoerceValue(builder,
                        EmitExpression(context, builder, returnStatement.Value, values, storage, model, llvmFunctions),
                        model.ExpressionTypes[returnStatement.Value], emittedReturnType, model);
                    if (EmitExitFinallies(context, builder, function, values, storage, model, llvmFunctions,
                            emittedReturnType, loops, 0))
                        return true;
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
                    var thenTerminated = EmitStatement(context, builder, function, conditional.Then, values, storage, model, llvmFunctions, emittedReturnType, loops);
                    if (!thenTerminated) builder.BuildBr(mergeBlock);
                    builder.PositionAtEnd(elseBlock);
                    var elseTerminated = conditional.Else is not null
                        && EmitStatement(context, builder, function, conditional.Else, values, storage, model, llvmFunctions, emittedReturnType, loops);
                    if (!elseTerminated) builder.BuildBr(mergeBlock);
                    builder.PositionAtEnd(mergeBlock);
                    // Both arms returned: nothing branches to the merge block, so it must still end in a terminator.
                    if (!mergeBlock.AsValue().Uses.Any())
                    {
                        builder.BuildUnreachable();
                        return true;
                    }
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
                        // As for value switches: every type pattern is tested in order, and the last test falls through
                        // to the default target, whatever position `default` has.
                        var patternCases = switchStatement.Cases
                            .Select((item, index) => (item, index))
                            .Where(pair => !pair.item.IsDefault)
                            .ToArray();
                        var dispatch = function.AppendBasicBlock("switch.dispatch");
                        builder.BuildBr(dispatch);
                        var next = dispatch;
                        if (patternCases.Length == 0)
                        {
                            builder.PositionAtEnd(dispatch);
                            builder.BuildBr(defaultTarget);
                        }
                        foreach (var (item, index) in patternCases)
                        {
                            builder.PositionAtEnd(next);
                            var targetType = FromTypeSyntax(item.PatternType!, model);
                            var match = EmitRuntimeTypeCheck(builder, selector, selectorType, targetType, model);
                            var miss = index == patternCases[^1].index
                                ? defaultTarget
                                : function.AppendBasicBlock($"switch.next{index}");
                            builder.BuildCondBr(match, bodies[index], miss);
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
                                LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)caseIndex, false), "union.match");
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
                        // Tests each non-default label in order. The last test falls through to the default target,
                        // so no test block is left without a terminator, and cases after `default` are still tested.
                        var valueCases = switchStatement.Cases
                            .Select((item, index) => (item, index))
                            .Where(pair => !pair.item.IsDefault)
                            .ToArray();
                        var next = function.AppendBasicBlock("switch.dispatch");
                        builder.BuildBr(next);
                        if (valueCases.Length == 0)
                        {
                            builder.PositionAtEnd(next);
                            builder.BuildBr(defaultTarget);
                        }
                        foreach (var (item, index) in valueCases)
                        {
                            builder.PositionAtEnd(next);
                            var label = EmitExpression(context, builder, item.Value!, values, storage, model, llvmFunctions);
                            var match = EmitSwitchEquality(builder, selector, label, selectorType, model);
                            var isLast = index == valueCases[^1].index;
                            var miss = isLast ? defaultTarget : function.AppendBasicBlock($"switch.next{index}");
                            builder.BuildCondBr(match, bodies[index], miss);
                            next = miss;
                        }
                    }

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
                            model, llvmFunctions, emittedReturnType, loops);
                        if (!bodyTerminated)
                            builder.BuildBr(index + 1 < bodies.Length ? bodies[index + 1] : end);
                    }
                    loops.Pop();
                    builder.PositionAtEnd(end);
                    // 'end' is reached by dispatch misses (no default), by 'break', or by falling off the last case.
                    // The switch only terminates the enclosing block when none of those edges exist.
                    if (end.AsValue().Uses.Any())
                        return false;
                    builder.BuildUnreachable();
                    return true;
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
                    var bodyTerminated = EmitStatement(context, builder, function, whileStatement.Body, values, storage, model, llvmFunctions, emittedReturnType, loops);
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
                        var protocolValueStorage = BuildEntryAlloca(builder, ToLlvmType(protocol.ElementType, model),
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
                            LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false), "for.in.has_value");
                        builder.BuildCondBr(hasValue, protocolBodyBlock, protocolEndBlock);
                        builder.PositionAtEnd(protocolBodyBlock);
                        builder.BuildStore(builder.BuildExtractValue(nextResult, 1, "for.in.option.value"),
                            protocolValueStorage);
                        var protocolScopeDepth = _ownedScopes.Count;
                        _ownedScopes.Push([]);
                        loops.Push((protocolEndBlock, protocolConditionBlock, protocolScopeDepth, true));
                        var protocolBodyTerminated = EmitStatement(context, builder, function, forInStatement.Body,
                            values, storage, model, llvmFunctions, emittedReturnType, loops);
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
                    var collectionStorage = BuildEntryAlloca(builder, ToLlvmType(collectionType, model), "for.in.collection");
                    var collectionValue = EmitExpression(context, builder, forInStatement.Collection, values, storage, model, llvmFunctions);
                    builder.BuildStore(collectionValue, collectionStorage);
                    var counter = BuildEntryAlloca(builder, _context.Int64Type, "for.in.index");
                    builder.BuildStore(LLVMValueRef.CreateConstInt(_context.Int64Type, 0, false), counter);
                    var valueStorage = BuildEntryAlloca(builder, ToLlvmType(elementType, model), forInStatement.VariableName + ".addr");
                    var hadPreviousStorage = storage.TryGetValue(forInStatement.VariableName, out var previousStorage);
                    storage[forInStatement.VariableName] = valueStorage;
                    var conditionBlock = function.AppendBasicBlock("for.in.cond");
                    var bodyBlock = function.AppendBasicBlock("for.in.body");
                    var incrementBlock = function.AppendBasicBlock("for.in.increment");
                    var endBlock = function.AppendBasicBlock("for.in.end");
                    builder.BuildBr(conditionBlock);
                    builder.PositionAtEnd(conditionBlock);
                    var currentIndex = builder.BuildLoad2(_context.Int64Type, counter, "for.in.index");
                    var length = LLVMValueRef.CreateConstInt(_context.Int64Type, (ulong)collectionType.ArrayLength, false);
                    var hasNext = builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, currentIndex, length, "for.in.has_next");
                    builder.BuildCondBr(hasNext, bodyBlock, endBlock);
                    builder.PositionAtEnd(bodyBlock);
                    var elementAddress = builder.BuildGEP2(ToLlvmType(collectionType, model), collectionStorage,
                        [LLVMValueRef.CreateConstInt(_context.Int64Type, 0, false), currentIndex],
                        (ReadOnlySpan<char>)"for.in.element");
                    var element = builder.BuildLoad2(ToLlvmType(elementType, model), elementAddress, "for.in.value");
                    builder.BuildStore(element, valueStorage);
                    var forInScopeDepth = _ownedScopes.Count;
                    _ownedScopes.Push([]);
                    loops.Push((endBlock, incrementBlock, forInScopeDepth, true));
                    var bodyTerminated = EmitStatement(context, builder, function, forInStatement.Body, values, storage,
                        model, llvmFunctions, emittedReturnType, loops);
                    loops.Pop();
                    if (!bodyTerminated) builder.BuildBr(incrementBlock);
                    builder.PositionAtEnd(incrementBlock);
                    currentIndex = builder.BuildLoad2(_context.Int64Type, counter, "for.in.index");
                    builder.BuildStore(builder.BuildAdd(currentIndex,
                        LLVMValueRef.CreateConstInt(_context.Int64Type, 1, false), "for.in.next"), counter);
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
                        && EmitStatement(context, builder, function, forStatement.Initializer, values, storage, model, llvmFunctions, emittedReturnType, loops))
                        return true;
                    var conditionBlock = function.AppendBasicBlock("for.cond");
                    var bodyBlock = function.AppendBasicBlock("for.body");
                    var incrementBlock = function.AppendBasicBlock("for.increment");
                    var endBlock = function.AppendBasicBlock("for.end");
                    builder.BuildBr(conditionBlock);
                    builder.PositionAtEnd(conditionBlock);
                    var condition = forStatement.Condition is null
                        ? LLVMValueRef.CreateConstInt(_context.Int1Type, 1, false)
                        : EmitExpression(context, builder, forStatement.Condition, values, storage, model, llvmFunctions);
                    builder.BuildCondBr(condition, bodyBlock, endBlock);
                    builder.PositionAtEnd(bodyBlock);
                    var forScopeDepth = _ownedScopes.Count;
                    _ownedScopes.Push([]);
                    loops.Push((endBlock, incrementBlock, forScopeDepth, true));
                    var bodyTerminated = EmitStatement(context, builder, function, forStatement.Body, values, storage, model, llvmFunctions, emittedReturnType, loops);
                    loops.Pop();
                    if (!bodyTerminated) builder.BuildBr(incrementBlock);
                    builder.PositionAtEnd(incrementBlock);
                    var incrementTerminated = forStatement.Increment is not null
                        && EmitStatement(context, builder, function, forStatement.Increment, values, storage, model, llvmFunctions, emittedReturnType, loops);
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
                if (EmitExitFinallies(context, builder, function, values, storage, model, llvmFunctions,
                        emittedReturnType, loops, TargetLoopDepth(loops, isContinue: false)))
                    return true;
                CleanupOwnedScopesTo(builder, values, storage, model, loops.Peek().ScopeDepth);
                builder.BuildBr(loops.Peek().Break);
                return true;
            case ContinueStatement:
                var loop = loops.First(item => item.IsLoop);
                if (EmitExitFinallies(context, builder, function, values, storage, model, llvmFunctions,
                        emittedReturnType, loops, TargetLoopDepth(loops, isContinue: true)))
                    return true;
                CleanupOwnedScopesTo(builder, values, storage, model, loop.ScopeDepth);
                builder.BuildBr(loop.Continue);
                return true;
            case DeleteStatement deleteStatement:
                {
                    var target = EmitExpression(context, builder, deleteStatement.Target, values, storage, model, llvmFunctions);
                    if (deleteStatement.Target is IdentifierExpression deleted && storage.TryGetValue(deleted.Name, out var deletedSlot)
                        && _dropFlags.ContainsKey(deletedSlot))
                    {
                        // A delete that may run after the object was already deleted only releases a live object.
                        if (model.RuntimeCheckedDeletes.Contains(deleteStatement))
                        {
                            var deleteRelease = function.AppendBasicBlock("delete.release");
                            var deleteSkip = function.AppendBasicBlock("delete.skip");
                            builder.BuildCondBr(builder.BuildLoad2(_context.Int1Type, _dropFlags[deletedSlot], "delete.alive"),
                                deleteRelease, deleteSkip);
                            builder.PositionAtEnd(deleteRelease);
                            EmitDestroyAndFree(builder, target);
                            builder.BuildBr(deleteSkip);
                            builder.PositionAtEnd(deleteSkip);
                        }
                        else
                        {
                            EmitDestroyAndFree(builder, target);
                        }
                        // The flag, not the owned list, tracks whether the object is still alive: a delete on one path
                        // must not stop the scope end from freeing it on the other.
                        SetDropFlag(builder, deletedSlot, false);
                    }
                    else
                    {
                        // The object is released through its runtime type, so the destructor of its most derived class runs.
                        EmitDestroyAndFree(builder, target);
                        if (deleteStatement.Target is IdentifierExpression released)
                            RemoveOwned(released.Name);
                    }
                    return false;
                }
            case ThrowStatement throwStatement:
                {
                    var thrown = EmitExpression(context, builder, throwStatement.Value, values, storage, model, llvmFunctions);
                    EmitThrowObject(builder, thrown);
                    return true;
                }
            case TryStatement tryStatement:
                return EmitTryStatement(context, builder, function, tryStatement, values, storage,
                    model, llvmFunctions, emittedReturnType, loops);
            default:
                throw new InvalidOperationException($"Unsupported statement '{statement.GetType().Name}'.");
        }
    }

    private bool EmitTryStatement(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMValueRef function,
        TryStatement statement,
        Dictionary<string, LLVMValueRef> values,
        Dictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions,
        SemanticType emittedReturnType,
        Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)> loops)
    {
        if (_usesWindowsSeh)
            return EmitWindowsSehTryStatement(context, builder, function, statement, values, storage,
                model, llvmFunctions, emittedReturnType, loops);

        var body = function.AppendBasicBlock("try.body");
        var normal = function.AppendBasicBlock("try.normal");
        var landing = function.AppendBasicBlock("try.landingpad");
        builder.BuildBr(body);
        builder.PositionAtEnd(body);
        var outerExceptionContext = _exceptionContexts.Count == 0 ? null : _exceptionContexts.Peek();
        _exceptionContexts.Push(new ExceptionContext(landing, default));
        var bodyReturned = EmitStatement(context, builder, function, statement.Body, values, storage,
            model, llvmFunctions, emittedReturnType, loops);
        _exceptionContexts.Pop();
        if (!bodyReturned)
            builder.BuildBr(normal);

        builder.PositionAtEnd(landing);
        if (statement.Finally is not null && outerExceptionContext is not null)
            _exceptionContexts.Push(outerExceptionContext);
        var landingType = _context.GetStructType(
            [LLVMTypeRef.CreatePointer(_context.Int8Type, 0), _context.Int32Type], false);
        var landingPad = builder.BuildLandingPad(landingType, _personality, 1, "exception.landingpad");
        landingPad.AddClause(LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(_context.Int8Type, 0)));
        var exception = builder.BuildExtractValue(landingPad, 0, "exception.handle");
        var exceptionalFinallyReturned = statement.Finally is not null
            && EmitStatement(context, builder, function, statement.Finally, values, storage,
                model, llvmFunctions, emittedReturnType, loops);
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
                model, llvmFunctions, emittedReturnType, loops);
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
                model, llvmFunctions, emittedReturnType, loops);
        if (statement.Finally is not null && outerExceptionContext is not null)
            _exceptionContexts.Pop();
        if (!normalFinallyReturned)
            builder.BuildBr(end);
        builder.PositionAtEnd(end);
        return normalFinallyReturned;
    }

    private LLVMValueRef EmitExceptionTypeCheck(
        LLVMBuilderRef builder, LLVMValueRef objectValue, SemanticType target, SemanticModel model)
    {
        var match = LLVMValueRef.CreateConstInt(_context.Int8Type, 0, false);
        foreach (var possible in model.Classes.Keys.Where(candidate => !model.Classes[candidate].IsGeneric).Where(candidate =>
                     IsClassDerivedFrom(candidate, target.StructName!, model)))
        {
            var candidate = BuildNoThrowCall(builder, _isInstance, new[] { objectValue, GetTypeName(builder, possible) }, "catch.match");
            match = builder.BuildOr(match, candidate, "catch.match");
        }

        return builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, match,
            LLVMValueRef.CreateConstInt(_context.Int8Type, 0, false), "catch.match");
    }


}
