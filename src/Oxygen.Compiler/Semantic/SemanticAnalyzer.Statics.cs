using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

// Static members of classes. A static field is one value shared by a class and all its instances, declared like a module-level
// variable: it is a numeric or bool value, and its initializer is a constant. Its name is the class's, so a generic class has one
// per instantiation, each copied from the template with the instance's type. A static method has no receiver, and is called as
// `Class.method(...)`.
public sealed partial class SemanticAnalyzer
{
    // The static fields of each instantiation of a generic class, by the instance's name. See InstanceStaticFields.
    private readonly Dictionary<string, GlobalVariableSymbol[]> _instanceStaticFields = new(StringComparer.Ordinal);

    // Whether the body being analysed is a static method, which has no `this` and no instance fields.
    private bool _currentIsStaticMethod;

    // The name of the class whose method body is being analysed, as declared, so a generic class names itself in its body.
    private string? _currentOwnerTemplate;

    // Declares the static fields of every class. A generic class's fields are declared as a template, and each instantiation
    // copies them. A non-generic class's fields are module-level variables for the backend.
    private void DeclareStaticFields(
        IReadOnlyList<CompilationUnit> units,
        IReadOnlyList<string?>? sourceNames,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functionsByName,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        for (var unitIndex = 0; unitIndex < units.Count; unitIndex++)
        {
            var unit = units[unitIndex];
            _sourceName = sourceNames is not null && unitIndex < sourceNames.Count ? sourceNames[unitIndex] : null;
            var moduleName = unit.ModuleName ?? "program";
            _typeScopeModule = moduleName;
            _typeScopeImports = unit.Imports;
            foreach (var declaration in unit.Classes)
            {
                if (!declaration.Fields.Any(field => field.IsStatic))
                    continue;
                if (declaration.Fields.GroupBy(field => field.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
                    throw Error($"Class '{declaration.Name}' contains duplicate fields", declaration.Location);
                var classKey = QualifiedTypeName(moduleName, declaration.Name);
                var cls = _classesByName[classKey];
                var fields = WithGenericTypes(declaration.DeclaredTypeParameters, () => declaration.Fields
                    .Where(field => field.IsStatic)
                    .Select(field => DeclareStaticField(cls, field, moduleName, unit.Imports, functionsByName,
                        expressionTypes, resolvedCalls))
                    .ToArray());
                _classesByName[classKey] = cls with { StaticFields = fields };
                if (declaration.DeclaredTypeParameters.Count == 0)
                    _globals.AddRange(fields);
            }
        }

        _sourceName = null;
    }

    private GlobalVariableSymbol DeclareStaticField(
        ClassSymbol cls,
        ClassFieldDeclaration field,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functionsByName,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls)
    {
        var name = field.Name;
        var type = ResolveType(field.Type);
        if (type.IsVoid)
            throw Error($"Static field '{name}' cannot have type void", field.Location, name.Length);
        // A static field of a reference type holds an object or null. Its only constant is null, so it starts as null.
        var isReference = type.Primitive is null && IsReferenceType(type);
        if (type.Primitive is null && !isReference)
            throw Error($"Static field '{name}' has type {type}; only numeric, bool, class and interface static fields are supported",
                field.Location, name.Length);

        object? value = null;
        if (field.Initializer is { } initializer)
        {
            if (isReference && initializer is not NullLiteralExpression)
                throw Error($"Static field '{name}' of type {type} can only be initialized with null", initializer.Location);
            if (!isReference && !IsConstantInitializer(initializer))
                throw Error($"Static field '{name}' must be initialized with a literal or an integer constant expression",
                    initializer.Location);
            var actual = AnalyzeExpression(initializer, GlobalScopeOf(moduleName), functionsByName, moduleName, imports,
                expressionTypes, resolvedCalls, expectedType: type);
            if (!CanAssign(type, initializer, actual))
                throw ErrorWithCode(DiagnosticCode.ConversionMismatch,
                    $"Cannot initialize '{name}' with {actual}; expected {type}", initializer.Location);
            value = isReference ? null : ConstantValueOf(initializer);
        }
        else if (field.IsConst)
        {
            throw Error($"Const static field '{name}' needs an initializer", field.Location, name.Length);
        }

        var declaration = new GlobalVariableDeclaration(field.Type, name, field.IsConst, field.Initializer, field.Location);
        var symbol = new GlobalVariableSymbol(name, cls.QualifiedName, type, field.IsConst, value, declaration);
        _globalSymbols[declaration] = symbol;
        return symbol;
    }

    // Declares static fields in a function's scope, so that the bodies of the class's methods name them directly. Each is recorded
    // as a global reference when it is read or written.
    private static void DeclareStaticFieldsIn(Scope scope, IReadOnlyList<GlobalVariableSymbol> fields)
    {
        foreach (var field in fields)
            scope.Declare(field.Name, new VariableSymbol(field.Type, field.IsConst, field.Declaration, false));
    }

    // The static fields of a class as one instantiation has them. A non-generic class has its own. A generic class has one set of
    // fields per instance, each with the instance's types, created once so that the instance's bodies and its specialized class
    // name the same variables. A template analysed with its own type parameters names no instance and registers nothing.
    private IReadOnlyList<GlobalVariableSymbol> InstanceStaticFields(
        ClassSymbol template,
        IReadOnlyDictionary<string, SemanticType> substitutions)
    {
        if (template.DeclaredTypeParameters.Count == 0)
            return template.StaticFields;
        if (template.DeclaredTypeParameters.Any(parameter =>
                !substitutions.TryGetValue(parameter, out var argument) || ContainsGenericParameter(argument)))
            return template.StaticFields;

        var instance = SemanticType.Class(template.QualifiedName,
            template.DeclaredTypeParameters.Select(parameter => substitutions[parameter]).ToArray());
        var key = instance.ToQualifiedString();
        if (_instanceStaticFields.TryGetValue(key, out var existing))
            return existing;

        var created = template.StaticFields.Select(field =>
        {
            var declaration = new GlobalVariableDeclaration(field.Declaration.Type, field.Name, field.IsConst,
                field.Declaration.Initializer, field.Declaration.Location);
            var symbol = new GlobalVariableSymbol(field.Name, key,
                SubstituteGenericType(field.Type, substitutions), field.IsConst, field.Value, declaration);
            _globalSymbols[declaration] = symbol;
            return symbol;
        }).ToArray();
        _instanceStaticFields[key] = created;
        _globals.AddRange(created);
        return created;
    }

    // The class that a member access names by its name, as in `Class.field`, or null when the target is not a class name. A
    // variable of the same name takes precedence.
    private ClassSymbol? StaticTargetClass(Expression target, Scope scope) =>
        target is IdentifierExpression identifier
            && FindTypeName(identifier.Name, identifier.Location) is { } className
            && _classesByName.TryGetValue(className, out var cls)
            && LookupVariableType(scope, identifier.Name, identifier.Location) is null
            ? cls
            : null;

    // The class that declares a static member, found among `cls` and its base classes, nearest first, or null when none declares
    // it. A static member of a base class is reached through a subclass name, as `Child.member`.
    private ClassSymbol? DeclaringStaticClass(ClassSymbol cls, Func<ClassSymbol, bool> declares)
    {
        for (ClassSymbol? owner = cls; owner is not null; owner = BaseClassOf(owner))
        {
            if (declares(owner))
                return owner;
        }

        return null;
    }

    private ClassSymbol? BaseClassOf(ClassSymbol cls) =>
        cls.BaseTypeName is not null && _classesByName.TryGetValue(cls.BaseTypeName, out var baseClass) ? baseClass : null;

    // A static member of a generic base class is named through the base's own instances, which a subclass name does not give.
    private void RequireReachableFrom(ClassSymbol cls, ClassSymbol owner, SourceLocation location, string name, int length)
    {
        if (!ReferenceEquals(owner, cls) && owner.DeclaredTypeParameters.Count > 0)
            throw Error($"Static member '{name}' of generic class '{owner.Name}' is not reached through the subclass '{cls.Name}'; write '{owner.Name}<T>.{name}' with the type arguments",
                location, length);
    }

    // The static field that `Class.member` names: the class's own, or one declared by a base class. A private one is visible
    // only in the class that declares it, and an instance field is an error.
    private GlobalVariableSymbol StaticFieldOf(ClassSymbol cls, MemberAccessExpression member, string moduleName)
    {
        var owner = DeclaringStaticClass(cls, candidate => candidate.StaticFields.Any(field => field.Name == member.MemberName));
        if (owner is null)
        {
            if (cls.Fields.Any(field => field.Name == member.MemberName))
                throw Error($"Field '{member.MemberName}' of '{cls.Name}' is an instance field; access it through an object",
                    member.Location, member.MemberName.Length);
            throw Error($"Class '{cls.Name}' has no static field named '{member.MemberName}'", member.Location, member.MemberName.Length);
        }

        RequireReachableFrom(cls, owner, member.Location, member.MemberName, member.MemberName.Length);
        var symbol = owner.StaticFields.First(field => field.Name == member.MemberName);
        if (!IsMemberAccessible(StaticFieldVisibility(owner, symbol), owner.QualifiedName, owner.ModuleName, moduleName, owner.QualifiedName))
            throw Error($"Static field '{member.MemberName}' of '{owner.Name}' cannot be used here", member.Location, member.MemberName.Length);
        return symbol;
    }

    // Whether `Class.name` names a static property. The parser compiles a static property to a static method `get_name` or `set_name`.
    private static bool IsStaticProperty(ClassSymbol cls, string name) =>
        cls.StaticMethods.Any(method => method.Name == $"get_{name}" || method.Name == $"set_{name}");

    // `Class.name` reads a static property through its getter, or writes it through its setter. The backend calls the accessor, which
    // has no receiver. A generic class has one property per instance, so its property is named with type arguments: `Class<T>.name`.
    private SemanticType AnalyzeStaticProperty(MemberAccessExpression member, ClassSymbol cls, bool forWrite, string moduleName)
    {
        var name = member.MemberName;
        if (cls.DeclaredTypeParameters.Count > 0)
            throw Error($"Static property '{name}' of generic class '{cls.Name}' is named with its type arguments, such as {cls.Name}<T>.{name}",
                member.Location, name.Length);
        var accessor = forWrite
            ? cls.StaticMethods.FirstOrDefault(method => method.Name == $"set_{name}")
                ?? throw Error($"Property '{name}' is read-only", member.Location, name.Length)
            : cls.StaticMethods.FirstOrDefault(method => method.Name == $"get_{name}")
                ?? throw Error($"Property '{name}' is write-only", member.Location, name.Length);
        RequireReachableFrom(cls, cls, member.Location, name, name.Length);
        if (!IsMemberAccessible(accessor.Visibility, MethodOwnerName(accessor), MethodOwnerModule(accessor), moduleName, cls.QualifiedName))
            throw Error($"Static property '{name}' of '{cls.Name}' cannot be used here", member.Location, name.Length);
        _usedFunctions.Add(accessor);
        if (forWrite)
        {
            _staticPropertySetters[member] = accessor;
            return accessor.Parameters[0].Type;
        }
        _staticPropertyGetters[member] = accessor;
        return accessor.ReturnType;
    }

    // The accessors of a static property are static methods, so an instance of a generic class has its own. `Class<T>.name` names the
    // property of that instance. Reading it calls the instance's getter, and writing it calls the setter. A compound assignment does
    // both, so it needs both. The call records the instance as its receiver type, which the backend uses to find the accessor.
    private SemanticType AnalyzeInstanceStaticProperty(
        CallExpression call,
        ClassSymbol cls,
        IReadOnlyList<SemanticType> typeArguments,
        string name,
        string moduleName,
        bool forWrite,
        bool compound)
    {
        if (cls.DeclaredTypeParameters.Count == 0)
            throw Error($"Class '{cls.Name}' is not generic, so its static property '{name}' is named as {cls.Name}.{name}",
                call.Location, name.Length);
        if (typeArguments.Count != cls.DeclaredTypeParameters.Count)
            throw Error($"Class '{cls.Name}' has {cls.DeclaredTypeParameters.Count} type parameter(s), got {typeArguments.Count}",
                call.Location, cls.Name.Length);
        RequireReachableFrom(cls, cls, call.Location, name, name.Length);
        var substitutions = cls.DeclaredTypeParameters
            .Zip(typeArguments, (parameter, argument) => (parameter, argument))
            .ToDictionary(item => item.parameter, item => item.argument, StringComparer.Ordinal);
        _callReceiverTypes[call] = SemanticType.Class(cls.QualifiedName, typeArguments.ToArray());
        var getter = cls.StaticMethods.FirstOrDefault(method => method.Name == $"get_{name}");
        var setter = cls.StaticMethods.FirstOrDefault(method => method.Name == $"set_{name}");
        if (forWrite || compound)
        {
            if (setter is null)
                throw Error($"Property '{name}' is read-only", call.Location, name.Length);
            RequireStaticAccessorVisible(setter, cls, name, moduleName, call.Location);
            _staticPropertySetters[call] = setter;
            _usedFunctions.Add(setter);
        }
        if (!forWrite)
        {
            if (getter is null)
                throw Error($"Property '{name}' is write-only", call.Location, name.Length);
            RequireStaticAccessorVisible(getter, cls, name, moduleName, call.Location);
            _staticPropertyGetters[call] = getter;
            _usedFunctions.Add(getter);
            return SubstituteGenericType(getter.ReturnType, substitutions);
        }
        return SubstituteGenericType(setter!.Parameters[0].Type, substitutions);
    }

    private void RequireStaticAccessorVisible(FunctionSymbol accessor, ClassSymbol cls, string name, string moduleName, SourceLocation location)
    {
        if (!IsMemberAccessible(accessor.Visibility, MethodOwnerName(accessor), MethodOwnerModule(accessor), moduleName, cls.QualifiedName))
            throw Error($"Static property '{name}' of '{cls.Name}' cannot be used here", location, name.Length);
    }

    // The generic class that a bare `Class<T>.name` names a static property of, or null. A variable is never a class name.
    private ClassSymbol? GenericStaticPropertyClass(CallExpression call, Scope scope, out string name)
    {
        name = string.Empty;
        var separator = call.Name.LastIndexOf('.');
        if (!call.IsBareName || call.ExplicitTypeArguments.Count == 0 || separator < 0)
            return null;
        var receiverName = call.Name[..separator];
        if (LookupVariableType(scope, receiverName, call.Location) is not null)
            return null;
        if (FindTypeName(receiverName, call.Location) is not { } className || !_classesByName.TryGetValue(className, out var cls)
            || cls.DeclaredTypeParameters.Count == 0)
            return null;
        name = call.Name[(separator + 1)..];
        return IsStaticProperty(cls, name) ? cls : null;
    }

    // `Class<T>.field` names the static field of one instance of a generic class. Each instance has its own field, which is the one
    // that its own bodies name by the bare name. The call node is recorded as a reference to that field's global.
    private SemanticType AnalyzeInstanceStaticField(
        CallExpression call,
        ClassSymbol cls,
        IReadOnlyList<SemanticType> typeArguments,
        string name,
        string moduleName)
    {
        if (cls.DeclaredTypeParameters.Count == 0)
            throw Error($"Class '{cls.Name}' is not generic, so its static field '{name}' is named as {cls.Name}.{name}",
                call.Location, name.Length);
        if (typeArguments.Count != cls.DeclaredTypeParameters.Count)
            throw Error($"Class '{cls.Name}' has {cls.DeclaredTypeParameters.Count} type parameter(s), got {typeArguments.Count}",
                call.Location, cls.Name.Length);
        RequireReachableFrom(cls, cls, call.Location, name, name.Length);
        var substitutions = cls.DeclaredTypeParameters
            .Zip(typeArguments, (parameter, argument) => (parameter, argument))
            .ToDictionary(item => item.parameter, item => item.argument, StringComparer.Ordinal);
        var field = InstanceStaticFields(cls, substitutions).First(candidate => candidate.Name == name);
        if (!IsMemberAccessible(StaticFieldVisibility(cls, field), cls.QualifiedName, cls.ModuleName, moduleName, cls.QualifiedName))
            throw Error($"Static field '{name}' of '{cls.Name}' cannot be used here", call.Location, name.Length);
        _globalReferences[call] = field;
        return field.Type;
    }

    // The visibility of a static field, which is recorded on its declaration in its class.
    private static Visibility StaticFieldVisibility(ClassSymbol cls, GlobalVariableSymbol symbol) =>
        cls.Declaration.Fields.FirstOrDefault(field => field.IsStatic && field.Name == symbol.Name)?.Visibility ?? Visibility.Public;

    // Resolves `Class.method(...)`, a call of a static method of a class, or of a generic class instance written as
    // `Class<T>.method(...)`. The method is ranked like any other overload. For a generic class the receiver type records the
    // instance, so the call is specialized to the instance's method, as an instance method call is.
    private SemanticType AnalyzeStaticCall(
        CallExpression call,
        ClassSymbol cls,
        string methodName,
        Scope scope,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        bool allowVoid)
    {
        var typeArguments = call.ExplicitTypeArguments.Count == 0
            ? null
            : call.ExplicitTypeArguments.Select(ResolveType).ToArray();
        // `Class<T>.field` with no parentheses names a static field of that instance. A union case of the same spelling is not a class.
        if (call.IsBareName && typeArguments is not null && cls.StaticFields.Any(field => field.Name == methodName))
            return AnalyzeInstanceStaticField(call, cls, typeArguments, methodName, moduleName);
        // `Class<T>.name` with no parentheses reads a static property of that instance, through its getter.
        if (call.IsBareName && typeArguments is not null && IsStaticProperty(cls, methodName))
            return AnalyzeInstanceStaticProperty(call, cls, typeArguments, methodName, moduleName, forWrite: false, compound: false);
        // Inside its own body, a generic class names its current instance: `Box.total()` is `Box<T>.total()` for its own T.
        if (typeArguments is null && cls.DeclaredTypeParameters.Count > 0 && _currentOwnerTemplate == cls.QualifiedName
            && cls.DeclaredTypeParameters.All(_genericTypes.ContainsKey))
            typeArguments = cls.DeclaredTypeParameters.Select(parameter => _genericTypes[parameter]).ToArray();
        if (typeArguments is null && cls.DeclaredTypeParameters.Count > 0)
            throw Error($"Class '{cls.Name}' is generic; write its type arguments, such as {cls.Name}<T>.{methodName}(...)",
                call.Location, cls.Name.Length);
        // A static method of a base class is called through the subclass name, as `Child.method(...)`, and its receiver is the base.
        var owner = DeclaringStaticClass(cls, candidate => candidate.StaticMethods.Any(method => method.Name == methodName)) ?? cls;
        var named = owner.StaticMethods.Where(method => method.Name == methodName).ToArray();
        if (named.Length == 0)
        {
            if (IsStaticProperty(cls, methodName))
                throw Error($"'{methodName}' of '{cls.Name}' is a static property, not a method", call.Location, methodName.Length);
            if (cls.StaticFields.Any(field => field.Name == methodName))
                throw Error($"'{methodName}' of '{cls.Name}' is a static field, not a method; a generic class's static field is named by its bare name inside the class", call.Location, methodName.Length);
            if (GetClassMethods(cls.QualifiedName, []).Any(method => method.Name == methodName))
                throw Error($"Method '{methodName}' of '{cls.Name}' is an instance method; call it on an object",
                    call.Location, methodName.Length);
            throw ErrorWithCode(DiagnosticCode.NotDeclared,
                $"Class '{cls.Name}' has no static method named '{methodName}'", call.Location, methodName.Length);
        }
        RequireReachableFrom(cls, owner, call.Location, methodName, methodName.Length);
        var ownerTypeArguments = ReferenceEquals(owner, cls) ? typeArguments : null;
        _callReceiverTypes[call] = SemanticType.Class(owner.QualifiedName, ownerTypeArguments);
        var substitutions = owner.DeclaredTypeParameters
            .Zip(ownerTypeArguments ?? [], (name, type) => (name, type))
            .ToDictionary(item => item.name, item => item.type, StringComparer.Ordinal);

        var candidates = named.Where(method => IsMemberAccessible(method.Visibility,
            MethodOwnerName(method), MethodOwnerModule(method), moduleName, owner.QualifiedName)).ToArray();
        if (candidates.Length == 0)
            throw Error($"Static method '{methodName}' of '{owner.Name}' cannot be used here", call.Location, methodName.Length);

        var argumentTypes = call.Arguments
            .Select(argument => AnalyzeExpression(argument, scope, functions, moduleName, imports, expressionTypes, resolvedCalls))
            .ToArray();
        var bound = candidates
            .Select(method => (method, order: BindArguments(method.Parameters, call.Arguments, methodName, out _)))
            .Where(item => item.order is not null)
            .Select(item => (item.method, order: item.order!))
            .ToList();
        var ranked = bound
            .Select(item => (item.method, item.order,
                cost: CandidateCost(item.method, call, argumentTypes, item.order, substitutions)))
            .Where(item => item.cost is not null)
            .Select(item => (item.method, item.order, cost: item.cost!.Value))
            .ToList();
        var matches = ranked.Count == 0
            ? []
            : ranked.Where(item => item.cost.CompareTo(ranked.Min(other => other.cost)) == 0).ToList();
        if (matches.Count == 0)
        {
            var reason = HasNamedArguments(call.Arguments)
                ? bound.Count == 0
                    ? BindArguments(candidates[0].Parameters, call.Arguments, methodName, out var problem) is null ? problem : null
                    : null
                : null;
            throw ErrorWithCode(DiagnosticCode.ArgumentCount,
                reason ?? $"No overload of '{methodName}' matches the argument types", call.Location, methodName.Length);
        }
        if (matches.Count > 1)
            throw Error($"Method call '{methodName}' is ambiguous: no overload is closer than the others", call.Location);

        var (method, order, _) = matches[0];
        resolvedCalls[call] = method;
        _argumentOrders[call] = order;
        _usedFunctions.Add(method);
        var returnType = SubstituteGenericType(method.ReturnType, substitutions);
        if (returnType.IsVoid && !allowVoid)
            throw Error($"Void function '{methodName}' cannot be used as an expression", call.Location);
        return returnType;
    }
}
