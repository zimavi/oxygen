using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Semantic;

// Expressions: operators, literals, member access, construction and the expression dispatcher.
public sealed partial class SemanticAnalyzer
{
    // Reads a variable. A deleted variable cannot be read, and reading its value (not only as a receiver) makes it escape.
    private SemanticType ReadIdentifier(IdentifierExpression identifier, Scope scope)
    {
        var symbol = scope.Lookup(identifier.Name, identifier.Location);
        if (symbol.Declaration is VariableDeclarationStatement declaration)
        {
            CheckAssigned(declaration, identifier.Name, identifier.Location);
            if (_deletedLocals.Contains(declaration))
                throw Error($"'{identifier.Name}' was deleted and cannot be used", identifier.Location, identifier.Name.Length);
            if (!_receiverUse)
                _valueUses.Add(declaration);
        }

        return symbol.Type;
    }

    private SemanticType AnalyzeExpression(
        Expression expression,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        SemanticType? expectedType = null,
        bool allowVoid = false)
    {
        var type = expression switch
        {
            IdentifierExpression identifier => ReadIdentifier(identifier, scope),
            ThisExpression thisExpression => scope.LookupThis(thisExpression.Location),
            IntegerLiteralExpression integer => IntegerLiteralType(integer),
            FloatingPointLiteralExpression floating => SemanticType.FromPrimitive(ParseNumericLiteralType(floating.Lexeme, true)),
            StringLiteralExpression => SemanticType.String,
            BooleanLiteralExpression => SemanticType.FromPrimitive(PrimitiveType.Bool),
            NullLiteralExpression => SemanticType.PointerTo(SemanticType.FromPrimitive(PrimitiveType.Void)),
            IsExpression pattern => AnalyzeIsExpression(
                pattern, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            StructLiteralExpression literal => AnalyzeStructLiteral(
                literal, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            ArrayLiteralExpression literal => AnalyzeArrayLiteral(literal, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            IndexExpression index => AnalyzeIndex(index, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            MemberAccessExpression member => AnalyzeMemberAccess(
                member, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            CastExpression cast => AnalyzeCast(cast, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            SizeOfExpression sizeOf => AnalyzeSizeOf(sizeOf, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            UnionConstructionExpression construction => AnalyzeUnionConstruction(construction, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            UnaryExpression unary => AnalyzeUnary(unary, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            BinaryExpression binary => AnalyzeBinary(binary, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            ConditionalExpression conditional => AnalyzeConditional(
                conditional, scope, functions, moduleName, imports, expressionTypes, resolvedCalls, expectedType),
            CallExpression call => AnalyzeCall(call, scope, functions, moduleName, imports, expressionTypes, resolvedCalls, expectedType, allowVoid),
            ClassConstructionExpression creation => AnalyzeClassConstructionExpression(
                creation, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            NewExpression creation => AnalyzeNewExpression(
                creation, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            _ => throw new InvalidOperationException($"Unsupported expression type {expression.GetType().Name}.")
        };

        expressionTypes[expression] = type;
        return type;
    }

    private SemanticType AnalyzeIsExpression(
        IsExpression expression,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var operandType = AnalyzeExpression(expression.Operand, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        AnalyzeTypePattern(expression.TargetType, operandType, expression.Location);
        if (expression.BindingName is not null && scope.ContainsInCurrentScope(expression.BindingName))
            throw Error($"A variable named '{expression.BindingName}' is already declared in this scope", expression.Location);
        return SemanticType.FromPrimitive(PrimitiveType.Bool);
    }

    private SemanticType AnalyzeTypePattern(TypeSyntax syntax, SemanticType operandType, SourceLocation location)
    {
        var targetType = ResolveType(syntax);
        if (!targetType.IsClass && !targetType.IsInterface)
            throw Error($"The 'is' pattern target must be a class or interface, got {targetType}", location);
        if (!operandType.IsClass && !operandType.IsInterface)
            throw Error($"The 'is' pattern operand must be a class or interface, got {operandType}", location);
        if (!IsReferenceCast(operandType, targetType, out _)
            && !IsReferenceCast(targetType, operandType, out _))
            throw Error($"Types {operandType} and {targetType} are not related", location);
        return targetType;
    }

    private SemanticType AnalyzeSizeOf(
        SizeOfExpression expression, Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions, string moduleName,
        IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression, SemanticType> types,
        IDictionary<CallExpression, FunctionSymbol> calls)
    {
        var target = expression.TargetType is not null
            ? ResolveType(expression.TargetType)
            : expression.Operand is not null
                ? AnalyzeExpression(expression.Operand, scope, functions, moduleName, imports, types, calls)
                : throw Error("sizeof requires a type or expression", expression.Location);
        if (!IsSized(target))
            throw Error($"Type {target} cannot be used with sizeof", expression.Location);
        return SemanticType.FromPrimitive(PrimitiveType.U64);
    }

    private static bool IsSized(SemanticType type) =>
        !type.IsVoid;

    private SemanticType AnalyzeUnionConstruction(
        UnionConstructionExpression construction, Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions, string moduleName,
        IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression, SemanticType> types,
        IDictionary<CallExpression, FunctionSymbol> calls)
    {
        if (!_unionsByName.TryGetValue(construction.UnionName, out var union))
            throw Error($"Union '{construction.UnionName}' is not declared", construction.Location);
        if (!union.CasesByName.TryGetValue(construction.CaseName, out var item))
            throw Error($"Union '{construction.UnionName}' has no case '{construction.CaseName}'", construction.Location);
        if (item.PayloadType is null && construction.Arguments.Count != 0
            || item.PayloadType is not null && construction.Arguments.Count != 1)
            throw Error($"Union case '{construction.CaseName}' has invalid argument count", construction.Location);
        if (item.PayloadType is not null)
        {
            var actual = AnalyzeExpression(construction.Arguments[0], scope, functions, moduleName, imports, types, calls);
            if (!CanAssign(item.PayloadType, construction.Arguments[0], actual))
                throw Error($"Union case '{construction.CaseName}' expects {item.PayloadType}, got {actual}", construction.Location);
        }
        return SemanticType.Union(union.Name);
    }

    private SemanticType AnalyzeArrayLiteral(ArrayLiteralExpression literal, Scope scope, IReadOnlyDictionary<string, List<FunctionSymbol>> functions, string moduleName, IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression, SemanticType> types, IDictionary<CallExpression, FunctionSymbol> calls)
    {
        if (literal.Elements.Count == 0) throw Error("Array literals cannot be empty", literal.Location);
        var element = AnalyzeExpression(literal.Elements[0], scope, functions, moduleName, imports, types, calls);
        foreach (var value in literal.Elements.Skip(1))
        {
            var actual = AnalyzeExpression(value, scope, functions, moduleName, imports, types, calls);
            if (actual != element) throw Error("Array literal elements must have matching types", value.Location);
        }
        return SemanticType.ArrayOf(element, literal.Elements.Count);
    }

    private SemanticType AnalyzeIndex(IndexExpression index, Scope scope, IReadOnlyDictionary<string, List<FunctionSymbol>> functions, string moduleName, IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression, SemanticType> types, IDictionary<CallExpression, FunctionSymbol> calls)
    {
        var target = AnalyzeExpression(index.Target, scope, functions, moduleName, imports, types, calls);
        var indexType = AnalyzeExpression(index.Index, scope, functions, moduleName, imports, types, calls);
        if (!indexType.IsNumeric) throw Error("Index must be numeric", index.Index.Location);
        if (!target.IsArray && !target.IsPointer)
            throw Error("Indexing requires an array or pointer", index.Target.Location);
        if (target.IsOpaquePointer)
            throw Error("Cannot dereference an opaque void pointer; cast it to a typed pointer first",
                index.Target.Location);
        if (index.Index is IntegerLiteralExpression constant && long.TryParse(constant.Lexeme, out var value)
            && target.IsArray && (value < 0 || value >= target.ArrayLength))
            throw Error($"Array index {value} is out of bounds for length {target.ArrayLength}", index.Index.Location);
        return target.IsArray ? target.ElementType! : target.Pointee!;
    }

    private SemanticType AnalyzeStructLiteral(
        StructLiteralExpression literal,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        if (!_structsByName.TryGetValue(literal.TypeName, out var structure))
            throw Error($"Struct '{literal.TypeName}' is not declared", literal.Location, literal.TypeName.Length);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in literal.Fields)
        {
            if (!fields.Add(field.Name))
                throw Error($"Struct field '{field.Name}' is specified more than once", field.Location, field.Name.Length);
            var declaration = structure.Fields.FirstOrDefault(candidate => candidate.Name == field.Name)
                ?? throw Error($"Struct '{literal.TypeName}' has no field named '{field.Name}'", field.Location, field.Name.Length);
            var valueType = AnalyzeExpression(field.Value, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
            if (!CanAssign(declaration.Type, field.Value, valueType))
                throw Error($"Cannot initialize field '{field.Name}' with {valueType}; expected {declaration.Type}", field.Value.Location);
        }
        if (fields.Count != structure.Fields.Count)
            throw Error($"Struct literal '{literal.TypeName}' must initialize every field", literal.Location, literal.TypeName.Length);
        return SemanticType.Struct(literal.TypeName);
    }

    private SemanticType AnalyzeMemberAccess(
        MemberAccessExpression member,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        if (member.Target is IdentifierExpression enumIdentifier
            && _enumsByName.TryGetValue(enumIdentifier.Name, out var enumSymbol))
        {
            var memberSymbol = enumSymbol.Members.FirstOrDefault(candidate => candidate.Name == member.MemberName)
                ?? throw Error($"Enum '{enumSymbol.Name}' has no member named '{member.MemberName}'", member.Location, member.MemberName.Length);
            return SemanticType.Enum(enumSymbol.Name);
        }

        // A null literal has no type that could have members, so the access can be rejected at compile time.
        if (member.Target is NullLiteralExpression)
            throw Error($"Cannot access member '{member.MemberName}' of the null literal, which has no members",
                member.Location, member.MemberName.Length);

        // A variable that is only the receiver of a member access is used, but its value does not escape.
        var previousReceiver = _receiverUse;
        _receiverUse = member.Target is IdentifierExpression;
        var targetType = AnalyzeExpression(member.Target, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        _receiverUse = previousReceiver;
        if (targetType.IsClass)
        {
            var cls = _classesByName[targetType.StructName!];
            var substitutions = cls.DeclaredTypeParameters
                .Zip(targetType.TypeArguments ?? [], (name, type) => (name, type))
                .ToDictionary(item => item.name, item => item.type, StringComparer.Ordinal);
            var property = FindProperty(cls, member.MemberName);
            if (property is not null)
            {
                var getter = property.Declaration.Accessors.FirstOrDefault(
                    accessor => accessor.Kind == PropertyAccessorKind.Get);
                if (getter is null)
                    throw Error($"Property '{property.Name}' is write-only", member.Location, property.Name.Length);
                EnsureAccessorVisible(property, getter, member.Location);
                return SubstituteGenericType(property.Type, substitutions);
            }
            var field = cls.Fields.FirstOrDefault(candidate => candidate.Name == member.MemberName);
            if (field is not null)
            {
                EnsureMemberAccessible(field.Visibility, "Field", field.Name, cls, moduleName, member.Location, cls.Name);
                return SubstituteGenericType(field.Type, substitutions);
            }
            if (cls.Methods.Any(method => method.Name == member.MemberName))
                return SemanticType.PointerTo(SemanticType.FromPrimitive(PrimitiveType.Void));
            throw Error($"Class '{cls.Name}' has no member named '{member.MemberName}'", member.Location);
        }
        if (targetType.IsInterface)
        {
            var interfaceSymbol = _interfacesByName[targetType.StructName!];
            var substitutions = interfaceSymbol.DeclaredTypeParameters
                .Zip(targetType.TypeArguments ?? [], (name, type) => (name, type))
                .ToDictionary(item => item.name, item => item.type, StringComparer.Ordinal);
            var contract = interfaceSymbol.DeclaredProperties
                .FirstOrDefault(candidate => candidate.Name == member.MemberName);
            if (contract is null)
                throw Error($"Interface '{targetType.StructName}' has no member named '{member.MemberName}'",
                    member.Location);
            var getter = contract.Declaration.Accessors.FirstOrDefault(
                accessor => accessor.Kind == PropertyAccessorKind.Get)
                ?? throw Error($"Property '{contract.Name}' is write-only", member.Location, contract.Name.Length);
            EnsureAccessorVisible(contract, getter, member.Location);
            return SubstituteGenericType(contract.Type, substitutions);
        }
        if (!targetType.IsStruct)
            throw Error($"Member access requires a struct or class value, got {targetType}", member.Location);
        var structure = _structsByName[targetType.StructName!];
        return structure.Fields.FirstOrDefault(field => field.Name == member.MemberName)?.Type
            ?? throw Error($"Struct '{targetType.StructName}' has no field named '{member.MemberName}'", member.Location, member.MemberName.Length);
    }

    private SemanticType AnalyzeNewExpression(
        NewExpression expression, Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions, string moduleName,
        IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression, SemanticType> types,
        IDictionary<CallExpression, FunctionSymbol> calls)
    {
        if (expression.Type.NamedType is null || !_classesByName.TryGetValue(expression.Type.NamedType, out var cls)
            || expression.Type.IsPointer || expression.Type.IsArray || expression.Type.IsString)
            throw Error("Only declared classes can be constructed with 'new'", expression.Location);
        var arguments = expression.Arguments.Select(argument =>
            AnalyzeExpression(argument, scope, functions, moduleName, imports, types, calls)).ToArray();
        var constructedType = expression.Type.DeclaredTypeArguments.Count == 0
            ? SemanticType.Class(cls.Name)
            : ResolveType(expression.Type);
        var substitutions = cls.DeclaredTypeParameters
            .Zip(constructedType.TypeArguments ?? [], (name, type) => (name, type))
            .ToDictionary(item => item.name, item => item.type, StringComparer.Ordinal);
        var matches = cls.Constructors.Where(constructor => IsMemberAccessible(constructor.Visibility, cls.Name, cls.ModuleName, moduleName))
            .Where(constructor => constructor.Parameters.Count == arguments.Length)
            .Where(constructor => constructor.Parameters.Select((parameter, index) =>
            {
                if (expression.Type.DeclaredTypeArguments.Count == 0)
                    InferClassTypeArguments(parameter.Type, arguments[index], cls.DeclaredTypeParameters, substitutions);
                return CanAssign(
                    SubstituteGenericType(parameter.Type, substitutions),
                    expression.Arguments[index],
                    arguments[index]);
            }).All(match => match)).ToArray();
        if (matches.Length == 0)
        {
            EnsureConstructorAccessible(cls, arguments.Length, moduleName, expression.Location);
            throw Error($"No constructor of '{cls.Name}' matches the supplied arguments", expression.Location);
        }
        if (matches.Length > 1)
            throw Error($"Constructor call for '{cls.Name}' is ambiguous", expression.Location);
        _constructorSelections[expression] = IndexOfConstructor(cls, matches[0]);
        if (substitutions.Count != cls.DeclaredTypeParameters.Count)
            throw Error($"Type '{cls.Name}' expects {cls.DeclaredTypeParameters.Count} type argument(s), but they could not be inferred",
                expression.Location);
        var inferredArguments = cls.DeclaredTypeParameters.Select(name => substitutions[name]).ToArray();
        ValidateTypeArgumentConstraints(cls.Declaration.DeclaredTypeParameters, inferredArguments, expression.Location);
        return SemanticType.Class(cls.Name,
            cls.DeclaredTypeParameters.Count == 0 ? null : inferredArguments);
    }

    private SemanticType AnalyzeClassConstructionExpression(
        ClassConstructionExpression expression, Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions, string moduleName,
        IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression, SemanticType> types,
        IDictionary<CallExpression, FunctionSymbol> calls)
    {
        if (!_classesByName.TryGetValue(expression.TypeName, out var cls))
            throw Error($"Class '{expression.TypeName}' is not declared", expression.Location);
        var arguments = expression.Arguments.Select(argument =>
            AnalyzeExpression(argument, scope, functions, moduleName, imports, types, calls)).ToArray();
        var substitutions = new Dictionary<string, SemanticType>(StringComparer.Ordinal);
        if (expression.ExplicitTypeArguments.Count > 0)
        {
            ValidateTypeArgumentCount(
                new TypeSyntax(null, false, expression.Location, NamedType: expression.TypeName,
                    TypeArguments: expression.ExplicitTypeArguments.ToArray()),
                cls.DeclaredTypeParameters.Count);
            foreach (var (name, type) in cls.DeclaredTypeParameters.Zip(
                         expression.ExplicitTypeArguments.Select(ResolveType)))
                substitutions[name] = type;
        }
        var matches = cls.Constructors.Where(constructor => IsMemberAccessible(constructor.Visibility, cls.Name, cls.ModuleName, moduleName))
            .Where(constructor => constructor.Parameters.Count == arguments.Length)
            .Where(constructor => constructor.Parameters.Select((parameter, index) =>
            {
                if (expression.ExplicitTypeArguments.Count == 0)
                    InferClassTypeArguments(parameter.Type, arguments[index], cls.DeclaredTypeParameters, substitutions);
                return CanAssign(
                    SubstituteGenericType(parameter.Type, substitutions),
                    expression.Arguments[index],
                    arguments[index]);
            }).All(match => match)).ToArray();
        if (matches.Length == 0)
        {
            EnsureConstructorAccessible(cls, arguments.Length, moduleName, expression.Location);
            throw Error($"No constructor of '{cls.Name}' matches the supplied arguments", expression.Location);
        }
        if (matches.Length > 1)
            throw Error($"Constructor call for '{cls.Name}' is ambiguous", expression.Location);
        _constructorSelections[expression] = IndexOfConstructor(cls, matches[0]);
        if (substitutions.Count != cls.DeclaredTypeParameters.Count)
            throw Error($"Type '{cls.Name}' expects {cls.DeclaredTypeParameters.Count} type argument(s), but they could not be inferred",
                expression.Location);
        var inferredArguments = cls.DeclaredTypeParameters.Select(name => substitutions[name]).ToArray();
        ValidateTypeArgumentConstraints(cls.Declaration.DeclaredTypeParameters, inferredArguments, expression.Location);
        return SemanticType.Class(
            expression.TypeName,
            cls.DeclaredTypeParameters.Count == 0 ? null : inferredArguments);
    }

    private SemanticType AnalyzeUnary(
        UnaryExpression expression,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        // A negated suffixed literal is range-checked with its sign, so -128i8 is accepted while 128i8 is not.
        if (expression is { Operator: TokenKind.Minus, Operand: IntegerLiteralExpression negatedLiteral }
            && negatedLiteral.Lexeme.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').Length != 0)
        {
            _negatedLiterals.Add(negatedLiteral);
            RequireLiteralFits("-" + negatedLiteral.Lexeme, -ParseIntegerLiteralDigits(negatedLiteral.Lexeme),
                ParseNumericLiteralType(negatedLiteral.Lexeme, false), negatedLiteral.Location);
        }
        var operandType = AnalyzeExpression(expression.Operand, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        if (expression.Operator == TokenKind.Ampersand)
        {
            if (!IsAddressable(expression.Operand, scope))
                throw Error("The '&' operator requires an addressable operand", expression.Location);
            return SemanticType.PointerTo(operandType);
        }

        if (expression.Operator == TokenKind.Star)
        {
            if (expression.Operand is NullLiteralExpression
                || expression.Operand is IdentifierExpression identifier
                    && _definitelyNullVariables.Contains(scope.Lookup(identifier.Name, identifier.Location)))
                throw Error("Cannot dereference a null pointer", expression.Location);
            if (!operandType.IsPointer)
                throw Error("The '*' operator requires a pointer operand", expression.Location);
            if (operandType.IsOpaquePointer)
                throw Error("Cannot dereference an opaque void pointer; cast it to a typed pointer first", expression.Location);
            return operandType.Pointee!;
        }

        if (expression.Operator == TokenKind.Bang)
        {
            if (operandType != SemanticType.FromPrimitive(PrimitiveType.Bool))
            {
                throw Error("The '!' operator requires a bool operand", expression.Location);
            }

            return operandType;
        }

        if (expression.Operator == TokenKind.Tilde)
        {
            if (!operandType.IsInteger)
                throw Error($"The '~' operator requires an integer operand, got {operandType}", expression.Location);
            return operandType;
        }

        if (expression.Operator is TokenKind.PlusPlus or TokenKind.MinusMinus)
        {
            if (!IsAddressable(expression.Operand, scope))
                throw Error($"The '{expression.Operator}' operator requires an addressable operand", expression.Location);
            ValidateWritableTarget(expression.Operand, scope);
            if (!operandType.IsInteger && !operandType.IsPointer)
                throw Error($"The '{expression.Operator}' operator requires an integer or pointer operand", expression.Location);
            return operandType;
        }

        if (!operandType.IsNumeric)
        {
            throw Error($"The '{expression.Operator}' operator requires a numeric operand", expression.Location);
        }

        return operandType;
    }

    // Integer operations that always fail at run time with a constant operand are rejected here: division or remainder by
    // the constant zero, and a shift count that is negative or not below the width of the shifted integer.
    private static void CheckConstantIntegerOperation(BinaryExpression expression, SemanticType left, SemanticType right)
    {
        if (expression.Operator is TokenKind.Slash or TokenKind.Percent && left.IsInteger && right.IsInteger
            && TryGetIntegerConstantValue(expression.Right, out var divisor) && divisor.IsZero)
            throw Error("Division by zero", expression.Right.Location);
        if (expression.Operator is TokenKind.LessThanLessThan or TokenKind.GreaterThanGreaterThan
            && left.IsInteger && TryGetIntegerConstantValue(expression.Right, out var count)
            && (count.Sign < 0 || count >= IntegerBitWidth(left.Primitive!.Value)))
            throw Error($"Shift count {count} is out of range for {left}", expression.Right.Location);
    }

    private SemanticType AnalyzeBinary(
        BinaryExpression expression,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var left = AnalyzeExpression(expression.Left, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        var right = AnalyzeExpression(expression.Right, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        CheckConstantIntegerOperation(expression, left, right);
        var isLogical = expression.Operator is TokenKind.AmpersandAmpersand or TokenKind.PipePipe;
        var isComparison = expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals
            or TokenKind.LessThan or TokenKind.LessThanOrEquals or TokenKind.GreaterThan
            or TokenKind.GreaterThanOrEquals;

        if (isLogical)
        {
            RequireType(left, SemanticType.FromPrimitive(PrimitiveType.Bool), expression.Location, "Logical operators");
            RequireType(right, SemanticType.FromPrimitive(PrimitiveType.Bool), expression.Location, "Logical operators");
            return SemanticType.FromPrimitive(PrimitiveType.Bool);
        }

        if (left.IsPointer || right.IsPointer)
        {
            if (left.IsPointer && right.IsInteger
                && expression.Operator is TokenKind.Plus or TokenKind.Minus)
                return left;
            if (left.IsPointer && expression.Right is NullLiteralExpression
                && expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals)
            {
                expressionTypes[expression.Right] = left;
                return SemanticType.FromPrimitive(PrimitiveType.Bool);
            }
            if (right.IsPointer && expression.Left is NullLiteralExpression
                && expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals)
            {
                expressionTypes[expression.Left] = right;
                return SemanticType.FromPrimitive(PrimitiveType.Bool);
            }
            if (left.IsPointer && right.IsPointer
                && expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals)
                return SemanticType.FromPrimitive(PrimitiveType.Bool);
            throw Error($"Operator '{expression.Operator}' is not valid for pointer operands", expression.Location);
        }

        // Strings compare for equality by their bytes; ordering is not defined.
        if (left.Kind == SemanticTypeKind.String && right.Kind == SemanticTypeKind.String
            && (expression.Operator == TokenKind.EqualsEquals || expression.Operator == TokenKind.BangEquals))
            return SemanticType.FromPrimitive(PrimitiveType.Bool);

        // Booleans compare for equality only; ordering and arithmetic on bool are not defined.
        var boolean = SemanticType.FromPrimitive(PrimitiveType.Bool);
        if (expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals
            && left == boolean && right == boolean)
            return boolean;

        var requiresInteger = expression.Operator is TokenKind.Ampersand or TokenKind.Pipe or TokenKind.Caret
            or TokenKind.LessThanLessThan or TokenKind.GreaterThanGreaterThan or TokenKind.Percent;
        if (requiresInteger && (!left.IsInteger || !right.IsInteger))
            throw Error($"Operator '{expression.Operator}' requires integer operands", expression.Location);
        if (!left.IsNumeric || !right.IsNumeric)
            throw Error($"Binary operator '{expression.Operator}' requires numeric operands", expression.Location);

        var commonType = GetCommonNumericType(left, right, expression);

        if (isComparison)
        {
            return SemanticType.FromPrimitive(PrimitiveType.Bool);
        }

        return commonType;
    }

    private SemanticType GetCommonNumericType(SemanticType left, SemanticType right, Expression expression)
    {
        if (left == right)
            return left;
        if (CanAssign(left, ((BinaryExpression)expression).Right, right))
            return left;
        if (CanAssign(right, ((BinaryExpression)expression).Left, left))
            return right;
        throw Error($"Binary operator '{((BinaryExpression)expression).Operator}' requires compatible numeric types",
            expression.Location);
    }

    private SemanticType AnalyzeConditional(
        ConditionalExpression expression,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        SemanticType? expectedType)
    {
        var condition = AnalyzeExpression(
            expression.Condition, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        RequireBoolean(condition, expression.Condition.Location);

        var whenTrue = AnalyzeExpression(
            expression.WhenTrue, scope, functions, moduleName, imports, expressionTypes, resolvedCalls,
            expectedType);
        var whenFalse = AnalyzeExpression(
            expression.WhenFalse, scope, functions, moduleName, imports, expressionTypes, resolvedCalls,
            expectedType);
        if (whenTrue == whenFalse)
            return whenTrue;
        if (CanAssign(whenTrue, expression.WhenFalse, whenFalse))
            return whenTrue;
        if (CanAssign(whenFalse, expression.WhenTrue, whenTrue))
            return whenFalse;

        throw Error(
            $"Conditional branches must have compatible types, got {whenTrue} and {whenFalse}",
            expression.Location);
    }

    private static bool IsAddressable(Expression expression, Scope scope) =>
        expression switch
        {
            IdentifierExpression identifier => scope.Lookup(identifier.Name, identifier.Location) is not null,
            ThisExpression => true,
            IndexExpression => true,
            UnaryExpression { Operator: TokenKind.Star, Operand: var operand } =>
                true,
            _ => false
        };

}
