using System.Globalization;
using System.Text;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Semantic;

namespace Oxygen.Llvm;

public sealed partial class LlvmModuleGenerator
{
    // The bytes go into a private constant global of exactly their length, with no NUL terminator added, so an embedded
    // zero byte is part of the value and the length stays correct.
    private LLVMValueRef EmitStringLiteral(LLVMBuilderRef builder, StringLiteralExpression literal) =>
        EmitStringBytes(builder, Encoding.UTF8.GetBytes(StringLiteralDecoder.Decode(literal.Lexeme, literal.Location)));

    // A string value whose bytes are the given UTF-8 text, with a private constant global holding the bytes.
    private LLVMValueRef EmitStringBytes(LLVMBuilderRef builder, byte[] bytes)
    {
        var module = builder.InsertBlock.Parent.GlobalParent;
        var data = module.AddGlobal(LLVMTypeRef.CreateArray(_context.Int8Type, (uint)bytes.Length), "oxygen.string");
        data.Initializer = LLVMValueRef.CreateConstArray(_context.Int8Type,
            bytes.Select(value => LLVMValueRef.CreateConstInt(_context.Int8Type, value, false)).ToArray());
        data.Linkage = LLVMLinkage.LLVMPrivateLinkage;
        data.IsGlobalConstant = true;
        data.Alignment = 1;
        var result = LLVMValueRef.CreateConstNull(ToLlvmStringType());
        result = builder.BuildInsertValue(result, data, 0, "string.pointer");
        return builder.BuildInsertValue(
            result,
            LLVMValueRef.CreateConstInt(_context.Int64Type, (ulong)bytes.Length, false),
            1,
            "string.length");
    }

    private LLVMValueRef EmitConditional(
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
        var resultStorage = BuildEntryAlloca(builder, ToLlvmType(resultType, model), "conditional.result");
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

    // The right operand runs only when the left one does not decide the result, so `a != null && a.x` never reads a.x
    // through a null a. The result is a phi over the two incoming edges.
    private LLVMValueRef EmitShortCircuit(LLVMContextRef context, LLVMBuilderRef builder, BinaryExpression expression,
        IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage,
        SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        var isAnd = expression.Operator == TokenKind.AmpersandAmpersand;
        var function = builder.InsertBlock.Parent;
        var left = EmitExpression(context, builder, expression.Left, values, storage, model, llvmFunctions);
        var leftEnd = builder.InsertBlock;
        var rightBlock = function.AppendBasicBlock(isAnd ? "and.rhs" : "or.rhs");
        var endBlock = function.AppendBasicBlock(isAnd ? "and.end" : "or.end");
        if (isAnd)
            builder.BuildCondBr(left, rightBlock, endBlock);
        else
            builder.BuildCondBr(left, endBlock, rightBlock);

        builder.PositionAtEnd(rightBlock);
        var right = EmitExpression(context, builder, expression.Right, values, storage, model, llvmFunctions);
        var rightEnd = builder.InsertBlock;
        builder.BuildBr(endBlock);

        builder.PositionAtEnd(endBlock);
        var result = builder.BuildPhi(_context.Int1Type, isAnd ? "and" : "or");
        var decided = LLVMValueRef.CreateConstInt(_context.Int1Type, isAnd ? 0UL : 1UL, false);
        result.AddIncoming([decided, right], [leftEnd, rightEnd], 2);
        return result;
    }

    private LLVMValueRef EmitBinary(LLVMContextRef context, LLVMBuilderRef builder, BinaryExpression expression, IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage, SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        if (expression.Operator is TokenKind.AmpersandAmpersand or TokenKind.PipePipe)
            return EmitShortCircuit(context, builder, expression, values, storage, model, llvmFunctions);
        var left = EmitExpression(context, builder, expression.Left, values, storage, model, llvmFunctions);
        var right = EmitExpression(context, builder, expression.Right, values, storage, model, llvmFunctions);
        return EmitBinaryOperation(builder, expression.Operator, left, model.ExpressionTypes[expression.Left], right,
            model.ExpressionTypes[expression.Right], model.ExpressionTypes[expression], model);
    }

    // The operation on two evaluated operands. The operand types decide the conversions and the signedness of comparisons
    // and division; the result type decides pointer arithmetic and whether the operation is floating-point.
    private LLVMValueRef EmitBinaryOperation(
        LLVMBuilderRef builder,
        TokenKind op,
        LLVMValueRef left,
        SemanticType leftType,
        LLVMValueRef right,
        SemanticType rightType,
        SemanticType resultType,
        SemanticModel model)
    {
        if (leftType.Kind == SemanticTypeKind.String && rightType.Kind == SemanticTypeKind.String)
        {
            var equal = EmitStringEquality(builder, left, right);
            return op == TokenKind.BangEquals ? builder.BuildNot(equal, "string.not.equal") : equal;
        }
        if (leftType != rightType && leftType.IsNumeric && rightType.IsNumeric)
        {
            var common = CommonNumericType(leftType, rightType);
            left = CoerceValue(builder, left, leftType, common, model);
            right = CoerceValue(builder, right, rightType, common, model);
        }
        if ((leftType.IsPointer || rightType.IsPointer)
            && op is TokenKind.EqualsEquals or TokenKind.BangEquals)
        {
            var predicate = op == TokenKind.EqualsEquals
                ? LLVMIntPredicate.LLVMIntEQ
                : LLVMIntPredicate.LLVMIntNE;
            return builder.BuildICmp(predicate, left, right, "ptr.compare");
        }
        if (resultType.IsPointer)
        {
            var index = right;
            index = ExtendIndexToInt64(builder, right, rightType, "ptr.index");
            var pointee = ToLlvmType(resultType.Pointee!, model);
            return op == TokenKind.Plus
                ? builder.BuildGEP2(pointee, left, new LLVMValueRef[] { index }, (ReadOnlySpan<char>)"ptr.add")
                : builder.BuildGEP2(pointee, left,
                    new LLVMValueRef[] { builder.BuildNeg(index, "ptr.neg") }, (ReadOnlySpan<char>)"ptr.sub");
        }
        var isFloatingPoint = resultType.Primitive is PrimitiveType.F32 or PrimitiveType.F64;
        if (op is TokenKind.EqualsEquals or TokenKind.BangEquals
            or TokenKind.LessThan or TokenKind.LessThanOrEquals
            or TokenKind.GreaterThan or TokenKind.GreaterThanOrEquals)
        {
            // A comparison yields bool, so its operands decide between float and integer predicates.
            var operandsAreFloatingPoint = leftType.Primitive is PrimitiveType.F32 or PrimitiveType.F64
                || rightType.Primitive is PrimitiveType.F32 or PrimitiveType.F64;
            if (operandsAreFloatingPoint)
            {
                var predicate = op switch
                {
                    TokenKind.EqualsEquals => LLVMRealPredicate.LLVMRealOEQ,
                    // Unordered: NaN is not equal to anything, including itself.
                    TokenKind.BangEquals => LLVMRealPredicate.LLVMRealUNE,
                    TokenKind.LessThan => LLVMRealPredicate.LLVMRealOLT,
                    TokenKind.LessThanOrEquals => LLVMRealPredicate.LLVMRealOLE,
                    TokenKind.GreaterThan => LLVMRealPredicate.LLVMRealOGT,
                    _ => LLVMRealPredicate.LLVMRealOGE
                };
                return builder.BuildFCmp(predicate, left, right, "fcmp");
            }
            var unsignedOperands = IsUnsignedInteger(leftType) || IsUnsignedInteger(rightType);
            var intPredicate = op switch
            {
                TokenKind.EqualsEquals => LLVMIntPredicate.LLVMIntEQ,
                TokenKind.BangEquals => LLVMIntPredicate.LLVMIntNE,
                TokenKind.LessThan => unsignedOperands ? LLVMIntPredicate.LLVMIntULT : LLVMIntPredicate.LLVMIntSLT,
                TokenKind.LessThanOrEquals => unsignedOperands ? LLVMIntPredicate.LLVMIntULE : LLVMIntPredicate.LLVMIntSLE,
                TokenKind.GreaterThan => unsignedOperands ? LLVMIntPredicate.LLVMIntUGT : LLVMIntPredicate.LLVMIntSGT,
                _ => unsignedOperands ? LLVMIntPredicate.LLVMIntUGE : LLVMIntPredicate.LLVMIntSGE
            };
            return builder.BuildICmp(intPredicate, left, right, "icmp");
        }
        if (!isFloatingPoint && op is TokenKind.Slash or TokenKind.Percent)
            EmitIntegerDivisionChecks(builder, left, right, IsUnsignedInteger(leftType));
        if (!isFloatingPoint && op is TokenKind.LessThanLessThan or TokenKind.GreaterThanGreaterThan)
            EmitShiftRangeCheck(builder, left, right);
        return op switch
        {
            TokenKind.Plus when isFloatingPoint => builder.BuildFAdd(left, right, "fadd"),
            TokenKind.Minus when isFloatingPoint => builder.BuildFSub(left, right, "fsub"),
            TokenKind.Star when isFloatingPoint => builder.BuildFMul(left, right, "fmul"),
            TokenKind.Slash when isFloatingPoint => builder.BuildFDiv(left, right, "fdiv"),
            TokenKind.Plus => builder.BuildAdd(left, right, "add"),
            TokenKind.Minus => builder.BuildSub(left, right, "sub"),
            TokenKind.Star => builder.BuildMul(left, right, "mul"),
            TokenKind.Slash => IsUnsignedInteger(leftType)
                ? builder.BuildUDiv(left, right, "div")
                : builder.BuildSDiv(left, right, "div"),
            TokenKind.Percent => IsUnsignedInteger(leftType)
                ? builder.BuildURem(left, right, "rem")
                : builder.BuildSRem(left, right, "rem"),
            TokenKind.Ampersand => builder.BuildAnd(left, right, "and"),
            TokenKind.Pipe => builder.BuildOr(left, right, "or"),
            TokenKind.Caret => builder.BuildXor(left, right, "xor"),
            TokenKind.LessThanLessThan => builder.BuildShl(left, right, "shl"),
            TokenKind.GreaterThanGreaterThan => IsUnsignedInteger(leftType)
                ? builder.BuildLShr(left, right, "lshr")
                : builder.BuildAShr(left, right, "ashr"),
            _ => throw new InvalidOperationException($"Unsupported binary operator '{op}'.")
        };
    }

    // Integer division and remainder are undefined in LLVM for a zero divisor and for INT_MIN / -1 (signed only). Oxygen
    // reports both through the runtime instead, so the undefined operation is never executed.
    private void EmitIntegerDivisionChecks(LLVMBuilderRef builder, LLVMValueRef left, LLVMValueRef right, bool isUnsigned)
    {
        var divisorIsZero = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, right,
            LLVMValueRef.CreateConstInt(right.TypeOf, 0, false), "div.zero");
        EmitArithmeticCheck(builder, divisorIsZero, ArithmeticFailureKind.DivideByZero, LLVMValueRef.CreateConstInt(_context.Int64Type, 0, false));
        if (isUnsigned)
            return;
        var width = left.TypeOf.IntWidth;
        var minimum = LLVMValueRef.CreateConstInt(left.TypeOf, 1UL << (int)(width - 1), false);
        var leftIsMinimum = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, minimum, "div.min");
        var rightIsMinusOne = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, right,
            LLVMValueRef.CreateConstInt(right.TypeOf, ulong.MaxValue, true), "div.minus.one");
        var overflows = builder.BuildAnd(leftIsMinimum, rightIsMinusOne, "div.overflow");
        EmitArithmeticCheck(builder, overflows, ArithmeticFailureKind.SignedOverflow, LLVMValueRef.CreateConstInt(_context.Int64Type, 0, false));
    }

    // A shift count must be less than the bit width of the shifted value; LLVM leaves other counts undefined.
    private void EmitShiftRangeCheck(LLVMBuilderRef builder, LLVMValueRef left, LLVMValueRef count)
    {
        var width = left.TypeOf.IntWidth;
        var outOfRange = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, count,
            LLVMValueRef.CreateConstInt(count.TypeOf, width, false), "shift.range");
        var operand = builder.BuildZExtOrBitCast(count, _context.Int64Type, "shift.count");
        EmitArithmeticCheck(builder, outOfRange, ArithmeticFailureKind.ShiftOutOfRange, operand);
    }

    private enum ArithmeticFailureKind
    {
        DivideByZero = 1,
        SignedOverflow = 2,
        ShiftOutOfRange = 3
    }

    // Branches to the runtime failure hook when `failed` is true. The hook does not return.
    private void EmitArithmeticCheck(LLVMBuilderRef builder, LLVMValueRef failed, ArithmeticFailureKind kind,
        LLVMValueRef operand)
    {
        var function = builder.InsertBlock.Parent;
        var failureBlock = function.AppendBasicBlock("arith.failure");
        var continueBlock = function.AppendBasicBlock("arith.continue");
        builder.BuildCondBr(failed, failureBlock, continueBlock);
        builder.PositionAtEnd(failureBlock);
        BuildNoThrowCall(builder, _arithmeticFailure, [LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)kind, false), operand]);
        builder.BuildUnreachable();
        builder.PositionAtEnd(continueBlock);
    }

    private static bool IsUnsignedInteger(SemanticType type) =>
        type.Primitive is PrimitiveType.U8 or PrimitiveType.U16 or PrimitiveType.U32 or PrimitiveType.U64
            or PrimitiveType.USize;

    private SemanticType CommonNumericType(SemanticType left, SemanticType right)
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

    private LLVMValueRef EmitUnary(LLVMContextRef context, LLVMBuilderRef builder, UnaryExpression expression, IReadOnlyDictionary<string, LLVMValueRef> values, IReadOnlyDictionary<string, LLVMValueRef> storage, SemanticModel model, IReadOnlyDictionary<FunctionSymbol, LLVMValueRef> llvmFunctions)
    {
        if (expression.Operator is TokenKind.PlusPlus or TokenKind.MinusMinus)
        {
            var type = model.ExpressionTypes[expression.Operand];
            // A member is changed through its place, which also covers a property. Any other target has an address.
            var place = expression.Operand is MemberAccessExpression
                ? EmitPlace(context, builder, expression.Operand, values, storage, model, llvmFunctions)
                : MemoryPlace(builder, ToLlvmType(type, model),
                    EmitLValueAddress(context, builder, expression.Operand, values, storage, model, llvmFunctions), "increment.old");
            var oldValue = place.Load();
            LLVMValueRef updated;
            if (type.IsPointer)
            {
                var index = LLVMValueRef.CreateConstInt(_context.Int64Type, 1, false);
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

            place.Store(updated);
            return expression.IsPostfix ? oldValue : updated;
        }

        if (expression.Operator == TokenKind.Ampersand)
        {
            if (expression.Operand is IdentifierExpression identifier)
                return VariableAddress(identifier, storage, model);
            if (expression.Operand is MemberAccessExpression)
                return EmitLValueAddress(context, builder, expression.Operand, values, storage, model, llvmFunctions);
            if (expression.Operand is UnaryExpression
                { Operator: TokenKind.Star, Operand: var dereferenced })
                return EmitExpression(context, builder, dereferenced, values, storage, model, llvmFunctions);
        }
        var operand = EmitExpression(context, builder, expression.Operand, values, storage, model, llvmFunctions);
        if (expression.Operator == TokenKind.Star)
            return builder.BuildLoad2(ToLlvmType(model.ExpressionTypes[expression], model), operand, "deref");
        var isFloatingPoint = model.ExpressionTypes[expression].Primitive is PrimitiveType.F32 or PrimitiveType.F64;
        if (expression.Operator == TokenKind.Bang)
            return builder.BuildNot(operand, "not");
        // Bitwise complement: exclusive or with all ones, for any integer width.
        if (expression.Operator == TokenKind.Tilde)
            return builder.BuildNot(operand, "bitwise.not");
        return expression.Operator == TokenKind.Minus
            ? isFloatingPoint
                ? builder.BuildFNeg(operand, "fneg")
                : builder.BuildNeg(operand, "neg")
            : throw new InvalidOperationException($"Unsupported unary operator '{expression.Operator}'.");
    }

    private LLVMValueRef EmitLValueAddress(
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
            IdentifierExpression identifier => VariableAddress(identifier, storage, model),
            MemberAccessExpression => EmitPlace(context, builder, expression, values, storage, model, llvmFunctions).Address
                ?? throw new InvalidOperationException("Internal compiler error: this member has no address."),
            UnaryExpression { Operator: TokenKind.Star, Operand: var pointer } =>
                EmitExpression(context, builder, pointer, values, storage, model, llvmFunctions),
            IndexExpression index => EmitArrayAddress(context, builder, index, values, storage, model, llvmFunctions),
            _ => throw new InvalidOperationException("Increment target is not supported by the LLVM backend.")
        };
    }

}
