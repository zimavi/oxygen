using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

// Default parameter values. A parameter may name a constant that a call uses when it omits that argument. Defaults come
// after the required parameters, each is a constant of the parameter's type, and an extern function or a parameter whose
// type uses a generic parameter cannot have one.
public sealed partial class SemanticAnalyzer
{
    // Two overloads of one name conflict when a call could match both: some argument count is accepted by both, and their
    // first parameters of that count have the same types. Without defaults this is "the same parameter types", and a
    // return type never takes part.
    private static bool OverloadsConflict(IReadOnlyList<ParameterSymbol> left, IReadOnlyList<ParameterSymbol> right)
    {
        var low = Math.Max(ParameterCounts.Required(left), ParameterCounts.Required(right));
        var high = Math.Min(left.Count, right.Count);
        for (var count = low; count <= high; count++)
        {
            if (left.Take(count).Select(parameter => parameter.Type)
                .SequenceEqual(right.Take(count).Select(parameter => parameter.Type)))
                return true;
        }

        return false;
    }

    // Whether two of these overloads, which share a name, conflict.
    private static bool HasConflictingPair<T>(IReadOnlyList<T> overloads, Func<T, IReadOnlyList<ParameterSymbol>> parametersOf)
    {
        for (var left = 0; left < overloads.Count; left++)
        {
            for (var right = left + 1; right < overloads.Count; right++)
            {
                if (OverloadsConflict(parametersOf(overloads[left]), parametersOf(overloads[right])))
                    return true;
            }
        }

        return false;
    }

    // The error for two overloads of one name that conflict. Without defaults, the overloads take the same parameter types.
    private static string OverloadConflictMessage(string name, IReadOnlyList<ParameterSymbol> left, IReadOnlyList<ParameterSymbol> right) =>
        left.Concat(right).Any(parameter => parameter.Declaration.DefaultValue is not null)
            ? $"A function named '{name}' conflicts with an overload: a call could match both"
            : $"A function named '{name}' with the same parameter types is already declared";

    // Checks the defaults of one function, method or constructor, and analyzes each default so the backend has its type.
    // Errors are reported for every parameter, and a default with an error is not analyzed further.
    private void ValidateParameterDefaults(
        string functionName,
        IReadOnlyList<ParameterSymbol> parameters,
        bool isExternal,
        string moduleName,
        IReadOnlyList<ImportDeclaration> imports,
        IReadOnlyDictionary<string, List<FunctionSymbol>> functions,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        DiagnosticBag? diagnostics,
        string? sourceName)
    {
        var seenDefault = false;
        foreach (var parameter in parameters)
        {
            var declaration = parameter.Declaration;
            if (declaration.DefaultValue is null)
            {
                if (seenDefault)
                    ReportOrThrow(diagnostics,
                        $"Parameter '{parameter.Name}' of '{functionName}' has no default value, but it follows a parameter that has one",
                        declaration.Location, parameter.Name.Length, sourceName, DiagnosticCode.DefaultParameter);
                continue;
            }

            seenDefault = true;
            var value = declaration.DefaultValue;
            if (isExternal)
            {
                ReportOrThrow(diagnostics,
                    $"Extern function '{functionName}' cannot have a default value for '{parameter.Name}', because C has no default arguments",
                    declaration.Location, parameter.Name.Length, sourceName, DiagnosticCode.DefaultParameter);
                continue;
            }

            if (ContainsGenericParameter(parameter.Type))
            {
                ReportOrThrow(diagnostics,
                    $"Parameter '{parameter.Name}' of '{functionName}' has a generic type, so it cannot have a default value",
                    declaration.Location, parameter.Name.Length, sourceName, DiagnosticCode.DefaultParameter);
                continue;
            }

            if (!IsConstantInitializer(value))
            {
                ReportOrThrow(diagnostics,
                    $"The default value of '{parameter.Name}' must be a bool literal, a number, or an integer constant expression",
                    value.Location, 1, sourceName, DiagnosticCode.DefaultParameter);
                continue;
            }

            try
            {
                var actual = AnalyzeExpression(value, GlobalScopeOf(moduleName), functions, moduleName, imports,
                    expressionTypes, resolvedCalls, expectedType: parameter.Type);
                if (!CanAssign(parameter.Type, value, actual))
                    ReportOrThrow(diagnostics,
                        $"The default value of '{parameter.Name}' has type {actual}; expected {parameter.Type}",
                        value.Location, 1, sourceName, DiagnosticCode.DefaultParameter);
            }
            catch (SemanticException exception) when (diagnostics is not null)
            {
                diagnostics.Add(DiagnosticPhase.Semantic, exception.Message, exception.Location, exception.Length,
                    exception.IsSourceAttributed ? exception.SourceName : sourceName, exception.Code);
            }
        }
    }

    // Checks the defaults of every function, method and constructor once all of them are declared.
    private void ValidateDefaultParameters(
        IReadOnlyDictionary<string, List<FunctionSymbol>> functionsByName,
        IReadOnlyDictionary<FunctionSymbol, string?> functionSources,
        IReadOnlyDictionary<FunctionSymbol, (string Name, IReadOnlyList<ImportDeclaration> Imports)> functionModules,
        IDictionary<Expression, SemanticType> expressionTypes,
        IDictionary<CallExpression, FunctionSymbol> resolvedCalls,
        DiagnosticBag? diagnostics)
    {
        foreach (var symbol in functionsByName.Values.SelectMany(overloads => overloads).ToList())
        {
            var module = functionModules[symbol];
            ValidateParameterDefaults(symbol.Name, symbol.Parameters, symbol.IsExternal, module.Name, module.Imports,
                functionsByName, expressionTypes, resolvedCalls, diagnostics, functionSources.GetValueOrDefault(symbol));
        }

        foreach (var (className, context) in _classContexts)
        {
            if (!_classesByName.TryGetValue(className, out var cls))
                continue;
            foreach (var constructor in cls.Constructors)
            {
                ValidateParameterDefaults(cls.Name, constructor.Parameters, false, context.ModuleName, context.Imports,
                    functionsByName, expressionTypes, resolvedCalls, diagnostics, context.SourceName);
            }
        }
    }
}
