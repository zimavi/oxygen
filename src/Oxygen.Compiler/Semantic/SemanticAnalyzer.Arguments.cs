using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

// Binding of call arguments to parameters. An argument is positional, or named with `name: value`. Positional arguments come
// first and fill the parameters in order. Named arguments fill the parameters they name, in any order. A parameter with a
// default value may be left out, and then its default is used. The binding of each call is recorded, so the backend emits the
// arguments in parameter order.
public sealed partial class SemanticAnalyzer
{
    // The binding of a parameter that no argument fills: its default is used.
    private const int DefaultArgument = -1;

    // The binding of a parameter while the arguments are being bound. It never remains in a finished binding.
    private const int UnboundArgument = -2;

    // The binding of each call, keyed by the call's node: for each parameter, the index of the argument that fills it, or
    // DefaultArgument. A call without named arguments is bound in position, so its binding is the identity.
    private readonly Dictionary<SyntaxNode, int[]> _argumentOrders = new(ReferenceEqualityComparer.Instance);

    // The value of an argument: the value of a named argument, or the argument itself.
    private static Expression ValueOf(Expression argument) =>
        argument is NamedArgumentExpression named ? named.Value : argument;

    private static bool HasNamedArguments(IReadOnlyList<Expression> arguments) =>
        arguments.Any(argument => argument is NamedArgumentExpression);

    // Binds the arguments of a call of `functionName` to the parameters. Returns the binding, or null with the reason in
    // `problem`. The reason is written for a call with named arguments; a positional call keeps the messages of its callers.
    private static int[]? BindArguments(
        IReadOnlyList<ParameterSymbol> parameters,
        IReadOnlyList<Expression> arguments,
        string functionName,
        out string? problem)
    {
        problem = null;
        var order = new int[parameters.Count];
        Array.Fill(order, UnboundArgument);
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] is NamedArgumentExpression named)
            {
                var position = -1;
                for (var candidate = 0; candidate < parameters.Count && position < 0; candidate++)
                {
                    if (parameters[candidate].Name == named.Name)
                        position = candidate;
                }

                if (position < 0)
                {
                    problem = $"Function '{functionName}' has no parameter named '{named.Name}'";
                    return null;
                }

                if (order[position] != UnboundArgument)
                {
                    problem = $"Parameter '{named.Name}' of '{functionName}' is given more than once";
                    return null;
                }

                order[position] = index;
                continue;
            }

            if (arguments.Take(index).Any(argument => argument is NamedArgumentExpression))
            {
                problem = "A positional argument cannot follow a named argument";
                return null;
            }

            if (index >= parameters.Count)
            {
                problem = CountProblem(functionName, parameters, arguments.Count);
                return null;
            }

            order[index] = index;
        }

        for (var position = 0; position < parameters.Count; position++)
        {
            if (order[position] != UnboundArgument)
                continue;
            if (parameters[position].Declaration.DefaultValue is null)
            {
                problem = HasNamedArguments(arguments)
                    ? $"Parameter '{parameters[position].Name}' of '{functionName}' needs an argument"
                    : CountProblem(functionName, parameters, arguments.Count);
                return null;
            }

            order[position] = DefaultArgument;
        }

        return order;
    }

    // The error for a method call that no overload accepts. With named arguments the reason is the first overload's binding
    // problem, since that is what the caller wrote wrong.
    private static SemanticException NoMatchingMethod(
        CallExpression call,
        string methodName,
        IEnumerable<IReadOnlyList<ParameterSymbol>> overloads)
    {
        if (HasNamedArguments(call.Arguments))
        {
            foreach (var parameters in overloads)
            {
                BindArguments(parameters, call.Arguments, methodName, out var problem);
                if (problem is not null)
                    return ErrorWithCode(DiagnosticCode.ArgumentCount, problem, call.Location, methodName.Length);
            }
        }

        return Error($"No overload of '{methodName}' matches the argument types", call.Location);
    }

    private static string CountProblem(string functionName, IReadOnlyList<ParameterSymbol> parameters, int count) =>
        $"Function '{functionName}' expects {ParameterCounts.Describe(parameters)} argument(s), got {count}";
}
