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
            foreach (var declaration in unit.Classes)
            {
                if (!declaration.Fields.Any(field => field.IsStatic))
                    continue;
                if (declaration.Fields.GroupBy(field => field.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
                    throw Error($"Class '{declaration.Name}' contains duplicate fields", declaration.Location);
                var cls = _classesByName[declaration.Name];
                var fields = WithGenericTypes(declaration.DeclaredTypeParameters, () => declaration.Fields
                    .Where(field => field.IsStatic)
                    .Select(field => DeclareStaticField(cls, field, moduleName, unit.Imports, functionsByName,
                        expressionTypes, resolvedCalls))
                    .ToArray());
                _classesByName[declaration.Name] = cls with { StaticFields = fields };
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
        if (type.Primitive is null)
            throw Error($"Static field '{name}' has type {type}; only numeric and bool static fields are supported",
                field.Location, name.Length);

        object? value = null;
        if (field.Initializer is { } initializer)
        {
            if (!IsConstantInitializer(initializer))
                throw Error($"Static field '{name}' must be initialized with a literal or an integer constant expression",
                    initializer.Location);
            var actual = AnalyzeExpression(initializer, GlobalScopeOf(moduleName), functionsByName, moduleName, imports,
                expressionTypes, resolvedCalls, expectedType: type);
            if (!CanAssign(type, initializer, actual))
                throw ErrorWithCode(DiagnosticCode.ConversionMismatch,
                    $"Cannot initialize '{name}' with {actual}; expected {type}", initializer.Location);
            value = ConstantValueOf(initializer);
        }
        else if (field.IsConst)
        {
            throw Error($"Const static field '{name}' needs an initializer", field.Location, name.Length);
        }

        var declaration = new GlobalVariableDeclaration(field.Type, name, field.IsConst, field.Initializer, field.Location);
        var symbol = new GlobalVariableSymbol(name, $"{cls.ModuleName}.{cls.Name}", type, field.IsConst, value, declaration);
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

        var instance = SemanticType.Class(template.Name,
            template.DeclaredTypeParameters.Select(parameter => substitutions[parameter]).ToArray());
        var key = instance.ToString()!;
        if (_instanceStaticFields.TryGetValue(key, out var existing))
            return existing;

        var created = template.StaticFields.Select(field =>
        {
            var declaration = new GlobalVariableDeclaration(field.Declaration.Type, field.Name, field.IsConst,
                field.Declaration.Initializer, field.Declaration.Location);
            var symbol = new GlobalVariableSymbol(field.Name, $"{template.ModuleName}.{key}",
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
            && _classesByName.TryGetValue(identifier.Name, out var cls)
            && LookupVariableType(scope, identifier.Name, identifier.Location) is null
            ? cls
            : null;

    // The static field that `Class.member` names. Only the class's own static fields are reached this way. A private one is
    // visible only in its class, an instance field is an error, and a field declared by a base class is named through that base.
    private GlobalVariableSymbol StaticFieldOf(ClassSymbol cls, MemberAccessExpression member, string moduleName)
    {
        var symbol = cls.StaticFields.FirstOrDefault(field => field.Name == member.MemberName);
        if (symbol is null)
        {
            if (cls.Fields.Any(field => field.Name == member.MemberName))
                throw Error($"Field '{member.MemberName}' of '{cls.Name}' is an instance field; access it through an object",
                    member.Location, member.MemberName.Length);
            var declaringBase = BaseClassWithStaticField(cls, member.MemberName);
            if (declaringBase is not null)
                throw Error(
                    $"Static field '{member.MemberName}' is declared by '{declaringBase.Name}', not '{cls.Name}'; write '{declaringBase.Name}.{member.MemberName}'",
                    member.Location, member.MemberName.Length);
            throw Error($"Class '{cls.Name}' has no static field named '{member.MemberName}'", member.Location, member.MemberName.Length);
        }

        if (!IsMemberAccessible(StaticFieldVisibility(cls, symbol), cls.Name, cls.ModuleName, moduleName, cls.Name))
            throw Error($"Static field '{member.MemberName}' of '{cls.Name}' cannot be used here", member.Location, member.MemberName.Length);
        return symbol;
    }

    private ClassSymbol? BaseClassWithStaticField(ClassSymbol cls, string name)
    {
        for (var baseName = cls.BaseTypeName; baseName is not null && _classesByName.TryGetValue(baseName, out var baseClass);
             baseName = baseClass.BaseTypeName)
        {
            if (baseClass.StaticFields.Any(field => field.Name == name))
                return baseClass;
        }

        return null;
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
        // Inside its own body, a generic class names its current instance: `Box.total()` is `Box<T>.total()` for its own T.
        if (typeArguments is null && cls.DeclaredTypeParameters.Count > 0 && _currentOwnerTemplate == cls.Name
            && cls.DeclaredTypeParameters.All(_genericTypes.ContainsKey))
            typeArguments = cls.DeclaredTypeParameters.Select(parameter => _genericTypes[parameter]).ToArray();
        if (typeArguments is null && cls.DeclaredTypeParameters.Count > 0)
            throw Error($"Class '{cls.Name}' is generic; write its type arguments, such as {cls.Name}<T>.{methodName}(...)",
                call.Location, cls.Name.Length);
        _callReceiverTypes[call] = SemanticType.Class(cls.Name, typeArguments);
        var substitutions = cls.DeclaredTypeParameters
            .Zip(typeArguments ?? [], (name, type) => (name, type))
            .ToDictionary(item => item.name, item => item.type, StringComparer.Ordinal);

        var named = cls.StaticMethods.Where(method => method.Name == methodName).ToArray();
        if (named.Length == 0)
        {
            if (cls.StaticFields.Any(field => field.Name == methodName))
                throw Error($"'{methodName}' of '{cls.Name}' is a static field, not a method; a generic class's static field is named by its bare name inside the class", call.Location, methodName.Length);
            if (GetClassMethods(cls.Name, []).Any(method => method.Name == methodName))
                throw Error($"Method '{methodName}' of '{cls.Name}' is an instance method; call it on an object",
                    call.Location, methodName.Length);
            throw ErrorWithCode(DiagnosticCode.NotDeclared,
                $"Class '{cls.Name}' has no static method named '{methodName}'", call.Location, methodName.Length);
        }

        var candidates = named.Where(method => IsMemberAccessible(method.Visibility,
            MethodOwnerName(method), MethodOwnerModule(method), moduleName, cls.Name)).ToArray();
        if (candidates.Length == 0)
            throw Error($"Static method '{methodName}' of '{cls.Name}' cannot be used here", call.Location, methodName.Length);

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
