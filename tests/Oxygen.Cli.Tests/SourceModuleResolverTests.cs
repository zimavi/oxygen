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

    // The file names of every resolved source, so the tests can tell which standard modules were loaded.
    private static List<string> StandardFileNames(ResolvedSourceModules resolved) =>
        resolved.Sources.Select(source => Path.GetFileName(source.Name)).ToList();

    private static ResolvedSourceModules Resolve(IReadOnlyList<SourceText> roots, string? oxygenPath) =>
        new SourceModuleResolver(new Tokenizer(Log.Logger), Log.Logger).Resolve(roots, oxygenPath);
}
