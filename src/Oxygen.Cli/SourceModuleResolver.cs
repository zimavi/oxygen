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
    // Paths are compared the way the filesystem compares them: without regard to case on Windows and macOS, and exactly elsewhere.
    private static readonly bool CaseInsensitivePaths = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    private static readonly StringComparer PathComparer = CaseInsensitivePaths
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = CaseInsensitivePaths
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private const string ExceptionModuleName = "std.exception";

    public ResolvedSourceModules Resolve(
        IReadOnlyList<SourceText> roots,
        bool loadStandardLibrary = true,
        IReadOnlySet<string>? providedModules = null) =>
        Resolve(roots, Environment.GetEnvironmentVariable("OXYGEN_PATH"), loadStandardLibrary, providedModules);

    // The compiled files are the given roots, the files that declare a module the program needs, and the standard library files
    // that imports reach. A module is needed when a compiled file imports it, or when a compiled file declares it. Other files in
    // the program's folders are not compiled, so a file that is unrelated to the program cannot stop the build.
    //
    // Every program needs the standard library: std.exception supplies the exception that null-reference checks throw.
    // A missing standard library is therefore reported here rather than surfacing later in the backend.
    // With loadStandardLibrary false (--no-std), the standard library is not loaded at all: std.exception is not added, a standard library
    // is not looked for, and an import of a std module must be one of the sources of the build.
    // The modules in providedModules come from a native library (--stdlib), so their source is not loaded: an import of one of them is
    // satisfied by the library, and std.exception is not added when the library provides it.
    internal ResolvedSourceModules Resolve(
        IReadOnlyList<SourceText> roots,
        string? oxygenPath,
        bool loadStandardLibrary,
        IReadOnlySet<string>? providedModules = null)
    {
        var provided = providedModules ?? new HashSet<string>(StringComparer.Ordinal);
        var loadExceptionModule = loadStandardLibrary && !provided.Contains(ExceptionModuleName);
        var sources = new List<SourceText>();
        var units = new List<CompilationUnit>();
        var diagnostics = new DiagnosticBag();
        var rootDirectories = roots
            .Select(source => Path.GetDirectoryName(Path.GetFullPath(source.Name)))
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => directory!)
            .Distinct(PathComparer)
            .ToArray();

        var standardLibraries = loadStandardLibrary ? FindStandardLibraries(rootDirectories, oxygenPath) : [];
        // A build that loads the standard library needs its exception module, and says so when it cannot find it.
        if (loadExceptionModule && standardLibraries.Count == 0)
        {
            diagnostics.Add(
                DiagnosticPhase.Toolchain,
                DiagnosticSeverity.Error,
                "The standard library was not found, so std.exception is unavailable. Set OXYGEN_PATH to the folder that contains stdlib, " +
                "or pass --no-std to build without the standard library.",
                new SourceLocation(1, 1),
                sourceName: roots.FirstOrDefault()?.Name);
        }
        else if (loadExceptionModule && !standardLibraries.Any(directory => File.Exists(Path.Combine(directory, "exception.oxy"))))
        {
            diagnostics.Add(
                DiagnosticPhase.Toolchain,
                DiagnosticSeverity.Error,
                $"The standard library in '{standardLibraries[0]}' has no exception.oxy, so std.exception is unavailable.",
                new SourceLocation(1, 1),
                sourceName: roots.FirstOrDefault()?.Name);
        }

        var programModules = IndexProgramModules(rootDirectories, roots, standardLibraries, diagnostics);
        var queued = new Queue<SourceText>();
        var needed = new HashSet<string>(StringComparer.Ordinal);
        // Queues the program's files that declare a module, the first time the module is needed.
        void NeedModule(string moduleName)
        {
            if (!needed.Add(moduleName) || !programModules.TryGetValue(moduleName, out var files))
                return;
            foreach (var file in files)
                queued.Enqueue(file);
        }

        foreach (var root in roots)
        {
            queued.Enqueue(root);
            NeedModule(ModuleNameOf(root));
        }

        // std.exception is always loaded, because null-reference checks throw its NullReferenceException. Other standard
        // modules load when a loaded module imports them. Without the standard library (--no-std) neither loads.
        if (loadExceptionModule)
            EnqueueStandardModule(queued, diagnostics, standardLibraries, "exception.oxy");

        var seen = new HashSet<string>(PathComparer);
        while (queued.Count > 0)
        {
            var source = queued.Dequeue();
            if (!seen.Add(GetCanonicalPath(source.Name)))
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
            NeedModule(parsed.Unit.ModuleName ?? "program");
            foreach (var import in parsed.Unit.Imports)
            {
                // A module that a native library provides is not loaded from source, whatever its name, so a file of that name next to the
                // program is not compiled. An import of std.a.b is the module file a/b.oxy in a standard library folder.
                if (provided.Contains(import.Name))
                {
                    // The native library supplies the module, so there is no source to load.
                }
                else if (!import.Name.StartsWith("std.", StringComparison.Ordinal))
                    NeedModule(import.Name);
                else if (loadStandardLibrary)
                    EnqueueStandardModule(queued, diagnostics, standardLibraries,
                        import.Name["std.".Length..].Replace('.', Path.DirectorySeparatorChar) + ".oxy");
                else
                {
                    // Without the standard library, a std module can only come from the build's own sources.
                    NeedModule(import.Name);
                    if (!programModules.ContainsKey(import.Name))
                        diagnostics.Add(DiagnosticPhase.Toolchain,
                            $"Module '{import.Name}' is not part of this build. --no-std does not load the standard library, so build " +
                            "the module with the other sources.",
                            import.Location, import.Name.Length, source.Name);
                }
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

    // The program's own .oxy files, by the module each one declares. The roots come first, then the other files of the program's
    // folders in name order. A file's module is read from its header only, so a file with errors elsewhere is still indexed. A
    // file that cannot be read is a diagnostic, since it might be one that the program needs.
    private Dictionary<string, List<SourceText>> IndexProgramModules(
        IReadOnlyList<string> rootDirectories,
        IReadOnlyList<SourceText> roots,
        IReadOnlyList<string> standardLibraries,
        DiagnosticBag diagnostics)
    {
        var index = new Dictionary<string, List<SourceText>>(StringComparer.Ordinal);
        var indexed = new HashSet<string>(PathComparer);
        void Add(SourceText source)
        {
            if (!indexed.Add(GetCanonicalPath(source.Name)))
                return;
            var moduleName = ModuleNameOf(source);
            if (!index.TryGetValue(moduleName, out var files))
                index[moduleName] = files = [];
            files.Add(source);
        }

        foreach (var root in roots)
            Add(root);

        foreach (var sourceDirectory in rootDirectories)
        {
            try
            {
                foreach (var path in SourceFilesUnder(sourceDirectory, diagnostics).OrderBy(path => path, PathComparer))
                {
                    if (standardLibraries.Any(library => IsInside(path, library)))
                        continue;

                    try
                    {
                        Add(new SourceText(path, File.ReadAllText(path)));
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

        return index;
    }

    // The .oxy files under a folder, including its subfolders. A subfolder that cannot be read is skipped with a warning: the program may
    // not need its modules, and when it does, the missing module is reported at the import. The folder itself is not skipped; its error
    // reaches the caller, which reports it.
    private static List<string> SourceFilesUnder(string directory, DiagnosticBag diagnostics)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                files.AddRange(Directory.GetFiles(current, "*.oxy"));
                foreach (var child in Directory.GetDirectories(current))
                    pending.Push(child);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (current == directory)
                    throw;
                diagnostics.Add(
                    DiagnosticPhase.Toolchain,
                    DiagnosticSeverity.Warning,
                    $"Skipped the source folder '{current}', which cannot be read: {exception.Message}",
                    new SourceLocation(1, 1),
                    sourceName: current);
            }
        }
        return files;
    }

    // The module a file declares, read from the start of the file: an optional visibility, then `module` and the dotted name.
    // A file without a module declaration belongs to the program's own module, as the parser decides it.
    private string ModuleNameOf(SourceText source)
    {
        var tokens = tokenizer.TokenizeWithDiagnostics(source.Text, source.Name).Tokens;
        var index = 0;
        if (index < tokens.Count && tokens[index].Kind is TokenKind.Public or TokenKind.Private)
            index++;
        if (index >= tokens.Count || tokens[index].Kind != TokenKind.Module)
            return "program";

        var parts = new List<string>();
        for (index++; index < tokens.Count && tokens[index].Kind is TokenKind.Identifier or TokenKind.Dot; index++)
        {
            if (tokens[index].Kind == TokenKind.Identifier)
                parts.Add(tokens[index].Lexeme);
        }
        return parts.Count == 0 ? "program" : string.Join(".", parts);
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
            PathComparison);

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
                    if (!standardLibraries.Contains(fullPath, PathComparer))
                        standardLibraries.Add(fullPath);
                    break;
                }

                directory = directory.Parent;
            }
        }

        if (standardLibraries.Count == 0 && !string.IsNullOrWhiteSpace(oxygenPath))
        {
            var oxygenDirectory = new DirectoryInfo(Path.GetFullPath(oxygenPath));
            var standardLibraryDirectory = oxygenDirectory.Name.Equals("stdlib", PathComparison)
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
