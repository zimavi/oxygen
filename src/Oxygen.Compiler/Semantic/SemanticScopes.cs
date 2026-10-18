using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Semantic;

public sealed partial class SemanticAnalyzer
{
    private sealed record VariableSymbol(
        SemanticType Type,
        bool IsConst,
        SyntaxNode Declaration,
        bool IsParameter);

    private sealed class Scope
    {
        private readonly Dictionary<string, VariableSymbol> _variables = new(StringComparer.Ordinal);
        private readonly Scope? _parent;
        private readonly Action<VariableSymbol> _markUsed;
        private readonly SemanticType? _thisType;
        private readonly string? _thisError;

        public Scope(Scope? parent = null, Action<VariableSymbol>? markUsed = null, SemanticType? thisType = null,
            string? thisError = null)
        {
            _parent = parent;
            _markUsed = markUsed ?? (_ => { });
            _thisType = thisType ?? parent?._thisType;
            _thisError = thisError ?? parent?._thisError;
        }

        public void Declare(string name, VariableSymbol symbol) => _variables.Add(name, symbol);

        public bool ContainsInCurrentScope(string name) => _variables.ContainsKey(name);

        public int LookupDepth(string name, SourceLocation location) =>
            _variables.ContainsKey(name) ? 0
            : _parent is not null ? _parent.LookupDepth(name, location) + 1
            : throw Error($"Variable '{name}' is not declared", location, name.Length);

        public VariableSymbol Lookup(string name, SourceLocation location) =>
            _variables.TryGetValue(name, out var symbol)
                ? MarkUsed(symbol)
                : _parent?.Lookup(name, location)
                  ?? throw Error($"Variable '{name}' is not declared", location, name.Length);

        public SemanticType LookupThis(SourceLocation location) =>
            _thisType ?? throw Error(_thisError ?? "'this' is only valid inside a class instance body", location, 4);

        private VariableSymbol MarkUsed(VariableSymbol symbol)
        {
            _markUsed(symbol);
            return symbol;
        }
    }
}
