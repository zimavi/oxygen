using System.Globalization;
using System.Text.Json;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
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
            var common = CommonNumericType(leftType, rightType);
            left = CoerceValue(builder, left, leftType, common, model);
            right = CoerceValue(builder, right, rightType, common, model);
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

    private static SemanticType CommonNumericType(SemanticType left, SemanticType right)
    {
        if (left == right)
            return left;
        if (left.Primitive is PrimitiveType.F64 || right.Primitive is PrimitiveType.F64)
            return SemanticType.FromPrimitive(PrimitiveType.F64);
        if (left.Primitive is PrimitiveType.F32 || right.Primitive is PrimitiveType.F32)
            return SemanticType.FromPrimitive(PrimitiveType.F32);
        var leftBits = IntegerBitWidth(left.Primitive!.Value);
        var rightBits = IntegerBitWidth(right.Primitive!.Value);
        if (leftBits != rightBits)
            return leftBits > rightBits ? left : right;
        return left;
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

}
