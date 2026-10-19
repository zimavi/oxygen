using System.Globalization;
using System.Text.Json;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    private static void EmitConstructor(LLVMContextRef context, LLVMBuilderRef builder, LLVMValueRef function,
            ClassSymbol cls, ConstructorSymbol ctor, SemanticModel model,
            IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        if (_usesExceptions)
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
            var ownFields = cls.Fields.Where(field => cls.DeclaresField(field.Name)).ToArray();
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
    private static void EmitVoidBody(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMValueRef function,
        BlockStatement body,
        Dictionary<string, LLVMValueRef> values,
        Dictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        foreach (var statement in body.Statements)
        {
            if (EmitStatement(context, builder, function, statement, values, storage, model, llvmFunctions,
                    SemanticType.FromPrimitive(PrimitiveType.Void),
                    new Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)>()))
                return;
        }
        EmitTracePop(builder);
        builder.BuildRetVoid();
    }

    // Calls the constructor that semantic analysis selected for `: this(...)`, `: base(...)`, or the implicit base
    // call. The arguments are evaluated in the constructor's parameter scope.
    private static void EmitConstructorChain(
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
        var arguments = declaration.Initializer?.Arguments ?? [];
        var args = new List<LLVMValueRef> { values["this"] };
        for (var i = 0; i < arguments.Count; i++)
            args.Add(CoerceValue(builder, EmitExpression(context, builder, arguments[i], values, storage, model, llvmFunctions),
                model.ExpressionTypes[arguments[i]], target.Parameters[i].Type, model));
        builder.BuildCall2(targetFunction.FunctionType, targetFunction, args.ToArray(), (ReadOnlySpan<char>)string.Empty);
    }

    private static LLVMValueRef CreateZeroValue(SemanticType type, SemanticModel model)
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
                    : new[]
                    {
                        CreateZeroValue(SemanticType.FromPrimitive(PrimitiveType.I32), model),
                        CreateZeroValue(UnionPayloadType(type, model), model)
                    };
            var packed = type.Kind == SemanticTypeKind.Struct && model.Structs[type.StructName!].IsPacked;
            return LLVMValueRef.CreateConstStruct(fields, packed);
        }
        if (type.IsPointer || type.IsClass || type.IsInterface)
            return LLVMValueRef.CreateConstNull(ToLlvmType(type, model));
        return LLVMValueRef.CreateConstInt(ToLlvmType(type, model), 0, false);
    }

    private static void EmitDestructor(LLVMContextRef context, LLVMBuilderRef builder, LLVMValueRef function,
            ClassSymbol cls, SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        if (_usesExceptions)
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
        _ownedScopes.Push([]);
        _exceptionContexts = new Stack<ExceptionContext>();
        _finallyScopes = new Stack<FinallyScope>();
        EmitVoidBody(context, builder, function, cls.Destructor!.Body, values, storage, model, llvmFunctions);
    }

    private static void EmitNativeMain(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMModuleRef module,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var runtimeInitializeType = LLVMTypeRef.CreateFunction(_context.VoidType, [], false);
        var runtimeInitialize = module.AddFunction("__oxygen_runtime_initialize", runtimeInitializeType);
        var argvType = LLVMTypeRef.CreatePointer(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), 0);
        var nativeMainType = LLVMTypeRef.CreateFunction(_context.Int32Type, [_context.Int32Type, argvType], false);
        var nativeMain = module.AddFunction("main", nativeMainType);
        if (_usesExceptions)
            nativeMain.PersonalityFn = _personality;
        nativeMain.GetParam(0).Name = "argc";
        nativeMain.GetParam(1).Name = "argv";

        var entry = nativeMain.AppendBasicBlock("entry");
        builder.PositionAtEnd(entry);
        builder.BuildCall2(runtimeInitializeType, runtimeInitialize, []);
        var registerType = module.AddFunction(
            "__oxygen_runtime_register_type",
            LLVMTypeRef.CreateFunction(_context.VoidType,
                [LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                    LLVMTypeRef.CreatePointer(_context.Int8Type, 0)], false));
        foreach (var cls in model.Classes.Values.Where(cls => !cls.IsGeneric))
        {
            InitializeRuntimeTypeInfo(builder, cls.Name, cls.BaseTypeName, 1, model);
        }
        foreach (var interfaceSymbol in model.Interfaces.Values.Where(iface => !iface.IsGeneric))
        {
            InitializeRuntimeTypeInfo(builder, interfaceSymbol.Name, null, 2, model);
        }
        foreach (var cls in model.Classes.Values.Where(cls => !cls.IsGeneric))
        {
            if (cls.BaseTypeName is not null)
                RegisterRuntimeType(builder, registerType, cls.Name, cls.BaseTypeName);
            foreach (var interfaceName in cls.InterfaceNames)
                RegisterRuntimeType(builder, registerType, cls.Name, interfaceName);
        }

        var oxygenMain = model.MainFunction
            ?? throw new InvalidOperationException("Semantic model does not contain a main function.");
        var oxygenMainType = llvmFunctions[oxygenMain].FunctionType;
        LLVMValueRef result;
        if (_usesExceptions)
        {
            var normal = nativeMain.AppendBasicBlock("oxygen.main.normal");
            var unwind = nativeMain.AppendBasicBlock("oxygen.main.unwind");
            if (_usesWindowsSeh)
            {
                result = builder.BuildInvoke2(oxygenMainType, llvmFunctions[oxygenMain],
                    Array.Empty<LLVMValueRef>(), normal, unwind,
                    oxygenMainType.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind ? string.Empty : "oxygen.main");
                builder.PositionAtEnd(unwind);
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
                result = builder.BuildInvoke2(
                    oxygenMainType,
                    llvmFunctions[oxygenMain],
                    Array.Empty<LLVMValueRef>(),
                    normal,
                    unwind,
                    oxygenMainType.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind ? string.Empty : "oxygen.main");
                builder.PositionAtEnd(unwind);
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
        }
        else
        {
            result = builder.BuildCall2(
                oxygenMainType, llvmFunctions[oxygenMain], Array.Empty<LLVMValueRef>(),
                oxygenMainType.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind ? string.Empty : "oxygen.main");
        }
        if (oxygenMainType.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind)
            builder.BuildRet(LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false));
        else
            builder.BuildRet(result);
    }

    private static void RegisterRuntimeType(
        LLVMBuilderRef builder,
        LLVMValueRef registerType,
        string actualType,
        string parentType)
    {
        builder.BuildCall2(registerType.FunctionType, registerType,
            [GetTypeName(builder, actualType), GetTypeName(builder, parentType)]);
    }

    private static void EmitFunction(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMValueRef function,
        FunctionSymbol symbol,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        _currentFunction = function;
        if (_usesExceptions)
            function.PersonalityFn = _personality;
        var entry = function.AppendBasicBlock("entry");
        builder.PositionAtEnd(entry);
        EmitTracePush(builder, symbol.Name, symbol.ModuleName, symbol.Declaration.Location);
        var values = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
        var storage = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
        _ownedScopes = new Stack<List<OwnedLocal>>();
        _exceptionContexts = new Stack<ExceptionContext>();
        _finallyScopes = new Stack<FinallyScope>();
        _ownedScopes.Push([]);
        var parameterOffset = IsClassMethod(symbol, model) ? 1 : 0;
        if (parameterOffset != 0)
        {
            var className = symbol.ModuleName[(symbol.ModuleName.LastIndexOf('.') + 1)..];
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
            var owner = model.Classes.Values.First(cls => cls.Name == symbol.ModuleName[(symbol.ModuleName.LastIndexOf('.') + 1)..]);
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
    }

    private static void EmitTracePush(LLVMBuilderRef builder, string functionName, string fileName, SourceLocation location)
    {
        if (!_usesExceptions)
            return;
        var functionNameValue = builder.BuildGlobalStringPtr(functionName, "oxygen.trace.function");
        var fileNameValue = builder.BuildGlobalStringPtr(fileName, "oxygen.trace.file");
        builder.BuildCall2(_tracePush.FunctionType, _tracePush,
            [functionNameValue, fileNameValue,
                LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)Math.Max(0, location.Line), false),
                LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)Math.Max(0, location.Column), false)]);
    }

    private static void EmitTracePop(LLVMBuilderRef builder)
    {
        if (_usesExceptions)
            builder.BuildCall2(_tracePop.FunctionType, _tracePop, []);
    }

    private static void InitializeClassFieldStorage(
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
                _classTypes[cls.Name],
                thisValue,
                (uint)FieldOffset(cls, model, index),
                $"field.{cls.Fields[index].Name}");
            storage[cls.Fields[index].Name] = fieldAddress;
            storage["this." + cls.Fields[index].Name] = fieldAddress;
        }
    }

    private static void CleanupOwnedScopes(
        LLVMBuilderRef builder,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model)
    {
        CleanupOwnedScopesTo(builder, values, storage, model, 0);
    }

    private static void EmitOwnedScopeDestructors(
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

    private static void CleanupOwnedScopesTo(
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

    private static void CleanupOwnedScope(
        LLVMBuilderRef builder,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        List<OwnedLocal>? scope = null)
    {
        scope ??= _ownedScopes.Peek();
        foreach (var owned in scope.AsEnumerable().Reverse())
        {
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
                address = builder.BuildAlloca(ToLlvmType(owned.Type, model), owned.Name + ".cleanup");
                builder.BuildStore(direct, address);
            }
            else
                continue;
            builder.BuildCall2(destructor.FunctionType, destructor, [address]);
        }
        scope.Clear();
    }

    private static void RemoveOwned(string name)
    {
        foreach (var scope in _ownedScopes)
            scope.RemoveAll(owned => owned.Name == name);
    }

    private static void TransferOwnership(string sourceName, string targetName)
    {
        RemoveOwned(sourceName);
    }

}
