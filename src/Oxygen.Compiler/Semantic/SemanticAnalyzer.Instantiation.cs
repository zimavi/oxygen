using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

public sealed partial class SemanticAnalyzer
{
    private sealed record DeclarationContext(
        string ModuleName,
        IReadOnlyList<ImportDeclaration> Imports,
        string? SourceName);

    // Module, imports and source file of each declared function and type, keyed by a type's qualified name, so
    // instantiations are analyzed in the context of their declaration rather than the context of whoever uses them.
    private readonly Dictionary<FunctionDeclaration, DeclarationContext> _functionContexts =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, DeclarationContext> _typeContexts = new(StringComparer.Ordinal);
    private readonly HashSet<FunctionDeclaration> _analyzedSpecializations = new(ReferenceEqualityComparer.Instance);

    // Field initializers of the class instantiation whose constructor is being analyzed.
    private IReadOnlyDictionary<ClassFieldDeclaration, Expression>? _fieldInitializerOverrides;

    private static bool IsOpenSpecialization(FunctionSymbol symbol) =>
        symbol.SpecializationArguments?.Any(ContainsGenericParameter) == true;

    // The module that declares Printable and the built-in implementations of it for primitive types.
    private const string PrintableModule = "std.io";

    private static bool IsStandardLibraryModule(string moduleName) =>
        moduleName == "std" || moduleName.StartsWith("std.", StringComparison.Ordinal);

    // Checks that each type argument satisfies the constraint of its parameter (`<T : Interface>`).
    // Arguments that are still generic parameters are checked when the enclosing declaration is
    // instantiated with concrete types.
    private void ValidateTypeArgumentConstraints(
        IReadOnlyList<TypeParameterSyntax> parameters,
        IReadOnlyList<SemanticType> arguments,
        SourceLocation location)
    {
        for (var index = 0; index < parameters.Count && index < arguments.Count; index++)
        {
            if (ContainsGenericParameter(arguments[index]))
                continue;
            foreach (var constraint in parameters[index].Constraints)
            {
                var interfaceName = FindTypeName(constraint, parameters[index].Location);
                if (interfaceName is null || !_interfacesByName.ContainsKey(interfaceName))
                    throw ErrorWithCode(DiagnosticCode.NotDeclared, $"Interface '{constraint}' is not declared", parameters[index].Location, constraint.Length);
                if (!Satisfies(arguments[index], interfaceName))
                    throw Error(
                        $"Type '{arguments[index]}' does not satisfy the constraint '{constraint}' of type parameter " +
                        $"'{parameters[index].Name}'",
                        location);
            }
        }
    }

    // Whether a concrete type can be used where `interfaceName` is required. Primitive types and string
    // implement std.io.Printable by a built-in rule, except void, which has no value to write; classes implement
    // the interfaces they declare, including those of their base classes; interface types satisfy their own interface.
    private bool Satisfies(SemanticType type, string interfaceName)
    {
        var iface = _interfacesByName[interfaceName];
        if (type.IsVoid)
            return false;
        if (type.Kind is SemanticTypeKind.Primitive or SemanticTypeKind.String)
            return iface.Name == "Printable" && iface.ModuleName == PrintableModule;
        if (type.IsInterface)
            return string.Equals(type.StructName, interfaceName, StringComparison.Ordinal);
        if (!type.IsClass)
            return false;
        for (var cls = _classesByName.GetValueOrDefault(type.StructName!); cls is not null;
             cls = cls.BaseTypeName is null ? null : _classesByName.GetValueOrDefault(cls.BaseTypeName))
        {
            if (cls.InterfaceNames.Contains(interfaceName, StringComparer.Ordinal))
                return true;
        }
        return false;
    }

    // Analyzes the body of a generic function specialization once, in the generic's own module.
    private void AnalyzeSpecialization(
        FunctionSymbol specialized,
        FunctionSymbol generic,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        IReadOnlyList<ImportDeclaration> callerImports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        if (IsOpenSpecialization(specialized) || !_analyzedSpecializations.Add(specialized.Declaration))
            return;
        var context = _functionContexts.GetValueOrDefault(generic.Declaration);
        var substitutions = generic.DeclaredGenericParameters
            .Zip(specialized.SpecializationArguments!, (name, type) => (name, type))
            .ToDictionary(pair => pair.name, pair => pair.type, StringComparer.Ordinal);
        AnalyzeInstantiatedMember(
            $"{generic.QualifiedName}<{string.Join(", ", specialized.SpecializationArguments!)}>",
            context?.SourceName,
            substitutions,
            reportWarnings: true,
            () => AnalyzeFunction(specialized, functions, generic.ModuleName, context?.Imports ?? callerImports,
                expressionTypes, resolvedCalls));
    }

    // Runs a nested analysis of an instantiated declaration without disturbing the state of the
    // function whose analysis requested it, and attributes errors to the instantiation.
    private void AnalyzeInstantiatedMember(
        string instantiation,
        string? sourceName,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        bool reportWarnings,
        Action analyze,
        IReadOnlyDictionary<ClassFieldDeclaration, Expression>? fieldInitializers = null)
    {
        var declaredVariables = _declaredVariables.ToList();
        var pointerStates = new Dictionary<VariableSymbol, PointerNullness>(_pointerStates);
        var ownerClass = _currentOwnerClass;
        var isConstructor = _currentIsConstructor;
        var isStaticMethod = _currentIsStaticMethod;
        var ownerTemplate = _currentOwnerTemplate;
        var loopDepth = _loopDepth;
        var breakableDepth = _breakableDepth;
        var genericTypes = _genericTypes;
        var currentSourceName = _sourceName;
        var diagnostics = _diagnostics;
        var initializers = _fieldInitializerOverrides;
        try
        {
            _genericTypes = substitutions;
            _sourceName = sourceName;
            _fieldInitializerOverrides = fieldInitializers;
            if (!reportWarnings)
                _diagnostics = null;
            analyze();
        }
        catch (SemanticException exception) when (!exception.IsSourceAttributed)
        {
            throw new SemanticException($"{exception.Message} (in instantiation '{instantiation}')",
                exception.Location, exception.Length)
            {
                IsSourceAttributed = true,
                SourceName = sourceName
            };
        }
        finally
        {
            _declaredVariables.Clear();
            _declaredVariables.AddRange(declaredVariables);
            _pointerStates.Clear();
            foreach (var (variable, nullness) in pointerStates)
                _pointerStates[variable] = nullness;
            _currentOwnerClass = ownerClass;
            _currentIsConstructor = isConstructor;
            _currentIsStaticMethod = isStaticMethod;
            _currentOwnerTemplate = ownerTemplate;
            _loopDepth = loopDepth;
            _breakableDepth = breakableDepth;
            _genericTypes = genericTypes;
            _sourceName = currentSourceName;
            _diagnostics = diagnostics;
            _fieldInitializerOverrides = initializers;
        }
    }

    // The member declarations of one generic class instantiation, cloned for its type arguments
    // and analyzed, keyed by the generic declaration they were instantiated from.
    private sealed record ClassInstantiation(
        IReadOnlyDictionary<FunctionDeclaration, FunctionDeclaration> Methods,
        IReadOnlyDictionary<ConstructorDeclaration, ConstructorDeclaration> Constructors,
        DestructorDeclaration? Destructor,
        IReadOnlyDictionary<ClassFieldDeclaration, Expression> FieldInitializers,
        IReadOnlyDictionary<PropertyAccessorDeclaration, BlockStatement> AccessorBodies)
    {
        public IEnumerable<Expression> Expressions() =>
            FieldInitializers.Values.SelectMany(EnumerateExpressions)
                .Concat(Methods.Values.SelectMany(method => EnumerateExpressions(method.Body)))
                .Concat(Constructors.Values.SelectMany(constructor => constructor.Initializer?.Arguments ?? []))
                .Concat(Constructors.Values.SelectMany(constructor => EnumerateExpressions(constructor.Body)))
                .Concat(EnumerateExpressions(Destructor?.Body))
                .Concat(AccessorBodies.Values.SelectMany(body => EnumerateExpressions(body)));
    }

    // Clones the members of generic class `cls` for the concrete `instance` and analyzes them with
    // the class's type parameters bound, recording concrete facts for every cloned node.
    private ClassInstantiation InstantiateClass(
        ClassSymbol cls,
        SemanticType instance,
        IReadOnlyDictionary<string, SemanticType> substitutions,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var context = _typeContexts.GetValueOrDefault(cls.QualifiedName);
        var imports = context?.Imports ?? [];
        var ownerModule = cls.ModuleName;
        var name = instance.ToString()!;
        var cloner = new InstantiationCloner(substitutions);
        SemanticType Substitute(SemanticType type) => SubstituteGenericType(type, substitutions);

        var fieldInitializers = new Dictionary<ClassFieldDeclaration, Expression>(ReferenceEqualityComparer.Instance);
        foreach (var field in cls.Fields)
            if (field.Declaration.Initializer is not null)
                fieldInitializers[field.Declaration] = cloner.Expression(field.Declaration.Initializer);

        var methods = new Dictionary<FunctionDeclaration, FunctionDeclaration>(ReferenceEqualityComparer.Instance);
        foreach (var method in cls.Methods.Concat(cls.StaticMethods))
        {
            var clone = cloner.Function(method.Declaration);
            methods[method.Declaration] = clone;
            // Generic methods are specialized at their call sites; external methods have no body.
            if (method.IsGeneric || method.IsExternal)
                continue;
            var symbol = method with
            {
                ReturnType = Substitute(method.ReturnType),
                Parameters = method.Parameters.Select((parameter, index) => parameter with
                {
                    Type = Substitute(parameter.Type),
                    Declaration = clone.Parameters[index]
                }).ToArray(),
                Declaration = clone
            };
            AnalyzeInstantiatedMember(name, context?.SourceName, substitutions, reportWarnings: false,
                () => AnalyzeFunction(symbol, functions, cls.ModuleName, imports, expressionTypes, resolvedCalls));
        }

        var constructors = new Dictionary<ConstructorDeclaration, ConstructorDeclaration>(
            ReferenceEqualityComparer.Instance);
        foreach (var constructor in cls.Constructors)
        {
            var clone = cloner.Constructor(constructor.Declaration);
            constructors[constructor.Declaration] = clone;
            var symbol = constructor with
            {
                Parameters = constructor.Parameters.Select((parameter, index) => parameter with
                {
                    Type = Substitute(parameter.Type),
                    Declaration = clone.Parameters[index]
                }).ToArray(),
                Declaration = clone
            };
            AnalyzeInstantiatedMember(name, context?.SourceName, substitutions, reportWarnings: false,
                () => AnalyzeFunction(CreateConstructorFunctionSymbol(cls.ModuleName, symbol), functions, ownerModule,
                    imports, expressionTypes, resolvedCalls, symbol.Declaration),
                fieldInitializers);
        }

        DestructorDeclaration? destructor = null;
        if (cls.Destructor is not null)
        {
            destructor = cloner.Destructor(cls.Destructor);
            AnalyzeInstantiatedMember(name, context?.SourceName, substitutions, reportWarnings: false,
                () => AnalyzeFunction(CreateDestructorFunctionSymbol(cls.ModuleName, destructor), functions, ownerModule,
                    imports, expressionTypes, resolvedCalls));
        }

        // A getter or setter is analysed as a method of the instance, with its property type for these arguments.
        var accessorBodies = new Dictionary<PropertyAccessorDeclaration, BlockStatement>(ReferenceEqualityComparer.Instance);
        foreach (var property in cls.DeclaredProperties)
        {
            foreach (var accessor in property.Declaration.Accessors)
            {
                if (accessor.Body is null || accessor.Kind is not (PropertyAccessorKind.Get or PropertyAccessorKind.Set))
                    continue;
                var clone = cloner.Block(accessor.Body);
                accessorBodies[accessor] = clone;
                var symbol = CreateAccessorSymbol(cls, property with { Type = Substitute(property.Type) }, accessor,
                    cls.ModuleName, clone);
                AnalyzeInstantiatedMember(name, context?.SourceName, substitutions, reportWarnings: false,
                    () => AnalyzeFunction(symbol, functions, cls.ModuleName, imports, expressionTypes, resolvedCalls));
            }
        }

        return new ClassInstantiation(methods, constructors, destructor, fieldInitializers, accessorBodies);
    }

    // Deep-copies declarations for one generic instantiation. Type parameters are replaced by the
    // concrete type syntax, and every statement and expression becomes a fresh node, so the
    // reference-keyed semantic tables (expression types, resolved calls, receiver types) keep
    // the facts of each instantiation separate from the generic declaration and from each other.
    private sealed class InstantiationCloner(IReadOnlyDictionary<string, SemanticType> substitutions)
    {
        // With substitutesOwnTypeParameters the function's own type parameters are being bound (a
        // generic function specialization). Otherwise the substitutions belong to an enclosing
        // class, and the function's own type parameters shadow them.
        public FunctionDeclaration Function(FunctionDeclaration source, bool substitutesOwnTypeParameters = false)
        {
            if (substitutesOwnTypeParameters)
                return source with
                {
                    ReturnType = Type(source.ReturnType),
                    Parameters = source.Parameters.Select(Parameter).ToArray(),
                    Body = Block(source.Body),
                    TypeParameters = []
                };
            var cloner = source.DeclaredTypeParameters.Any(parameter => substitutions.ContainsKey(parameter.Name))
                ? new InstantiationCloner(substitutions
                    .Where(pair => source.DeclaredTypeParameters.All(parameter => parameter.Name != pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))
                : this;
            return source with
            {
                ReturnType = cloner.Type(source.ReturnType),
                Parameters = source.Parameters.Select(cloner.Parameter).ToArray(),
                Body = cloner.Block(source.Body)
            };
        }

        public ConstructorDeclaration Constructor(ConstructorDeclaration source) =>
            source with
            {
                Parameters = source.Parameters.Select(Parameter).ToArray(),
                Body = Block(source.Body),
                Initializer = source.Initializer is null ? null : InitializerCall(source.Initializer)
            };

        public ConstructorInitializer InitializerCall(ConstructorInitializer source) =>
            source with { Arguments = source.Arguments.Select(Expression).ToArray() };

        public DestructorDeclaration Destructor(DestructorDeclaration source) =>
            source with { Body = Block(source.Body) };

        public ParameterDeclaration Parameter(ParameterDeclaration source) =>
            source with { Type = Type(source.Type) };

        public TypeSyntax Type(TypeSyntax source)
        {
            if (source.NamedType is not null && source.DeclaredTypeArguments.Count == 0
                && substitutions.TryGetValue(source.NamedType, out var replacement))
                return FromSemantic(replacement, source.Location);
            return source with
            {
                Pointee = source.Pointee is null ? null : Type(source.Pointee),
                ElementType = source.ElementType is null ? null : Type(source.ElementType),
                TypeArguments = source.TypeArguments?.Select(Type).ToArray()
            };
        }

        public BlockStatement Block(BlockStatement source) =>
            source with { Statements = source.Statements.Select(Statement).ToArray() };

        public Statement Statement(Statement source) => source switch
        {
            BlockStatement item => Block(item),
            VariableDeclarationStatement item => item with
            {
                Type = Type(item.Type),
                Initializer = item.Initializer is null ? null : Expression(item.Initializer)
            },
            AssignmentStatement item => item with { Target = Expression(item.Target), Value = Expression(item.Value) },
            ReturnStatement item => item with { Value = item.Value is null ? null : Expression(item.Value) },
            ExpressionStatement item => item with { Expression = Expression(item.Expression) },
            IfStatement item => item with
            {
                Condition = Expression(item.Condition),
                Then = Statement(item.Then),
                Else = item.Else is null ? null : Statement(item.Else)
            },
            WhileStatement item => item with { Condition = Expression(item.Condition), Body = Statement(item.Body) },
            ForStatement item => item with
            {
                Initializer = item.Initializer is null ? null : Statement(item.Initializer),
                Condition = item.Condition is null ? null : Expression(item.Condition),
                Increment = item.Increment is null ? null : Statement(item.Increment),
                Body = Statement(item.Body)
            },
            ForInStatement item => item with
            {
                VariableType = Type(item.VariableType),
                Collection = Expression(item.Collection),
                Body = Statement(item.Body)
            },
            ThrowStatement item => item with { Value = item.Value is null ? null : Expression(item.Value) },
            DeleteStatement item => item with { Target = Expression(item.Target) },
            TryStatement item => item with
            {
                Body = Block(item.Body),
                Catches = item.Catches.Select(clause => clause with
                {
                    ExceptionType = clause.ExceptionType is null ? null : Type(clause.ExceptionType),
                    Body = Block(clause.Body)
                }).ToArray(),
                Finally = item.Finally is null ? null : Block(item.Finally)
            },
            SwitchStatement item => item with
            {
                Value = Expression(item.Value),
                Cases = item.Cases.Select(clause => clause with
                {
                    Value = clause.Value is null ? null : Expression(clause.Value),
                    Body = Statement(clause.Body),
                    PatternType = clause.PatternType is null ? null : Type(clause.PatternType)
                }).ToArray()
            },
            _ => source with { }
        };

        public Expression Expression(Expression source) => source switch
        {
            UnaryExpression item => item with { Operand = Expression(item.Operand) },
            BinaryExpression item => item with { Left = Expression(item.Left), Right = Expression(item.Right) },
            ConditionalExpression item => item with
            {
                Condition = Expression(item.Condition),
                WhenTrue = Expression(item.WhenTrue),
                WhenFalse = Expression(item.WhenFalse)
            },
            IsExpression item => item with { TargetType = Type(item.TargetType), Operand = Expression(item.Operand) },
            NamedArgumentExpression item => item with { Value = Expression(item.Value) },
            CallExpression item => item with
            {
                Receiver = item.Receiver is null ? null : Expression(item.Receiver),
                Arguments = item.Arguments.Select(Expression).ToArray(),
                TypeArguments = item.TypeArguments?.Select(Type).ToArray()
            },
            ClassConstructionExpression item => item with
            {
                Arguments = item.Arguments.Select(Expression).ToArray(),
                TypeArguments = item.TypeArguments?.Select(Type).ToArray()
            },
            NewExpression item => item with { Type = Type(item.Type), Arguments = item.Arguments.Select(Expression).ToArray() },
            CastExpression item => item with { TargetType = Type(item.TargetType), Operand = Expression(item.Operand) },
            SizeOfExpression item => item with
            {
                TargetType = item.TargetType is null ? null : Type(item.TargetType),
                Operand = item.Operand is null ? null : Expression(item.Operand)
            },
            LengthExpression item => item with { Target = Expression(item.Target) },
            MemberAccessExpression item => item with { Target = Expression(item.Target) },
            IndexExpression item => item with { Target = Expression(item.Target), Index = Expression(item.Index) },
            ArrayLiteralExpression item => item with { Elements = item.Elements.Select(Expression).ToArray() },
            StructLiteralExpression item => item with
            {
                Fields = item.Fields.Select(field => field with { Value = Expression(field.Value) }).ToArray()
            },
            UnionConstructionExpression item => item with { Arguments = item.Arguments.Select(Expression).ToArray() },
            _ => source with { }
        };

        private static TypeSyntax FromSemantic(SemanticType type, SourceLocation location) =>
            type.IsPointer
                ? new TypeSyntax(null, false, location, Pointee: FromSemantic(type.Pointee!, location))
                : type.IsArray
                    ? new TypeSyntax(null, false, location, ArrayLength: type.ArrayLength,
                        ElementType: FromSemantic(type.ElementType!, location))
                    : type.Kind == SemanticTypeKind.Primitive
                        ? new TypeSyntax(type.Primitive, false, location)
                        : type.Kind == SemanticTypeKind.String
                            ? new TypeSyntax(null, false, location, IsString: true)
                            : new TypeSyntax(null, false, location,
                                NamedType: type.StructName ?? type.EnumName ?? type.GenericParameterName,
                                TypeArguments: type.TypeArguments?.Select(argument =>
                                    FromSemantic(argument, location)).ToArray());
    }
}
