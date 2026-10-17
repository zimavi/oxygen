using System.Globalization;
using System.Text.Json;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public static class OxygenNameMangler
{
    public static string Mangle(string moduleName, FunctionSymbol function)
    {
        if (function.IsExternal && !string.IsNullOrWhiteSpace(function.NativeName))
            return function.NativeName;
        var parameterTypes = string.Concat(function.Parameters.Select(parameter => EncodeType(parameter.Type)));
        var signature = function.Parameters.Count == 0
            ? string.Empty
            : $"_p{function.Parameters.Count}_{parameterTypes}";
        var specialization = function.IsSpecialized
            ? $"_g{string.Concat(function.SpecializationArguments!.Select(EncodeType))}"
            : string.Empty;
        return $"_Ox_v0_m{moduleName.Length}{moduleName}_f{function.Name.Length}{function.Name}{signature}{specialization}";
    }

    public static string EncodeType(SemanticType type) => type.Kind == SemanticTypeKind.String
        ? "s"
        : type.IsArray ? $"a{type.ArrayLength}_{EncodeType(type.ElementType!)}"
        : type.Primitive switch
        {
            PrimitiveType.I8 => "i8", PrimitiveType.I16 => "i16", PrimitiveType.I32 => "i32",
            PrimitiveType.I64 => "i64", PrimitiveType.U8 => "u8", PrimitiveType.U16 => "u16",
            PrimitiveType.U32 => "u32", PrimitiveType.U64 => "u64", PrimitiveType.F32 => "f32",
            PrimitiveType.F64 => "f64", PrimitiveType.Bool => "b", PrimitiveType.Void => "v",
            _ when type.Kind == SemanticTypeKind.Pointer => "p" + EncodeType(type.Pointee!),
            _ when type.Kind == SemanticTypeKind.GenericParameter => "g" + type.GenericParameterName!.Length + type.GenericParameterName,
            _ when type.Kind == SemanticTypeKind.Enum => "e" + type.EnumName!.Length + type.EnumName,
            _ when type.Kind == SemanticTypeKind.Union => "u" + type.EnumName!.Length + type.EnumName,
            _ => throw new InvalidOperationException($"Unsupported semantic type '{type}'.")
        };

    public static string MangleConstructor(string moduleName, ClassSymbol cls, ConstructorSymbol constructor) =>
        $"_Ox_v0_m{moduleName.Length}{moduleName}_c{cls.Name.Length}{cls.Name}_p{constructor.Parameters.Count}_{string.Concat(constructor.Parameters.Select(p => EncodeType(p.Type)))}";

    public static string MangleDestructor(string moduleName, ClassSymbol cls) =>
        $"_Ox_v0_m{moduleName.Length}{moduleName}_d{cls.Name.Length}{cls.Name}";
}

public sealed class LlvmModuleGenerator
{
    private static LLVMValueRef _arrayBoundsFailure;
    private static LLVMValueRef _enumCastFailure;
    private static LLVMValueRef _isInstance;
    private static LLVMValueRef _invalidCast;
    private static LLVMValueRef _currentFunction;
    private static readonly Dictionary<string, LLVMTypeRef> _classTypes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LLVMValueRef> _classTypeNames = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LLVMValueRef> _constructors = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LLVMValueRef> _destructors = new(StringComparer.Ordinal);
    private static LLVMValueRef _oxygenAlloc;
    private static LLVMValueRef _oxygenFree;
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
            _classTypeNames.Clear();
            _constructors.Clear();
            _destructors.Clear();
            foreach (var cls in model.Classes.Values)
            {
                var fields = new[] { LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0) }
                    .Concat(cls.Fields.Select(field => ToLlvmType(field.Type, model))).ToArray();
                _classTypes[cls.Name] = LLVMTypeRef.CreateStruct(fields, false);
            }
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
            foreach (var cls in model.Classes.Values)
            {
                foreach (var ctor in cls.Constructors)
                {
                    var type = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void,
                        new[] { ToLlvmType(SemanticType.Class(cls.Name), model) }
                            .Concat(ctor.Parameters.Select(p => ToLlvmType(p.Type, model))).ToArray(), false);
                    _constructors[ConstructorKey(cls, ctor)] = module.AddFunction(
                        OxygenNameMangler.MangleConstructor(cls.ModuleName, cls, ctor), type);
                }
                if (cls.Destructor is not null)
                    _destructors[cls.Name] = module.AddFunction(
                        OxygenNameMangler.MangleDestructor(cls.ModuleName, cls),
                        LLVMTypeRef.CreateFunction(LLVMTypeRef.Void,
                            [ToLlvmType(SemanticType.Class(cls.Name), model)], false));
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
            var entry = function.AppendBasicBlock("entry");
            builder.PositionAtEnd(entry);
            var values = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
            var storage = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
            var thisValue = function.GetParam(0);
            values["this"] = thisValue;
            InitializeClassFieldStorage(builder, values, storage, cls);
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
                    new Stack<(LLVMBasicBlockRef, LLVMBasicBlockRef)>());
            builder.BuildRetVoid();
        }

    private static void EmitDestructor(LLVMContextRef context, LLVMBuilderRef builder, LLVMValueRef function,
            ClassSymbol cls, SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
        {
            var entry = function.AppendBasicBlock("entry");
            builder.PositionAtEnd(entry);
            _currentFunction = function;
            var values = new Dictionary<string, LLVMValueRef> { ["this"] = function.GetParam(0) };
            var storage = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
            InitializeClassFieldStorage(builder, values, storage, cls);
            foreach (var statement in cls.Destructor!.Body.Statements)
                EmitStatement(context, builder, function, statement, values, storage,
                    new HashSet<string>(), model, llvmFunctions, SemanticType.FromPrimitive(PrimitiveType.Void),
                    new Stack<(LLVMBasicBlockRef, LLVMBasicBlockRef)>());
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
        nativeMain.GetParam(0).Name = "argc";
        nativeMain.GetParam(1).Name = "argv";

        var entry = nativeMain.AppendBasicBlock("entry");
        builder.PositionAtEnd(entry);
        builder.BuildCall2(runtimeInitializeType, runtimeInitialize, []);

        var oxygenMain = model.MainFunction
            ?? throw new InvalidOperationException("Semantic model does not contain a main function.");
        var oxygenMainType = llvmFunctions[oxygenMain].FunctionType;
        var result = builder.BuildCall2(
            oxygenMainType,
            llvmFunctions[oxygenMain],
            Array.Empty<LLVMValueRef>(),
            oxygenMainType.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind ? string.Empty : "oxygen.main");
        if (oxygenMainType.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind)
            builder.BuildRet(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false));
        else
            builder.BuildRet(result);
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
        var entry = function.AppendBasicBlock("entry");
        builder.PositionAtEnd(entry);
        var values = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
        var storage = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
        var storageRequirements = CollectStorageRequirements(symbol.Declaration.Body);
        var parameterOffset = IsClassMethod(symbol, model) ? 1 : 0;
        if (parameterOffset != 0)
        {
            values["this"] = function.GetParam(0);
            var className = symbol.ModuleName[(symbol.ModuleName.LastIndexOf('.') + 1)..];
            InitializeClassFieldStorage(builder, values, storage, model.Classes[className]);
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
        }

        var emittedReturnType = symbol.ReturnType;
        var returned = false;
        var loops = new Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue)>();
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
        ClassSymbol cls)
    {
        var thisValue = values["this"];
        for (var index = 0; index < cls.Fields.Count; index++)
        {
            var fieldAddress = builder.BuildStructGEP2(
                _classTypes[cls.Name],
                thisValue,
                (uint)index + 1,
                $"field.{cls.Fields[index].Name}");
            storage[cls.Fields[index].Name] = fieldAddress;
            storage["this." + cls.Fields[index].Name] = fieldAddress;
        }
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
        Stack<(LLVMBasicBlockRef Break, LLVMBasicBlockRef Continue)> loops)
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
                return false;
            case AssignmentStatement assignment:
                var assigned = EmitExpression(context, builder, assignment.Value, values, storage, model, llvmFunctions);
                if (assignment.Target is IdentifierExpression identifier)
                    builder.BuildStore(CoerceValue(builder, assigned, model.ExpressionTypes[assignment.Value],
                        model.ExpressionTypes[assignment.Target], model), storage[identifier.Name]);
                else if (assignment.Target is MemberAccessExpression member
                    && member.Target is IdentifierExpression targetIdentifier)
                {
                    var targetType = model.ExpressionTypes[member.Target];
                    if (targetType.IsClass)
                    {
                        var objectValue = values.TryGetValue(targetIdentifier.Name, out var direct)
                            ? direct
                            : builder.BuildLoad2(ToLlvmType(targetType, model), storage[targetIdentifier.Name], targetIdentifier.Name);
                        var fieldIndex = model.Classes[targetType.StructName!].Fields
                            .Select((field, index) => (field, index))
                            .First(pair => pair.field.Name == member.MemberName).index + 1;
                        var address = builder.BuildStructGEP2(_classTypes[targetType.StructName!], objectValue,
                            (uint)fieldIndex, $"field.{member.MemberName}");
                        builder.BuildStore(CoerceValue(builder, assigned, model.ExpressionTypes[assignment.Value],
                            model.ExpressionTypes[assignment.Target], model), address);
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
                if (returnStatement.Value is null)
                {
                    if (emittedReturnType.IsVoid)
                        builder.BuildRetVoid();
                    else
                        builder.BuildRet(LLVMValueRef.CreateConstInt(ToLlvmType(emittedReturnType, model), 0, false));
                }
                else
                    builder.BuildRet(CoerceValue(builder,
                        EmitExpression(context, builder, returnStatement.Value, values, storage, model, llvmFunctions),
                        model.ExpressionTypes[returnStatement.Value], emittedReturnType, model));
                return true;
            case ExpressionStatement expression:
                EmitExpression(context, builder, expression.Expression, values, storage, model, llvmFunctions);
                return false;
            case BlockStatement block:
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
                        return true;
                return false;
            case IfStatement conditional:
            {
                var thenBlock = function.AppendBasicBlock("if.then");
                var elseBlock = function.AppendBasicBlock("if.else");
                var mergeBlock = function.AppendBasicBlock("if.end");
                var condition = EmitExpression(context, builder, conditional.Condition, values, storage, model, llvmFunctions);
                builder.BuildCondBr(condition, thenBlock, elseBlock);
                builder.PositionAtEnd(thenBlock);
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
                var value = EmitExpression(context, builder, switchStatement.Value, values, storage, model, llvmFunctions);
                var union = model.Unions[model.ExpressionTypes[switchStatement.Value].EnumName!];
                var end = function.AppendBasicBlock("switch.end");
                var next = function.AppendBasicBlock("switch.case0");
                builder.BuildBr(next);
                var terminated = true;
                for (var index = 0; index < switchStatement.Cases.Count; index++)
                {
                    var item = switchStatement.Cases[index];
                    var caseBlock = next;
                    next = index + 1 < switchStatement.Cases.Count
                        ? function.AppendBasicBlock($"switch.case{index + 1}")
                        : end;
                    builder.PositionAtEnd(caseBlock);
                    var tag = builder.BuildExtractValue(value, 0, "union.tag");
                    var caseIndex = union.Cases.Select((candidate, candidateIndex) => (candidate, candidateIndex))
                        .First(pair => pair.candidate.Name == item.CaseName).candidateIndex;
                    var match = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, tag,
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)caseIndex, false), "union.match");
                    var bodyBlock = function.AppendBasicBlock($"switch.body{index}");
                    builder.BuildCondBr(match, bodyBlock, next);
                    builder.PositionAtEnd(bodyBlock);
                    if (item.BindingName is not null)
                    {
                        var payload = builder.BuildExtractValue(value, 1, "union.payload");
                        values[item.BindingName] = payload;
                    }
                    var bodyTerminated = EmitStatement(context, builder, function, item.Body, values, storage,
                        storageRequirements, model, llvmFunctions, emittedReturnType, loops);
                    terminated &= bodyTerminated;
                    if (!bodyTerminated) builder.BuildBr(end);
                }
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
                loops.Push((endBlock, conditionBlock));
                var bodyTerminated = EmitStatement(context, builder, function, whileStatement.Body, values, storage, storageRequirements, model, llvmFunctions, emittedReturnType, loops);
                loops.Pop();
                if (!bodyTerminated) builder.BuildBr(conditionBlock);
                builder.PositionAtEnd(endBlock);
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
                loops.Push((endBlock, incrementBlock));
                var bodyTerminated = EmitStatement(context, builder, function, forStatement.Body, values, storage, storageRequirements, model, llvmFunctions, emittedReturnType, loops);
                loops.Pop();
                if (!bodyTerminated) builder.BuildBr(incrementBlock);
                builder.PositionAtEnd(incrementBlock);
                var incrementTerminated = forStatement.Increment is not null
                    && EmitStatement(context, builder, function, forStatement.Increment, values, storage, storageRequirements, model, llvmFunctions, emittedReturnType, loops);
                if (!incrementTerminated) builder.BuildBr(conditionBlock);
                builder.PositionAtEnd(endBlock);
                return false;
            }
            case BreakStatement:
                builder.BuildBr(loops.Peek().Break);
                return true;
            case ContinueStatement:
                builder.BuildBr(loops.Peek().Continue);
                return true;
            default:
                throw new InvalidOperationException($"Unsupported statement '{statement.GetType().Name}'.");
        }
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
            NewExpression creation => EmitNewExpression(context, builder, creation, values, storage, model, llvmFunctions),
            IndexExpression index => builder.BuildLoad2(ToLlvmType(model.ExpressionTypes[index], model),
                EmitArrayAddress(context, builder, index, values, storage, model, llvmFunctions), "array.element"),
            CallExpression { Name: "length", Arguments: [var target] } => LLVMValueRef.CreateConstInt(
                LLVMTypeRef.Int32, (ulong)model.ExpressionTypes[target].ArrayLength, false),
            MemberAccessExpression member => EmitMemberAccess(context, builder, member, values, storage, model, llvmFunctions),
            CastExpression cast => EmitCast(context, builder, cast, values, storage, model, llvmFunctions),
            SizeOfExpression sizeOf => EmitSizeOf(sizeOf, model),
            BinaryExpression binary => EmitBinary(context, builder, binary, values, storage, model, llvmFunctions),
            UnaryExpression unary => EmitUnary(context, builder, unary, values, storage, model, llvmFunctions),
            CallExpression call => EmitCall(context, builder, call, values, storage, model, llvmFunctions),
            _ => throw new InvalidOperationException($"Unsupported expression '{expression.GetType().Name}'.")
        };

    private static LLVMValueRef EmitNewExpression(
        LLVMContextRef context, LLVMBuilderRef builder, NewExpression expression,
        IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var className = expression.Type.NamedType
            ?? throw new InvalidOperationException("Class construction requires a named type.");
        var cls = model.Classes[className];
        var classType = ToLlvmType(SemanticType.Class(className), model);
        var raw = builder.BuildCall2(_oxygenAlloc.FunctionType, _oxygenAlloc,
            new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, SizeOf(SemanticType.Class(className), model), false) },
            "object");
        var instance = builder.BuildBitCast(raw, classType, "object.cast");
        var vtable = builder.BuildStructGEP2(_classTypes[className], instance, 0, "object.vtable");
        builder.BuildStore(GetTypeName(builder, className), vtable);
        var ctor = cls.Constructors.First(candidate => candidate.Parameters.Count == expression.Arguments.Count);
        var args = new List<LLVMValueRef> { instance };
        for (var i = 0; i < expression.Arguments.Count; i++)
            args.Add(CoerceValue(builder, EmitExpression(context, builder, expression.Arguments[i], values, storage, model, llvmFunctions),
                model.ExpressionTypes[expression.Arguments[i]], ctor.Parameters[i].Type, model));
        builder.BuildCall2(_constructors[ConstructorKey(cls, ctor)].FunctionType,
            _constructors[ConstructorKey(cls, ctor)], args.ToArray(), (ReadOnlySpan<char>)string.Empty);
        return instance;
    }

    private static LLVMValueRef EmitUnionConstruction(
        LLVMContextRef context, LLVMBuilderRef builder, UnionConstructionExpression construction,
        IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var union = model.Unions[construction.UnionName];
        var index = union.Cases.Select((item, i) => (item, i)).First(pair => pair.item.Name == construction.CaseName).i;
        var result = LLVMValueRef.CreateConstNull(ToLlvmType(SemanticType.Union(construction.UnionName), model));
        result = builder.BuildInsertValue(result,
            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)index, false), 0, "union.tag");
        if (construction.Arguments.Count == 1)
            result = builder.BuildInsertValue(result,
                EmitExpression(context, builder, construction.Arguments[0], values, storage, model, llvmFunctions), 1, "union.payload");
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
        var arrayType = model.ExpressionTypes[index.Target];
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
        if (IsReferenceType(targetType) && IsReferenceType(operandType))
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

    private static bool IsReferenceType(SemanticType type) => type.IsClass || type.IsInterface;

    private static LLVMValueRef GetTypeName(LLVMBuilderRef builder, string typeName)
    {
        if (_classTypeNames.TryGetValue(typeName, out var value))
            return value;

        value = builder.BuildGlobalStringPtr(typeName, $"oxygen.type.{typeName}");
        _classTypeNames[typeName] = value;
        return value;
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
                : builder.BuildGlobalStringPtr(source.StructName!, "oxygen.cast.source");
            builder.BuildCall2(_invalidCast.FunctionType, _invalidCast,
                [sourceName, GetTypeName(builder, target.StructName!)]);
            builder.BuildUnreachable();
            builder.PositionAtEnd(continueBlock);
        }

        return target.IsClass
            ? builder.BuildBitCast(objectValue, ToLlvmType(target, model), "class.cast")
            : objectValue;
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
            SemanticTypeKind.Class => (ulong)(8 + model.Classes[type.StructName!].Fields.Sum(field => (long)SizeOf(field.Type, model))),
            SemanticTypeKind.Interface => 16,
            SemanticTypeKind.String => 16,
            SemanticTypeKind.Array => checked((ulong)type.ArrayLength * SizeOf(type.ElementType!, model)),
            SemanticTypeKind.Struct => (ulong)model.Structs[type.StructName!].Fields
                .Sum(field => checked((long)SizeOf(field.Type, model))),
            SemanticTypeKind.Union => 8,
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
            _ => throw new InvalidOperationException($"Unsupported binary operator '{expression.Operator}'.")
        };
    }

    private static LLVMValueRef EmitUnary(LLVMContextRef context, LLVMBuilderRef builder, UnaryExpression expression, IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage, SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
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

    private static LLVMValueRef EmitCall(LLVMContextRef context, LLVMBuilderRef builder, CallExpression expression, IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage, SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var parts = expression.Name.Split('.');
        if (parts.Length == 2 && model.Unions.TryGetValue(parts[0], out var union))
        {
            var index = union.Cases.Select((item, i) => (item, i)).First(pair => pair.item.Name == parts[1]).i;
            var result = LLVMValueRef.CreateConstNull(ToLlvmType(SemanticType.Union(parts[0]), model));
            result = builder.BuildInsertValue(result, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)index, false), 0, "union.tag");
            if (expression.Arguments.Count == 1)
                result = builder.BuildInsertValue(result,
                    EmitExpression(context, builder, expression.Arguments[0], values, storage, model, llvmFunctions), 1, "union.payload");
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

            arguments.Add(value);
        }

        var resultName = function.ReturnType.IsVoid ? string.Empty : "call";
        return builder.BuildCall2(llvmFunctions[function].FunctionType, llvmFunctions[function], arguments.ToArray(), resultName);
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
        _ when type.Kind == SemanticTypeKind.Class => LLVMTypeRef.CreatePointer(_classTypes[type.StructName!], 0),
        _ when type.Kind == SemanticTypeKind.Interface => LLVMTypeRef.CreateStruct(
            [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)], false),
        _ when type.Kind == SemanticTypeKind.Struct => LLVMTypeRef.CreateStruct(
            model.Structs[type.StructName!].Fields.Select(field => ToLlvmType(field.Type, model)).ToArray(),
            model.Structs[type.StructName!].IsPacked),
        _ when type.Kind == SemanticTypeKind.String => ToLlvmStringType(),
        _ when type.Kind == SemanticTypeKind.Array => LLVMTypeRef.CreateArray(ToLlvmType(type.ElementType!, model), (uint)type.ArrayLength),
        _ when type.Kind == SemanticTypeKind.Enum => ToLlvmType(
            SemanticType.FromPrimitive(model.Enums[type.EnumName!].UnderlyingType), model),
        _ when type.Kind == SemanticTypeKind.Union => LLVMTypeRef.CreateStruct([LLVMTypeRef.Int32, LLVMTypeRef.Int32], false),
        _ => throw new InvalidOperationException($"Unsupported semantic type '{type}'.")
    };

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

public sealed class GeneratedLlvmModule(
    LLVMContextRef context,
    LLVMModuleRef module,
    LLVMBuilderRef builder,
    NativeModuleMetadata metadata) : IDisposable
{
    public LLVMModuleRef Module => module;
    public NativeModuleMetadata Metadata => metadata;
    public override string ToString() => module.PrintToString();

    public void BuildNative(NativeBuildOptions options, INativeToolRunner? toolRunner = null) =>
        new NativeBuildPipeline(toolRunner).Build(this, options);

    public void WriteMetadata(string path) => NativeModuleMetadataSerializer.Write(path, metadata);

    public void Dispose()
    {
        builder.Dispose();
        module.Dispose();
        context.Dispose();
    }
}
