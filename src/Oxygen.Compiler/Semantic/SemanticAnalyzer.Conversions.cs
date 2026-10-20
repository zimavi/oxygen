using System.Numerics;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

// Conversions: implicit and explicit conversions, and the overload conversion cost.
public sealed partial class SemanticAnalyzer
{
    private SemanticType AnalyzeCast(
        CastExpression cast,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var target = ResolveType(cast.TargetType);
        var operand = AnalyzeExpression(cast.Operand, scope, functions, moduleName, imports, expressionTypes, resolvedCalls,
            expectedType: target);
        if (target.IsEnum)
        {
            var enumSymbol = _enumsByName[target.EnumName!];
            var underlying = SemanticType.FromPrimitive(enumSymbol.UnderlyingType);
            if (operand != underlying)
                throw Error($"Cannot cast {operand} to {target}; expected {underlying}", cast.Operand.Location);
            if (TryGetConstantInteger(cast.Operand, out var value)
                && !enumSymbol.Members.Any(member => member.Value == value))
                throw Error($"Value {value} is not a member of enum '{enumSymbol.Name}'", cast.Operand.Location);
            return target;
        }

        if (operand.IsEnum)
        {
            var enumSymbol = _enumsByName[operand.EnumName!];
            if (target != SemanticType.FromPrimitive(enumSymbol.UnderlyingType))
                throw Error($"Enum '{enumSymbol.Name}' can only be cast to {enumSymbol.UnderlyingType}", cast.TargetType.Location);
            return target;
        }

        if (operand.IsNumeric && target.IsNumeric)
            return target;

        if (target.IsPointer && operand.IsNumeric && IsPointerInteger(operand))
            return target;
        if (operand.IsPointer && target.IsNumeric && IsPointerInteger(target))
            return target;

        if (target.IsPointer && operand.IsPointer &&
            (target.IsOpaquePointer || operand.IsOpaquePointer))
            return target;

        if (IsReferenceCast(operand, target, out _))
            return target;

        throw Error($"Unsupported cast from {operand} to {target}", cast.Location);
    }

    // Whether an enum member value is in the range of its underlying integer type.
    private static bool FitsUnderlying(BigInteger value, PrimitiveType underlying) =>
        TryGetIntegerRange(underlying, out var minimum, out var maximum) && value >= minimum && value <= maximum;

    private bool CanAssign(SemanticType target, Expression value, SemanticType actual) =>
        ConversionCostOf(target, value, actual) is not null;

    // A reference type: an interface, or a class that is not a value class. Its values are references to objects, or null.
    private bool IsReferenceType(SemanticType type) =>
        type.IsInterface || (type.IsClass && !IsValueClassType(type));

    private bool IsValueClassType(SemanticType type) =>
        type.IsClass && _classesByName.TryGetValue(type.StructName!, out var cls) && cls.IsValue;

    // Whether a comparison is between references: a reference compared with a reference of a related type, or with null.
    private bool IsReferenceComparison(BinaryExpression expression, SemanticType left, SemanticType right) =>
        (IsReferenceType(left) && (IsReferenceType(right) || expression.Right is NullLiteralExpression))
        || (IsReferenceType(right) && expression.Left is NullLiteralExpression);

    // References compare when one converts to the other, and a null operand takes the other operand's type, so that it is
    // emitted as a null reference of that type.
    private void RequireIdentityComparison(
        BinaryExpression expression,
        SemanticType left,
        SemanticType right,
        IDictionary<Expression, SemanticType> expressionTypes)
    {
        if (expression.Left is NullLiteralExpression)
        {
            expressionTypes[expression.Left] = right;
            return;
        }
        if (expression.Right is NullLiteralExpression)
        {
            expressionTypes[expression.Right] = left;
            return;
        }
        if (!CanAssign(left, expression.Right, right) && !CanAssign(right, expression.Left, left))
            throw ErrorWithCode(DiagnosticCode.OperandRequirement,
                $"Cannot compare {left} with {right}: references compare only when one of them converts to the other",
                expression.Location);
    }

    // How far an implicit conversion is from an exact match; smaller is closer. The fields are compared in order: any
    // conversion loses to an exact match; a conversion within one kind (integer to integer, float to float) beats one
    // that crosses kinds; a narrowing conversion (only ever of a fitting constant) loses to a widening one; and a class
    // or interface is closer the fewer steps up the hierarchy it takes to reach it.
    private readonly record struct ConversionCost(int NotExact, int CrossKind, int Narrowing, int Distance)
        : IComparable<ConversionCost>
    {
        public static readonly ConversionCost Exact = new(0, 0, 0, 0);

        public int CompareTo(ConversionCost other) =>
            (NotExact, CrossKind, Narrowing, Distance).CompareTo((other.NotExact, other.CrossKind, other.Narrowing, other.Distance));
    }

    // The cost of implicitly converting an argument to a parameter type, or null when no implicit conversion exists.
    private ConversionCost? ConversionCostOf(SemanticType target, Expression value, SemanticType actual)
    {
        if (SemanticTypeComparer.Instance.Equals(target, actual))
            return ConversionCost.Exact;
        if (target.IsUnion && actual.IsUnion
            && target.EnumName == actual.EnumName
            && ((actual.TypeArguments is null || actual.TypeArguments.Count == 0)
                || SemanticTypeComparer.Instance.Equals(target, actual)))
            return ConversionCost.Exact;
        if (target.IsPointer && value is NullLiteralExpression)
            return ConversionCost.Exact;
        // The null reference of a class or interface, which is a zero value of that type.
        if (value is NullLiteralExpression && IsReferenceType(target))
            return ConversionCost.Exact;
        // A type parameter converts implicitly to each interface of its constraints, as a class does to an interface it implements.
        if (actual.IsGenericParameter && target.IsInterface
            && _typeParameterConstraints.TryGetValue(actual.GenericParameterName!, out var constraints)
            && constraints.Contains(target.StructName!, StringComparer.Ordinal))
            return new ConversionCost(1, 0, 0, 1);
        if (IsReferenceCast(actual, target, out var implicitConversion) && implicitConversion)
            return new ConversionCost(1, 0, 0, HierarchyDistance(actual.StructName!, target.StructName!));
        if (!target.IsNumeric || !actual.IsNumeric)
            return null;

        var crossKind = IsFloatingPointType(actual) != IsFloatingPointType(target) ? 1 : 0;
        if (IsImplicitlySafeNumericConversion(actual, target))
            return new ConversionCost(1, crossKind, 0, 0);
        // Only an integer constant that fits may narrow implicitly. A float never converts implicitly unless widening.
        if (IsFloatingPointType(actual))
            return null;
        if (!TryGetConstantInteger(value, out var constant) || !IsRepresentable(constant, target))
            return null;
        return new ConversionCost(1, crossKind, NumericBitWidth(target) < NumericBitWidth(actual) ? 1 : 0, 0);
    }

    // The number of steps from a class up to an ancestor class or interface: base classes and implemented interfaces
    // each count as one step. An interface reached through a base class is further than that base class.
    private int HierarchyDistance(string sourceName, string targetName)
    {
        var frontier = new List<string> { sourceName };
        var seen = new HashSet<string>(StringComparer.Ordinal) { sourceName };
        for (var depth = 0; frontier.Count > 0; depth++)
        {
            if (frontier.Contains(targetName, StringComparer.Ordinal))
                return depth;
            var next = new List<string>();
            foreach (var name in frontier)
            {
                if (!_classesByName.TryGetValue(name, out var cls))
                    continue;
                IEnumerable<string> parents = cls.BaseTypeName is null
                    ? cls.InterfaceNames
                    : cls.InterfaceNames.Prepend(cls.BaseTypeName);
                foreach (var parent in parents)
                    if (seen.Add(parent))
                        next.Add(parent);
            }

            frontier = next;
        }

        return int.MaxValue;
    }

    private static bool IsFloatingPointType(SemanticType type) =>
        type.Primitive is PrimitiveType.F32 or PrimitiveType.F64;

    private static int NumericBitWidth(SemanticType type) =>
        type.Primitive switch
        {
            PrimitiveType.F32 => 32,
            PrimitiveType.F64 => 64,
            { } primitive => IntegerBitWidth(primitive),
            null => throw new InvalidOperationException($"Type '{type}' is not numeric.")
        };

    private bool IsReferenceCast(SemanticType source, SemanticType target, out bool implicitConversion)
    {
        implicitConversion = false;
        if ((!source.IsClass && !source.IsInterface) || (!target.IsClass && !target.IsInterface))
            return false;

        if (source.IsClass && target.IsClass)
        {
            if (IsClassDerivedFrom(source.StructName!, target.StructName!))
            {
                implicitConversion = true;
                return true;
            }

            return IsClassDerivedFrom(target.StructName!, source.StructName!);
        }

        if (source.IsClass && target.IsInterface)
        {
            if (!ClassImplementsInterface(source.StructName!, target.StructName!))
                return false;

            implicitConversion = true;
            return true;
        }

        if (source.IsInterface && target.IsClass)
            return ClassImplementsInterface(target.StructName!, source.StructName!);

        return false;
    }

    private static bool IsPointerInteger(SemanticType type) =>
        type.Primitive is PrimitiveType.USize or PrimitiveType.ISize;

    private static bool IsImplicitlySafeNumericConversion(SemanticType source, SemanticType target)
    {
        if (source.Primitive == target.Primitive)
            return true;
        if (source.Primitive == PrimitiveType.F32 && target.Primitive == PrimitiveType.F64)
            return true;
        if (source.Primitive is not ({ } sourcePrimitive) || target.Primitive is not ({ } targetPrimitive))
            return false;
        // An integer converts to a float without loss when the float's significand holds every value of the integer:
        // 24 bits for f32 (up to 16-bit integers) and 53 bits for f64 (up to 32-bit integers).
        if (IsInteger(sourcePrimitive) && (targetPrimitive is PrimitiveType.F32 or PrimitiveType.F64))
            return IntegerBitWidth(sourcePrimitive) <= (targetPrimitive == PrimitiveType.F64 ? 32 : 16);
        if (!IsInteger(sourcePrimitive) || !IsInteger(targetPrimitive))
            return false;

        var sourceBits = IntegerBitWidth(sourcePrimitive);
        var targetBits = IntegerBitWidth(targetPrimitive);
        var sourceUnsigned = IsUnsigned(sourcePrimitive);
        var targetUnsigned = IsUnsigned(targetPrimitive);
        if (sourceUnsigned == targetUnsigned)
            return targetBits >= sourceBits;
        // Only an unsigned value fits a wider signed type; a negative signed value never fits an unsigned type.
        return sourceUnsigned && targetBits > sourceBits;
    }

    private static bool IsInteger(PrimitiveType type) =>
        type is PrimitiveType.I8 or PrimitiveType.I16 or PrimitiveType.I32 or PrimitiveType.I64
            or PrimitiveType.U8 or PrimitiveType.U16 or PrimitiveType.U32 or PrimitiveType.U64
            or PrimitiveType.USize or PrimitiveType.ISize;

    private static bool IsUnsigned(PrimitiveType type) =>
        type is PrimitiveType.U8 or PrimitiveType.U16 or PrimitiveType.U32 or PrimitiveType.U64 or PrimitiveType.USize;

    private static int IntegerBitWidth(PrimitiveType type) =>
        type is PrimitiveType.I8 or PrimitiveType.U8 ? 8
        : type is PrimitiveType.I16 or PrimitiveType.U16 ? 16
        : type is PrimitiveType.I32 or PrimitiveType.U32 ? 32
        : 64;

    // Whether an integer constant holds its exact value in the target. An integer reaches a float only when the float holds
    // it exactly, so a value that rounds is rejected, as it is for any other implicit conversion to a float.
    private static bool IsRepresentable(BigInteger value, SemanticType target)
    {
        var primitive = target.Primitive;
        if (primitive is PrimitiveType.F32 or PrimitiveType.F64)
            return IsExactFloat(value, primitive.Value);
        return primitive is { } integer && TryGetIntegerRange(integer, out var minimum, out var maximum)
            && value >= minimum && value <= maximum;
    }

    private static bool IsExactFloat(BigInteger value, PrimitiveType type)
    {
        if (type == PrimitiveType.F32)
        {
            var single = (float)value;
            return !float.IsInfinity(single) && (BigInteger)single == value;
        }
        var doubled = (double)value;
        return !double.IsInfinity(doubled) && (BigInteger)doubled == value;
    }

}
