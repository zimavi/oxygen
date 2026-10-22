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
        VariableSymbol symbol;
        try
        {
            symbol = scope.Lookup(identifier.Name, identifier.Location);
        }
        catch (SemanticException) when (PrivateInheritedFieldOwner(identifier.Name) is { } owner)
        {
            throw Error($"Field '{identifier.Name}' of class '{owner.Name}' is private and cannot be used here",
                identifier.Location, identifier.Name.Length);
        }
        catch (SemanticException) when (_currentIsStaticMethod && _currentOwnerClass?.Fields.Any(field => field.Name == identifier.Name) == true)
        {
            throw Error($"Instance field '{identifier.Name}' cannot be used in a static method", identifier.Location, identifier.Name.Length);
        }
        RecordGlobalReference(identifier, symbol);
        if (symbol.Declaration is VariableDeclarationStatement declaration)
        {
            // A struct local read as the root of a field path is checked on that path, not as a whole.
            if (!_skippedPartReads.Contains(identifier))
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
                literal, scope, functions, moduleName, imports, expressionTypes, resolvedCalls, expectedType),
            ArrayLiteralExpression literal => AnalyzeArrayLiteral(literal, scope, functions, moduleName, imports, expressionTypes, resolvedCalls, expectedType),
            IndexExpression index => AnalyzeIndex(index, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            MemberAccessExpression member => AnalyzeMemberAccess(
                member, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            CastExpression cast => AnalyzeCast(cast, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            SizeOfExpression sizeOf => AnalyzeSizeOf(sizeOf, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            UnionConstructionExpression construction => AnalyzeUnionConstruction(construction, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            // `&name` of a function is its address; `&name` of a variable is the variable's address, which AnalyzeUnary handles.
            UnaryExpression { Operator: TokenKind.Ampersand, Operand: IdentifierExpression name } reference
                when IsFunctionName(name, scope, moduleName, imports, functions) =>
                AnalyzeFunctionReference(reference, name, expectedType, moduleName, imports, functions),
            FunctionAddressExpression address => AnalyzeFunctionAddress(
                address, scope, moduleName, imports, functions, expressionTypes, resolvedCalls, expectedType),
            UnaryExpression unary => AnalyzeUnary(unary, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            LambdaExpression lambda => AnalyzeLambda(lambda, functions, moduleName, imports, expressionTypes, resolvedCalls),
            IndirectCallExpression indirect => AnalyzeIndirectCallExpression(
                indirect, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            BinaryExpression binary => AnalyzeBinary(binary, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            ConditionalExpression conditional => AnalyzeConditional(
                conditional, scope, functions, moduleName, imports, expressionTypes, resolvedCalls, expectedType),
            CallExpression call => AnalyzeCallExpression(call, scope, functions, moduleName, imports, expressionTypes, resolvedCalls, expectedType, allowVoid),
            // A named argument is analyzed as its value. The call that it belongs to binds it to a parameter by its name.
            NamedArgumentExpression named => AnalyzeExpression(named.Value, scope, functions, moduleName, imports, expressionTypes, resolvedCalls),
            ClassConstructionExpression creation => AnalyzeClassConstructionExpression(
                creation, scope, functions, moduleName, imports, expressionTypes, resolvedCalls, expectedType),
            NewExpression creation => AnalyzeNewExpression(
                creation, scope, functions, moduleName, imports, expressionTypes, resolvedCalls, expectedType),
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
            throw ErrorWithCode(DiagnosticCode.AlreadyDeclared, $"A variable named '{expression.BindingName}' is already declared in this scope", expression.Location);
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
                : throw ErrorWithCode(DiagnosticCode.OperandRequirement, "sizeof requires a type or expression", expression.Location);
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
        if (FindTypeName(construction.UnionName, construction.Location) is not { } unionName
            || !_unionsByName.TryGetValue(unionName, out var union))
            throw ErrorWithCode(DiagnosticCode.NotDeclared, $"Union '{construction.UnionName}' is not declared", construction.Location);
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
        return SemanticType.Union(union.QualifiedName);
    }

    // An array literal in a context that expects an array takes that element type, and each element must convert to it
    // implicitly, so `i64[2] a = { 1, 2 };` is valid. Without an expected array type, the first element sets the type.
    private SemanticType AnalyzeArrayLiteral(ArrayLiteralExpression literal, Scope scope, IReadOnlyDictionary<string, List<FunctionSymbol>> functions, string moduleName, IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression, SemanticType> types, IDictionary<CallExpression, FunctionSymbol> calls, SemanticType? expectedType)
    {
        // `{0}` as a struct value zero-fills every field. No other brace literal is a struct value.
        if (expectedType is { IsStruct: true })
        {
            if (literal.Elements is not [IntegerLiteralExpression zero] || !long.TryParse(zero.Lexeme, out var zeroValue) || zeroValue != 0)
                throw Error("A struct brace literal must be {0}, which zero-fills every field", literal.Location);
            return expectedType;
        }
        // `{0}` as an array value zero-fills every element, as it does for a struct.
        if (expectedType is { IsArray: true } && literal.Elements is [IntegerLiteralExpression arrayZero]
            && long.TryParse(arrayZero.Lexeme, out var arrayZeroValue) && arrayZeroValue == 0)
            return expectedType;
        if (literal.Elements.Count == 0) throw Error("Array literals cannot be empty", literal.Location);
        if (expectedType is { IsArray: true, ElementType: { } expectedElement })
        {
            foreach (var value in literal.Elements)
            {
                var actual = AnalyzeExpression(value, scope, functions, moduleName, imports, types, calls, expectedType: expectedElement);
                if (!CanAssign(expectedElement, value, actual))
                    throw Error($"Array literal element has type {actual}; expected {expectedElement}", value.Location);
            }
            return SemanticType.ArrayOf(expectedElement, literal.Elements.Count);
        }
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
        if (!target.IsArray && !target.IsPointer && target.Kind != SemanticTypeKind.String)
            throw ErrorWithCode(DiagnosticCode.OperandRequirement, "Indexing requires an array, a string or a pointer", index.Target.Location);
        // A string element is one byte, which is read but never written.
        if (target.Kind == SemanticTypeKind.String)
            return SemanticType.FromPrimitive(PrimitiveType.U8);
        if (target.IsOpaquePointer)
            throw Error("Cannot dereference an opaque void pointer; cast it to a typed pointer first",
                index.Target.Location);
        // Indexing a pointer reads or writes the element it addresses, so the pointer is a dereference.
        if (target.IsPointer)
            ClassifyDereference(index.Target, scope, index.Location);
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
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        SemanticType? expectedType = null)
    {
        if (FindTypeName(literal.TypeName, literal.Location) is not { } structName
            || !_structsByName.TryGetValue(structName, out var structure))
            throw ErrorWithCode(DiagnosticCode.NotDeclared, $"Struct '{literal.TypeName}' is not declared", literal.Location, literal.TypeName.Length);
        // A generic struct's type arguments come from the type the literal initializes, since the literal names none.
        var isGeneric = (structure.TypeParameters ?? []).Count > 0;
        var use = expectedType is { IsStruct: true } && expectedType.StructName == structName
            ? expectedType
            : SemanticType.Struct(structName);
        if (isGeneric && use.TypeArguments is not { Count: > 0 })
            throw Error($"Struct '{literal.TypeName}' is generic, so the literal needs a declared type that gives its type arguments",
                literal.Location, literal.TypeName.Length);
        var substitutions = StructTypeArguments(structure, use);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in literal.Fields)
        {
            if (!fields.Add(field.Name))
                throw ErrorWithCode(DiagnosticCode.AlreadyDeclared, $"Struct field '{field.Name}' is specified more than once", field.Location, field.Name.Length);
            var declaration = structure.Fields.FirstOrDefault(candidate => candidate.Name == field.Name)
                ?? throw Error($"Struct '{literal.TypeName}' has no field named '{field.Name}'", field.Location, field.Name.Length);
            var fieldType = SubstituteGenericType(declaration.Type, substitutions);
            var valueType = AnalyzeExpression(field.Value, scope, functions, moduleName, imports, expressionTypes, resolvedCalls, fieldType);
            if (!CanAssign(fieldType, field.Value, valueType))
                throw ErrorWithCode(DiagnosticCode.ConversionMismatch, $"Cannot initialize field '{field.Name}' with {valueType}; expected {fieldType}", field.Value.Location);
        }
        if (fields.Count != structure.Fields.Count)
            throw Error($"Struct literal '{literal.TypeName}' must initialize every field", literal.Location, literal.TypeName.Length);
        return use;
    }

    private SemanticType AnalyzeMemberAccess(
        MemberAccessExpression member,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        bool forWrite = false)
    {
        // `Class.field` names a static field of the class. A generic class has one per instantiation, so it is named inside the
        // class's own body, where the bare name is used instead.
        if (StaticTargetClass(member.Target, scope) is { } staticClass)
        {
            // A static property is named like a static field: reading it calls its getter, and writing it calls its setter.
            if (IsStaticProperty(staticClass, member.MemberName))
                return AnalyzeStaticProperty(member, staticClass, forWrite, moduleName);
            if (staticClass.DeclaredTypeParameters.Count > 0)
                throw Error($"Static field '{member.MemberName}' of generic class '{staticClass.Name}' is named by its bare name inside the class",
                    member.Location, member.MemberName.Length);
            var staticField = StaticFieldOf(staticClass, member, moduleName);
            if (forWrite && staticField.IsConst)
                throw ErrorWithCode(DiagnosticCode.ConstAssignment,
                    $"Cannot assign to const static field '{staticField.Name}'", member.Location, member.MemberName.Length);
            _globalReferences[member] = staticField;
            return staticField.Type;
        }

        if (member.Target is IdentifierExpression enumIdentifier
            && FindTypeName(enumIdentifier.Name, member.Location) is { } enumName
            && _enumsByName.TryGetValue(enumName, out var enumSymbol))
        {
            var memberSymbol = enumSymbol.Members.FirstOrDefault(candidate => candidate.Name == member.MemberName)
                ?? throw Error($"Enum '{enumSymbol.Name}' has no member named '{member.MemberName}'", member.Location, member.MemberName.Length);
            return SemanticType.Enum(enumSymbol.QualifiedName);
        }

        // A null literal has no type that could have members, so the access can be rejected at compile time.
        if (member.Target is NullLiteralExpression)
            throw Error($"Cannot access member '{member.MemberName}' of the null literal, which has no members",
                member.Location, member.MemberName.Length);

        // A field of a struct local is read on its own path, so the other fields of the local are not part of this read.
        if (!forWrite && !_skippedPartReads.Contains(member) && ResolveStructPlace(member, scope) is { } readPlace)
            CheckPartsAssigned(readPlace.Declaration, readPlace.Name, readPlace.Path, member.Location, member.MemberName.Length);
        // A variable that is only the receiver of a member access is used, but its value does not escape.
        var previousReceiver = _receiverUse;
        _receiverUse = member.Target is IdentifierExpression;
        var walked = StartStructWalk(member.Target, scope);
        SemanticType targetType;
        try
        {
            targetType = AnalyzeExpression(member.Target, scope, functions, moduleName, imports, expressionTypes, resolvedCalls);
        }
        finally
        {
            EndStructWalk(walked);
            _receiverUse = previousReceiver;
        }
        // A type parameter has the properties of its constraint interfaces, read through the interface that declares them.
        if (targetType.IsGenericParameter
            && ConstraintDeclaring(targetType, member.MemberName, member.Location,
                constraint => constraint.DeclaredProperties.Any(property => property.Name == member.MemberName)) is { } declaringInterface)
            targetType = SemanticType.Interface(declaringInterface.QualifiedName);
        // An array and a string have a read-only length, of type usize.
        if ((targetType.IsArray || targetType.Kind == SemanticTypeKind.String) && member.MemberName == "length")
            return SemanticType.FromPrimitive(PrimitiveType.USize);
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
                // A write to a set-only property names it without reading it. Its setter is checked by the write itself.
                if (getter is null && !forWrite)
                    throw Error($"Property '{property.Name}' is write-only", member.Location, property.Name.Length);
                if (getter is not null)
                    EnsureAccessorVisible(property, getter, member.Location);
                return SubstituteGenericType(property.Type, substitutions);
            }
            var field = cls.Fields.FirstOrDefault(candidate => candidate.Name == member.MemberName);
            if (field is not null)
            {
                // A private field is checked against the class that declares it, so an inherited one is not visible to its subclass.
                // Protected and public fields are checked against the receiver's class, as before.
                var checkedClass = field.Visibility == Visibility.Private ? DeclaringClass(field, cls) : cls;
                EnsureMemberAccessible(field.Visibility, "Field", field.Name, checkedClass, moduleName, member.Location,
                    checkedClass.QualifiedName);
                return SubstituteGenericType(field.Type, substitutions);
            }
            // A static field of a base class is reached through an object of the subclass, as through the subclass name. A static
            // field of the object's own class is not: it is named through the class.
            if (cls.StaticFields.Any(field => field.Name == member.MemberName))
                throw Error($"Static field '{member.MemberName}' of '{cls.Name}' is accessed through its class, as {cls.Name}.{member.MemberName}",
                    member.Location, member.MemberName.Length);
            if (BaseClassOf(cls) is { } baseOfObject
                && DeclaringStaticClass(baseOfObject, candidate => candidate.StaticFields.Any(field => field.Name == member.MemberName)) is not null)
            {
                var inheritedStatic = StaticFieldOf(cls, member, moduleName);
                if (forWrite && inheritedStatic.IsConst)
                    throw ErrorWithCode(DiagnosticCode.ConstAssignment,
                        $"Cannot assign to const static field '{inheritedStatic.Name}'", member.Location, member.MemberName.Length);
                _globalReferences[member] = inheritedStatic;
                return inheritedStatic.Type;
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
                throw Error($"Interface '{targetType}' has no member named '{member.MemberName}'",
                    member.Location);
            var getter = contract.Declaration.Accessors.FirstOrDefault(
                accessor => accessor.Kind == PropertyAccessorKind.Get);
            if (getter is null && !forWrite)
                throw Error($"Property '{contract.Name}' is write-only", member.Location, contract.Name.Length);
            if (getter is not null)
                EnsureAccessorVisible(contract, getter, member.Location);
            return SubstituteGenericType(contract.Type, substitutions);
        }
        if (!targetType.IsStruct)
            throw ErrorWithCode(DiagnosticCode.OperandRequirement, $"Member access requires a struct or class value, got {targetType}", member.Location);
        var structure = _structsByName[targetType.StructName!];
        var structField = structure.Fields.FirstOrDefault(candidate => candidate.Name == member.MemberName)
            ?? throw Error($"Struct '{targetType}' has no field named '{member.MemberName}'", member.Location, member.MemberName.Length);
        // The field's type is the type of the struct as it is used, so a generic struct's type parameters are replaced by its arguments.
        return SubstituteGenericType(structField.Type, StructTypeArguments(structure, targetType));
    }

    // The type arguments of a use of a generic struct, by the struct's parameter names. A non-generic struct has none.
    private static Dictionary<string, SemanticType> StructTypeArguments(StructSymbol structure, SemanticType use) =>
        (structure.TypeParameters ?? [])
            .Zip(use.TypeArguments ?? [], (name, argument) => (name, argument))
            .ToDictionary(pair => pair.name, pair => pair.argument, StringComparer.Ordinal);

    private SemanticType AnalyzeNewExpression(
        NewExpression expression, Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions, string moduleName,
        IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression, SemanticType> types,
        IDictionary<CallExpression, FunctionSymbol> calls,
        SemanticType? expectedType = null)
    {
        var newName = expression.Type.NamedType is null ? null : FindTypeName(expression.Type.NamedType, expression.Location);
        if (newName is null || !_classesByName.TryGetValue(newName, out var cls)
            || expression.Type.IsPointer || expression.Type.IsArray || expression.Type.IsString)
            throw Error("Only declared classes can be constructed with 'new'", expression.Location);
        RequireConstructibleHere(cls, expression.Location);
        var arguments = expression.Arguments.Select(argument =>
            AnalyzeExpression(argument, scope, functions, moduleName, imports, types, calls)).ToArray();
        var explicitTypeArguments = expression.Type.DeclaredTypeArguments.Count == 0
            ? null
            : ResolveType(expression.Type).TypeArguments;
        return SelectConstructor(expression, cls, expression.Arguments, arguments, explicitTypeArguments, expectedType, moduleName);
    }

    private SemanticType AnalyzeClassConstructionExpression(
        ClassConstructionExpression expression, Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions, string moduleName,
        IReadOnlyList<ImportDeclaration> imports, IDictionary<Expression, SemanticType> types,
        IDictionary<CallExpression, FunctionSymbol> calls,
        SemanticType? expectedType = null)
    {
        // `Name(...)` builds an object when Name is a class. When no class has that name but a function does, it is a call of
        // that function, whatever the capitalization. The call is recorded against the expression and emitted in its place.
        var className = FindTypeName(expression.TypeName, expression.Location);
        // A dotted name that is not a class is a union case, a static method, or a module function, which are all calls.
        var isQualifiedCall = className is null && expression.TypeName.Contains('.', StringComparison.Ordinal);
        if (isQualifiedCall || ((className is null || !_classesByName.ContainsKey(className))
            && ResolveFunctions(expression.TypeName, moduleName, imports, functions).Any()))
        {
            var call = new CallExpression(expression.TypeName, expression.Arguments, expression.Location, expression.TypeArguments);
            _constructionCalls[expression] = call;
            // The backend emits this call in place of the construction, so the call carries the type that the construction has.
            var callType = AnalyzeCall(call, scope, functions, moduleName, imports, types, calls, expectedType, false);
            types[call] = callType;
            return callType;
        }
        if (className is null || !_classesByName.TryGetValue(className, out var cls))
            throw ErrorWithCode(DiagnosticCode.NotDeclared, $"Class '{expression.TypeName}' is not declared", expression.Location);
        RequireConstructibleHere(cls, expression.Location);
        var arguments = expression.Arguments.Select(argument =>
            AnalyzeExpression(argument, scope, functions, moduleName, imports, types, calls)).ToArray();
        IReadOnlyList<SemanticType>? explicitTypeArguments = null;
        if (expression.ExplicitTypeArguments.Count > 0)
        {
            ValidateTypeArgumentCount(
                new TypeSyntax(null, false, expression.Location, NamedType: expression.TypeName,
                    TypeArguments: expression.ExplicitTypeArguments.ToArray()),
                cls.DeclaredTypeParameters.Count);
            explicitTypeArguments = expression.ExplicitTypeArguments.Select(ResolveType).ToArray();
        }
        return SelectConstructor(expression, cls, expression.Arguments, arguments, explicitTypeArguments, expectedType, moduleName);
    }

    // Chooses the constructor that a construction calls, and the type arguments of its class. A construction that states them
    // uses them. Otherwise each constructor infers them from the argument types and the expected type, and it is a candidate
    // when the arguments convert to its parameters after that. The closest candidate is not chosen here: a construction has
    // exactly one matching constructor, and two are ambiguous.
    private SemanticType SelectConstructor(
        Expression construction,
        ClassSymbol cls,
        IReadOnlyList<Expression> arguments,
        IReadOnlyList<SemanticType> argumentTypes,
        IReadOnlyList<SemanticType>? explicitTypeArguments,
        SemanticType? expectedType,
        string moduleName)
    {
        var location = construction.Location;
        IReadOnlyDictionary<string, SemanticType>? explicitBindings = explicitTypeArguments is null
            ? null
            : cls.DeclaredTypeParameters.Zip(explicitTypeArguments, (name, type) => (name, type))
                .ToDictionary(item => item.name, item => item.type, StringComparer.Ordinal);
        // Each accessible constructor binds the arguments to its parameters, then infers the class's type arguments from them.
        var bound = cls.Constructors
            .Where(constructor => IsMemberAccessible(constructor.Visibility, cls.Name, cls.ModuleName, moduleName))
            .Select(constructor =>
            {
                var order = BindArguments(constructor.Parameters, arguments, cls.Name, out var problem);
                var bindings = order is null
                    ? null
                    : explicitBindings ?? InferConstructorTypeArguments(cls, constructor, argumentTypes, order, expectedType);
                return (constructor, order, problem, bindings);
            })
            .ToList();
        var matches = bound
            .Where(item => item.order is not null && item.bindings is not null
                && Enumerable.Range(0, item.order.Length)
                    .Where(position => item.order[position] != DefaultArgument)
                    .All(position => CanAssign(
                        SubstituteGenericType(item.constructor.Parameters[position].Type, item.bindings),
                        ValueOf(arguments[item.order[position]]),
                        argumentTypes[item.order[position]])))
            .ToArray();
        if (matches.Length == 0)
        {
            EnsureConstructorAccessible(cls, arguments.Count, moduleName, location);
            var accepting = bound.Where(item => item.order is not null).ToList();
            if (explicitBindings is null && cls.DeclaredTypeParameters.Count > 0 && accepting.Count > 0
                && accepting.All(item => item.bindings is null))
                throw ErrorWithCode(DiagnosticCode.CannotInfer,
                    $"Cannot infer the type arguments of '{cls.Name}' from its arguments; write them explicitly, such as {cls.Name}<T>(...)",
                    location, cls.Name.Length);
            if (HasNamedArguments(arguments))
                throw ErrorWithCode(DiagnosticCode.ArgumentCount,
                    bound.Select(item => item.problem).FirstOrDefault(text => text is not null)
                        ?? $"No constructor of '{cls.Name}' takes these arguments",
                    location, cls.Name.Length);
            throw Error($"No constructor of '{cls.Name}' matches the supplied arguments", location);
        }
        if (matches.Length > 1)
            throw Error($"Constructor call for '{cls.Name}' is ambiguous", location);

        var chosen = matches[0];
        _constructorSelections[construction] = IndexOfConstructor(cls, chosen.constructor);
        _argumentOrders[construction] = chosen.order!;
        var typeArguments = cls.DeclaredTypeParameters.Select(name => chosen.bindings![name]).ToArray();
        ValidateTypeArgumentConstraints(cls.Declaration.DeclaredTypeParameters, typeArguments, location);
        return SemanticType.Class(cls.QualifiedName, cls.DeclaredTypeParameters.Count == 0 ? null : typeArguments);
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
            if (!IsAddressable(expression.Operand, scope) && !IsFieldOfClassObject(expression.Operand, expressionTypes))
                throw ErrorWithCode(DiagnosticCode.OperandRequirement, "The '&' operator requires an addressable operand", expression.Location);
            // A string's bytes are read-only, so no pointer into them may be formed.
            if (expression.Operand is IndexExpression { Target: var stringTarget }
                && expressionTypes.TryGetValue(stringTarget, out var indexedType) && indexedType.Kind == SemanticTypeKind.String)
                throw ErrorWithCode(DiagnosticCode.OperandRequirement,
                    "The '&' operator cannot take the address of a string element, because string bytes are read-only", expression.Location);
            return SemanticType.PointerTo(operandType);
        }

        if (expression.Operator == TokenKind.Star)
        {
            if (expression.Operand is NullLiteralExpression)
                throw Error("Cannot dereference a null pointer", expression.Location);
            if (!operandType.IsPointer)
                throw ErrorWithCode(DiagnosticCode.OperandRequirement, "The '*' operator requires a pointer operand", expression.Location);
            if (operandType.IsOpaquePointer)
                throw Error("Cannot dereference an opaque void pointer; cast it to a typed pointer first", expression.Location);
            ClassifyDereference(expression.Operand, scope, expression.Location);
            return operandType.Pointee!;
        }

        if (expression.Operator == TokenKind.Bang)
        {
            if (operandType != SemanticType.FromPrimitive(PrimitiveType.Bool))
            {
                throw ErrorWithCode(DiagnosticCode.OperandRequirement, "The '!' operator requires a bool operand", expression.Location);
            }

            return operandType;
        }

        if (expression.Operator == TokenKind.Tilde)
        {
            // The complement of a flags value is a value of the same flags enum.
            if (IsFlagsEnum(operandType))
                return operandType;
            if (!operandType.IsInteger)
                throw ErrorWithCode(DiagnosticCode.OperandRequirement, $"The '~' operator requires an integer operand, got {operandType}", expression.Location);
            return operandType;
        }

        if (expression.Operator is TokenKind.PlusPlus or TokenKind.MinusMinus)
        {
            // A member is changed through its field or its setter, so it is a valid target even when it has no address.
            if (!IsAddressable(expression.Operand, scope) && expression.Operand is not MemberAccessExpression)
                throw ErrorWithCode(DiagnosticCode.OperandRequirement, $"The '{expression.Operator}' operator requires an addressable operand", expression.Location);
            ValidateWritableTarget(expression.Operand, scope, expressionTypes);
            if (!operandType.IsInteger && !operandType.IsPointer)
                throw ErrorWithCode(DiagnosticCode.OperandRequirement, $"The '{expression.Operator}' operator requires an integer or pointer operand", expression.Location);
            // Moving a pointer by one element gives it an address that is not known, so its nullness is forgotten.
            if (operandType.IsPointer && expression.Operand is IdentifierExpression moved)
                SetPointerNullness(scope.Lookup(moved.Name, moved.Location), PointerNullness.Unknown);
            return operandType;
        }

        if (!operandType.IsNumeric)
        {
            throw ErrorWithCode(DiagnosticCode.OperandRequirement, $"The '{expression.Operator}' operator requires a numeric operand", expression.Location);
        }

        return operandType;
    }

    // Integer operations that always fail at run time with a constant operand are rejected here: division or remainder by
    // the constant zero, and a shift count that is negative or not below the width of the shifted integer.
    private static void CheckConstantIntegerOperation(BinaryExpression expression, SemanticType left, SemanticType right)
    {
        if (expression.Operator is TokenKind.Slash or TokenKind.Percent && left.IsInteger && right.IsInteger
            && TryGetConstantInteger(expression.Right, out var divisor) && divisor.IsZero)
            throw Error("Division by zero", expression.Right.Location);
        if (expression.Operator is TokenKind.LessThanLessThan or TokenKind.GreaterThanGreaterThan
            && left.IsInteger && TryGetConstantInteger(expression.Right, out var count)
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
        return BinaryResultType(expression, left, right, expressionTypes);
    }

    // The type of a binary operation once its operands are analyzed. A compound assignment is validated with the same rules,
    // applied to its target and value.
    private SemanticType BinaryResultType(
        BinaryExpression expression,
        SemanticType left,
        SemanticType right,
        IDictionary<Expression, SemanticType> expressionTypes)
    {
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

        // Reference classes and interfaces compare by identity: two references are equal when they name the same object, and a
        // null reference equals only null. A value class has no identity, so its values are not compared.
        if (expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals && IsReferenceComparison(expression, left, right))
        {
            RequireIdentityComparison(expression, left, right, expressionTypes);
            return SemanticType.FromPrimitive(PrimitiveType.Bool);
        }
        if (expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals && (IsValueClassType(left) || IsValueClassType(right)))
            throw ErrorWithCode(DiagnosticCode.OperandRequirement,
                "A value class has no identity, so its values cannot be compared with == or !=; compare their fields", expression.Location);

        if ((left.IsPointer || right.IsPointer) && !left.IsFunctionPointer && !right.IsFunctionPointer)
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
            {
                // Pointers compare when they point to the same type, or when either is an opaque void pointer.
                if (!SemanticTypeComparer.Instance.Equals(left.Pointee, right.Pointee)
                    && !left.IsOpaquePointer && !right.IsOpaquePointer)
                    throw Error($"Cannot compare {left} with {right}: the pointers point to different types",
                        expression.Location);
                return SemanticType.FromPrimitive(PrimitiveType.Bool);
            }
            throw Error($"Operator '{expression.Operator}' is not valid for pointer operands", expression.Location);
        }

        // Function pointers compare by address, and only for equality. The null literal is compared with any function pointer, and two
        // function pointers compare when they have the same signature.
        if (left.IsFunctionPointer || right.IsFunctionPointer)
        {
            if (expression.Operator is not (TokenKind.EqualsEquals or TokenKind.BangEquals))
                throw Error($"Operator '{expression.Operator}' is not valid for function pointer operands", expression.Location);
            if (left.IsFunctionPointer && expression.Right is NullLiteralExpression)
            {
                expressionTypes[expression.Right] = left;
                return SemanticType.FromPrimitive(PrimitiveType.Bool);
            }
            if (right.IsFunctionPointer && expression.Left is NullLiteralExpression)
            {
                expressionTypes[expression.Left] = right;
                return SemanticType.FromPrimitive(PrimitiveType.Bool);
            }
            if (left != right)
                throw Error($"Cannot compare {left} with {right}: the function pointers have different signatures", expression.Location);
            return SemanticType.FromPrimitive(PrimitiveType.Bool);
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

        // Two values of one enum compare for equality, by their members.
        if (expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals && left.IsEnum && left == right)
            return SemanticType.FromPrimitive(PrimitiveType.Bool);

        // A flags value is tested against the literal 0, as in `(flags & Red) != 0`.
        if (expression.Operator is TokenKind.EqualsEquals or TokenKind.BangEquals
            && TryFlagsZeroComparison(expression, left, right, expressionTypes))
            return SemanticType.FromPrimitive(PrimitiveType.Bool);

        // The bitwise operators combine the values of one flags enum, and the result is a value of that enum.
        if (expression.Operator is TokenKind.Ampersand or TokenKind.Pipe or TokenKind.Caret && (left.IsEnum || right.IsEnum))
            return FlagsOperationType(expression, left, right);

        var requiresInteger = expression.Operator is TokenKind.Ampersand or TokenKind.Pipe or TokenKind.Caret
            or TokenKind.LessThanLessThan or TokenKind.GreaterThanGreaterThan or TokenKind.Percent;
        if (requiresInteger && (!left.IsInteger || !right.IsInteger))
            throw ErrorWithCode(DiagnosticCode.OperandRequirement, $"Operator '{expression.Operator}' requires integer operands", expression.Location);
        if (!left.IsNumeric || !right.IsNumeric)
            throw ErrorWithCode(DiagnosticCode.OperandRequirement, $"Binary operator '{expression.Operator}' requires numeric operands", expression.Location);

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
        var binary = (BinaryExpression)expression;
        throw ErrorWithCode(DiagnosticCode.MixedOperandTypes,
            $"Binary operator '{binary.Operator}' has operands of different types: {left} and {right}", expression.Location);
    }

    private bool IsFlagsEnum(SemanticType type) =>
        type.IsEnum && _enumsByName.TryGetValue(type.EnumName!, out var symbol) && symbol.IsFlags;

    // `|`, `&` and `^` need both operands of one @flags enum. The result is that enum.
    private SemanticType FlagsOperationType(BinaryExpression expression, SemanticType left, SemanticType right)
    {
        var flags = left.IsEnum ? left : right;
        if (!IsFlagsEnum(flags))
            throw ErrorWithCode(DiagnosticCode.OperandRequirement,
                $"Operator '{expression.Operator}' combines values of a @flags enum, and {flags} is not declared @flags", expression.Location);
        if (left != right)
            throw ErrorWithCode(DiagnosticCode.MixedFlagsTypes,
                $"Operator '{expression.Operator}' combines values of one @flags enum, got {left} and {right}", expression.Location);
        return left;
    }

    // `flags == 0` and `flags != 0` compare a flags value with the integer literal 0. The literal takes the enum's type, so
    // that it is compared bit for bit with the value.
    private bool TryFlagsZeroComparison(
        BinaryExpression expression,
        SemanticType left,
        SemanticType right,
        IDictionary<Expression, SemanticType> expressionTypes)
    {
        if (IsFlagsEnum(left) && IsZeroLiteral(expression.Right))
        {
            expressionTypes[expression.Right] = left;
            return true;
        }
        if (IsFlagsEnum(right) && IsZeroLiteral(expression.Left))
        {
            expressionTypes[expression.Left] = right;
            return true;
        }
        return false;
    }

    private static bool IsZeroLiteral(Expression expression) =>
        expression is IntegerLiteralExpression && TryGetConstantInteger(expression, out var value) && value.IsZero;

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

    // A field of a class object has an address. A property has none: it is read and written through its accessors.
    private bool IsFieldOfClassObject(Expression expression, IDictionary<Expression, SemanticType> expressionTypes) =>
        expression is MemberAccessExpression member
        && expressionTypes.TryGetValue(member.Target, out var targetType)
        && IsOwnedClass(targetType)
        && FindProperty(_classesByName[targetType.StructName!], member.MemberName) is null;

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
