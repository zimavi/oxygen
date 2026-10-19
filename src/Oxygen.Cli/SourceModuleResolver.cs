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
    public ResolvedSourceModules Resolve(IReadOnlyList<SourceText> roots) =>
        Resolve(roots, Environment.GetEnvironmentVariable("OXYGEN_PATH"));

    // Every program needs the standard library: std.exception supplies the exception that null-reference checks throw.
    // A missing standard library is therefore reported here rather than surfacing later in the backend.
    internal ResolvedSourceModules Resolve(IReadOnlyList<SourceText> roots, string? oxygenPath)
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

        var standardLibraries = FindStandardLibraries(rootDirectories, oxygenPath);
        if (standardLibraries.Count == 0)
        {
            diagnostics.Add(
                DiagnosticPhase.Toolchain,
                DiagnosticSeverity.Error,
                "The standard library was not found, so std.exception is unavailable. Set OXYGEN_PATH to the folder that contains stdlib.",
                new SourceLocation(1, 1),
                sourceName: roots.FirstOrDefault()?.Name);
        }
        else if (!standardLibraries.Any(directory => File.Exists(Path.Combine(directory, "exception.oxy"))))
        {
            diagnostics.Add(
                DiagnosticPhase.Toolchain,
                DiagnosticSeverity.Error,
                $"The standard library in '{standardLibraries[0]}' has no exception.oxy, so std.exception is unavailable.",
                new SourceLocation(1, 1),
                sourceName: roots.FirstOrDefault()?.Name);
        }

        // The program's own folders are compiled as a whole. The standard library is not: its modules load on demand, below.
        foreach (var sourceDirectory in rootDirectories)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*.oxy", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (visitedFiles.Contains(GetCanonicalPath(path)) || standardLibraries.Any(library => IsInside(path, library)))
                        continue;

                    try
                    {
                        queued.Enqueue(new SourceText(path, File.ReadAllText(path)));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        diagnostics.Add(
                            DiagnosticPhase.Toolchain,
                            DiagnosticSeverity.Error,
                            $"Unable to read source file '{path}': {exception.Message}",
                            new SourceLocation(1, 1),
                            sourceName: path);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(
                    DiagnosticPhase.Toolchain,
                    DiagnosticSeverity.Error,
                    $"Unable to enumerate source directory '{sourceDirectory}': {exception.Message}",
                    new SourceLocation(1, 1),
                    sourceName: sourceDirectory);
            }
        }

        // std.exception is always loaded, because null-reference checks throw its NullReferenceException. Other standard
        // modules load when a loaded module imports them.
        EnqueueStandardModule(queued, diagnostics, standardLibraries, "exception.oxy");

        while (queued.Count > 0)
        {
            var source = queued.Dequeue();
            if (!visitedFiles.Add(GetCanonicalPath(source.Name)))
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
            foreach (var import in parsed.Unit.Imports)
            {
                // An import of std.a.b is the module file a/b.oxy in a standard library folder.
                if (import.Name.StartsWith("std.", StringComparison.Ordinal))
                    EnqueueStandardModule(queued, diagnostics, standardLibraries,
                        import.Name["std.".Length..].Replace('.', Path.DirectorySeparatorChar) + ".oxy");
            }
        }

        foreach (var pair in sources.Zip(units))
        {
            var aliases = new HashSet<string>(StringComparer.Ordinal);
            foreach (var import in pair.Second.Imports)
            {
                if (import.Alias is not null && !aliases.Add(import.Alias))
                {
                    diagnostics.Add(
                        DiagnosticPhase.Semantic,
                        $"Import alias '{import.Alias}' is declared more than once",
                        import.Location,
                        import.Alias.Length,
                        pair.First.Name); 
                }
            }
        }

        return new ResolvedSourceModules(sources, units, diagnostics.Diagnostics);
    }

    // Queues the first standard library file with this relative path, if a standard library folder has one.
    private static void EnqueueStandardModule(
        Queue<SourceText> queued,
        DiagnosticBag diagnostics,
        IReadOnlyList<string> standardLibraries,
        string relativePath)
    {
        var path = standardLibraries
            .Select(library => Path.Combine(library, relativePath))
            .FirstOrDefault(File.Exists);
        if (path is null)
            return;

        try
        {
            queued.Enqueue(new SourceText(path, File.ReadAllText(path)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(
                DiagnosticPhase.Toolchain,
                DiagnosticSeverity.Error,
                $"Unable to read source file '{path}': {exception.Message}",
                new SourceLocation(1, 1),
                sourceName: path);
        }
    }

    private static bool IsInside(string path, string directory) =>
        Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    // The stdlib folder found above each source root. OXYGEN_PATH names one only when no root has one above it.
    private static List<string> FindStandardLibraries(IReadOnlyList<string> rootDirectories, string? oxygenPath)
    {
        var standardLibraries = new List<string>();

        foreach (var rootDirectory in rootDirectories)
        {
            var directory = new DirectoryInfo(rootDirectory);
            while (directory is not null)
            {
                var standardLibraryDirectory = Path.Combine(directory.FullName, "stdlib");
                if (Directory.Exists(standardLibraryDirectory))
                {
                    var fullPath = Path.GetFullPath(standardLibraryDirectory);
                    if (!standardLibraries.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                        standardLibraries.Add(fullPath);
                    break;
                }

                directory = directory.Parent;
            }
        }

        if (standardLibraries.Count == 0 && !string.IsNullOrWhiteSpace(oxygenPath))
        {
            var oxygenDirectory = new DirectoryInfo(Path.GetFullPath(oxygenPath));
            var standardLibraryDirectory = oxygenDirectory.Name.Equals("stdlib", StringComparison.OrdinalIgnoreCase)
                ? oxygenDirectory.FullName
                : Path.Combine(oxygenDirectory.FullName, "stdlib");
            if (Directory.Exists(standardLibraryDirectory))
                standardLibraries.Add(Path.GetFullPath(standardLibraryDirectory));
        }

        return standardLibraries;
    }

    // Returns the path with symbolic links and junctions resolved in every component, so the same
    // file reached through different directories (for example, a linked stdlib inside a source
    // directory) is compiled only once.
    private static string GetCanonicalPath(string path)
    {
        const int maxLinkExpansions = 40;
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var pending = new Stack<string>(fullPath[root.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Reverse());
        var current = root;
        var expansions = 0;

        while (pending.Count > 0)
        {
            current = Path.Combine(current, pending.Pop());
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is null)
                continue;

            if (++expansions > maxLinkExpansions)
                return fullPath;

            FileSystemInfo? target;
            try
            {
                target = entry.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (IOException)
            {
                return fullPath;
            }

            if (target is null)
                return fullPath;

            // The link target can itself contain linked components, so resolve it from its root again.
            var targetPath = Path.GetFullPath(target.FullName);
            var targetRoot = Path.GetPathRoot(targetPath) ?? string.Empty;
            foreach (var component in targetPath[targetRoot.Length..]
                         .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                         .Reverse())
                pending.Push(component);
            current = targetRoot;
        }

        return Path.TrimEndingDirectorySeparator(current);
    }
}
