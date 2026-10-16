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
        return $"_Ox_v0_m{moduleName.Length}{moduleName}_f{function.Name.Length}{function.Name}{signature}";
    }

    public static string EncodeType(SemanticType type) => type.Kind == SemanticTypeKind.String
        ? "s"
        : type.Primitive switch
        {
            PrimitiveType.I8 => "i8", PrimitiveType.I16 => "i16", PrimitiveType.I32 => "i32",
            PrimitiveType.I64 => "i64", PrimitiveType.U8 => "u8", PrimitiveType.U16 => "u16",
            PrimitiveType.U32 => "u32", PrimitiveType.U64 => "u64", PrimitiveType.F32 => "f32",
            PrimitiveType.F64 => "f64", PrimitiveType.Bool => "b", PrimitiveType.Void => "v",
            _ when type.Kind == SemanticTypeKind.Pointer => "p" + EncodeType(type.Pointee!),
            _ => throw new InvalidOperationException($"Unsupported semantic type '{type}'.")
        };
}

public sealed class LlvmModuleGenerator
{
    private static LLVMTypeRef ToLlvmStringType() =>
        LLVMTypeRef.CreateStruct([LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.Int64], false);

    public GeneratedLlvmModule Generate(string moduleName, SemanticModel model)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        ArgumentNullException.ThrowIfNull(model);

        var context = LLVMContextRef.Create();
        var module = context.CreateModuleWithName(moduleName);
        var builder = context.CreateBuilder();
        var symbols = model.Functions.Values.ToList();
        var llvmFunctions = new Dictionary<FunctionSymbol, LLVMValueRef>();

        try
        {
            foreach (var symbol in symbols)
            {
                var functionType = ToLlvmFunctionType(symbol, model);
                llvmFunctions[symbol] = module.AddFunction(OxygenNameMangler.Mangle(symbol.ModuleName, symbol), functionType);
            }

            foreach (var symbol in symbols)
            {
                if (!symbol.IsExternal)
                    EmitFunction(context, builder, llvmFunctions[symbol], symbol, model, llvmFunctions);
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
        var entry = function.AppendBasicBlock("entry");
        builder.PositionAtEnd(entry);
        var values = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
        var storage = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
        var storageRequirements = CollectStorageRequirements(symbol.Declaration.Body);
        for (var index = 0; index < symbol.Parameters.Count; index++)
        {
            var parameter = function.GetParam((uint)index);
            parameter.Name = symbol.Parameters[index].Name;
            if (storageRequirements.Contains(symbol.Parameters[index].Name))
            {
                var parameterStorage = builder.BuildAlloca(
                    ToLlvmType(symbol.Parameters[index].Type, model),
                    parameter.Name + ".addr");
                builder.BuildStore(parameter, parameterStorage);
                storage.Add(symbol.Parameters[index].Name, parameterStorage);
            }
            else
            {
                values.Add(symbol.Parameters[index].Name, parameter);
            }
        }

        var emittedReturnType = symbol.ReturnType;
        var returned = false;
        foreach (var statement in symbol.Declaration.Body.Statements)
        {
            EmitStatement(
                context,
                builder,
                statement,
                values,
                storage,
                storageRequirements,
                model,
                llvmFunctions,
                emittedReturnType,
                ref returned);
        }

        if (!returned)
        {
            if (emittedReturnType.IsVoid)
                builder.BuildRetVoid();
            else
                builder.BuildRet(LLVMValueRef.CreateConstInt(ToLlvmType(emittedReturnType, model), 0, false));
        }
    }

    private static void EmitStatement(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        Statement statement,
        Dictionary<string, LLVMValueRef> values,
        Dictionary<string, LLVMValueRef> storage,
        IReadOnlySet<string> storageRequirements,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions,
        SemanticType emittedReturnType,
        ref bool returned)
    {
        switch (statement)
        {
            case VariableDeclarationStatement declaration:
                var declaredType = declaration.Initializer is not null && model.ExpressionTypes.TryGetValue(
                    declaration.Initializer, out var initializerType)
                    ? initializerType
                    : FromTypeSyntax(declaration.Type);
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
                        builder.BuildStore(initializer, declarationStorage);
                        storage[declaration.Name] = declarationStorage;
                    }
                    else
                    {
                        values[declaration.Name] = initializer;
                    }
                }
                else
                {
                    var declarationStorage = builder.BuildAlloca(
                        ToLlvmType(declaredType, model),
                        declaration.Name + ".addr");
                    storage[declaration.Name] = declarationStorage;
                }
                break;
            case AssignmentStatement assignment:
                var assigned = EmitExpression(context, builder, assignment.Value, values, storage, model, llvmFunctions);
                if (assignment.Target is IdentifierExpression identifier)
                    builder.BuildStore(assigned, storage[identifier.Name]);
                else if (assignment.Target is MemberAccessExpression member
                    && member.Target is IdentifierExpression targetIdentifier)
                {
                    var current = builder.BuildLoad2(
                        ToLlvmType(model.ExpressionTypes[member.Target], model),
                        storage[targetIdentifier.Name],
                        targetIdentifier.Name);
                    var fieldIndex = model.Structs[model.ExpressionTypes[member.Target].StructName!].Fields
                        .Select((field, index) => (field, index))
                        .First(pair => pair.field.Name == member.MemberName).index;
                    var updated = builder.BuildInsertValue(current, assigned, (uint)fieldIndex, "struct.update");
                    builder.BuildStore(updated, storage[targetIdentifier.Name]);
                }
                else if (assignment.Target is UnaryExpression { Operator: TokenKind.Star, Operand: var operand })
                    builder.BuildStore(assigned, EmitExpression(context, builder, operand, values, storage, model, llvmFunctions));
                else
                    throw new InvalidOperationException("Unsupported assignment target.");
                break;
            case ReturnStatement returnStatement:
                if (returnStatement.Value is null)
                {
                    if (emittedReturnType.IsVoid)
                        builder.BuildRetVoid();
                    else
                        builder.BuildRet(LLVMValueRef.CreateConstInt(ToLlvmType(emittedReturnType, model), 0, false));
                }
                else
                    builder.BuildRet(EmitExpression(context, builder, returnStatement.Value, values, storage, model, llvmFunctions));
                returned = true;
                break;
            case ExpressionStatement expression:
                EmitExpression(context, builder, expression.Expression, values, storage, model, llvmFunctions);
                break;
            case BlockStatement block:
                foreach (var nested in block.Statements)
                    EmitStatement(
                        context,
                        builder,
                        nested,
                        values,
                        storage,
                        storageRequirements,
                        model,
                        llvmFunctions,
                        emittedReturnType,
                        ref returned);
                break;
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
            IntegerLiteralExpression integer when model.ExpressionTypes[integer].IsPointer =>
                LLVMValueRef.CreateConstNull(ToLlvmType(model.ExpressionTypes[integer], model)),
            IntegerLiteralExpression integer => LLVMValueRef.CreateConstInt(
                LLVMTypeRef.Int32, ulong.Parse(integer.Lexeme, CultureInfo.InvariantCulture), false),
            FloatingPointLiteralExpression floating => LLVMValueRef.CreateConstRealOfString(LLVMTypeRef.Double, floating.Lexeme),
            StringLiteralExpression literal => EmitStringLiteral(builder, literal),
            BooleanLiteralExpression boolean => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, boolean.Value ? 1UL : 0UL, false),
            StructLiteralExpression literal => EmitStructLiteral(context, builder, literal, values, storage, model, llvmFunctions),
            MemberAccessExpression member => EmitMemberAccess(context, builder, member, values, storage, model, llvmFunctions),
            BinaryExpression binary => EmitBinary(context, builder, binary, values, storage, model, llvmFunctions),
            UnaryExpression unary => EmitUnary(context, builder, unary, values, storage, model, llvmFunctions),
            CallExpression call => EmitCall(context, builder, call, values, storage, model, llvmFunctions),
            _ => throw new InvalidOperationException($"Unsupported expression '{expression.GetType().Name}'.")
        };

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
        var target = EmitExpression(context, builder, member.Target, values, storage, model, llvmFunctions);
        var targetType = model.ExpressionTypes[member.Target];
        var index = model.Structs[targetType.StructName!].Fields
            .Select((field, index) => (field, index))
            .First(pair => pair.field.Name == member.MemberName).index;
        return builder.BuildExtractValue(target, (uint)index, $"struct.{member.MemberName}");
    }

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
        var function = model.ResolvedCalls[expression];
        var arguments = new LLVMValueRef[expression.Arguments.Count];
        for (var index = 0; index < expression.Arguments.Count; index++)
        {
            var argument = expression.Arguments[index];
            var value = EmitExpression(context, builder, argument, values, storage, model, llvmFunctions);
            if (UsesPointerStringAbi(function) && function.Parameters[index].Type.Kind == SemanticTypeKind.String)
            {
                var storagePointer = builder.BuildAlloca(ToLlvmStringType(), "cabi.string");
                builder.BuildStore(value, storagePointer);
                value = storagePointer;
            }

            arguments[index] = value;
        }

        var resultName = function.ReturnType.IsVoid ? string.Empty : "call";
        return builder.BuildCall2(llvmFunctions[function].FunctionType, llvmFunctions[function], arguments, resultName);
    }

    private static LLVMTypeRef ToLlvmFunctionType(FunctionSymbol symbol, SemanticModel model)
    {
        var parameterTypes = symbol.Parameters
            .Select(parameter => UsesPointerStringAbi(symbol) && parameter.Type.Kind == SemanticTypeKind.String
                ? LLVMTypeRef.CreatePointer(ToLlvmStringType(), 0)
                : ToLlvmType(parameter.Type, model))
            .ToArray();
        return LLVMTypeRef.CreateFunction(ToLlvmType(symbol.ReturnType, model), parameterTypes, false);
    }

    private static bool UsesPointerStringAbi(FunctionSymbol symbol) =>
        symbol.IsExternal &&
        symbol.NativeName is "oxygen_write_stdout" or "oxygen_write_stderr";

    private static LLVMTypeRef ToLlvmType(SemanticType type, SemanticModel model) => type.Primitive switch
    {
        PrimitiveType.I8 or PrimitiveType.U8 => LLVMTypeRef.Int8,
        PrimitiveType.I16 or PrimitiveType.U16 => LLVMTypeRef.Int16,
        PrimitiveType.I32 or PrimitiveType.U32 => LLVMTypeRef.Int32,
        PrimitiveType.I64 or PrimitiveType.U64 => LLVMTypeRef.Int64,
        PrimitiveType.Bool => LLVMTypeRef.Int1,
        PrimitiveType.F32 => LLVMTypeRef.Float,
        PrimitiveType.F64 => LLVMTypeRef.Double,
        PrimitiveType.Void => LLVMTypeRef.Void,
        _ when type.Kind == SemanticTypeKind.Pointer => LLVMTypeRef.CreatePointer(ToLlvmType(type.Pointee!, model), 0),
        _ when type.Kind == SemanticTypeKind.Struct => LLVMTypeRef.CreateStruct(
            model.Structs[type.StructName!].Fields.Select(field => ToLlvmType(field.Type, model)).ToArray(),
            model.Structs[type.StructName!].IsPacked),
        _ when type.Kind == SemanticTypeKind.String => ToLlvmStringType(),
        _ => throw new InvalidOperationException($"Unsupported semantic type '{type}'.")
    };

    private static SemanticType FromTypeSyntax(TypeSyntax syntax)
    {
        if (syntax.IsString)
            return syntax.IsPointer
                ? SemanticType.PointerTo(FromTypeSyntax(syntax.Pointee!))
                : SemanticType.String;
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
