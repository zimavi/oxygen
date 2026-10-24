using System.Globalization;
using System.Text.Json;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    private void EmitConstructor(LLVMContextRef context, LLVMBuilderRef builder, LLVMValueRef function,
            ClassSymbol cls, ConstructorSymbol ctor, SemanticModel model,
            IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        _noThrowFunction = IsNoThrowConstructor(ctor, model);
        if (UnwindsHere)
            function.PersonalityFn = _personality;
        var entry = function.AppendBasicBlock("entry");
        builder.PositionAtEnd(entry);
        EmitTracePush(builder, cls.Name, cls.ModuleName, ctor.Declaration.Location);
        var values = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
        var storage = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
        var thisValue = function.GetParam(0);
        values["this"] = thisValue;
        InitializeClassFieldStorage(builder, values, storage, cls, model);
        // Field initializers see the fields only, so they keep this view when a parameter shadows a field.
        var fieldValues = new Dictionary<string, LLVMValueRef>(values, StringComparer.Ordinal);
        var fieldStorage = new Dictionary<string, LLVMValueRef>(storage, StringComparer.Ordinal);
        _ownedScopes = new Stack<List<OwnedLocal>>();
        _temporaryScopes.Clear();
        _ownedScopes.Push([]);
        _exceptionContexts = new Stack<ExceptionContext>();
        _finallyScopes = new Stack<FinallyScope>();
        for (var i = 0; i < ctor.Parameters.Count; i++)
        {
            var parameter = function.GetParam((uint)i + 1);
            DeclareLocalSlot(builder, values, storage, ctor.Parameters[i].Name,
                ToLlvmType(ctor.Parameters[i].Type, model), parameter);
        }
        _currentFunction = function;

        // The chained constructor runs before anything this constructor initializes. `this(...)` delegates all
        // initialization to another constructor of the class; `base(...)` and the implicit base call initialize
        // the inherited part, after which this class's own fields are initialized.
        var delegatesToThis = ctor.Declaration.Initializer?.Kind == ConstructorInitializerKind.This;
        if (model.ConstructorChainTargets.ContainsKey(ctor.Declaration))
            EmitConstructorChain(context, builder, cls, ctor.Declaration, values, storage, model, llvmFunctions);
        if (!delegatesToThis)
        {
            var ownFields = cls.OwnFields.ToArray();
            if (ctor.IsSynthesized)
            {
                foreach (var field in ownFields)
                    builder.BuildStore(CreateZeroValue(field.Type, model), fieldStorage["this." + field.Name]);
            }
            foreach (var field in ownFields)
            {
                if (field.Declaration.Initializer is null)
                    continue;
                var initializer = EmitExpression(context, builder, field.Declaration.Initializer,
                    fieldValues, fieldStorage, model, llvmFunctions);
                builder.BuildStore(CoerceValue(builder, initializer,
                    model.ExpressionTypes[field.Declaration.Initializer], field.Type, model),
                    fieldStorage["this." + field.Name]);
            }
        }
        EmitVoidBody(context, builder, function, ctor.Declaration.Body, values, storage, model, llvmFunctions);
    }

    // Emits the statements of a constructor or destructor body. A return inside it has already emitted the exit, so the
    // trailing return is only added when the body falls through.
    private void EmitVoidBody(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMValueRef function,
        BlockStatement body,
        Dictionary<string, LLVMValueRef> values,
        Dictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        BeginUnwindCleanup(body, model);
        var returned = false;
        foreach (var statement in body.Statements)
        {
            if (EmitStatement(context, builder, function, statement, values, storage, model, llvmFunctions,
                    SemanticType.FromPrimitive(PrimitiveType.Void),
                    new Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)>()))
            {
                returned = true;
                break;
            }
        }
        if (!returned)
        {
            EmitTracePop(builder);
            builder.BuildRetVoid();
        }
        EndUnwindCleanup(model);
    }

    // Calls the constructor that semantic analysis selected for `: this(...)`, `: base(...)`, or the implicit base
    // call. The arguments are evaluated in the constructor's parameter scope.
    private void EmitConstructorChain(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        ClassSymbol cls,
        ConstructorDeclaration declaration,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var targetsThis = declaration.Initializer?.Kind == ConstructorInitializerKind.This;
        var targetClass = targetsThis ? cls : model.Classes[cls.BaseTypeName!];
        var target = targetClass.Constructors[model.ConstructorChainTargets[declaration]];
        var targetFunction = _constructors[ConstructorKey(targetClass, target)];
        IReadOnlyList<Expression> arguments = declaration.Initializer is { } initializer
            ? ArgumentsInParameterOrder(model, initializer, target.Parameters, initializer.Arguments)
            : ArgumentsWithDefaults(target.Parameters, []);
        var args = new List<LLVMValueRef> { values["this"] };
        for (var i = 0; i < arguments.Count; i++)
            args.Add(CoerceValue(builder, EmitExpression(context, builder, arguments[i], values, storage, model, llvmFunctions),
                model.ExpressionTypes[arguments[i]], target.Parameters[i].Type, model));
        builder.BuildCall2(targetFunction.FunctionType, targetFunction, args.ToArray(), (ReadOnlySpan<char>)string.Empty);
    }

    private LLVMValueRef CreateZeroValue(SemanticType type, SemanticModel model)
    {
        if (IsValueClass(type, model))
        {
            var fields = model.Classes[type.StructName!].Fields
                .Select(field => CreateZeroValue(field.Type, model)).ToArray();
            return LLVMValueRef.CreateConstStruct(fields, false);
        }
        if (type.Primitive is PrimitiveType.F32 or PrimitiveType.F64)
            return LLVMValueRef.CreateConstReal(ToLlvmType(type, model), 0);
        if (type.IsArray || type.IsStruct || type.IsUnion || type.Kind == SemanticTypeKind.String)
        {
            var llvmType = ToLlvmType(type, model);
            if (type.IsArray)
            {
                var elementZero = CreateZeroValue(type.ElementType!, model);
                return LLVMValueRef.CreateConstArray(llvmType.ElementType,
                    Enumerable.Repeat(elementZero, type.ArrayLength).ToArray());
            }

            var fields = type.Kind == SemanticTypeKind.String
                ? new[] { CreateZeroValue(SemanticType.PointerTo(SemanticType.FromPrimitive(PrimitiveType.I8)), model),
                    CreateZeroValue(SemanticType.FromPrimitive(PrimitiveType.I64), model) }
                : type.Kind == SemanticTypeKind.Struct
                    ? model.Structs[type.StructName!].Fields.Select(field => CreateZeroValue(field.Type, model)).ToArray()
                    : new[] { CreateZeroValue(SemanticType.FromPrimitive(PrimitiveType.I32), model) }
                        .Concat(UnionPayloadTypes(type, model).Select(payload => CreateZeroValue(payload, model)))
                        .ToArray();
            var packed = type.Kind == SemanticTypeKind.Struct && model.Structs[type.StructName!].IsPacked;
            return LLVMValueRef.CreateConstStruct(fields, packed);
        }
        if (type.IsPointer || type.IsFunctionPointer || type.IsClass || type.IsInterface)
            return LLVMValueRef.CreateConstNull(ToLlvmType(type, model));
        return LLVMValueRef.CreateConstInt(ToLlvmType(type, model), 0, false);
    }

    // Whether a function's body is @nothrow: declared so, or inferred. Such a body is emitted with plain calls and no unwinding.
    private static bool IsNoThrowFunction(FunctionSymbol symbol, SemanticModel model) =>
        symbol.DeclaresNoThrow || model.InferredNoThrow.Contains(symbol.Declaration);

    private static bool IsNoThrowConstructor(ConstructorSymbol constructor, SemanticModel model) =>
        constructor.DeclaresNoThrow || model.InferredNoThrow.Contains(constructor.Declaration);

    private void EmitDestructor(LLVMContextRef context, LLVMBuilderRef builder, LLVMValueRef function,
            ClassSymbol cls, SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        // The flag comes first: a nothrow destructor has no unwinding and no trace frame, as a nothrow function has none.
        _noThrowFunction = model.InferredNoThrow.Contains(cls.Destructor!);
        if (UnwindsHere)
            function.PersonalityFn = _personality;
        var entry = function.AppendBasicBlock("entry");
        builder.PositionAtEnd(entry);
        EmitTracePush(builder, cls.Name, cls.ModuleName, cls.Destructor!.Location);
        _currentFunction = function;
        var values = new Dictionary<string, LLVMValueRef> { ["this"] = function.GetParam(0) };
        var storage = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
        InitializeClassFieldStorage(builder, values, storage, cls, model);
        // Locals destroyed by a return come from this destructor only, not from the function emitted before it.
        _ownedScopes = new Stack<List<OwnedLocal>>();
        _temporaryScopes.Clear();
        _ownedScopes.Push([]);
        _exceptionContexts = new Stack<ExceptionContext>();
        _finallyScopes = new Stack<FinallyScope>();
        EmitVoidBody(context, builder, function, cls.Destructor!.Body, values, storage, model, llvmFunctions);
    }

    // The native main calls the Oxygen main with a handler in every mode. A throw from linked code that was built with exceptions (a
    // library) can reach it even in a program built without exceptions, and then the program is reported and ends (docs/exceptions.md).
    private void EmitNativeMain(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMModuleRef module,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var runtimeInitializeType = LLVMTypeRef.CreateFunction(_context.VoidType, [_context.Int64Type], false);
        var runtimeInitialize = DeclareNativeFunction(module, "__oxygen_runtime_initialize", runtimeInitializeType);
        var argvType = LLVMTypeRef.CreatePointer(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), 0);
        var nativeMainType = LLVMTypeRef.CreateFunction(_context.Int32Type, [_context.Int32Type, argvType], false);
        var nativeMain = DeclareNativeFunction(module, "main", nativeMainType);
        nativeMain.PersonalityFn = _personality;
        nativeMain.GetParam(0).Name = "argc";
        nativeMain.GetParam(1).Name = "argv";

        var entry = nativeMain.AppendBasicBlock("entry");
        builder.PositionAtEnd(entry);
        builder.BuildCall2(runtimeInitializeType, runtimeInitialize,
            [LLVMValueRef.CreateConstInt(_context.Int64Type, _exceptionsEnabled ? ExceptionsEnabledFlag : 0, false)]);

        var oxygenMain = model.MainFunction
            ?? throw new InvalidOperationException("Semantic model does not contain a main function.");
        var oxygenMainType = llvmFunctions[oxygenMain].FunctionType;
        var returnsVoid = oxygenMainType.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind;
        var normal = nativeMain.AppendBasicBlock("oxygen.main.normal");
        var unwind = nativeMain.AppendBasicBlock("oxygen.main.unwind");
        var result = builder.BuildInvoke2(
            oxygenMainType,
            llvmFunctions[oxygenMain],
            Array.Empty<LLVMValueRef>(),
            normal,
            unwind,
            returnsVoid ? string.Empty : "oxygen.main");

        builder.PositionAtEnd(unwind);
        if (_target.UsesSeh)
        {
            var catchBody = nativeMain.AppendBasicBlock("main.catchpad");
            var catchSwitch = builder.BuildCatchSwitch(default, default, 1, "main.catchswitch");
            catchSwitch.AddHandler(catchBody);
            builder.PositionAtEnd(catchBody);
            var catchPad = builder.BuildCatchPad(catchSwitch,
                new LLVMValueRef[]
                {
                    LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(_context.Int8Type, 0)),
                    LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(_context.Int8Type, 0))
                },
                "main.catchpad");
            using var funclet = LLVMOperandBundleRef.Create("funclet", new LLVMValueRef[] { catchPad });
            var exceptionPointer = GetWindowsExceptionPointer(catchPad);
            var nativeException = builder.BuildCall2(
                exceptionPointer.FunctionType, exceptionPointer,
                new LLVMValueRef[] { catchPad }, "native.exception");
            // The runtime takes the native handle, as on POSIX, and extracts the Oxygen object itself. Passing the
            // extracted object here would make the runtime read it as an EXCEPTION_POINTERS and fault.
            builder.BuildCallWithOperandBundles(_exceptionUnhandled.FunctionType, _exceptionUnhandled,
                new LLVMValueRef[] { nativeException }, new LLVMOperandBundleRef[] { funclet });
            builder.BuildUnreachable();
        }
        else
        {
            var landingType = _context.GetStructType(
                [LLVMTypeRef.CreatePointer(_context.Int8Type, 0), _context.Int32Type], false);
            var landingPad = builder.BuildLandingPad(landingType, _personality, 1, "main.landingpad");
            landingPad.AddClause(LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(_context.Int8Type, 0)));
            var exception = builder.BuildExtractValue(landingPad, 0, "main.exception");
            builder.BuildCall2(_exceptionUnhandled.FunctionType, _exceptionUnhandled,
                new[] { exception }, (ReadOnlySpan<char>)string.Empty);
            builder.BuildUnreachable();
        }

        builder.PositionAtEnd(normal);
        if (returnsVoid)
            builder.BuildRet(LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false));
        else
            builder.BuildRet(result);
    }

    private void EmitFunction(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMValueRef function,
        FunctionSymbol symbol,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        _currentFunction = function;
        _noThrowFunction = IsNoThrowFunction(symbol, model);
        if (UnwindsHere)
            function.PersonalityFn = _personality;
        var entry = function.AppendBasicBlock("entry");
        builder.PositionAtEnd(entry);
        EmitTracePush(builder, symbol.Name, symbol.ModuleName, symbol.Declaration.Location);
        var values = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
        var storage = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
        _ownedScopes = new Stack<List<OwnedLocal>>();
        _temporaryScopes.Clear();
        _exceptionContexts = new Stack<ExceptionContext>();
        _finallyScopes = new Stack<FinallyScope>();
        _ownedScopes.Push([]);
        BeginUnwindCleanup(symbol.Declaration.Body, model);
        var parameterOffset = IsClassMethod(symbol, model) ? 1 : 0;
        if (parameterOffset != 0)
        {
            var className = symbol.ModuleName;
            var classSymbol = model.Classes[className];
            values["this"] = classSymbol.IsValue
                ? builder.BuildAlloca(_classTypes[className], "this.addr")
                : function.GetParam(0);
            if (classSymbol.IsValue)
                builder.BuildStore(function.GetParam(0), values["this"]);
            InitializeClassFieldStorage(builder, values, storage, model.Classes[className], model);
        }
        for (var index = 0; index < symbol.Parameters.Count; index++)
        {
            var parameter = function.GetParam((uint)(index + parameterOffset));
            parameter.Name = symbol.Parameters[index].Name;
            DeclareLocalSlot(builder, values, storage, symbol.Parameters[index].Name,
                ToLlvmType(symbol.Parameters[index].Type, model), parameter);
            // A parameter that is a class reference belongs to the caller. Only a value class parameter is a copy the
            // function destroys.
            if (symbol.Parameters[index].Type.IsClass && IsValueClass(symbol.Parameters[index].Type, model))
                _ownedScopes.Peek().Add(new OwnedLocal(null, symbol.Parameters[index].Name, symbol.Parameters[index].Type));
        }

        if (_accessorSymbols.Contains(symbol))
        {
            var owner = model.Classes.Values.First(cls => cls.QualifiedName == symbol.ModuleName);
            var property = owner.DeclaredProperties.First(property =>
                symbol.Name == $"get_{property.Name}" || symbol.Name == $"set_{property.Name}");
            var accessor = property.Declaration.Accessors.First(accessor =>
                symbol.Name == (accessor.Kind == PropertyAccessorKind.Get
                    ? $"get_{property.Name}" : $"set_{property.Name}"));
            if (accessor.IsAuto)
            {
                var address = storage["this." + property.Name];
                if (accessor.Kind == PropertyAccessorKind.Get)
                {
                    EmitTracePop(builder);
                    builder.BuildRet(builder.BuildLoad2(ToLlvmType(property.Type, model), address,
                        "property.get"));
                }
                else
                    builder.BuildStore(function.GetParam(1), address);
                if (accessor.Kind == PropertyAccessorKind.Set)
                {
                    EmitTracePop(builder);
                    builder.BuildRetVoid();
                }
                return;
            }
        }

        var emittedReturnType = symbol.ReturnType;
        var returned = false;
        var loops = new Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)>();
        foreach (var statement in symbol.Declaration.Body.Statements)
        {
            returned = EmitStatement(
                context,
                builder,
                function,
                statement,
                values,
                storage,
                model,
                llvmFunctions,
                emittedReturnType,
                loops);
            if (returned)
                break;
        }

        if (!returned)
        {
            CleanupOwnedScopes(builder, values, storage, model);
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
        EndUnwindCleanup(model);
    }

    // A @nothrow function has no trace frame: nothing in it can throw, so no exception that is reported can have it on its trace.
    private void EmitTracePush(LLVMBuilderRef builder, string functionName, string fileName, SourceLocation location)
    {
        if (!UnwindsHere)
            return;
        var functionNameValue = builder.BuildGlobalStringPtr(functionName, "oxygen.trace.function");
        var fileNameValue = builder.BuildGlobalStringPtr(fileName, "oxygen.trace.file");
        builder.BuildCall2(_tracePush.FunctionType, _tracePush,
            [functionNameValue, fileNameValue,
                LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)Math.Max(0, location.Line), false),
                LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)Math.Max(0, location.Column), false)]);
    }

    private void EmitTracePop(LLVMBuilderRef builder)
    {
        if (UnwindsHere)
            builder.BuildCall2(_tracePop.FunctionType, _tracePop, []);
    }

    private void InitializeClassFieldStorage(
        LLVMBuilderRef builder,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IDictionary<string, LLVMValueRef> storage,
        ClassSymbol cls,
        SemanticModel model)
    {
        var thisValue = values["this"];
        for (var index = 0; index < cls.Fields.Count; index++)
        {
            var fieldAddress = builder.BuildStructGEP2(
                _classTypes[cls.QualifiedName],
                thisValue,
                (uint)FieldOffset(cls, model, index),
                $"field.{cls.Fields[index].Name}");
            storage[cls.Fields[index].Name] = fieldAddress;
            storage["this." + cls.Fields[index].Name] = fieldAddress;
        }
    }

    private void CleanupOwnedScopes(
        LLVMBuilderRef builder,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model)
    {
        CleanupOwnedScopesTo(builder, values, storage, model, 0);
    }

    private void EmitOwnedScopeDestructors(
        LLVMBuilderRef builder,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model)
    {
        var snapshots = _ownedScopes.Select(scope => scope.ToArray()).ToArray();
        foreach (var scope in snapshots)
            CleanupOwnedScope(builder, values, storage, model, scope.ToList());
        var index = 0;
        foreach (var scope in _ownedScopes)
        {
            scope.AddRange(snapshots[index]);
            index++;
        }
    }

    // A jump (break or continue) out of the scopes above a depth. The jump releases their locals on its own path only: the code after the jump
    // is still in those scopes, so the scopes stay as they are.
    private void CleanupOwnedScopesForJump(
        LLVMBuilderRef builder,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        int scopeDepth)
    {
        foreach (var scope in _ownedScopes.Take(_ownedScopes.Count - scopeDepth).ToList())
            CleanupOwnedScope(builder, values, storage, model, scope.ToList());
    }

    private void CleanupOwnedScopesTo(
        LLVMBuilderRef builder,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        int scopeDepth)
    {
        while (_ownedScopes.Count > scopeDepth)
        {
            CleanupOwnedScope(builder, values, storage, model, _ownedScopes.Peek());
            _ownedScopes.Pop();
        }
    }

    private void CleanupOwnedScope(
        LLVMBuilderRef builder,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        List<OwnedLocal>? scope = null)
    {
        scope ??= _ownedScopes.Peek();
        foreach (var owned in scope.AsEnumerable().Reverse())
        {
            if (owned.Catch is not null)
            {
                // A caught exception the catch frees: the runtime lets go of it, then its destructor runs and its memory is freed.
                var caught = values[owned.Name];
                builder.BuildCall2(_releaseCaughtException.FunctionType, _releaseCaughtException, [caught],
                    (ReadOnlySpan<char>)string.Empty);
                EmitDestroyAndFree(builder, caught);
                continue;
            }
            // An owned iterator of a for-in loop is released when the loop's scope ends, on every way out of the loop.
            if (owned.Release)
            {
                var released = values.TryGetValue(owned.Name, out var directReleased)
                    ? directReleased
                    : builder.BuildLoad2(ToLlvmType(owned.Type, model), storage[owned.Name], owned.Name);
                EmitReleaseCounted(builder, OwnedObjectPointer(builder, released, owned.Type));
                continue;
            }
            // The scope has ended on this path, so an exception from here on must not destroy the local again.
            if (owned.Declaration is { } endingDeclaration)
                ClearUnwindFlag(builder, endingDeclaration);
            if (owned.Declaration is { } freedDeclaration
                && (model.AutomaticallyFreedLocals.Contains(freedDeclaration) || model.ReleasedLocals.Contains(freedDeclaration)))
            {
                // An allocation the function owns: its destructor runs, then its memory is released.
                var allocated = OwnedObjectPointer(builder, values.TryGetValue(owned.Name, out var directAllocated)
                    ? directAllocated
                    : builder.BuildLoad2(ToLlvmType(owned.Type, model), storage[owned.Name], owned.Name), owned.Type);
                if (storage.TryGetValue(owned.Name, out var ownedSlot) && _dropFlags.ContainsKey(ownedSlot))
                {
                    // A local that may have been deleted already releases its object only while the object is alive.
                    var scopeRelease = builder.InsertBlock.Parent.AppendBasicBlock("scope.release");
                    var scopeSkip = builder.InsertBlock.Parent.AppendBasicBlock("scope.skip");
                    builder.BuildCondBr(builder.BuildLoad2(_context.Int1Type, _dropFlags[ownedSlot], "scope.alive"),
                        scopeRelease, scopeSkip);
                    builder.PositionAtEnd(scopeRelease);
                    EmitScopeRelease(builder, allocated, model.ReleasedLocals.Contains(freedDeclaration));
                    builder.BuildBr(scopeSkip);
                    builder.PositionAtEnd(scopeSkip);
                }
                else
                {
                    EmitScopeRelease(builder, allocated, model.ReleasedLocals.Contains(freedDeclaration));
                }
                continue;
            }
            if (!owned.Type.IsClass || !_destructors.TryGetValue(owned.Type.StructName!, out var destructor))
                continue;
            if (!IsValueClass(owned.Type, model))
            {
                var reference = values.TryGetValue(owned.Name, out var directReference)
                    ? directReference
                    : builder.BuildLoad2(ToLlvmType(owned.Type, model), storage[owned.Name], owned.Name);
                builder.BuildCall2(destructor.FunctionType, destructor, [reference]);
                continue;
            }
            LLVMValueRef address;
            if (storage.TryGetValue(owned.Name, out var existingAddress))
                address = existingAddress;
            else if (values.TryGetValue(owned.Name, out var direct))
            {
                address = BuildEntryAlloca(builder, ToLlvmType(owned.Type, model), owned.Name + ".cleanup");
                builder.BuildStore(direct, address);
            }
            else
                continue;
            builder.BuildCall2(destructor.FunctionType, destructor, [address]);
        }
        scope.Clear();
    }

    private void RemoveOwned(string name)
    {
        foreach (var scope in _ownedScopes)
            scope.RemoveAll(owned => owned.Name == name);
    }

    private void TransferOwnership(string sourceName, string targetName)
    {
        RemoveOwned(sourceName);
    }

}
