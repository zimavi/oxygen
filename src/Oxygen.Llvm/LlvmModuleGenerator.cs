using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
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
    private static readonly Dictionary<(string ClassName, string PropertyName, PropertyAccessorKind Kind), FunctionSymbol> _propertyAccessors = new();
    private static readonly HashSet<FunctionSymbol> _accessorSymbols = [];
    private static readonly Dictionary<(string ClassName, string InterfaceName), LLVMValueRef> _interfaceTables = new();
    private static readonly Dictionary<string, IReadOnlyList<FunctionSymbol>> _interfaceSlots = new(StringComparer.Ordinal);
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
    private static LLVMValueRef _tracePush;
    private static LLVMValueRef _tracePop;
    private static LLVMValueRef _exceptionPointer;
    private static LLVMModuleRef _module;
    private static LLVMValueRef _personality;
    private sealed record ExceptionContext(LLVMBasicBlockRef Unwind, LLVMValueRef Pad);
    private static Stack<ExceptionContext> _exceptionContexts = new();
    private static bool _usesExceptions;
    private static bool _usesWindowsSeh;
    private static LLVMTypeRef ToLlvmStringType() =>
        LLVMTypeRef.CreateStruct([LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.Int64], false);

    public GeneratedLlvmModule Generate(string moduleName, SemanticModel model)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        ArgumentNullException.ThrowIfNull(model);

        var context = LLVMContextRef.Create();
        var module = context.CreateModuleWithName(moduleName);
        _module = module;
        _exceptionPointer = default;
        var builder = context.CreateBuilder();
        _propertyAccessors.Clear();
        _accessorSymbols.Clear();
        _interfaceTables.Clear();
        _interfaceSlots.Clear();
        var accessorSymbols = model.Classes.Values.SelectMany(cls => CreatePropertyAccessorSymbols(cls, model))
            .Concat(model.Interfaces.Values.SelectMany(iface => CreateInterfaceAccessorSymbols(iface))).ToList();
        var symbols = model.Functions.Values
            .Concat(model.ResolvedCalls.Values)
            .Concat(model.Classes.Values.SelectMany(cls => cls.Methods))
            .Concat(model.Interfaces.Values.SelectMany(iface => iface.Methods))
            .Concat(accessorSymbols)
            .Where(symbol => !symbol.IsGeneric)
            .Distinct()
            .ToList();
        var llvmFunctions = new Dictionary<FunctionSymbol, LLVMValueRef>();

        try
        {
            _usesExceptions = RequiresExceptionRuntime(model);
            _usesWindowsSeh = _usesExceptions && IsWindowsNativeSehTarget();

            _classTypes.Clear();
            _runtimeTypeInfos.Clear();
            _runtimeTypeInfoType = LLVMTypeRef.CreateStruct(
                [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.Int32,
                    LLVMTypeRef.Int32, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                    LLVMTypeRef.Int32], false);
            _constructors.Clear();
            _destructors.Clear();
            _virtualSlotCount = model.Classes.Values.Select(cls => GetVirtualMethods(cls, model).Count)
                .DefaultIfEmpty().Max();
            foreach (var cls in model.Classes.Values)
                _classTypes[cls.Name] = context.CreateNamedStruct($"class.{cls.Name}");
            foreach (var cls in model.Classes.Values)
            {
                var fields = (cls.IsValue
                        ? Enumerable.Empty<LLVMTypeRef>()
                        : new[] { LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0) }
                            .Concat(Enumerable.Repeat(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), _virtualSlotCount)))
                    .Concat(cls.Fields.Select(field => ToLlvmType(field.Type, model))).ToArray();
                _classTypes[cls.Name].StructSetBody(fields, false);
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
            foreach (var iface in model.Interfaces.Values)
            {
                var slots = GetInterfaceSlots(iface);
                _interfaceSlots[iface.Name] = slots;
                foreach (var cls in model.Classes.Values.Where(cls => ClassImplementsInterface(cls.Name, iface.Name, model)))
                    AddInterfaceTable(module, cls, iface, slots, llvmFunctions, model);
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
            _personality = module.AddFunction(_usesWindowsSeh ? "__C_specific_handler" : "__gxx_personality_v0",
                _usesWindowsSeh
                    ? LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [], true)
                    : LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32,
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
            _tracePush = module.AddFunction("__oxygen_runtime_trace_push",
                LLVMTypeRef.CreateFunction(LLVMTypeRef.Void,
                    [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                        LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.Int32,
                        LLVMTypeRef.Int32], false));
            _tracePop = module.AddFunction("__oxygen_runtime_trace_pop",
                LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [], false));
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

    private static bool IsWindowsNativeSehTarget() =>
        OperatingSystem.IsWindows()
        && !RuntimeInformation.RuntimeIdentifier.Contains("mingw", StringComparison.OrdinalIgnoreCase)
        && !RuntimeInformation.OSDescription.Contains("mingw", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresExceptionRuntime(SemanticModel model)
    {
        IEnumerable<BlockStatement> bodies = model.Functions.Values
            .Select(function => function.Declaration.Body)
            .Concat(model.Classes.Values.SelectMany(cls => cls.Constructors.Select(ctor => ctor.Declaration.Body)))
            .Concat(model.Classes.Values
                .Where(cls => cls.Destructor is not null)
                .Select(cls => cls.Destructor!.Body))
            .Concat(model.Classes.Values.SelectMany(cls => cls.Methods.Select(method => method.Declaration.Body)))
            .Concat(model.Interfaces.Values.SelectMany(iface => iface.Methods.Select(method => method.Declaration.Body)));

        return bodies.Any(ContainsExceptionStatement);
    }

    private static bool ContainsExceptionStatement(BlockStatement block) =>
        block.Statements.Any(ContainsExceptionStatement);

    private static bool ContainsExceptionStatement(Statement statement) =>
        statement switch
        {
            ThrowStatement => true,
            TryStatement => true,
            BlockStatement block => ContainsExceptionStatement(block),
            IfStatement conditional => ContainsExceptionStatement(conditional.Then)
                || (conditional.Else is not null && ContainsExceptionStatement(conditional.Else)),
            WhileStatement loop => ContainsExceptionStatement(loop.Body),
            ForStatement loop => (loop.Initializer is not null && ContainsExceptionStatement(loop.Initializer))
                || (loop.Increment is not null && ContainsExceptionStatement(loop.Increment))
                || ContainsExceptionStatement(loop.Body),
            ForInStatement loop => ContainsExceptionStatement(loop.Body),
            _ => false
        };

    private static string ConstructorKey(ClassSymbol cls, ConstructorSymbol ctor) =>
            cls.Name + "(" + string.Join(",", ctor.Parameters.Select(p => OxygenNameMangler.EncodeType(p.Type))) + ")";

}
