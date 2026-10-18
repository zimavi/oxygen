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
        InitializeExceptionStackTrace(builder, thisValue, storage, cls, model, llvmFunctions);
        if (ctor.IsSynthesized)
        {
            foreach (var field in cls.Fields)
                builder.BuildStore(CreateZeroValue(field.Type, model), storage["this." + field.Name]);
        }
        foreach (var field in cls.Fields)
        {
            if (field.Declaration.Initializer is null)
                continue;
            var initializer = EmitExpression(context, builder, field.Declaration.Initializer,
                values, storage, model, llvmFunctions);
            builder.BuildStore(CoerceValue(builder, initializer,
                model.ExpressionTypes[field.Declaration.Initializer], field.Type, model),
                storage["this." + field.Name]);
        }
        var storageRequirements = CollectStorageRequirements(ctor.Declaration.Body);
        for (var i = 0; i < ctor.Parameters.Count; i++)
        {
            var parameter = function.GetParam((uint)i + 1);
            if (storageRequirements.Contains(ctor.Parameters[i].Name))
            {
                var parameterStorage = builder.BuildAlloca(
                    ToLlvmType(ctor.Parameters[i].Type, model),
                    ctor.Parameters[i].Name + ".addr");
                builder.BuildStore(parameter, parameterStorage);
                storage[ctor.Parameters[i].Name] = parameterStorage;
            }
            else
            {
                values[ctor.Parameters[i].Name] = parameter;
            }
        }
        _currentFunction = function;
        foreach (var statement in ctor.Declaration.Body.Statements)
            EmitStatement(context, builder, function, statement, values, storage, storageRequirements,
                model, llvmFunctions, SemanticType.FromPrimitive(PrimitiveType.Void),
                new Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)>());
        EmitTracePop(builder);
        builder.BuildRetVoid();
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
        foreach (var statement in cls.Destructor!.Body.Statements)
            EmitStatement(context, builder, function, statement, values, storage,
                new HashSet<string>(), model, llvmFunctions, SemanticType.FromPrimitive(PrimitiveType.Void),
                new Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)>());
        EmitTracePop(builder);
        builder.BuildRetVoid();
    }

    private static void EmitNativeMain(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        LLVMModuleRef module,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var runtimeInitializeType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [], false);
        var runtimeInitialize = module.AddFunction("__oxygen_runtime_initialize", runtimeInitializeType);
        var argvType = LLVMTypeRef.CreatePointer(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), 0);
        var nativeMainType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [LLVMTypeRef.Int32, argvType], false);
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
            LLVMTypeRef.CreateFunction(LLVMTypeRef.Void,
                [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                    LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)], false));
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
                        LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)),
                        LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0))
                    },
                    "main.catchpad");
                using var funclet = LLVMOperandBundleRef.Create("funclet", new LLVMValueRef[] { catchPad });
                var exceptionPointer = GetWindowsExceptionPointer(catchPad);
                var nativeException = builder.BuildCall2(
                    exceptionPointer.FunctionType, exceptionPointer,
                    new LLVMValueRef[] { catchPad }, "native.exception");
                var exception = builder.BuildCallWithOperandBundles(
                    _exceptionObject.FunctionType, _exceptionObject,
                    new LLVMValueRef[] { nativeException },
                    new LLVMOperandBundleRef[] { funclet }, "main.exception");
                builder.BuildCallWithOperandBundles(_exceptionUnhandled.FunctionType, _exceptionUnhandled,
                    new LLVMValueRef[] { exception }, new LLVMOperandBundleRef[] { funclet });
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
                var landingType = LLVMTypeRef.CreateStruct(
                    [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.Int32], false);
                var landingPad = builder.BuildLandingPad(landingType, _personality, 1, "main.landingpad");
                landingPad.AddClause(LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)));
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
            builder.BuildRet(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false));
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
        _ownedScopes.Push([]);
        var storageRequirements = CollectStorageRequirements(symbol.Declaration.Body);
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
            if (storageRequirements.Contains(symbol.Parameters[index].Name))
            {
                var parameterStorage = builder.BuildAlloca(
                    ToLlvmType(symbol.Parameters[index].Type, model),
                    parameter.Name + ".addr");
                builder.BuildStore(parameter, parameterStorage);
                storage[symbol.Parameters[index].Name] = parameterStorage;
            }
            else
            {
                values.Add(symbol.Parameters[index].Name, parameter);
            }
            if (symbol.Parameters[index].Type.IsClass)
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
                    builder.BuildStore(values["value"], address);
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
                storageRequirements,
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
                builder.BuildRet(LLVMValueRef.CreateConstInt(ToLlvmType(emittedReturnType, model), 0, false));
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
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)Math.Max(0, location.Line), false),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)Math.Max(0, location.Column), false)]);
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

    private static void InitializeExceptionStackTrace(
        LLVMBuilderRef builder,
        LLVMValueRef thisValue,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        ClassSymbol cls,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        if (!model.Classes.ContainsKey("Exception")
            || !model.Classes.ContainsKey("StackTrace")
            || !IsClassDerivedFrom(cls.Name, "Exception", model)
            || !storage.TryGetValue("this.stack_trace", out var fieldAddress))
            return;

        var stackTraceClass = model.Classes["StackTrace"];
        var constructor = stackTraceClass.Constructors.FirstOrDefault(candidate =>
            candidate.Parameters.Count == 1
            && candidate.Parameters[0].Type.IsClass
            && candidate.Parameters[0].Type.StructName == "Exception");
        if (constructor is null)
            return;

        var instance = builder.BuildAlloca(_classTypes["StackTrace"], "exception.stack_trace");
        var vtable = builder.BuildStructGEP2(_classTypes["StackTrace"], instance, 0, "exception.stack_trace.vtable");
        builder.BuildStore(GetTypeName(builder, "StackTrace"), vtable);
        var virtualMethods = GetVirtualMethods(stackTraceClass, model);
        for (var slot = 0; slot < _virtualSlotCount; slot++)
        {
            var slotAddress = builder.BuildStructGEP2(
                _classTypes["StackTrace"], instance, (uint)slot + 1, "exception.stack_trace.vtable.slot");
            var target = slot < virtualMethods.Count
                ? builder.BuildBitCast(llvmFunctions[virtualMethods[slot]],
                    LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "exception.stack_trace.method")
                : LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0));
            builder.BuildStore(target, slotAddress);
        }

        var exceptionValue = CoerceValue(builder, thisValue, SemanticType.Class(cls.Name),
            SemanticType.Class("Exception"), model);
        builder.BuildCall2(
            _constructors[ConstructorKey(stackTraceClass, constructor)].FunctionType,
            _constructors[ConstructorKey(stackTraceClass, constructor)],
            [instance, exceptionValue]);
        builder.BuildStore(instance, fieldAddress);
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
