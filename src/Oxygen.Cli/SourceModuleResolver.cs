using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Parsing;
using Serilog;

namespace Oxygen.Cli;

internal sealed record ResolvedSourceModules(
    IReadOnlyList<SourceText> Sources,
    IReadOnlyList<CompilationUnit> Units,
    IReadOnlyList<Diagnostic> Diagnostics);

internal sealed class SourceModuleResolver(Tokenizer tokenizer, ILogger logger)
{
    public ResolvedSourceModules Resolve(IReadOnlyList<SourceText> roots)
    {
        var sources = new List<SourceText>();
        var units = new List<CompilationUnit>();
        var diagnostics = new DiagnosticBag();
        var queued = new Queue<SourceText>(roots);
        var visitedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootDirectories = roots
            .Select(source => Path.GetDirectoryName(Path.GetFullPath(source.Name)))
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => directory!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var rootDirectory in rootDirectories)
        {
            foreach (var path in Directory.EnumerateFiles(rootDirectory, "*.oxy", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!visitedFiles.Contains(Path.GetFullPath(path)))
                    queued.Enqueue(new SourceText(path, File.ReadAllText(path)));
            }
        }

        while (queued.Count > 0)
        {
            var source = queued.Dequeue();
            var fullPath = Path.GetFullPath(source.Name);
            if (!visitedFiles.Add(fullPath))
                continue;

            var tokenization = tokenizer.TokenizeWithDiagnostics(source.Text, source.Name);
            diagnostics.AddRange(tokenization.Diagnostics);
            if (tokenization.Diagnostics.Count > 0)
                continue;

            var parsed = new Parser(tokenization.Tokens, logger).ParseWithDiagnostics(source.Name);
            diagnostics.AddRange(parsed.Diagnostics);
            if (parsed.Diagnostics.Count > 0)
                continue;

            sources.Add(source);
            units.Add(parsed.Unit);

        }

        foreach (var pair in sources.Zip(units))
        {
            foreach (var import in pair.Second.Imports)
            {
                if (!units.Any(unit => string.Equals(unit.ModuleName, import.Name, StringComparison.Ordinal)))
                {
                    diagnostics.Add(
                        DiagnosticPhase.Semantic,
                        $"Imported module '{import.Name}' could not be found",
                        import.Location,
                        import.Name.Length,
                        pair.First.Name);
                }
            }
        }

        return new ResolvedSourceModules(sources, units, diagnostics.Diagnostics);
    }

}
