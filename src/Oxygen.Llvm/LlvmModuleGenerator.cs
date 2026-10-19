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
    private LLVMValueRef _arrayBoundsFailure;
    private LLVMValueRef _enumCastFailure;
    private LLVMValueRef _arithmeticFailure;
    private LLVMValueRef _isInstance;
    private LLVMValueRef _invalidCast;
    private LLVMValueRef _currentFunction;
    private readonly Dictionary<string, LLVMTypeRef> _classTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LLVMValueRef> _runtimeTypeInfos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LLVMValueRef> _constructors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LLVMValueRef> _destructors = new(StringComparer.Ordinal);
    private readonly Dictionary<(string ClassName, string PropertyName, PropertyAccessorKind Kind), FunctionSymbol> _propertyAccessors = new();
    private readonly HashSet<FunctionSymbol> _accessorSymbols = [];
    private readonly Dictionary<(string ClassName, string InterfaceName), LLVMValueRef> _interfaceTables = new();
    private readonly Dictionary<string, IReadOnlyList<FunctionSymbol>> _interfaceSlots = new(StringComparer.Ordinal);
    private int _virtualSlotCount;
    private sealed record OwnedLocal(VariableDeclarationStatement? Declaration, string Name, SemanticType Type);
    private Stack<List<OwnedLocal>> _ownedScopes = new();
    private LLVMValueRef _oxygenAlloc;
    private LLVMValueRef _oxygenFree;
    private LLVMValueRef _memcmp;
    private LLVMTypeRef _runtimeTypeInfoType;
    private LLVMValueRef _exceptionThrow;
    private LLVMValueRef _exceptionObject;
    private LLVMValueRef _exceptionEndCatch;
    private LLVMValueRef _exceptionRethrow;
    private LLVMValueRef _exceptionUnhandled;
    private LLVMValueRef _tracePush;
    private LLVMValueRef _tracePop;
    private LLVMValueRef _exceptionPointer;
    private LLVMModuleRef _module;
    private LLVMValueRef _personality;
    private sealed record ExceptionContext(LLVMBasicBlockRef Unwind, LLVMValueRef Pad);
    private Stack<ExceptionContext> _exceptionContexts = new();
    private bool _usesExceptions;
    private bool _usesWindowsSeh;
    // The runtime "alive" flag of each local that some `delete` releases, keyed by the local's stack slot.
    private readonly Dictionary<LLVMValueRef, LLVMValueRef> _dropFlags = new();
    // The cleanup of the current function's owned locals, for an exception that leaves their scope. _unwindBlock is
    // default when the function needs none. See LlvmModuleGenerator.Unwind.cs.
    private readonly List<UnwindLocal> _unwindLocals = [];
    private LLVMBasicBlockRef _unwindBlock;
    private LLVMValueRef _unwindPad;
    private LLVMValueRef _unwindLandingPad;
    // Context that owns every type and value created during Generate. Type factories must come from this context,
    // not from LLVMTypeRef's static (global-context) properties, or the module fails verification.
    private LLVMContextRef _context;
    private LLVMTypeRef ToLlvmStringType() =>
        _context.GetStructType([LLVMTypeRef.CreatePointer(_context.Int8Type, 0), _context.Int64Type], false);

    public GeneratedLlvmModule Generate(string moduleName, SemanticModel model)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        ArgumentNullException.ThrowIfNull(model);

        var context = LLVMContextRef.Create();
        _context = context;
        var module = context.CreateModuleWithName(moduleName);
        // Set explicitly: LLVM's default layout gives i64 a 4-byte ABI alignment, which the x86-64 ABIs that the runtime
        // is built for do not. Struct sizes, offsets and sizeof all follow from this layout.
        module.DataLayout = OperatingSystem.IsWindows()
            ? "e-m:w-p270:32:32-p271:32:32-p272:64:64-i64:64-f80:128-n8:16:32:64-S128"
            : "e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-f80:128-n8:16:32:64-S128";
        _module = module;
        _exceptionPointer = default;
        var builder = context.CreateBuilder();
        try
        {
            _propertyAccessors.Clear();
            _accessorSymbols.Clear();
            _interfaceTables.Clear();
            _interfaceSlots.Clear();
            var concreteClasses = model.Classes.Values.Where(cls => !cls.IsGeneric).ToArray();
            var concreteInterfaces = model.Interfaces.Values.Where(iface => !iface.IsGeneric).ToArray();
            var accessorSymbols = concreteClasses.SelectMany(cls => CreatePropertyAccessorSymbols(cls, model))
                .Concat(concreteInterfaces.SelectMany(iface => CreateInterfaceAccessorSymbols(iface))).ToList();
            var symbols = model.Functions.Values
                .Concat(model.ResolvedCalls.Values)
                .Concat(concreteClasses.SelectMany(cls => cls.Methods))
                .Concat(concreteInterfaces.SelectMany(iface => iface.Methods))
                .Concat(accessorSymbols)
                .Where(symbol => !symbol.IsGeneric && !HasGenericAggregateOwner(symbol, model)
                    && !HasGenericType(symbol.ReturnType)
                    && symbol.Parameters.All(parameter => !HasGenericType(parameter.Type)))
                .Distinct()
                .ToList();
            var llvmFunctions = new Dictionary<FunctionSymbol, LLVMValueRef>();

            // Null-reference checks can raise NullReferenceException from any class member access, and std.exception is always
            // loaded, so every module needs the exception runtime.
            _usesExceptions = true;
            _usesWindowsSeh = _usesExceptions && IsWindowsNativeSehTarget();

            _classTypes.Clear();
            _dropFlags.Clear();
            _runtimeTypeInfos.Clear();
            _runtimeTypeInfoType = _context.GetStructType(
                [LLVMTypeRef.CreatePointer(_context.Int8Type, 0), _context.Int32Type,
                    _context.Int32Type, LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                    _context.Int32Type, LLVMTypeRef.CreatePointer(_context.Int8Type, 0)], false);
            _constructors.Clear();
            _destructors.Clear();
            _virtualSlotCount = concreteClasses.Select(cls => GetVirtualMethods(cls, model).Count)
                .DefaultIfEmpty().Max();
            foreach (var cls in concreteClasses)
                _classTypes[cls.Name] = context.CreateNamedStruct($"class.{cls.Name}");
            foreach (var cls in concreteClasses)
            {
                var fields = (cls.IsValue
                        ? Enumerable.Empty<LLVMTypeRef>()
                        : new[] { LLVMTypeRef.CreatePointer(_context.Int8Type, 0) }
                            .Concat(Enumerable.Repeat(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), _virtualSlotCount)))
                    .Concat(cls.Fields.Select(field => ToLlvmType(field.Type, model))).ToArray();
                _classTypes[cls.Name].StructSetBody(fields, false);
            }

            foreach (var cls in concreteClasses)
                _runtimeTypeInfos[cls.Name] = AddRuntimeTypeInfoGlobal(module, cls.Name);
            foreach (var interfaceSymbol in concreteInterfaces)
                _runtimeTypeInfos[interfaceSymbol.Name] = AddRuntimeTypeInfoGlobal(module, interfaceSymbol.Name);
            foreach (var symbol in symbols)
            {
                var functionType = ToLlvmFunctionType(symbol, model);
                var name = OxygenNameMangler.Mangle(symbol.ModuleName, symbol);
                // Externals that name the same C symbol with the same signature (from_c_string in two stdlib modules)
                // share one declaration.
                llvmFunctions[symbol] = DeclareNativeFunction(module, name, functionType);
            }
            DeclareGlobalVariables(module, model);
            foreach (var iface in concreteInterfaces)
            {
                var slots = GetInterfaceSlots(iface);
                _interfaceSlots[iface.Name] = slots;
                foreach (var cls in concreteClasses.Where(cls => ClassImplementsInterface(cls.Name, iface.Name, model)))
                    AddInterfaceTable(module, cls, iface, slots, llvmFunctions, model);
            }
            var allocType = LLVMTypeRef.CreateFunction(
                LLVMTypeRef.CreatePointer(_context.Int8Type, 0), [_context.Int64Type], false);
            _oxygenAlloc = DeclareNativeFunction(
                module, "oxygen_alloc", allocType);
            var freeType = LLVMTypeRef.CreateFunction(_context.VoidType,
                [LLVMTypeRef.CreatePointer(_context.Int8Type, 0)], false);
            _oxygenFree = DeclareNativeFunction(
                module, "oxygen_free", freeType);
            _memcmp = DeclareNativeFunction(module, "memcmp",
                LLVMTypeRef.CreateFunction(_context.Int32Type,
                    [LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                        LLVMTypeRef.CreatePointer(_context.Int8Type, 0), _context.Int64Type], false));
            _personality = DeclareNativeFunction(module, _usesWindowsSeh ? "__C_specific_handler" : "__gxx_personality_v0",
                _usesWindowsSeh
                    ? LLVMTypeRef.CreateFunction(_context.Int32Type, [], true)
                    : LLVMTypeRef.CreateFunction(_context.Int32Type,
                        [_context.Int32Type, _context.Int32Type, _context.Int64Type,
                            LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                            LLVMTypeRef.CreatePointer(_context.Int8Type, 0)], false));
            _exceptionThrow = DeclareNativeFunction(module, "__oxygen_runtime_throw_object",
                LLVMTypeRef.CreateFunction(_context.VoidType,
                    [LLVMTypeRef.CreatePointer(_context.Int8Type, 0)], false));
            _exceptionObject = DeclareNativeFunction(module, "__oxygen_runtime_exception_object",
                LLVMTypeRef.CreateFunction(LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                    [LLVMTypeRef.CreatePointer(_context.Int8Type, 0)], false));
            _exceptionEndCatch = DeclareNativeFunction(module, "__oxygen_runtime_end_catch",
                LLVMTypeRef.CreateFunction(_context.VoidType, [], false));
            _exceptionRethrow = DeclareNativeFunction(module, "__oxygen_runtime_rethrow",
                LLVMTypeRef.CreateFunction(_context.VoidType, [], false));
            _exceptionUnhandled = DeclareNativeFunction(module, "__oxygen_runtime_unhandled_object",
                LLVMTypeRef.CreateFunction(_context.VoidType,
                    [LLVMTypeRef.CreatePointer(_context.Int8Type, 0)], false));
            _tracePush = DeclareNativeFunction(module, "__oxygen_runtime_trace_push",
                LLVMTypeRef.CreateFunction(_context.VoidType,
                    [LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                        LLVMTypeRef.CreatePointer(_context.Int8Type, 0), _context.Int32Type,
                        _context.Int32Type], false));
            _tracePop = DeclareNativeFunction(module, "__oxygen_runtime_trace_pop",
                LLVMTypeRef.CreateFunction(_context.VoidType, [], false));
            foreach (var cls in concreteClasses)
            {
                foreach (var ctor in cls.Constructors)
                {
                    var type = LLVMTypeRef.CreateFunction(_context.VoidType,
                        new[] { LLVMTypeRef.CreatePointer(_classTypes[cls.Name], 0) }
                            .Concat(ctor.Parameters.Select(p => ToLlvmType(p.Type, model))).ToArray(), false);
                    _constructors[ConstructorKey(cls, ctor)] = DeclareNativeFunction(module, 
                        OxygenNameMangler.MangleConstructor(cls.ModuleName, cls, ctor), type);
                }

                if (cls.Destructor is not null)
                    _destructors[cls.Name] = DeclareNativeFunction(module, 
                        OxygenNameMangler.MangleDestructor(cls.ModuleName, cls),
                        LLVMTypeRef.CreateFunction(_context.VoidType,
                            [LLVMTypeRef.CreatePointer(_classTypes[cls.Name], 0)], false));
            }
            var boundsFailureType = LLVMTypeRef.CreateFunction(
                _context.VoidType, [_context.Int64Type, _context.Int64Type], false);
            _arrayBoundsFailure = DeclareNativeFunction(module, "__oxygen_runtime_array_bounds_failure", boundsFailureType);
            _enumCastFailure = DeclareNativeFunction(module, 
                "__oxygen_runtime_enum_cast_failure",
                LLVMTypeRef.CreateFunction(_context.VoidType, [_context.Int64Type], false));
            _arithmeticFailure = DeclareNativeFunction(module, 
                "__oxygen_runtime_arithmetic_failure",
                LLVMTypeRef.CreateFunction(_context.VoidType, [_context.Int32Type, _context.Int64Type], false));
            _isInstance = DeclareNativeFunction(
                module,
                "__oxygen_runtime_is_instance",
                LLVMTypeRef.CreateFunction(_context.Int8Type,
                    [LLVMTypeRef.CreatePointer(_context.Int8Type, 0), LLVMTypeRef.CreatePointer(_context.Int8Type, 0)],
                    false));
            _invalidCast = DeclareNativeFunction(
                module,
                "__oxygen_runtime_invalid_cast",
                LLVMTypeRef.CreateFunction(_context.VoidType,
                    [LLVMTypeRef.CreatePointer(_context.Int8Type, 0), LLVMTypeRef.CreatePointer(_context.Int8Type, 0)],
                    false));

            foreach (var symbol in symbols)
            {
                if (!symbol.IsExternal && !IsInterfaceContract(symbol, model))
                    EmitFunction(context, builder, llvmFunctions[symbol], symbol, model, llvmFunctions);
            }
            foreach (var cls in concreteClasses)
            {
                foreach (var ctor in cls.Constructors)
                    EmitConstructor(context, builder, _constructors[ConstructorKey(cls, ctor)], cls, ctor, model, llvmFunctions);
                if (cls.Destructor is not null)
                    EmitDestructor(context, builder, _destructors[cls.Name], cls, model, llvmFunctions);
            }

            EmitNativeMain(context, builder, module, model, llvmFunctions);

            LlvmModuleVerifier.Verify(module);

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

    private static string ConstructorKey(ClassSymbol cls, ConstructorSymbol ctor) =>
            cls.Name + "(" + string.Join(",", ctor.Parameters.Select(p => OxygenNameMangler.EncodeType(p.Type))) + ")";

    private static bool IsInterfaceContract(FunctionSymbol symbol, SemanticModel model) =>
        model.Interfaces.Values.Any(iface => iface.Methods.Contains(symbol));

}
