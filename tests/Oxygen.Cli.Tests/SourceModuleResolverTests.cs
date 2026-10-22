using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;
using Xunit;

namespace Oxygen.Cli.Tests;

public sealed class SourceModuleResolverTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public SourceModuleResolverTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void AProgramWithoutAStandardLibraryIsAToolchainError()
    {
        // The temporary directory has no stdlib above it, and no OXYGEN_PATH is given.
        var source = new SourceText(Path.Combine(_directory, "main.oxy"), "i32 main() { return 0; }");

        var resolved = Resolve([source], oxygenPath: null);

        var diagnostic = Assert.Single(resolved.Diagnostics);
        Assert.Equal(DiagnosticPhase.Toolchain, diagnostic.Phase);
        Assert.Contains("standard library", diagnostic.Message);
    }

    // A native library that provides the program's std modules supplies them, so no standard library folder is needed for them.
    [Fact]
    public void AProgramWhoseStdImportsAreAllProvidedByALibraryNeedsNoStandardLibrary()
    {
        var source = new SourceText(Path.Combine(_directory, "main.oxy"), "import std.io;\ni32 main() { return 0; }");

        var resolved = new SourceModuleResolver(new Tokenizer(Log.Logger), Log.Logger)
            .Resolve([source], oxygenPath: null, loadStandardLibrary: true, providedModules: new HashSet<string>(["std.io", "std.exception"]));

        Assert.Empty(resolved.Diagnostics);
        Assert.Equal(["main.oxy"], StandardFileNames(resolved));
    }

    // A module that the library provides is not loaded from the standard library's source, and the modules it does not provide still are.
    [Fact]
    public void AModuleThatALibraryProvidesIsNotLoadedFromSource()
    {
        var source = new SourceText(Path.Combine(_directory, "main.oxy"), "import std.io;\nimport std.memory;\ni32 main() { return 0; }");

        var resolved = new SourceModuleResolver(new Tokenizer(Log.Logger), Log.Logger)
            .Resolve([source], NativeProgram.RepositoryRoot, loadStandardLibrary: true, providedModules: new HashSet<string>(["std.io"]));

        Assert.Empty(resolved.Diagnostics);
        var names = StandardFileNames(resolved);
        Assert.DoesNotContain("io.oxy", names);
        Assert.Contains("memory.oxy", names);
        Assert.Contains("exception.oxy", names);
    }

    // A module that a native library provides is not compiled from a file of that name next to the program.
    [Fact]
    public void AProvidedModuleIsNotLoadedFromAFileNextToTheProgram()
    {
        File.WriteAllText(Path.Combine(_directory, "util.oxy"), "module util; public i32 helper() { return 1; }");
        var source = new SourceText(Path.Combine(_directory, "main.oxy"), "import util;\ni32 main() { return 0; }");

        var resolved = new SourceModuleResolver(new Tokenizer(Log.Logger), Log.Logger)
            .Resolve([source], oxygenPath: null, loadStandardLibrary: true, providedModules: new HashSet<string>(["util", "std.exception"]));

        Assert.Empty(resolved.Diagnostics);
        Assert.Equal(["main.oxy"], StandardFileNames(resolved));
    }

    [Fact]
    public void OxygenPathLocatesTheStandardLibraryThatHoldsStdException()
    {
        var source = new SourceText(Path.Combine(_directory, "main.oxy"), "i32 main() { return 0; }");

        var resolved = Resolve([source], oxygenPath: NativeProgram.RepositoryRoot);

        Assert.Empty(resolved.Diagnostics);
        Assert.Contains(resolved.Sources, text => Path.GetFileName(text.Name) == "exception.oxy");
    }

    [Fact]
    public void AProgramWithoutImportsLoadsOnlyStdExceptionAndTheStringsItImports()
    {
        var source = new SourceText(Path.Combine(_directory, "main.oxy"), "i32 main() { return 0; }");

        var resolved = Resolve([source], NativeProgram.RepositoryRoot);

        Assert.Empty(resolved.Diagnostics);
        Assert.Equal(["exception.oxy", "main.oxy", "strings.oxy"], StandardFileNames(resolved).Order());
    }

    [Fact]
    public void ImportingAModuleLoadsItsImportsAndNothingUnrelated()
    {
        var source = new SourceText(Path.Combine(_directory, "main.oxy"),
            "import std.io;\ni32 main() { return 0; }");

        var resolved = Resolve([source], NativeProgram.RepositoryRoot);

        Assert.Empty(resolved.Diagnostics);
        var names = StandardFileNames(resolved);
        Assert.Contains("io.oxy", names);
        Assert.Contains("memory.oxy", names);
        Assert.Contains("strings.oxy", names);
        Assert.DoesNotContain("collection.oxy", names);
    }

    [Fact]
    public void AFileInTheProgramFolderThatTheProgramDoesNotNeedIsNotCompiled()
    {
        // The broken file declares a module that nothing imports, so its syntax errors are not part of the program.
        File.WriteAllText(Path.Combine(_directory, "unrelated.oxy"), "module unrelated; i32 broken( {");
        var source = new SourceText(Path.Combine(_directory, "main.oxy"), "i32 main() { return 0; }");

        var resolved = Resolve([source], NativeProgram.RepositoryRoot);

        Assert.Empty(resolved.Diagnostics);
        Assert.DoesNotContain(resolved.Sources, text => Path.GetFileName(text.Name) == "unrelated.oxy");
    }

    [Fact]
    public void AnImportedModuleIsFoundByTheModuleThatAFileDeclaresNotByItsFileName()
    {
        File.WriteAllText(Path.Combine(_directory, "helpers.oxy"),
            "module util;\npublic i32 one() { return 1; }\n");
        var source = new SourceText(Path.Combine(_directory, "main.oxy"),
            "import util;\ni32 main() { return one(); }");

        var resolved = Resolve([source], NativeProgram.RepositoryRoot);

        Assert.Empty(resolved.Diagnostics);
        Assert.Contains(resolved.Sources, text => Path.GetFileName(text.Name) == "helpers.oxy");
    }

    [Fact]
    public void EveryFileOfTheProgramsOwnModuleIsCompiled()
    {
        // A file without a module declaration belongs to the program's own module, as main.oxy does.
        File.WriteAllText(Path.Combine(_directory, "more.oxy"), "i32 helper() { return 2; }\n");
        var source = new SourceText(Path.Combine(_directory, "main.oxy"), "i32 main() { return 0; }");

        var resolved = Resolve([source], NativeProgram.RepositoryRoot);

        Assert.Empty(resolved.Diagnostics);
        Assert.Contains(resolved.Sources, text => Path.GetFileName(text.Name) == "more.oxy");
    }

    // The file names of every resolved source, so the tests can tell which standard modules were loaded.
    private static List<string> StandardFileNames(ResolvedSourceModules resolved) =>
        resolved.Sources.Select(source => Path.GetFileName(source.Name)).ToList();

    private static ResolvedSourceModules Resolve(IReadOnlyList<SourceText> roots, string? oxygenPath) =>
        new SourceModuleResolver(new Tokenizer(Log.Logger), Log.Logger).Resolve(roots, oxygenPath, loadStandardLibrary: true);
}
