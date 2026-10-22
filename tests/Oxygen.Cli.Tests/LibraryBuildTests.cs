using System;
using System.Linq;
using System.Text;
using Oxygen.Llvm;
using Xunit;

namespace Oxygen.Cli.Tests;

// The library build: a static archive of a module with no main, whose object carries the module's metadata in the .oxmeta section.
public sealed class LibraryBuildTests : IDisposable
{
    private const string Library = "module shapes; struct Box { i32 x; } i32 add(i32 a, i32 b) { return a + b; }";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public LibraryBuildTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void ALibraryIsAnArchiveWhoseObjectCarriesTheModuleMetadata()
    {
        NativeProgram.RequireLibraryToolchain();
        var source = WriteSource(Library);
        var output = Path.Combine(_directory, "shapes.a");

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable, ["build", source, "--library", "-o", output]);

        Assert.True(run.ExitCode == 0, run.Error + run.Output);
        var payload = Assert.Single(NativeModuleSection.ReadPayloads(output));
        var metadata = NativeModuleMetadataSerializer.Deserialize(Encoding.UTF8.GetString(payload));
        Assert.Equal("shapes", metadata.ModuleName);
        Assert.Equal(["Box"], metadata.Types.Select(type => type.Name));
        Assert.Equal(["add"], metadata.Functions.Select(function => function.SourceName));
    }

    [Fact]
    public void ALibraryIsNotLinkedSoTheRuntimeOptionsAreRejected()
    {
        var source = WriteSource(Library);

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "--library", "--runtime", Path.Combine(_directory, "liboxygenrt.a")]);

        Assert.Equal(ExitCodes.SourceError, run.ExitCode);
        Assert.Contains("--library does not link", run.Error);
    }

    [SkippableFact]
    public void ALibraryOfSeveralModulesCarriesTheMetadataOfEachModule()
    {
        NativeProgram.RequireLibraryToolchain();
        var alpha = Path.Combine(_directory, "alpha.oxy");
        File.WriteAllText(alpha, "module alpha; public i32 one() { return 1; }");
        var beta = Path.Combine(_directory, "beta.oxy");
        File.WriteAllText(beta, "module beta; import alpha; public i32 two() { return alpha.one() + 1; }");
        var output = Path.Combine(_directory, "both.a");

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable, ["build", alpha, beta, "--library", "-o", output]);

        Assert.True(run.ExitCode == 0, run.Error + run.Output);
        // Each module's object carries its own payload, so the archive has one per module.
        var modules = NativeModuleSection.ReadPayloads(output)
            .Select(payload => NativeModuleMetadataSerializer.Deserialize(Encoding.UTF8.GetString(payload)).ModuleName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["alpha", "beta"], modules);
    }

    [Fact]
    public void DynamicNeedsALibrary()
    {
        var source = WriteSource(Library);

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable, ["build", source, "--dynamic"]);

        Assert.Equal(ExitCodes.SourceError, run.ExitCode);
        Assert.Contains("--dynamic builds a shared library, so it needs --library", run.Error);
    }

    // A shared library links the import library of each standard library that it names with --stdlib. A library without one is an error,
    // reported before anything is linked. On Windows the DLL is linked through its import library; elsewhere the shared object is linked
    // directly, so the case does not arise.
    [SkippableFact]
    public void ASharedLibraryNeedsTheImportLibraryOfEachStandardLibraryItNames()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Only a Windows DLL is linked through a separate import library.");
        var source = WriteSource(Library);
        var standardLibrary = Path.Combine(_directory, "std.dll");
        File.WriteAllText(standardLibrary, "not a library");

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "--library", "--dynamic", "--stdlib", standardLibrary]);

        Assert.Equal(ExitCodes.ToolchainFailure, run.ExitCode);
        Assert.Contains("No library to link for", run.Error);
    }

    private string WriteSource(string text)
    {
        var path = Path.Combine(_directory, "shapes.oxy");
        File.WriteAllText(path, text);
        return path;
    }
}
