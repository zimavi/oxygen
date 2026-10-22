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
    // The target of the module. It is the host's target unless one is given.
    private readonly OxygenTarget _target;

    public LlvmModuleGenerator(OxygenTarget? target = null)
    {
        _target = target ?? OxygenTarget.Host;
    }

    private LLVMValueRef _arrayBoundsFailure;
    private LLVMValueRef _stringBoundsFailure;
    private LLVMValueRef _enumCastFailure;
    private LLVMValueRef _arithmeticFailure;
    private LLVMValueRef _isInstance;
    private LLVMValueRef _invalidCast;
    private LLVMValueRef _currentFunction;
    private readonly Dictionary<string, LLVMTypeRef> _classTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LLVMValueRef> _runtimeTypeInfos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LLVMValueRef> _runtimeTypeNameStrings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (LLVMValueRef Table, uint Count)> _runtimeSupertypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LLVMValueRef> _constructors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LLVMValueRef> _destructors = new(StringComparer.Ordinal);
    private readonly Dictionary<(string ClassName, string PropertyName, PropertyAccessorKind Kind), FunctionSymbol> _propertyAccessors = new();
    private readonly HashSet<FunctionSymbol> _accessorSymbols = [];
    private readonly Dictionary<(string ClassName, string InterfaceName), LLVMValueRef> _interfaceTables = new();
    private readonly Dictionary<string, IReadOnlyList<FunctionSymbol>> _interfaceSlots = new(StringComparer.Ordinal);
    // An owned local of a scope. A catch binding that the catch frees has no declaration: its clause is kept instead, and its scope end releases
    // the exception (EmitCatchBody).
    private sealed record OwnedLocal(VariableDeclarationStatement? Declaration, string Name, SemanticType Type, CatchClause? Catch = null);
    private Stack<List<OwnedLocal>> _ownedScopes = new();
    private LLVMValueRef _oxygenAlloc;
    private LLVMValueRef _oxygenFree;
    private LLVMValueRef _memcmp;
    private LLVMTypeRef _runtimeTypeInfoType;
    private LLVMValueRef _exceptionThrow;
    private LLVMValueRef _exceptionContinueThrow;
    private LLVMValueRef _exceptionObject;
    private LLVMValueRef _exceptionEndCatch;
    private LLVMValueRef _exceptionRethrow;
    private LLVMValueRef _nullReferenceFailure;
    private LLVMValueRef _nullPointerFailure;
    private LLVMValueRef _exceptionUnhandled;
    private LLVMValueRef _tracePush;
    private LLVMValueRef _tracePop;
    private LLVMValueRef _traceDepth;
    private LLVMValueRef _traceRestore;
    private LLVMValueRef _releaseExceptionTrace;
    private LLVMValueRef _releaseCaughtException;
    private LLVMValueRef _adoptFailureText;
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

    // The generic entries are the module's generic declarations (see NativeGenericMetadata), which go into the module's metadata.
    // A library has no native entry point: its code is linked into a program, which has one. So a library has no native main.
    public GeneratedLlvmModule Generate(
        string moduleName,
        SemanticModel model,
        IReadOnlyList<NativeGenericMetadata>? generics = null,
        bool library = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        ArgumentNullException.ThrowIfNull(model);

        var context = LLVMContextRef.Create();
        _context = context;
        var module = context.CreateModuleWithName(moduleName);
        // The triple selects the object format and the calling conventions, and the data layout is set explicitly: LLVM's default
        // layout gives i64 a 4-byte ABI alignment, which the x86-64 ABIs that the runtime is built for do not. Struct sizes,
        // offsets and sizeof all follow from this layout.
        module.Target = _target.Triple;
        module.DataLayout = _target.DataLayout;
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
            // The class or interface that declares each method and property accessor. Its native name is built from it.
            var methodOwners = new Dictionary<FunctionSymbol, OxygenNameMangler.MethodOwner>();
            var accessorSymbols = new List<FunctionSymbol>();
            foreach (var cls in concreteClasses)
            {
                var owner = new OxygenNameMangler.MethodOwner(cls.ModuleName, cls.Name, IsInterface: false);
                foreach (var method in cls.Methods.Concat(cls.StaticMethods))
                    methodOwners.TryAdd(method, owner);
                foreach (var accessor in CreatePropertyAccessorSymbols(cls, model))
                {
                    accessorSymbols.Add(accessor);
                    methodOwners.TryAdd(accessor, owner);
                }
            }
            foreach (var iface in concreteInterfaces)
            {
                var owner = new OxygenNameMangler.MethodOwner(iface.ModuleName, iface.Name, IsInterface: true);
                foreach (var method in iface.Methods)
                    methodOwners.TryAdd(method, owner);
                foreach (var accessor in CreateInterfaceAccessorSymbols(iface))
                {
                    accessorSymbols.Add(accessor);
                    methodOwners.TryAdd(accessor, owner);
                }
            }
            var symbols = model.Functions.Values
                .Concat(model.ResolvedCalls.Values)
                .Concat(concreteClasses.SelectMany(cls => cls.Methods))
                .Concat(concreteClasses.SelectMany(cls => cls.StaticMethods))
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
            _usesWindowsSeh = _usesExceptions && _target.UsesSeh;

            _classTypes.Clear();
            _dropFlags.Clear();
            _runtimeTypeInfos.Clear();
            _runtimeTypeNameStrings.Clear();
            _failureHelpers.Clear();
            _runtimeSupertypes.Clear();
            _runtimeTypeInfoType = _context.GetStructType(
                [LLVMTypeRef.CreatePointer(_context.Int8Type, 0), _context.Int32Type,
                    _context.Int32Type, LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                    _context.Int32Type, LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                    LLVMTypeRef.CreatePointer(_context.Int8Type, 0), _context.Int32Type], false);
            _constructors.Clear();
            _destructors.Clear();
            foreach (var cls in concreteClasses)
                _classTypes[cls.QualifiedName] = context.CreateNamedStruct($"class.{cls.QualifiedName}");
            foreach (var cls in concreteClasses)
                _classTypes[cls.QualifiedName].StructSetBody(ClassLayout(cls, model), false);

            foreach (var cls in concreteClasses)
                _runtimeTypeInfos[cls.QualifiedName] = AddRuntimeTypeInfoGlobal(module, cls.QualifiedName,
                    OwnsTypeInThisObject(cls.ModuleName, cls.Name, moduleName) || DefinedByLibrary(cls.ModuleName, IsInstantiationName(cls.Name), model),
                    DefinedByLibrary(cls.ModuleName, IsInstantiationName(cls.Name), model));
            foreach (var interfaceSymbol in concreteInterfaces)
                _runtimeTypeInfos[interfaceSymbol.QualifiedName] = AddRuntimeTypeInfoGlobal(module, interfaceSymbol.QualifiedName,
                    OwnsTypeInThisObject(interfaceSymbol.ModuleName, interfaceSymbol.Name, moduleName)
                    || DefinedByLibrary(interfaceSymbol.ModuleName, IsInstantiationName(interfaceSymbol.Name), model),
                    DefinedByLibrary(interfaceSymbol.ModuleName, IsInstantiationName(interfaceSymbol.Name), model));
            // The supertype tables refer to the type information above, so they are created after it.
            foreach (var cls in concreteClasses)
                _runtimeSupertypes[cls.QualifiedName] = AddRuntimeSupertypeTable(module, cls.QualifiedName,
                    RuntimeSupertypeNames(cls, model));
            foreach (var interfaceSymbol in concreteInterfaces)
                _runtimeSupertypes[interfaceSymbol.QualifiedName] = AddRuntimeSupertypeTable(module, interfaceSymbol.QualifiedName, []);
            foreach (var symbol in symbols)
            {
                var functionType = ToLlvmFunctionType(symbol, model);
                var name = MangledNameOf(symbol, methodOwners);
                // Externals that name the same C symbol with the same signature (from_c_string in two stdlib modules)
                // share one declaration.
                llvmFunctions[symbol] = DeclareNativeFunction(module, name, functionType);
            }
            DeclareGlobalVariables(module, model, moduleName, library, llvmFunctions);
            foreach (var iface in concreteInterfaces)
            {
                var slots = GetInterfaceSlots(iface);
                _interfaceSlots[iface.QualifiedName] = slots;
                foreach (var cls in concreteClasses.Where(cls => ClassImplementsInterface(cls.QualifiedName, iface.QualifiedName, model)))
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
            _exceptionContinueThrow = DeclareNativeFunction(module, "__oxygen_runtime_continue_throw",
                LLVMTypeRef.CreateFunction(_context.VoidType,
                    [LLVMTypeRef.CreatePointer(_context.Int8Type, 0)], false));
            _exceptionObject = DeclareNativeFunction(module, "__oxygen_runtime_exception_object",
                LLVMTypeRef.CreateFunction(LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                    [LLVMTypeRef.CreatePointer(_context.Int8Type, 0)], false));
            _exceptionEndCatch = DeclareNativeFunction(module, "__oxygen_runtime_end_catch",
                LLVMTypeRef.CreateFunction(_context.VoidType, [], false));
            _exceptionRethrow = DeclareNativeFunction(module, "__oxygen_runtime_rethrow",
                LLVMTypeRef.CreateFunction(_context.VoidType, [], false));
            _nullReferenceFailure = DeclareNativeFunction(module, "__oxygen_runtime_null_reference_failure",
                LLVMTypeRef.CreateFunction(_context.VoidType, [], false));
            _nullPointerFailure = DeclareNativeFunction(module, "__oxygen_runtime_null_pointer_failure",
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
            _traceDepth = DeclareNativeFunction(module, "__oxygen_runtime_trace_depth",
                LLVMTypeRef.CreateFunction(_context.Int64Type, [], false));
            _traceRestore = DeclareNativeFunction(module, "__oxygen_runtime_trace_restore",
                LLVMTypeRef.CreateFunction(_context.VoidType, [_context.Int64Type], false));
            _releaseExceptionTrace = DeclareNativeFunction(module, "__oxygen_runtime_release_exception_trace",
                LLVMTypeRef.CreateFunction(_context.VoidType,
                    [LLVMTypeRef.CreatePointer(_context.Int8Type, 0)], false));
            _releaseCaughtException = DeclareNativeFunction(module, "__oxygen_runtime_release_caught_exception",
                LLVMTypeRef.CreateFunction(_context.VoidType,
                    [LLVMTypeRef.CreatePointer(_context.Int8Type, 0)], false));
            _adoptFailureText = DeclareNativeFunction(module, "__oxygen_runtime_adopt_failure_text",
                LLVMTypeRef.CreateFunction(_context.VoidType,
                    [LLVMTypeRef.CreatePointer(_context.Int8Type, 0), LLVMTypeRef.CreatePointer(_context.Int8Type, 0)], false));
            foreach (var cls in concreteClasses)
            {
                foreach (var ctor in cls.Constructors)
                {
                    var type = LLVMTypeRef.CreateFunction(_context.VoidType,
                        new[] { LLVMTypeRef.CreatePointer(_classTypes[cls.QualifiedName], 0) }
                            .Concat(ctor.Parameters.Select(p => ToLlvmType(p.Type, model))).ToArray(), false);
                    _constructors[ConstructorKey(cls, ctor)] = DeclareNativeFunction(module, 
                        OxygenNameMangler.MangleConstructor(cls.ModuleName, cls, ctor), type);
                }

                if (cls.Destructor is not null)
                    _destructors[cls.QualifiedName] = DeclareNativeFunction(module, 
                        OxygenNameMangler.MangleDestructor(cls.ModuleName, cls),
                        LLVMTypeRef.CreateFunction(_context.VoidType,
                            [LLVMTypeRef.CreatePointer(_classTypes[cls.QualifiedName], 0)], false));
            }
            var boundsFailureType = LLVMTypeRef.CreateFunction(
                _context.VoidType, [_context.Int64Type, _context.Int64Type], false);
            _arrayBoundsFailure = DeclareNativeFunction(module, "__oxygen_runtime_array_bounds_failure", boundsFailureType);
            _stringBoundsFailure = DeclareNativeFunction(module, "__oxygen_runtime_string_bounds_failure", boundsFailureType);
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
                if (symbol.IsExternal || IsInterfaceContract(symbol, model) || !DefinesInThisObject(symbol, library, moduleName, methodOwners))
                    continue;
                if (DefinedByLibrary(OwningModuleOf(symbol, methodOwners), IsInstance(symbol, methodOwners), model))
                    continue;
                var function = llvmFunctions[symbol];
                EmitFunction(context, builder, function, symbol, model, llvmFunctions);
                if (library && IsInstance(symbol, methodOwners))
                    function.Linkage = LLVMLinkage.LLVMLinkOnceODRLinkage;
            }
            foreach (var cls in concreteClasses)
            {
                if (library && !DefinesClassInThisObject(cls, moduleName))
                    continue;
                if (DefinedByLibrary(cls.ModuleName, IsInstantiationName(cls.Name), model))
                    continue;
                foreach (var ctor in cls.Constructors)
                {
                    var constructor = _constructors[ConstructorKey(cls, ctor)];
                    EmitConstructor(context, builder, constructor, cls, ctor, model, llvmFunctions);
                    if (library && IsInstantiationName(cls.Name))
                        constructor.Linkage = LLVMLinkage.LLVMLinkOnceODRLinkage;
                }
                if (cls.Destructor is not null)
                {
                    var destructor = _destructors[cls.QualifiedName];
                    EmitDestructor(context, builder, destructor, cls, model, llvmFunctions);
                    if (library && IsInstantiationName(cls.Name))
                        destructor.Linkage = LLVMLinkage.LLVMLinkOnceODRLinkage;
                }
            }

            // The failure helpers are written after the functions, which may have declared them.
            EmitFailureHelpers(model, llvmFunctions);

            // Every type's information is defined here, as a constant, in every object: a library has no startup code of its own.
            foreach (var cls in concreteClasses.Where(cls => !DefinedByLibrary(cls.ModuleName, IsInstantiationName(cls.Name), model)))
                DefineRuntimeTypeInfo(module, cls.QualifiedName, 1, model);
            foreach (var interfaceSymbol in concreteInterfaces.Where(iface =>
                         !DefinedByLibrary(iface.ModuleName, IsInstantiationName(iface.Name), model)))
                DefineRuntimeTypeInfo(module, interfaceSymbol.QualifiedName, 2, model);

            if (!library)
                EmitNativeMain(context, builder, module, model, llvmFunctions);
            AllowNullPointerAccess(context, module);

            LlvmModuleVerifier.Verify(module);

            var metadata = new NativeModuleMetadata
            {
                ModuleName = moduleName,
                TargetTriple = _target.Triple,
                ObjectLayoutVersion = NativeModuleMetadata.CurrentObjectLayoutVersion,
                EntryPoint = "main",
                // The module's own functions, and its externals: an extern is declared here and defined by the C runtime, so the object
                // does not define it, but a program that imports the module must still declare it to call it. Methods and property
                // accessors are described under their types, and the functions of other modules are not this module's to describe.
                Functions = symbols
                    .Where(symbol => !methodOwners.ContainsKey(symbol) && symbol.ModuleName == moduleName)
                    .Select(symbol => new NativeFunctionMetadata
                {
                    ModuleName = symbol.ModuleName,
                    SourceName = symbol.Name,
                    NativeName = MangledNameOf(symbol, methodOwners),
                    ReturnType = OxygenNameMangler.EncodeType(symbol.ReturnType),
                    Visibility = symbol.Visibility.ToString().ToLowerInvariant(),
                    ParameterTypes = symbol.Parameters
                        .Select(parameter => OxygenNameMangler.EncodeType(parameter.Type))
                        .ToArray(),
                    ExternalName = symbol.CInterop?.ExternalName,
                    CallingConvention = symbol.CInterop?.CallingConvention ?? "c"
                }).ToArray(),
                Types = TypeMetadataOf(model, moduleName, methodOwners),
                Generics = generics ?? []
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

    // An access through a null pointer is a hardware fault (docs/exceptions.md), so LLVM must not assume that address 0 is
    // unmapped. Without this attribute an optimized build can turn such an access into a trap, which the fault handler never sees.
    private static void AllowNullPointerAccess(LLVMContextRef context, LLVMModuleRef module)
    {
        var attribute = CreateNullPointerIsValidAttribute(context);
        for (var function = module.FirstFunction; function.Handle != IntPtr.Zero; function = function.NextFunction)
            if (!function.IsDeclaration)
                function.AddAttributeAtIndex(LLVMAttributeIndex.LLVMAttributeFunctionIndex, attribute);
    }

    // LLVM reads this as an enum attribute, the one clang emits for -fno-delete-null-pointer-checks. A string attribute with the
    // same name is ignored.
    private static unsafe LLVMAttributeRef CreateNullPointerIsValidAttribute(LLVMContextRef context)
    {
        ReadOnlySpan<byte> name = "null_pointer_is_valid"u8;
        fixed (byte* pointer = name)
            return context.CreateEnumAttribute(
                LLVM.GetEnumAttributeKindForName((sbyte*)pointer, (nuint)name.Length), 0);
    }

    private static string ConstructorKey(ClassSymbol cls, ConstructorSymbol ctor) =>
            cls.QualifiedName + "(" + string.Join(",", ctor.Parameters.Select(p => OxygenNameMangler.EncodeType(p.Type))) + ")";

    // The native name of a function. A method is named by the class or interface that declares it, not by its module.
    private static string MangledNameOf(
        FunctionSymbol symbol,
        IReadOnlyDictionary<FunctionSymbol, OxygenNameMangler.MethodOwner> methodOwners) =>
        methodOwners.TryGetValue(symbol, out var owner)
            ? OxygenNameMangler.Mangle(owner.ModuleName, symbol, owner)
            : OxygenNameMangler.Mangle(symbol.ModuleName, symbol);

    private static bool IsInterfaceContract(FunctionSymbol symbol, SemanticModel model) =>
        model.Interfaces.Values.Any(iface => iface.Methods.Contains(symbol));

}
