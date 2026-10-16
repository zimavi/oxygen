using System.Globalization;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public static class OxygenNameMangler
{
    public static string Mangle(string moduleName, FunctionSymbol function)
    {
        var parameterTypes = string.Concat(function.Parameters.Select(parameter => EncodeType(parameter.Type)));
        var signature = function.Parameters.Count == 0
            ? string.Empty
            : $"_p{function.Parameters.Count}_{parameterTypes}";
        return $"_Ox_v0_m{moduleName.Length}{moduleName}_f{function.Name.Length}{function.Name}{signature}";
    }

    private static string EncodeType(SemanticType type) => type.Kind == SemanticTypeKind.String
        ? "s"
        : type.Primitive switch
        {
            PrimitiveType.I8 => "i8", PrimitiveType.I16 => "i16", PrimitiveType.I32 => "i32",
            PrimitiveType.I64 => "i64", PrimitiveType.U8 => "u8", PrimitiveType.U16 => "u16",
            PrimitiveType.U32 => "u32", PrimitiveType.U64 => "u64", PrimitiveType.F32 => "f32",
            PrimitiveType.F64 => "f64", PrimitiveType.Bool => "b", PrimitiveType.Void => "v",
            _ => throw new InvalidOperationException($"Unsupported semantic type '{type}'.")
        };
}

public sealed class LlvmModuleGenerator
{
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
                var parameterTypes = symbol.Parameters.Select(parameter => ToLlvmType(parameter.Type)).ToArray();
                var functionType = LLVMTypeRef.CreateFunction(ToLlvmType(symbol.ReturnType), parameterTypes, false);
                llvmFunctions[symbol] = module.AddFunction(OxygenNameMangler.Mangle(moduleName, symbol), functionType);
            }

            foreach (var symbol in symbols)
            {
                EmitFunction(context, builder, llvmFunctions[symbol], symbol, model, llvmFunctions);
            }

            EmitNativeMain(context, builder, module, model, llvmFunctions);

            if (module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out var error))
                throw new InvalidOperationException($"LLVM verification failed: {error}");

            return new GeneratedLlvmModule(context, module, builder);
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
        for (var index = 0; index < symbol.Parameters.Count; index++)
        {
            var parameter = function.GetParam((uint)index);
            parameter.Name = symbol.Parameters[index].Name;
            values.Add(symbol.Parameters[index].Name, parameter);
        }

        var emittedReturnType = symbol.ReturnType;
        var returned = false;
        foreach (var statement in symbol.Declaration.Body.Statements)
        {
            EmitStatement(context, builder, statement, values, model, llvmFunctions, emittedReturnType, ref returned);
        }

        if (!returned)
        {
            if (emittedReturnType.IsVoid)
                builder.BuildRetVoid();
            else
                builder.BuildRet(LLVMValueRef.CreateConstInt(ToLlvmType(emittedReturnType), 0, false));
        }
    }

    private static void EmitStatement(
        LLVMContextRef context,
        LLVMBuilderRef builder,
        Statement statement,
        Dictionary<string, LLVMValueRef> values,
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions,
        SemanticType emittedReturnType,
        ref bool returned)
    {
        switch (statement)
        {
            case VariableDeclarationStatement declaration when declaration.Initializer is not null:
                values[declaration.Name] = EmitExpression(context, builder, declaration.Initializer, values, model, llvmFunctions);
                break;
            case AssignmentStatement assignment:
                values[assignment.Name] = EmitExpression(context, builder, assignment.Value, values, model, llvmFunctions);
                break;
            case ReturnStatement returnStatement:
                if (returnStatement.Value is null)
                {
                    if (emittedReturnType.IsVoid)
                        builder.BuildRetVoid();
                    else
                        builder.BuildRet(LLVMValueRef.CreateConstInt(ToLlvmType(emittedReturnType), 0, false));
                }
                else
                    builder.BuildRet(EmitExpression(context, builder, returnStatement.Value, values, model, llvmFunctions));
                returned = true;
                break;
            case ExpressionStatement expression:
                EmitExpression(context, builder, expression.Expression, values, model, llvmFunctions);
                break;
            case BlockStatement block:
                foreach (var nested in block.Statements)
                    EmitStatement(context, builder, nested, values, model, llvmFunctions, emittedReturnType, ref returned);
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
        SemanticModel model,
        IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions) =>
        expression switch
        {
            IdentifierExpression identifier => values[identifier.Name],
            IntegerLiteralExpression integer => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, ulong.Parse(integer.Lexeme, CultureInfo.InvariantCulture), false),
            FloatingPointLiteralExpression floating => LLVMValueRef.CreateConstRealOfString(LLVMTypeRef.Double, floating.Lexeme),
            BooleanLiteralExpression boolean => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, boolean.Value ? 1UL : 0UL, false),
            BinaryExpression binary => EmitBinary(context, builder, binary, values, model, llvmFunctions),
            UnaryExpression unary => EmitUnary(context, builder, unary, values, model, llvmFunctions),
            CallExpression call => EmitCall(context, builder, call, values, model, llvmFunctions),
            _ => throw new InvalidOperationException($"Unsupported expression '{expression.GetType().Name}'.")
        };

    private static LLVMValueRef EmitBinary(LLVMContextRef context, LLVMBuilderRef builder, BinaryExpression expression, IReadOnlyDictionary<string, LLVMValueRef> values, SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var left = EmitExpression(context, builder, expression.Left, values, model, llvmFunctions);
        var right = EmitExpression(context, builder, expression.Right, values, model, llvmFunctions);
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

    private static LLVMValueRef EmitUnary(LLVMContextRef context, LLVMBuilderRef builder, UnaryExpression expression, IReadOnlyDictionary<string, LLVMValueRef> values, SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var operand = EmitExpression(context, builder, expression.Operand, values, model, llvmFunctions);
        var isFloatingPoint = model.ExpressionTypes[expression].Primitive is PrimitiveType.F32 or PrimitiveType.F64;
        return expression.Operator == TokenKind.Minus
            ? isFloatingPoint
                ? builder.BuildFNeg(operand, "fneg")
                : builder.BuildNeg(operand, "neg")
            : throw new InvalidOperationException($"Unsupported unary operator '{expression.Operator}'.");
    }

    private static LLVMValueRef EmitCall(LLVMContextRef context, LLVMBuilderRef builder, CallExpression expression, IReadOnlyDictionary<string, LLVMValueRef> values, SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var function = model.Functions.Values.Single(symbol =>
            symbol.Name == expression.Name &&
            symbol.Parameters.Count == expression.Arguments.Count &&
            symbol.Parameters.Select(parameter => parameter.Type)
                .SequenceEqual(expression.Arguments.Select(argument => model.ExpressionTypes[argument])));
        var arguments = expression.Arguments.Select(argument => EmitExpression(context, builder, argument, values, model, llvmFunctions)).ToArray();
        return builder.BuildCall2(llvmFunctions[function].FunctionType, llvmFunctions[function], arguments, "call");
    }

    private static LLVMTypeRef ToLlvmType(SemanticType type) => type.Primitive switch
    {
        PrimitiveType.I8 or PrimitiveType.U8 => LLVMTypeRef.Int8,
        PrimitiveType.I16 or PrimitiveType.U16 => LLVMTypeRef.Int16,
        PrimitiveType.I32 or PrimitiveType.U32 => LLVMTypeRef.Int32,
        PrimitiveType.I64 or PrimitiveType.U64 => LLVMTypeRef.Int64,
        PrimitiveType.Bool => LLVMTypeRef.Int1,
        PrimitiveType.F32 => LLVMTypeRef.Float,
        PrimitiveType.F64 => LLVMTypeRef.Double,
        PrimitiveType.Void => LLVMTypeRef.Void,
        _ => throw new InvalidOperationException($"Unsupported semantic type '{type}'.")
    };
}

public sealed class GeneratedLlvmModule(LLVMContextRef context, LLVMModuleRef module, LLVMBuilderRef builder) : IDisposable
{
    public LLVMModuleRef Module => module;
    public override string ToString() => module.PrintToString();

    public void BuildNative(NativeBuildOptions options, INativeToolRunner? toolRunner = null) =>
        new NativeBuildPipeline(toolRunner).Build(this, options);

    public void Dispose()
    {
        builder.Dispose();
        module.Dispose();
        context.Dispose();
    }
}
