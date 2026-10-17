using System.Globalization;
using System.Text.Json;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed class LlvmModuleGenerator
{
    private static LLVMValueRef _arrayBoundsFailure;
    private static LLVMValueRef _enumCastFailure;
    private static LLVMValueRef _isInstance;
    private static LLVMValueRef _invalidCast;
    private static LLVMValueRef _currentFunction;
    private static readonly Dictionary<string, LLVMTypeRef> _classTypes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LLVMValueRef> _runtimeTypeInfos = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LLVMValueRef> _constructors = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LLVMValueRef> _destructors = new(StringComparer.Ordinal);
    private static int _virtualSlotCount;
    private sealed record OwnedLocal(VariableDeclarationStatement? Declaration, string Name, SemanticType Type);
    private static Stack<List<OwnedLocal>> _ownedScopes = new();
    private static LLVMValueRef _oxygenAlloc;
    private static LLVMValueRef _oxygenFree;
    private static LLVMValueRef _memcmp;
    private static LLVMTypeRef _runtimeTypeInfoType;
    private static LLVMValueRef _exceptionThrow;
    private static LLVMValueRef _exceptionObject;
    private static LLVMValueRef _exceptionEndCatch;
    private static LLVMValueRef _exceptionRethrow;
    private static LLVMValueRef _exceptionUnhandled;
    private static LLVMValueRef _personality;
    private sealed record ExceptionContext(LLVMBasicBlockRef Unwind);
    private static Stack<ExceptionContext> _exceptionContexts = new();
    private static LLVMTypeRef ToLlvmStringType() =>
        LLVMTypeRef.CreateStruct([LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.Int64], false);

    public GeneratedLlvmModule Generate(string moduleName, SemanticModel model)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        ArgumentNullException.ThrowIfNull(model);

        var context = LLVMContextRef.Create();
        var module = context.CreateModuleWithName(moduleName);
        var builder = context.CreateBuilder();
        var symbols = model.Functions.Values
            .Concat(model.ResolvedCalls.Values)
            .Where(symbol => !symbol.IsGeneric)
            .Distinct()
            .ToList();
        var llvmFunctions = new Dictionary<FunctionSymbol, LLVMValueRef>();

        try
        {
            _classTypes.Clear();
            _runtimeTypeInfos.Clear();
            _runtimeTypeInfoType = LLVMTypeRef.CreateStruct(
                [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.Int32,
                    LLVMTypeRef.Int32, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)], false);
            _constructors.Clear();
            _destructors.Clear();
            _virtualSlotCount = model.Classes.Values.Select(cls => GetVirtualMethods(cls, model).Count)
                .DefaultIfEmpty().Max();
            foreach (var cls in model.Classes.Values)
            {
                var fields = (cls.IsValue
                        ? Enumerable.Empty<LLVMTypeRef>()
                        : new[] { LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0) }
                            .Concat(Enumerable.Repeat(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), _virtualSlotCount)))
                    .Concat(cls.Fields.Select(field => ToLlvmType(field.Type, model))).ToArray();
                _classTypes[cls.Name] = LLVMTypeRef.CreateStruct(fields, false);
            }
            foreach (var cls in model.Classes.Values)
                _runtimeTypeInfos[cls.Name] = AddRuntimeTypeInfoGlobal(module, cls.Name);
            foreach (var interfaceSymbol in model.Interfaces.Values)
                _runtimeTypeInfos[interfaceSymbol.Name] = AddRuntimeTypeInfoGlobal(module, interfaceSymbol.Name);
            foreach (var symbol in symbols)
            {
                var functionType = ToLlvmFunctionType(symbol, model);
                llvmFunctions[symbol] = module.AddFunction(OxygenNameMangler.Mangle(symbol.ModuleName, symbol), functionType);
            }
            var allocType = LLVMTypeRef.CreateFunction(
                LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), [LLVMTypeRef.Int64], false);
            _oxygenAlloc = GetOrDeclareRuntimeFunction(
                module, "oxygen_alloc", allocType, llvmFunctions);
            var freeType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void,
                [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)], false);
            _oxygenFree = GetOrDeclareRuntimeFunction(
                module, "oxygen_free", freeType, llvmFunctions);
            _memcmp = module.AddFunction("memcmp",
                LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32,
                    [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                        LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.Int64], false));
            _personality = module.AddFunction("__gxx_personality_v0",
                LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32,
                    [LLVMTypeRef.Int32, LLVMTypeRef.Int32, LLVMTypeRef.Int64,
                        LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                        LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)], false));
            _exceptionThrow = module.AddFunction("__oxygen_runtime_throw_object",
                LLVMTypeRef.CreateFunction(LLVMTypeRef.Void,
                    [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)], false));
            _exceptionObject = module.AddFunction("__oxygen_runtime_exception_object",
                LLVMTypeRef.CreateFunction(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                    [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)], false));
            _exceptionEndCatch = module.AddFunction("__oxygen_runtime_end_catch",
                LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [], false));
            _exceptionRethrow = module.AddFunction("__oxygen_runtime_rethrow",
                LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [], false));
            _exceptionUnhandled = module.AddFunction("__oxygen_runtime_unhandled_object",
                LLVMTypeRef.CreateFunction(LLVMTypeRef.Void,
                    [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)], false));
            foreach (var cls in model.Classes.Values)
            {
                foreach (var ctor in cls.Constructors)
                {
                    var type = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void,
                        new[] { LLVMTypeRef.CreatePointer(_classTypes[cls.Name], 0) }
                            .Concat(ctor.Parameters.Select(p => ToLlvmType(p.Type, model))).ToArray(), false);
                    _constructors[ConstructorKey(cls, ctor)] = module.AddFunction(
                        OxygenNameMangler.MangleConstructor(cls.ModuleName, cls, ctor), type);
                }
                if (cls.Destructor is not null)
                    _destructors[cls.Name] = module.AddFunction(
                        OxygenNameMangler.MangleDestructor(cls.ModuleName, cls),
                        LLVMTypeRef.CreateFunction(LLVMTypeRef.Void,
                            [LLVMTypeRef.CreatePointer(_classTypes[cls.Name], 0)], false));
            }
            var boundsFailureType = LLVMTypeRef.CreateFunction(
                LLVMTypeRef.Void, [LLVMTypeRef.Int64, LLVMTypeRef.Int64], false);
            _arrayBoundsFailure = module.AddFunction("__oxygen_runtime_array_bounds_failure", boundsFailureType);
            _enumCastFailure = module.AddFunction(
                "__oxygen_runtime_enum_cast_failure",
                LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [LLVMTypeRef.Int64], false));
            _isInstance = GetOrDeclareRuntimeFunction(
                module,
                "__oxygen_runtime_is_instance",
                LLVMTypeRef.CreateFunction(LLVMTypeRef.Int8,
                    [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)],
                    false),
                llvmFunctions);
            _invalidCast = GetOrDeclareRuntimeFunction(
                module,
                "__oxygen_runtime_invalid_cast",
                LLVMTypeRef.CreateFunction(LLVMTypeRef.Void,
                    [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)],
                    false),
                llvmFunctions);

            foreach (var symbol in symbols)
            {
                if (!symbol.IsExternal)
                    EmitFunction(context, builder, llvmFunctions[symbol], symbol, model, llvmFunctions);
            }
            foreach (var cls in model.Classes.Values)
            {
                foreach (var ctor in cls.Constructors)
                    EmitConstructor(context, builder, _constructors[ConstructorKey(cls, ctor)], cls, ctor, model, llvmFunctions);
                if (cls.Destructor is not null)
                    EmitDestructor(context, builder, _destructors[cls.Name], cls, model, llvmFunctions);
            }

            EmitNativeMain(context, builder, module, model, llvmFunctions);

            if (module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out var error))
                throw new InvalidOperationException($"LLVM verification failed: {error}");

            var metadata = new NativeModuleMetadata
            {
                ModuleName = moduleName,
                EntryPoint = "main",
                Functions = symbols.Select(symbol => new NativeFunctionMetadata
                {
                    ModuleName = symbol.ModuleName,
                    SourceName = symbol.Name,
                    NativeName = OxygenNameMangler.Mangle(symbol.ModuleName, symbol),
                    ReturnType = OxygenNameMangler.EncodeType(symbol.ReturnType),
                    Visibility = symbol.Visibility.ToString().ToLowerInvariant(),
                    ParameterTypes = symbol.Parameters
                        .Select(parameter => OxygenNameMangler.EncodeType(parameter.Type))
                        .ToArray(),
                    ExternalName = symbol.CInterop?.ExternalName,
                    CallingConvention = symbol.CInterop?.CallingConvention ?? "c"
                }).ToArray()
            };
            return new GeneratedLlvmModule(context, module, builder, metadata);
        }
        catch
        {
            builder.Dispose();
            module.Dispose();
            context.Dispose();
            throw;
        }
    }

    private static string ConstructorKey(ClassSymbol cls, ConstructorSymbol ctor) =>
            cls.Name + "(" + string.Join(",", ctor.Parameters.Select(p => OxygenNameMangler.EncodeType(p.Type))) + ")";

    private static IReadOnlyList<FunctionSymbol> GetVirtualMethods(ClassSymbol cls, SemanticModel model)
    {
        var methods = cls.BaseTypeName is not null && model.Classes.TryGetValue(cls.BaseTypeName, out var baseClass)
            ? GetVirtualMethods(baseClass, model).ToList()
            : [];
        foreach (var method in cls.Methods.Where(method => method.Declaration.IsVirtual || method.Declaration.IsOverride))
        {
            var slot = methods.FindIndex(candidate => candidate.Name == method.Name
                && candidate.Parameters.Select(parameter => parameter.Type)
                    .SequenceEqual(method.Parameters.Select(parameter => parameter.Type)));
            if (slot >= 0) methods[slot] = method;
            else methods.Add(method);
        }
        return methods;
    }

    private static int FieldOffset(ClassSymbol cls, SemanticModel model, int fieldIndex) =>
        cls.IsValue ? fieldIndex : 1 + _virtualSlotCount + fieldIndex;

    private static bool IsValueClass(SemanticType type, SemanticModel model) =>
        type.IsClass && model.Classes.TryGetValue(type.StructName!, out var cls) && cls.IsValue;

    private static LLVMTypeRef ClassPointerType(string name) =>
        LLVMTypeRef.CreatePointer(_classTypes[name], 0);

    private static LLVMValueRef GetOrDeclareRuntimeFunction(
        LLVMModuleRef module,
        string nativeName,
        LLVMTypeRef functionType,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        foreach (var pair in llvmFunctions)
        {
            if (pair.Key.IsExternal && pair.Key.NativeName == nativeName)
                return pair.Value;
        }

        return module.AddFunction(nativeName, functionType);
    }

    private static void EmitConstructor(LLVMContextRef context, LLVMBuilderRef builder, LLVMValueRef function,
            ClassSymbol cls, ConstructorSymbol ctor, SemanticModel model,
            IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
        {
            function.PersonalityFn = _personality;
            var entry = function.AppendBasicBlock("entry");
            builder.PositionAtEnd(entry);
            var values = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
            var storage = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
            var thisValue = function.GetParam(0);
            values["this"] = thisValue;
            InitializeClassFieldStorage(builder, values, storage, cls, model);
            if (ctor.IsSynthesized)
            {
                foreach (var field in cls.Fields)
                    builder.BuildStore(CreateZeroValue(field.Type, model), storage["this." + field.Name]);
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
                        CreateZeroValue(SemanticType.FromPrimitive(PrimitiveType.I32), model)
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
            function.PersonalityFn = _personality;
            var entry = function.AppendBasicBlock("entry");
            builder.PositionAtEnd(entry);
            _currentFunction = function;
            var values = new Dictionary<string, LLVMValueRef> { ["this"] = function.GetParam(0) };
            var storage = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
            InitializeClassFieldStorage(builder, values, storage, cls, model);
            foreach (var statement in cls.Destructor!.Body.Statements)
                EmitStatement(context, builder, function, statement, values, storage,
                    new HashSet<string>(), model, llvmFunctions, SemanticType.FromPrimitive(PrimitiveType.Void),
                    new Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue, int ScopeDepth, bool IsLoop)>());
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
        foreach (var cls in model.Classes.Values)
        {
            InitializeRuntimeTypeInfo(builder, cls.Name, cls.BaseTypeName, 1);
        }
        foreach (var interfaceSymbol in model.Interfaces.Values)
        {
            InitializeRuntimeTypeInfo(builder, interfaceSymbol.Name, null, 2);
        }
        foreach (var cls in model.Classes.Values)
        {
            if (cls.BaseTypeName is not null)
                RegisterRuntimeType(builder, registerType, cls.Name, cls.BaseTypeName);
            foreach (var interfaceName in cls.InterfaceNames)
                RegisterRuntimeType(builder, registerType, cls.Name, interfaceName);
        }

        var oxygenMain = model.MainFunction
            ?? throw new InvalidOperationException("Semantic model does not contain a main function.");
        var oxygenMainType = llvmFunctions[oxygenMain].FunctionType;
        var normal = nativeMain.AppendBasicBlock("oxygen.main.normal");
        var unwind = nativeMain.AppendBasicBlock("oxygen.main.unwind");
        var result = builder.BuildInvoke2(
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
        builder.PositionAtEnd(normal);
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
        function.PersonalityFn = _personality;
        var entry = function.AppendBasicBlock("entry");
        builder.PositionAtEnd(entry);
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
                builder.BuildRetVoid();
            else
                builder.BuildRet(LLVMValueRef.CreateConstInt(ToLlvmType(emittedReturnType, model), 0, false));
        }
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
                    if (targetType.IsClass)
                    {
                        var cls = model.Classes[targetType.StructName!];
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
                        builder.BuildRetVoid();
                    else
                        builder.BuildRet(LLVMValueRef.CreateConstInt(ToLlvmType(emittedReturnType, model), 0, false));
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
                builder.BuildInvoke2(_exceptionThrow.FunctionType, _exceptionThrow,
                    new[] { raw }, normal, _exceptionContexts.Peek().Unwind, "throw");
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
        var body = function.AppendBasicBlock("try.body");
        var normal = function.AppendBasicBlock("try.normal");
        var landing = function.AppendBasicBlock("try.landingpad");
        builder.BuildBr(body);
        builder.PositionAtEnd(body);
        var outerExceptionContext = _exceptionContexts.Count == 0 ? null : _exceptionContexts.Peek();
        _exceptionContexts.Push(new ExceptionContext(landing));
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


    private static LLVMValueRef EmitExpression(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        Expression expression,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions) =>
        expression switch
        {
            IdentifierExpression identifier when values.TryGetValue(identifier.Name, out var value) => value,
            IdentifierExpression identifier => builder.BuildLoad2(
                ToLlvmType(model.ExpressionTypes[identifier], model),
                storage[identifier.Name],
                identifier.Name),
            ThisExpression thisExpression => values["this"],
            IntegerLiteralExpression integer when model.ExpressionTypes[integer].IsPointer =>
                LLVMValueRef.CreateConstNull(ToLlvmType(model.ExpressionTypes[integer], model)),
            IntegerLiteralExpression integer => LLVMValueRef.CreateConstInt(
                ToLlvmType(model.ExpressionTypes[integer], model), ulong.Parse(NumericValue(integer.Lexeme), CultureInfo.InvariantCulture), false),
            FloatingPointLiteralExpression floating => LLVMValueRef.CreateConstRealOfString(
                ToLlvmType(model.ExpressionTypes[floating], model), NumericValue(floating.Lexeme)),
            StringLiteralExpression literal => EmitStringLiteral(builder, literal),
            BooleanLiteralExpression boolean => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, boolean.Value ? 1UL : 0UL, false),
            StructLiteralExpression literal => EmitStructLiteral(context, builder, literal, values, storage, model, llvmFunctions),
            ArrayLiteralExpression literal => EmitArrayLiteral(context, builder, literal, values, storage, model, llvmFunctions),
            UnionConstructionExpression construction => EmitUnionConstruction(context, builder, construction, values, storage, model, llvmFunctions),
            ClassConstructionExpression construction => EmitClassConstructionExpression(
                context, builder, construction, values, storage, model, llvmFunctions),
            NewExpression creation => EmitNewExpression(context, builder, creation, values, storage, model, llvmFunctions),
            IndexExpression index => builder.BuildLoad2(ToLlvmType(model.ExpressionTypes[index], model),
                EmitArrayAddress(context, builder, index, values, storage, model, llvmFunctions), "array.element"),
            CallExpression { Name: "length", Arguments: [var target] } => LLVMValueRef.CreateConstInt(
                LLVMTypeRef.Int32, (ulong)model.ExpressionTypes[target].ArrayLength, false),
            MemberAccessExpression member => EmitMemberAccess(context, builder, member, values, storage, model, llvmFunctions),
            CastExpression cast => EmitCast(context, builder, cast, values, storage, model, llvmFunctions),
            IsExpression pattern => EmitIsExpression(context, builder, pattern, values, storage, model, llvmFunctions),
            SizeOfExpression sizeOf => EmitSizeOf(sizeOf, model),
            BinaryExpression binary => EmitBinary(context, builder, binary, values, storage, model, llvmFunctions),
            ConditionalExpression conditional => EmitConditional(
                context, builder, conditional, values, storage, model, llvmFunctions),
            UnaryExpression unary => EmitUnary(context, builder, unary, values, storage, model, llvmFunctions),
            CallExpression call => EmitCall(context, builder, call, values, storage, model, llvmFunctions),
            _ => throw new InvalidOperationException($"Unsupported expression '{expression.GetType().Name}'.")
        };

    private static bool IsNativeSwitchType(SemanticType type) =>
        type.IsInteger || type.IsEnum || type.Primitive == PrimitiveType.Bool;

    private static LLVMValueRef EmitSwitchConstant(
        Expression expression,
        SemanticType selectorType,
        LLVMContextRef context,
        LLVMBuilderRef builder,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var label = EmitExpression(context, builder, expression, values, storage, model, llvmFunctions);
        return CoerceValue(builder, label, model.ExpressionTypes[expression], selectorType, model);
    }

    private static LLVMValueRef EmitSwitchEquality(
        LLVMBuilderRef builder,
        LLVMValueRef selector,
        LLVMValueRef label,
        SemanticType selectorType,
        SemanticModel model)
    {
        if (selectorType.Kind == SemanticTypeKind.String)
        {
            var selectorPointer = builder.BuildExtractValue(selector, 0, "switch.string.pointer");
            var labelPointer = builder.BuildExtractValue(label, 0, "switch.label.pointer");
            var selectorLength = builder.BuildExtractValue(selector, 1, "switch.string.length");
            var labelLength = builder.BuildExtractValue(label, 1, "switch.label.length");
            var sameLength = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, selectorLength, labelLength,
                "switch.string.length.match");
            var contents = builder.BuildCall2(_memcmp.FunctionType, _memcmp,
                new LLVMValueRef[] { selectorPointer, labelPointer, selectorLength }, "switch.string.compare");
            var sameContents = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, contents,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false), "switch.string.match");
            return builder.BuildAnd(sameLength, sameContents, "switch.string.equal");
        }

        if (selectorType.IsPointer)
            return builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, selector, label, "switch.pointer.equal");
        return builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, selector, label, "switch.equal");
    }

    private static LLVMValueRef EmitNewExpression(
        LLVMContextRef context, LLVMBuilderRef builder, NewExpression expression,
        IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var className = expression.Type.NamedType
            ?? throw new InvalidOperationException("Class construction requires a named type.");
        return EmitClassConstruction(context, builder, className, expression.Arguments, true,
            values, storage, model, llvmFunctions);
    }

    private static LLVMValueRef EmitClassConstructionExpression(
        LLVMContextRef context, LLVMBuilderRef builder, ClassConstructionExpression expression,
        IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions) =>
        EmitClassConstruction(context, builder, expression.TypeName, expression.Arguments, false,
            values, storage, model, llvmFunctions);

    private static LLVMValueRef EmitClassConstruction(
        LLVMContextRef context, LLVMBuilderRef builder, string className,
        IReadOnlyList<Expression> arguments, bool heapAllocated,
        IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var cls = model.Classes[className];
        var classType = ToLlvmType(SemanticType.Class(className), model);
        LLVMValueRef instance;
        if (heapAllocated)
        {
            var raw = builder.BuildCall2(_oxygenAlloc.FunctionType, _oxygenAlloc,
                new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, SizeOf(SemanticType.Class(className), model), false) },
                "object");
            instance = builder.BuildBitCast(raw, ClassPointerType(className), "object.cast");
        }
        else
        {
            instance = builder.BuildAlloca(_classTypes[className], "object.addr");
        }
        if (!cls.IsValue)
        {
            var vtable = builder.BuildStructGEP2(_classTypes[className], instance, 0, "object.vtable");
            builder.BuildStore(GetTypeName(builder, className), vtable);
            var virtualMethods = GetVirtualMethods(cls, model);
            for (var slot = 0; slot < _virtualSlotCount; slot++)
            {
                var slotAddress = builder.BuildStructGEP2(_classTypes[className], instance, (uint)slot + 1,
                    "object.vtable.slot");
                builder.BuildStore(LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)),
                    slotAddress);
            }
            for (var slot = 0; slot < virtualMethods.Count; slot++)
            {
                var slotAddress = builder.BuildStructGEP2(_classTypes[className], instance, (uint)slot + 1,
                    "object.vtable.slot");
                var target = llvmFunctions[virtualMethods[slot]];
                builder.BuildStore(builder.BuildBitCast(target,
                    LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "vtable.method"), slotAddress);
            }
        }
        var ctor = cls.Constructors.First(candidate => candidate.Parameters.Count == arguments.Count);
        var args = new List<LLVMValueRef> { instance };
        for (var i = 0; i < arguments.Count; i++)
            args.Add(CoerceValue(builder, EmitExpression(context, builder, arguments[i], values, storage, model, llvmFunctions),
                model.ExpressionTypes[arguments[i]], ctor.Parameters[i].Type, model));
        builder.BuildCall2(_constructors[ConstructorKey(cls, ctor)].FunctionType,
            _constructors[ConstructorKey(cls, ctor)], args.ToArray(), (ReadOnlySpan<char>)string.Empty);
        return cls.IsValue ? builder.BuildLoad2(classType, instance, "value") : instance;
    }

    private static LLVMValueRef EmitUnionConstruction(
        LLVMContextRef context, LLVMBuilderRef builder, UnionConstructionExpression construction,
        IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var union = model.Unions[construction.UnionName];
        var index = union.Cases.Select((item, i) => (item, i)).First(pair => pair.item.Name == construction.CaseName).i;
        var unionType = model.ExpressionTypes[construction];
        var result = LLVMValueRef.CreateConstNull(ToLlvmType(unionType, model));
        result = builder.BuildInsertValue(result,
            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)index, false), 0, "union.tag");
        if (construction.Arguments.Count == 1)
            result = builder.BuildInsertValue(result,
                CoerceValue(builder,
                    EmitExpression(context, builder, construction.Arguments[0], values, storage, model, llvmFunctions),
                    model.ExpressionTypes[construction.Arguments[0]], UnionPayloadType(unionType, model), model),
                1, "union.payload");
        return result;
    }

    private static LLVMValueRef EmitArrayLiteral(LLVMContextRef context, LLVMBuilderRef builder, ArrayLiteralExpression literal,
        IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var result = LLVMValueRef.CreateConstNull(ToLlvmType(model.ExpressionTypes[literal], model));
        for (var i = 0; i < literal.Elements.Count; i++)
            result = builder.BuildInsertValue(result, EmitExpression(context, builder, literal.Elements[i], values, storage, model, llvmFunctions), (uint)i, "array.element");
        return result;
    }

    private static LLVMValueRef EmitArrayAddress(LLVMContextRef context, LLVMBuilderRef builder, IndexExpression index,
        IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var targetType = model.ExpressionTypes[index.Target];
        if (targetType.IsPointer)
        {
            var pointerValue = EmitExpression(context, builder, index.Target, values, storage, model, llvmFunctions);
            var pointerIndex = EmitExpression(context, builder, index.Index, values, storage, model, llvmFunctions);
            if (model.ExpressionTypes[index.Index].Primitive != PrimitiveType.I64)
                pointerIndex = builder.BuildSExt(pointerIndex, LLVMTypeRef.Int64, "pointer.index");
            return builder.BuildGEP2(ToLlvmType(targetType.Pointee!, model), pointerValue,
                [pointerIndex], (ReadOnlySpan<char>)"pointer.address");
        }

        var arrayType = targetType;
        LLVMValueRef pointer;
        if (index.Target is IdentifierExpression identifier && storage.TryGetValue(identifier.Name, out var identifierStorage))
            pointer = identifierStorage;
        else if (index.Target is IndexExpression nested)
            pointer = EmitArrayAddress(context, builder, nested, values, storage, model, llvmFunctions);
        else
            throw new InvalidOperationException("Array indexing requires addressable storage.");
        var llvmIndex = EmitExpression(context, builder, index.Index, values, storage, model, llvmFunctions);
        if (model.ExpressionTypes[index.Index].Primitive != PrimitiveType.I64)
            llvmIndex = builder.BuildSExt(llvmIndex, LLVMTypeRef.Int64, "array.index");
        var length = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)arrayType.ArrayLength, false);
        var negative = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, llvmIndex,
            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false), "array.negative");
        var tooLarge = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, llvmIndex, length, "array.too.large");
        var invalid = builder.BuildOr(negative, tooLarge, "array.invalid");
        var failureBlock = _currentFunction.AppendBasicBlock("array.bounds.failure");
        var continueBlock = _currentFunction.AppendBasicBlock("array.bounds.continue");
        builder.BuildCondBr(invalid, failureBlock, continueBlock);
        builder.PositionAtEnd(failureBlock);
        builder.BuildCall2(_arrayBoundsFailure.FunctionType, _arrayBoundsFailure, [llvmIndex, length]);
        builder.BuildUnreachable();
        builder.PositionAtEnd(continueBlock);
        return builder.BuildGEP2(ToLlvmType(arrayType, model), pointer,
            new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false), llvmIndex }, (ReadOnlySpan<char>)"array.address");
    }

    private static LLVMValueRef EmitStructLiteral(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        StructLiteralExpression literal,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var result = LLVMValueRef.CreateConstNull(ToLlvmType(SemanticType.Struct(literal.TypeName), model));
        var structure = model.Structs[literal.TypeName];
        foreach (var field in literal.Fields)
        {
            var index = structure.Fields.Select((candidate, index) => (candidate, index))
                .First(pair => pair.candidate.Name == field.Name).index;
            result = builder.BuildInsertValue(
                result,
                EmitExpression(context, builder, field.Value, values, storage, model, llvmFunctions),
                (uint)index,
                $"struct.{field.Name}");
        }
        return result;
    }

    private static LLVMValueRef EmitMemberAccess(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        MemberAccessExpression member,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        if (member.Target is IdentifierExpression enumIdentifier
            && model.Enums.TryGetValue(enumIdentifier.Name, out var enumSymbol))
        {
            var value = enumSymbol.Members.First(candidate => candidate.Name == member.MemberName).Value;
            return LLVMValueRef.CreateConstInt(
                ToLlvmType(SemanticType.Enum(enumSymbol.Name), model),
                unchecked((ulong)value),
                false);
        }
        if (member.Target is ThisExpression)
        {
            var address = storage["this." + member.MemberName];
            return builder.BuildLoad2(ToLlvmType(model.ExpressionTypes[member], model), address,
                $"field.{member.MemberName}");
        }
        var target = EmitExpression(context, builder, member.Target, values, storage, model, llvmFunctions);
        var targetType = model.ExpressionTypes[member.Target];
        if (targetType.IsClass)
        {
            var cls = model.Classes[targetType.StructName!];
            var fieldIndex = cls.Fields.Select((field, index) => (field, index))
                .First(pair => pair.field.Name == member.MemberName).index + 1;
            if (cls.IsValue)
                return builder.BuildExtractValue(target, (uint)(fieldIndex - 1), $"field.{member.MemberName}");
            fieldIndex = FieldOffset(cls, model, fieldIndex - 1);
            var address = builder.BuildStructGEP2(_classTypes[cls.Name], target, (uint)fieldIndex,
                $"field.{member.MemberName}");
            return builder.BuildLoad2(ToLlvmType(model.ExpressionTypes[member], model), address,
                $"field.{member.MemberName}");
        }
        var index = model.Structs[targetType.StructName!].Fields
            .Select((field, index) => (field, index))
            .First(pair => pair.field.Name == member.MemberName).index;
        return builder.BuildExtractValue(target, (uint)index, $"struct.{member.MemberName}");
    }

    private static LLVMValueRef EmitCast(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        CastExpression cast,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var operand = EmitExpression(context, builder, cast.Operand, values, storage, model, llvmFunctions);
        var targetType = model.ExpressionTypes[cast];
        var operandType = model.ExpressionTypes[cast.Operand];
        if (IsReferenceType(targetType, model) && IsReferenceType(operandType, model))
            return EmitReferenceCast(builder, operand, operandType, targetType, model);
        if (targetType.IsPointer && operandType.IsPointer)
        {
            return builder.BuildBitCast(
                operand,
                ToLlvmType(targetType, model),
                "ptr.cast");
        }
        if (targetType.IsPointer && operandType.Primitive is PrimitiveType.USize or PrimitiveType.ISize)
            return builder.BuildIntToPtr(operand, ToLlvmType(targetType, model), "int.to.ptr");
        if (operandType.IsPointer && targetType.Primitive is PrimitiveType.USize or PrimitiveType.ISize)
            return builder.BuildPtrToInt(operand, ToLlvmType(targetType, model), "ptr.to.int");
        if (targetType.IsNumeric && operandType.IsNumeric)
        {
            var target = ToLlvmType(targetType, model);
            var source = ToLlvmType(operandType, model);
            if (targetType.Primitive is PrimitiveType.F32 or PrimitiveType.F64)
                return operandType.Primitive is PrimitiveType.F32 or PrimitiveType.F64
                    ? builder.BuildFPCast(operand, target, "float.cast")
                    : builder.BuildSIToFP(operand, target, "int.to.float");
            if (operandType.Primitive is PrimitiveType.F32 or PrimitiveType.F64)
                return builder.BuildFPToSI(operand, target, "float.to.int");
            var sourceBits = IntegerBitWidth(operandType.Primitive!.Value);
            var targetBits = IntegerBitWidth(targetType.Primitive!.Value);
            if (targetBits > sourceBits)
            {
                var signed = operandType.Primitive is PrimitiveType.I8 or PrimitiveType.I16
                    or PrimitiveType.I32 or PrimitiveType.I64;
                return signed
                    ? builder.BuildSExt(operand, target, "int.cast")
                    : builder.BuildZExt(operand, target, "int.cast");
            }
            return targetBits < sourceBits
                ? builder.BuildTrunc(operand, target, "int.cast")
                : operand;
        }
        if (!targetType.IsEnum)
            return operand;

        if (cast.Operand is IntegerLiteralExpression)
            return operand;

        var enumSymbol = model.Enums[targetType.EnumName!];
        var enumLlvmType = ToLlvmType(targetType, model);
        LLVMValueRef valid = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false);
        foreach (var member in enumSymbol.Members)
        {
            var candidate = LLVMValueRef.CreateConstInt(enumLlvmType, unchecked((ulong)member.Value), false);
            var match = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, operand, candidate, "enum.member");
            valid = builder.BuildOr(valid, match, "enum.valid");
        }
        var invalid = builder.BuildNot(valid, "enum.invalid");
        var failureBlock = _currentFunction.AppendBasicBlock("enum.cast.failure");
        var continueBlock = _currentFunction.AppendBasicBlock("enum.cast.continue");
        builder.BuildCondBr(invalid, failureBlock, continueBlock);
        builder.PositionAtEnd(failureBlock);
        var failureValue = enumSymbol.UnderlyingType is PrimitiveType.I64 or PrimitiveType.U64
            ? operand
            : builder.BuildZExt(operand, LLVMTypeRef.Int64, "enum.value");
        builder.BuildCall2(_enumCastFailure.FunctionType, _enumCastFailure, [failureValue]);
        builder.BuildUnreachable();
        builder.PositionAtEnd(continueBlock);
        return operand;
    }

    private static bool IsReferenceType(SemanticType type, SemanticModel model) =>
        (type.IsClass && !IsValueClass(type, model)) || type.IsInterface;

    private static void InitializeRuntimeTypeInfo(
        LLVMBuilderRef builder,
        string typeName,
        string? parentType,
        uint kind)
    {
        var info = _runtimeTypeInfos[typeName];
        var name = builder.BuildGlobalStringPtr(typeName, $"oxygen.type.name.{typeName}");
        builder.BuildStore(name, builder.BuildStructGEP2(_runtimeTypeInfoType, info, 0, "type.name"));
        builder.BuildStore(
            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, kind, false),
            builder.BuildStructGEP2(_runtimeTypeInfoType, info, 1, "type.kind"));
        builder.BuildStore(
            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false),
            builder.BuildStructGEP2(_runtimeTypeInfoType, info, 2, "type.flags"));
        var parent = parentType is null
            ? LLVMValueRef.CreateConstNull(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0))
            : GetTypeName(builder, parentType);
        builder.BuildStore(parent, builder.BuildStructGEP2(_runtimeTypeInfoType, info, 3, "type.parent"));
    }

    private static LLVMValueRef AddRuntimeTypeInfoGlobal(LLVMModuleRef module, string typeName)
    {
        var info = module.AddGlobal(_runtimeTypeInfoType, $"oxygen.type.info.{typeName}");
        info.Linkage = LLVMLinkage.LLVMInternalLinkage;
        info.Initializer = LLVMValueRef.CreateConstNull(_runtimeTypeInfoType);
        return info;
    }

    private static LLVMValueRef GetTypeName(LLVMBuilderRef builder, string typeName)
    {
        return _runtimeTypeInfos[typeName];
    }

    private static LLVMValueRef EmitReferenceCast(
        LLVMBuilderRef builder,
        LLVMValueRef operand,
        SemanticType source,
        SemanticType target,
        SemanticModel model)
    {
        if (source.IsClass && target.IsInterface)
        {
            var result = LLVMValueRef.CreateConstNull(ToLlvmType(target, model));
            result = builder.BuildInsertValue(
                result,
                builder.BuildBitCast(operand, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "interface.data"),
                0,
                "interface.data");
            return builder.BuildInsertValue(result, GetTypeName(builder, source.StructName!), 1, "interface.type");
        }

        var objectValue = source.IsInterface
            ? builder.BuildExtractValue(operand, 0, "cast.data")
            : operand;
        if (source.IsInterface)
            objectValue = builder.BuildBitCast(
                objectValue,
                LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                "cast.object");

        if (target.IsClass && (!source.IsClass || IsClassDerivedFrom(target.StructName!, source.StructName!, model)))
        {
            var possibleTypes = model.Classes.Keys
                .Where(candidate => IsClassDerivedFrom(candidate, target.StructName!, model))
                .Where(candidate => !source.IsInterface
                    || ClassImplementsInterface(candidate, source.StructName!, model))
                .ToArray();
            var valid = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0, false);
            foreach (var possibleType in possibleTypes)
            {
                var candidate = builder.BuildCall2(
                    _isInstance.FunctionType,
                    _isInstance,
                    [objectValue, GetTypeName(builder, possibleType)],
                    (ReadOnlySpan<char>)"cast.valid");
                valid = builder.BuildOr(valid, candidate, "cast.valid");
            }
            var invalid = builder.BuildICmp(
                LLVMIntPredicate.LLVMIntEQ,
                valid,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0, false),
                "cast.invalid");
            var failureBlock = _currentFunction.AppendBasicBlock("cast.failure");
            var continueBlock = _currentFunction.AppendBasicBlock("cast.continue");
            builder.BuildCondBr(invalid, failureBlock, continueBlock);
            builder.PositionAtEnd(failureBlock);
            var sourceName = source.IsClass
                ? GetTypeName(builder, source.StructName!)
                : builder.BuildExtractValue(operand, 1, "cast.source.type");
            builder.BuildCall2(_invalidCast.FunctionType, _invalidCast,
                [sourceName, GetTypeName(builder, target.StructName!)]);
            builder.BuildUnreachable();
            builder.PositionAtEnd(continueBlock);
        }

        return target.IsClass
            ? builder.BuildBitCast(objectValue, ToLlvmType(target, model), "class.cast")
            : objectValue;
    }

    private static LLVMValueRef EmitIsExpression(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        IsExpression expression,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var operand = EmitExpression(context, builder, expression.Operand, values, storage, model, llvmFunctions);
        return EmitRuntimeTypeCheck(builder, operand, model.ExpressionTypes[expression.Operand],
            FromTypeSyntax(expression.TargetType, model), model);
    }

    private static LLVMValueRef EmitRuntimeTypeCheck(
        LLVMBuilderRef builder,
        LLVMValueRef operand,
        SemanticType source,
        SemanticType target,
        SemanticModel model)
    {
        var objectValue = source.IsInterface
            ? builder.BuildExtractValue(operand, 0, "pattern.data")
            : builder.BuildBitCast(operand, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "pattern.object");
        var possibleTypes = model.Classes.Keys
            .Where(candidate => target.IsClass
                ? IsClassDerivedFrom(candidate, target.StructName!, model)
                : ClassImplementsInterface(candidate, target.StructName!, model))
            .ToArray();
        var match = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0, false);
        foreach (var possibleType in possibleTypes)
        {
            var candidate = builder.BuildCall2(_isInstance.FunctionType, _isInstance,
                [objectValue, GetTypeName(builder, possibleType)], (ReadOnlySpan<char>)"type.match");
            match = builder.BuildOr(match, candidate, "type.match");
        }
        return match;
    }

    private static LLVMValueRef EmitPatternBindingValue(
        LLVMBuilderRef builder,
        LLVMValueRef operand,
        SemanticType source,
        SemanticType target,
        SemanticModel model)
    {
        if (target.IsInterface)
        {
            if (source.IsInterface)
                return operand;
            var result = LLVMValueRef.CreateConstNull(ToLlvmType(target, model));
            result = builder.BuildInsertValue(result,
                builder.BuildBitCast(operand, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "pattern.data"),
                0, "pattern.data");
            return builder.BuildInsertValue(result, GetTypeName(builder, source.StructName!), 1, "pattern.type");
        }
        var objectValue = source.IsInterface
            ? builder.BuildExtractValue(operand, 0, "pattern.object")
            : operand;
        return builder.BuildBitCast(objectValue, ToLlvmType(target, model), "pattern.cast");
    }

    private static bool IsClassDerivedFrom(string sourceName, string targetName, SemanticModel model)
    {
        if (string.Equals(sourceName, targetName, StringComparison.Ordinal))
            return true;
        return model.Classes.TryGetValue(sourceName, out var source)
            && source.BaseTypeName is not null
            && model.Classes.ContainsKey(source.BaseTypeName)
            && IsClassDerivedFrom(source.BaseTypeName, targetName, model);
    }

    private static bool ClassImplementsInterface(string className, string interfaceName, SemanticModel model)
    {
        if (!model.Classes.TryGetValue(className, out var cls))
            return false;
        if (cls.InterfaceNames.Contains(interfaceName, StringComparer.Ordinal))
            return true;
        return cls.BaseTypeName is not null
            && model.Classes.ContainsKey(cls.BaseTypeName)
            && ClassImplementsInterface(cls.BaseTypeName, interfaceName, model);
    }

    private static LLVMValueRef EmitSizeOf(SizeOfExpression expression, SemanticModel model)
    {
        var type = expression.TargetType is not null
            ? FromTypeSyntax(expression.TargetType, model)
            : model.ExpressionTypes[expression.Operand!];
        return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)SizeOf(type, model), false);
    }

    private static ulong SizeOf(SemanticType type, SemanticModel model) =>
        type.Kind switch
        {
            SemanticTypeKind.Pointer => 8,
            SemanticTypeKind.Class => model.Classes[type.StructName!].IsValue
                ? (ulong)model.Classes[type.StructName!].Fields.Sum(field => (long)SizeOf(field.Type, model))
                : (ulong)(8 + model.Classes[type.StructName!].Fields.Sum(field => (long)SizeOf(field.Type, model))),
            SemanticTypeKind.Interface => 16,
            SemanticTypeKind.String => 16,
            SemanticTypeKind.Array => checked((ulong)type.ArrayLength * SizeOf(type.ElementType!, model)),
            SemanticTypeKind.Struct => (ulong)model.Structs[type.StructName!].Fields
                .Sum(field => checked((long)SizeOf(field.Type, model))),
            SemanticTypeKind.Union => checked(4UL + SizeOf(UnionPayloadType(type, model), model)),
            SemanticTypeKind.Enum => SizeOf(SemanticType.FromPrimitive(model.Enums[type.EnumName!].UnderlyingType), model),
            SemanticTypeKind.Primitive => type.Primitive switch
            {
                PrimitiveType.I8 or PrimitiveType.U8 or PrimitiveType.Bool => 1,
                PrimitiveType.I16 or PrimitiveType.U16 => 2,
                PrimitiveType.I32 or PrimitiveType.U32 or PrimitiveType.F32 => 4,
                PrimitiveType.I64 or PrimitiveType.U64 or PrimitiveType.F64 => 8,
                _ => throw new InvalidOperationException($"Type '{type}' has no size.")
            },
            _ => throw new InvalidOperationException($"Type '{type}' has no size.")
        };

    private static int IntegerBitWidth(PrimitiveType type) =>
        type switch
        {
            PrimitiveType.I8 or PrimitiveType.U8 or PrimitiveType.Bool => 8,
            PrimitiveType.I16 or PrimitiveType.U16 => 16,
            PrimitiveType.I32 or PrimitiveType.U32 => 32,
            PrimitiveType.I64 or PrimitiveType.U64 or PrimitiveType.USize or PrimitiveType.ISize => 64,
            _ => throw new InvalidOperationException($"'{type}' is not an integer type.")
        };

    private static HashSet<string> CollectStorageRequirements(BlockStatement body)
    {
        var requirements = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in body.Statements)
            CollectStorageRequirements(statement, requirements);
        return requirements;
    }

    private static void CollectStorageRequirements(Statement statement, HashSet<string> requirements)
    {
        switch (statement)
        {
            case AssignmentStatement assignment:
                if (assignment.Target is IdentifierExpression identifier)
                    requirements.Add(identifier.Name);
                else if (assignment.Target is MemberAccessExpression { Target: IdentifierExpression memberIdentifier })
                    requirements.Add(memberIdentifier.Name);
                else if (assignment.Target is IndexExpression { Target: IdentifierExpression indexedIdentifier })
                    requirements.Add(indexedIdentifier.Name);
                CollectStorageRequirements(assignment.Target, requirements);
                CollectStorageRequirements(assignment.Value, requirements);
                break;
            case VariableDeclarationStatement declaration when declaration.Initializer is not null:
                CollectStorageRequirements(declaration.Initializer, requirements);
                break;
            case ExpressionStatement expression:
                CollectStorageRequirements(expression.Expression, requirements);
                break;
            case ReturnStatement returnStatement when returnStatement.Value is not null:
                CollectStorageRequirements(returnStatement.Value, requirements);
                break;
            case BlockStatement block:
                foreach (var nested in block.Statements)
                    CollectStorageRequirements(nested, requirements);
                break;
            case IfStatement conditional:
                CollectStorageRequirements(conditional.Condition, requirements);
                CollectStorageRequirements(conditional.Then, requirements);
                if (conditional.Else is not null) CollectStorageRequirements(conditional.Else, requirements);
                break;
            case WhileStatement loop:
                CollectStorageRequirements(loop.Condition, requirements);
                CollectStorageRequirements(loop.Body, requirements);
                break;
            case ForStatement loop:
                if (loop.Initializer is not null) CollectStorageRequirements(loop.Initializer, requirements);
                if (loop.Condition is not null) CollectStorageRequirements(loop.Condition, requirements);
                if (loop.Increment is not null) CollectStorageRequirements(loop.Increment, requirements);
                CollectStorageRequirements(loop.Body, requirements);
                break;
            case ForInStatement loop:
                CollectStorageRequirements(loop.Collection, requirements);
                CollectStorageRequirements(loop.Body, requirements);
                break;
            case SwitchStatement switchStatement:
                CollectStorageRequirements(switchStatement.Value, requirements);
                foreach (var item in switchStatement.Cases) CollectStorageRequirements(item.Body, requirements);
                break;
        }
    }

    private static void CollectStorageRequirements(Expression expression, HashSet<string> requirements)
    {
        switch (expression)
        {
            case UnaryExpression { Operator: TokenKind.Ampersand, Operand: IdentifierExpression identifier }:
                requirements.Add(identifier.Name);
                break;
            case UnaryExpression unary:
                CollectStorageRequirements(unary.Operand, requirements);
                break;
            case BinaryExpression binary:
                CollectStorageRequirements(binary.Left, requirements);
                CollectStorageRequirements(binary.Right, requirements);
                break;
            case ConditionalExpression conditional:
                CollectStorageRequirements(conditional.Condition, requirements);
                CollectStorageRequirements(conditional.WhenTrue, requirements);
                CollectStorageRequirements(conditional.WhenFalse, requirements);
                break;
            case CallExpression call:
                foreach (var argument in call.Arguments)
                    CollectStorageRequirements(argument, requirements);
                break;
            case MemberAccessExpression member:
                CollectStorageRequirements(member.Target, requirements);
                break;
            case IndexExpression index:
                if (index.Target is IdentifierExpression arrayIdentifier)
                    requirements.Add(arrayIdentifier.Name);
                CollectStorageRequirements(index.Target, requirements);
                CollectStorageRequirements(index.Index, requirements);
                break;
            case CastExpression cast:
                CollectStorageRequirements(cast.Operand, requirements);
                break;
            case IsExpression pattern:
                CollectStorageRequirements(pattern.Operand, requirements);
                break;
        }
    }

    private static LLVMValueRef EmitStringLiteral(LLVMBuilderRef builder, StringLiteralExpression literal)
    {
        var value = JsonSerializer.Deserialize(
                literal.Lexeme,
                NativeModuleMetadataJsonContext.Default.String)
            ?? throw new InvalidOperationException("String literal decoded to null.");
        var pointer = builder.BuildGlobalStringPtr(value, "oxygen.string");
        var result = LLVMValueRef.CreateConstNull(ToLlvmStringType());
        result = builder.BuildInsertValue(result, pointer, 0, "string.pointer");
        return builder.BuildInsertValue(
            result,
            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)System.Text.Encoding.UTF8.GetByteCount(value), false),
            1,
            "string.length");
    }

    private static LLVMValueRef EmitConditional(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        ConditionalExpression expression,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var condition = EmitExpression(context, builder, expression.Condition, values, storage, model, llvmFunctions);
        var resultType = model.ExpressionTypes[expression];
        var resultStorage = builder.BuildAlloca(ToLlvmType(resultType, model), "conditional.result");
        var trueBlock = _currentFunction.AppendBasicBlock("conditional.true");
        var falseBlock = _currentFunction.AppendBasicBlock("conditional.false");
        var mergeBlock = _currentFunction.AppendBasicBlock("conditional.end");

        builder.BuildCondBr(condition, trueBlock, falseBlock);

        builder.PositionAtEnd(trueBlock);
        var whenTrue = EmitExpression(context, builder, expression.WhenTrue, values, storage, model, llvmFunctions);
        builder.BuildStore(
            CoerceValue(builder, whenTrue, model.ExpressionTypes[expression.WhenTrue], resultType, model),
            resultStorage);
        builder.BuildBr(mergeBlock);

        builder.PositionAtEnd(falseBlock);
        var whenFalse = EmitExpression(context, builder, expression.WhenFalse, values, storage, model, llvmFunctions);
        builder.BuildStore(
            CoerceValue(builder, whenFalse, model.ExpressionTypes[expression.WhenFalse], resultType, model),
            resultStorage);
        builder.BuildBr(mergeBlock);

        builder.PositionAtEnd(mergeBlock);
        return builder.BuildLoad2(ToLlvmType(resultType, model), resultStorage, "conditional.result");
    }

    private static LLVMValueRef EmitBinary(LLVMContextRef context, LLVMBuilderRef builder, BinaryExpression expression, IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage, SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var left = EmitExpression(context, builder, expression.Left, values, storage, model, llvmFunctions);
        var right = EmitExpression(context, builder, expression.Right, values, storage, model, llvmFunctions);
        var leftType = model.ExpressionTypes[expression.Left];
        var rightType = model.ExpressionTypes[expression.Right];
        if (leftType != rightType && leftType.IsNumeric && rightType.IsNumeric)
        {
            if (model.ExpressionTypes[expression] != SemanticType.FromPrimitive(PrimitiveType.Bool))
            {
                var common = model.ExpressionTypes[expression];
                if (leftType != common)
                    left = CoerceValue(builder, left, leftType, common, model);
                if (rightType != common)
                    right = CoerceValue(builder, right, rightType, common, model);
            }
            else if (leftType != rightType)
            {
                if (leftType.IsNumeric && rightType.IsNumeric)
                {
                    var common = leftType;
                    right = CoerceValue(builder, right, rightType, common, model);
                }
            }
        }
        if ((leftType.IsPointer || rightType.IsPointer)
            && expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals)
        {
            var predicate = expression.Operator == TokenKind.EqualsEquals
                ? LLVMIntPredicate.LLVMIntEQ
                : LLVMIntPredicate.LLVMIntNE;
            return builder.BuildICmp(predicate, left, right, "ptr.compare");
        }
        if (model.ExpressionTypes[expression].IsPointer)
        {
            var index = right;
            if (model.ExpressionTypes[expression.Right].Primitive != PrimitiveType.I64)
                index = builder.BuildSExt(right, LLVMTypeRef.Int64, "ptr.index");
            var pointee = ToLlvmType(model.ExpressionTypes[expression].Pointee!, model);
            return expression.Operator == TokenKind.Plus
                ? builder.BuildGEP2(pointee, left, new LLVMValueRef[] { index }, (ReadOnlySpan<char>)"ptr.add")
                : builder.BuildGEP2(pointee, left,
                    new LLVMValueRef[] { builder.BuildNeg(index, "ptr.neg") }, (ReadOnlySpan<char>)"ptr.sub");
        }
        var isFloatingPoint = model.ExpressionTypes[expression].Primitive is PrimitiveType.F32 or PrimitiveType.F64;
        if (expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals
            or TokenKind.LessThan or TokenKind.LessThanOrEquals
            or TokenKind.GreaterThan or TokenKind.GreaterThanOrEquals)
        {
            if (isFloatingPoint)
            {
                var predicate = expression.Operator switch
                {
                    TokenKind.EqualsEquals => LLVMRealPredicate.LLVMRealOEQ,
                    TokenKind.BangEquals => LLVMRealPredicate.LLVMRealONE,
                    TokenKind.LessThan => LLVMRealPredicate.LLVMRealOLT,
                    TokenKind.LessThanOrEquals => LLVMRealPredicate.LLVMRealOLE,
                    TokenKind.GreaterThan => LLVMRealPredicate.LLVMRealOGT,
                    _ => LLVMRealPredicate.LLVMRealOGE
                };
                return builder.BuildFCmp(predicate, left, right, "fcmp");
            }
            var intPredicate = expression.Operator switch
            {
                TokenKind.EqualsEquals => LLVMIntPredicate.LLVMIntEQ,
                TokenKind.BangEquals => LLVMIntPredicate.LLVMIntNE,
                TokenKind.LessThan => LLVMIntPredicate.LLVMIntSLT,
                TokenKind.LessThanOrEquals => LLVMIntPredicate.LLVMIntSLE,
                TokenKind.GreaterThan => LLVMIntPredicate.LLVMIntSGT,
                _ => LLVMIntPredicate.LLVMIntSGE
            };
            return builder.BuildICmp(intPredicate, left, right, "icmp");
        }
        if (expression.Operator is TokenKind.AmpersandAmpersand or TokenKind.PipePipe)
            return expression.Operator == TokenKind.AmpersandAmpersand
                ? builder.BuildAnd(left, right, "and")
                : builder.BuildOr(left, right, "or");
        return expression.Operator switch
        {
            TokenKind.Plus when isFloatingPoint => builder.BuildFAdd(left, right, "fadd"),
            TokenKind.Minus when isFloatingPoint => builder.BuildFSub(left, right, "fsub"),
            TokenKind.Star when isFloatingPoint => builder.BuildFMul(left, right, "fmul"),
            TokenKind.Slash when isFloatingPoint => builder.BuildFDiv(left, right, "fdiv"),
            TokenKind.Plus => builder.BuildAdd(left, right, "add"),
            TokenKind.Minus => builder.BuildSub(left, right, "sub"),
            TokenKind.Star => builder.BuildMul(left, right, "mul"),
            TokenKind.Slash => builder.BuildSDiv(left, right, "div"),
            TokenKind.Percent => builder.BuildSRem(left, right, "rem"),
            TokenKind.Ampersand => builder.BuildAnd(left, right, "and"),
            TokenKind.Pipe => builder.BuildOr(left, right, "or"),
            TokenKind.Caret => builder.BuildXor(left, right, "xor"),
            TokenKind.LessThanLessThan => builder.BuildShl(left, right, "shl"),
            TokenKind.GreaterThanGreaterThan => builder.BuildAShr(left, right, "ashr"),
            _ => throw new InvalidOperationException($"Unsupported binary operator '{expression.Operator}'.")
        };
    }

    private static LLVMValueRef EmitUnary(LLVMContextRef context, LLVMBuilderRef builder, UnaryExpression expression, IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage, SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        if (expression.Operator is TokenKind.PlusPlus or TokenKind.MinusMinus)
        {
            var address = EmitLValueAddress(context, builder, expression.Operand, values, storage, model, llvmFunctions);
            var type = model.ExpressionTypes[expression.Operand];
            var oldValue = builder.BuildLoad2(ToLlvmType(type, model), address, "increment.old");
            LLVMValueRef updated;
            if (type.IsPointer)
            {
                var index = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 1, false);
                if (expression.Operator == TokenKind.MinusMinus)
                    index = builder.BuildNeg(index, "decrement.index");
                updated = builder.BuildGEP2(ToLlvmType(type.Pointee!, model), oldValue,
                    new LLVMValueRef[] { index }, "increment.pointer");
            }
            else
            {
                var one = LLVMValueRef.CreateConstInt(ToLlvmType(type, model), 1, false);
                updated = expression.Operator == TokenKind.PlusPlus
                    ? builder.BuildAdd(oldValue, one, "increment")
                    : builder.BuildSub(oldValue, one, "decrement");
            }

            builder.BuildStore(updated, address);
            return expression.IsPostfix ? oldValue : updated;
        }

        if (expression.Operator == TokenKind.Ampersand)
        {
            if (expression.Operand is IdentifierExpression identifier)
                return storage[identifier.Name];
            if (expression.Operand is UnaryExpression
                { Operator: TokenKind.Star, Operand: var dereferenced })
                return EmitExpression(context, builder, dereferenced, values, storage, model, llvmFunctions);
        }
        var operand = EmitExpression(context, builder, expression.Operand, values, storage, model, llvmFunctions);
        if (expression.Operator == TokenKind.Star)
            return builder.BuildLoad2(ToLlvmType(model.ExpressionTypes[expression], model), operand, "deref");
        var isFloatingPoint = model.ExpressionTypes[expression].Primitive is PrimitiveType.F32 or PrimitiveType.F64;
        return expression.Operator == TokenKind.Minus
            ? isFloatingPoint
                ? builder.BuildFNeg(operand, "fneg")
                : builder.BuildNeg(operand, "neg")
            : throw new InvalidOperationException($"Unsupported unary operator '{expression.Operator}'.");
    }

    private static LLVMValueRef EmitLValueAddress(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        Expression expression,
        IReadOnlyDictionary<string, LLVMValueRef> values,
        IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        return expression switch
        {
            IdentifierExpression identifier => storage[identifier.Name],
            UnaryExpression { Operator: TokenKind.Star, Operand: var pointer } =>
                EmitExpression(context, builder, pointer, values, storage, model, llvmFunctions),
            IndexExpression index => EmitArrayAddress(context, builder, index, values, storage, model, llvmFunctions),
            _ => throw new InvalidOperationException("Increment target is not supported by the LLVM backend.")
        };
    }

    private static LLVMValueRef EmitCall(LLVMContextRef context, LLVMBuilderRef builder, CallExpression expression, IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage, SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var parts = expression.Name.Split('.');
        if (parts.Length == 2 && model.Unions.TryGetValue(parts[0], out var union))
        {
            var index = union.Cases.Select((item, i) => (item, i)).First(pair => pair.item.Name == parts[1]).i;
            var unionType = model.ExpressionTypes[expression];
            var result = LLVMValueRef.CreateConstNull(ToLlvmType(unionType, model));
            result = builder.BuildInsertValue(result, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)index, false), 0, "union.tag");
            if (expression.Arguments.Count == 1)
                result = builder.BuildInsertValue(result,
                    CoerceValue(builder,
                        EmitExpression(context, builder, expression.Arguments[0], values, storage, model, llvmFunctions),
                        model.ExpressionTypes[expression.Arguments[0]], UnionPayloadType(unionType, model), model),
                    1, "union.payload");
            return result;
        }
        var function = model.ResolvedCalls[expression];
        var method = IsClassMethod(function, model);
        var arguments = new List<LLVMValueRef>();
        if (method)
        {
            var separator = expression.Name.LastIndexOf('.');
            var receiver = expression.Name[..separator];
            if (!values.TryGetValue(receiver, out var receiverValue))
                receiverValue = builder.BuildLoad2(ToLlvmType(model.ExpressionTypes.First(pair => pair.Key is IdentifierExpression id && id.Name == receiver).Value, model),
                    storage[receiver], receiver);
            else if (receiver == "this"
                && model.Classes[function.ModuleName[(function.ModuleName.LastIndexOf('.') + 1)..]].IsValue)
            {
                var receiverClassName = function.ModuleName[(function.ModuleName.LastIndexOf('.') + 1)..];
                receiverValue = builder.BuildLoad2(
                    _classTypes[receiverClassName], receiverValue, "this.value");
            }
            arguments.Add(receiverValue);
        }
        for (var index = 0; index < expression.Arguments.Count; index++)
        {
            var argument = expression.Arguments[index];
            var value = EmitExpression(context, builder, argument, values, storage, model, llvmFunctions);
            value = CoerceValue(builder, value, model.ExpressionTypes[argument], function.Parameters[index].Type, model);
            if (UsesPointerStringAbi(function) && function.Parameters[index].Type.Kind == SemanticTypeKind.String)
            {
                var storagePointer = builder.BuildAlloca(ToLlvmStringType(), "cabi.string");
                builder.BuildStore(value, storagePointer);
                value = storagePointer;
            }

            if (argument is IdentifierExpression identifier && function.Parameters[index].Type.IsClass)
                RemoveOwned(identifier.Name);
            arguments.Add(value);
        }

        var resultName = function.ReturnType.IsVoid ? string.Empty : "call";
        if (method && (function.Declaration.IsVirtual || function.Declaration.IsOverride))
        {
            var receiverName = expression.Name[..expression.Name.LastIndexOf('.')];
            var receiverType = model.ExpressionTypes.First(pair =>
                pair.Key is IdentifierExpression id && id.Name == receiverName).Value;
            var receiverClass = model.Classes[receiverType.StructName!];
            var slotPair = GetVirtualMethods(receiverClass, model).Select((candidate, index) => (candidate, index))
                .FirstOrDefault(pair => pair.candidate.Name == function.Name
                    && pair.candidate.Parameters.Select(parameter => parameter.Type)
                        .SequenceEqual(function.Parameters.Select(parameter => parameter.Type)));
            if (slotPair.candidate is not null)
            {
                var slot = slotPair.index;
                var tableSlot = builder.BuildStructGEP2(_classTypes[receiverClass.Name], arguments[0],
                    (uint)slot + 1, "vtable.slot");
                var rawTarget = builder.BuildLoad2(
                    LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), tableSlot, "vtable.target");
                var target = builder.BuildBitCast(rawTarget, llvmFunctions[function].FunctionType, "vtable.call");
                return EmitCallOrInvoke(builder, llvmFunctions[function].FunctionType, target,
                    arguments.ToArray(), resultName);
            }
        }
        return EmitCallOrInvoke(builder, llvmFunctions[function].FunctionType, llvmFunctions[function],
            arguments.ToArray(), resultName);
    }

    private static LLVMValueRef EmitCallOrInvoke(
        LLVMBuilderRef builder,
        LLVMTypeRef functionType,
        LLVMValueRef target,
        LLVMValueRef[] arguments,
        string resultName)
    {
        if (_exceptionContexts.Count == 0)
            return builder.BuildCall2(functionType, target, arguments, resultName);

        var normal = _currentFunction.AppendBasicBlock("invoke.continue");
        var result = builder.BuildInvoke2(functionType, target, arguments, normal,
            _exceptionContexts.Peek().Unwind, resultName);
        builder.PositionAtEnd(normal);
        return result;
    }

    private static LLVMTypeRef ToLlvmFunctionType(FunctionSymbol symbol, SemanticModel model)
    {
        var parameterTypes = symbol.Parameters
            .Select(parameter => UsesPointerStringAbi(symbol) && parameter.Type.Kind == SemanticTypeKind.String
                ? LLVMTypeRef.CreatePointer(ToLlvmStringType(), 0)
                : ToLlvmType(parameter.Type, model))
            .ToList();
        if (IsClassMethod(symbol, model))
            parameterTypes.Insert(0, ToLlvmType(SemanticType.Class(symbol.ModuleName[(symbol.ModuleName.LastIndexOf('.') + 1)..]), model));
        return LLVMTypeRef.CreateFunction(ToLlvmType(symbol.ReturnType, model), parameterTypes.ToArray(), false);
    }

    private static bool IsClassMethod(FunctionSymbol symbol, SemanticModel model) =>
        model.Classes.Values.Any(cls => cls.Methods.Contains(symbol));

    private static bool UsesPointerStringAbi(FunctionSymbol symbol) =>
        symbol.IsExternal &&
        symbol.NativeName is "oxygen_write_stdout" or "oxygen_write_stderr";

    private static LLVMTypeRef ToLlvmType(SemanticType type, SemanticModel model) => type.Primitive switch
    {
        PrimitiveType.I8 or PrimitiveType.U8 => LLVMTypeRef.Int8,
        PrimitiveType.I16 or PrimitiveType.U16 => LLVMTypeRef.Int16,
        PrimitiveType.I32 or PrimitiveType.U32 => LLVMTypeRef.Int32,
        PrimitiveType.I64 or PrimitiveType.U64 or PrimitiveType.USize or PrimitiveType.ISize => LLVMTypeRef.Int64,
        PrimitiveType.Bool => LLVMTypeRef.Int1,
        PrimitiveType.F32 => LLVMTypeRef.Float,
        PrimitiveType.F64 => LLVMTypeRef.Double,
        PrimitiveType.Void => LLVMTypeRef.Void,
        _ when type.Kind == SemanticTypeKind.Pointer && type.IsOpaquePointer => LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
        _ when type.Kind == SemanticTypeKind.Pointer => LLVMTypeRef.CreatePointer(ToLlvmType(type.Pointee!, model), 0),
        _ when type.Kind == SemanticTypeKind.Class && !IsValueClass(type, model) =>
            ClassPointerType(type.StructName!),
        _ when type.Kind == SemanticTypeKind.Class => _classTypes[type.StructName!],
        _ when type.Kind == SemanticTypeKind.Interface => LLVMTypeRef.CreateStruct(
            [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)], false),
        _ when type.Kind == SemanticTypeKind.Struct => LLVMTypeRef.CreateStruct(
            model.Structs[type.StructName!].Fields.Select(field => ToLlvmType(field.Type, model)).ToArray(),
            model.Structs[type.StructName!].IsPacked),
        _ when type.Kind == SemanticTypeKind.String => ToLlvmStringType(),
        _ when type.Kind == SemanticTypeKind.Array => LLVMTypeRef.CreateArray(ToLlvmType(type.ElementType!, model), (uint)type.ArrayLength),
        _ when type.Kind == SemanticTypeKind.Enum => ToLlvmType(
            SemanticType.FromPrimitive(model.Enums[type.EnumName!].UnderlyingType), model),
        _ when type.Kind == SemanticTypeKind.Union => LLVMTypeRef.CreateStruct(
            [LLVMTypeRef.Int32, ToLlvmType(UnionPayloadType(type, model), model)], false),
        _ => throw new InvalidOperationException($"Unsupported semantic type '{type}'.")
    };

    private static SemanticType UnionPayloadType(SemanticType type, SemanticModel model)
    {
        var union = model.Unions[type.EnumName!];
        var payload = union.Cases.FirstOrDefault(item => item.PayloadType is not null)?.PayloadType;
        if (payload is null)
            return SemanticType.FromPrimitive(PrimitiveType.I8);
        return SubstituteGeneric(payload, union.DeclaredTypeParameters, type.TypeArguments);
    }

    private static SemanticType SubstituteGeneric(
        SemanticType type, IReadOnlyList<string> parameters, IReadOnlyList<SemanticType>? arguments)
    {
        if (type.IsGenericParameter && arguments is not null)
        {
            var index = parameters.ToList().IndexOf(type.GenericParameterName!);
            if (index >= 0 && index < arguments.Count)
                return arguments[index];
        }
        if (type.IsPointer)
            return SemanticType.PointerTo(SubstituteGeneric(type.Pointee!, parameters, arguments));
        if (type.TypeArguments is { Count: > 0 })
            return type with { TypeArguments = type.TypeArguments.Select(item =>
                SubstituteGeneric(item, parameters, arguments)).ToArray() };
        return type;
    }

    private static string NumericValue(string lexeme)
    {
        foreach (var suffix in new[] { "usize", "isize", "f32", "f64", "i16", "i32", "i64", "u16", "u32", "u64", "i8", "u8" })
            if (lexeme.EndsWith(suffix, StringComparison.Ordinal))
                return lexeme[..^suffix.Length];
        return lexeme;
    }

    private static LLVMValueRef CoerceValue(
        LLVMBuilderRef builder, LLVMValueRef value, SemanticType source, SemanticType target, SemanticModel model)
    {
        if (source == target)
            return value;
        if (source.IsClass && target.IsClass)
            return builder.BuildBitCast(value, ToLlvmType(target, model), "class.upcast");
        if (source.IsClass && target.IsInterface)
        {
            var result = LLVMValueRef.CreateConstNull(ToLlvmType(target, model));
            result = builder.BuildInsertValue(
                result,
                builder.BuildBitCast(value, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "interface.data"),
                0,
                "interface.data");
            return builder.BuildInsertValue(result, GetTypeName(builder, source.StructName!), 1, "interface.type");
        }
        if (source.IsPointer && target.IsNumeric)
            return builder.BuildPtrToInt(value, ToLlvmType(target, model), "ptr.to.int");
        if (target.IsPointer && source.IsNumeric)
            return builder.BuildIntToPtr(value, ToLlvmType(target, model), "int.to.ptr");
        if (source.IsNumeric && target.IsNumeric)
        {
            var targetType = ToLlvmType(target, model);
            if (target.Primitive is PrimitiveType.F32 or PrimitiveType.F64)
                return source.Primitive is PrimitiveType.F32 or PrimitiveType.F64
                    ? builder.BuildFPCast(value, targetType, "float.cast")
                    : builder.BuildSIToFP(value, targetType, "int.to.float");
            if (source.Primitive is PrimitiveType.F32 or PrimitiveType.F64)
                return builder.BuildFPToSI(value, targetType, "float.to.int");
            var sourceBits = IntegerBitWidth(source.Primitive!.Value);
            var targetBits = IntegerBitWidth(target.Primitive!.Value);
            if (targetBits > sourceBits)
                return source.Primitive is PrimitiveType.I8 or PrimitiveType.I16 or PrimitiveType.I32 or PrimitiveType.I64 or PrimitiveType.ISize
                    ? builder.BuildSExt(value, targetType, "int.cast")
                    : builder.BuildZExt(value, targetType, "int.cast");
            if (targetBits < sourceBits)
                return builder.BuildTrunc(value, targetType, "int.cast");
        }
        return value;
    }

    private static SemanticType FromTypeSyntax(TypeSyntax syntax, SemanticModel model)
    {
        if (syntax.IsArray)
            return SemanticType.ArrayOf(FromTypeSyntax(syntax.ElementType!, model), syntax.ArrayLength!.Value);
        if (syntax.IsPointer)
            return SemanticType.PointerTo(FromTypeSyntax(syntax.Pointee!, model));
        if (syntax.IsString)
            return SemanticType.String;
        if (syntax.NamedType is not null)
        {
            if (model.Classes.ContainsKey(syntax.NamedType))
                return SemanticType.Class(syntax.NamedType);
            if (model.Interfaces.ContainsKey(syntax.NamedType))
                return SemanticType.Interface(syntax.NamedType);
            if (model.Structs.ContainsKey(syntax.NamedType))
                return SemanticType.Struct(syntax.NamedType);
            if (model.Unions.ContainsKey(syntax.NamedType))
                return SemanticType.Union(syntax.NamedType);
            return SemanticType.Enum(syntax.NamedType);
        }
        if (syntax.Primitive is null)
            throw new InvalidOperationException("Inferred variable type is missing.");
        var type = SemanticType.FromPrimitive(syntax.Primitive.Value);
        return syntax.IsPointer ? SemanticType.PointerTo(type) : type;
    }
}
