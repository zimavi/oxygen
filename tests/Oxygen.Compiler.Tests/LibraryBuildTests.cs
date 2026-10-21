using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Parsing;
using Oxygen.Compiler.Semantic;
using Oxygen.Llvm;
using Serilog;
using Xunit;

namespace Oxygen.Compiler.Tests;

// A library is a module with no main. Its code is not linked on its own, so it has no native entry point, and it is archived with
// the metadata in its object file.
public sealed class LibraryBuildTests : IDisposable
{
    private const string Module = "module shapes; struct Box { i32 x; } public i32 add(i32 a, i32 b) { return a + b; }";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygen-library-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void AModuleWithoutMainAnalyzesWhenItIsALibrary()
    {
        var unit = Parse(Module);

        var result = new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics([unit], null, null, requireMain: false);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void AProgramWithoutMainIsStillAnError()
    {
        var unit = Parse(Module);

        var result = new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics([unit]);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("'main' function", StringComparison.Ordinal));
    }

    [Fact]
    public void ALibraryHasNoNativeMain()
    {
        var model = new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics([Parse(Module)], null, null, requireMain: false).Model!;

        using var generated = new LlvmModuleGenerator().Generate("shapes", model, library: true);
        var ir = generated.ToString();

        Assert.DoesNotContain("define i32 @main(", ir);
        Assert.Contains("_Ox_v1_m6shapes_f3add_p2_i32i32", ir);
    }

    [Fact]
    public void ABuiltLibraryArchivesItsObjectWithTheMetadataSection()
    {
        var model = new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics([Parse(Module)], null, null, requireMain: false).Model!;
        using var generated = new LlvmModuleGenerator().Generate("shapes", model, library: true);
        var runner = new RecordingRunner();
        var output = Path.Combine(_directory, "shapes.a");
        var temporary = Path.Combine(_directory, "work");

        new NativeBuildPipeline(runner).BuildLibrary(generated, new NativeBuildOptions
        {
            OutputPath = output,
            TemporaryDirectory = temporary,
            KeepTemporaryFiles = true
        });

        // The object is compiled from the IR, and the archive is made from the object, with the output path after rcs.
        Assert.Equal(["clang", "llvm-ar"], runner.Invocations.Select(invocation => invocation.Executable));
        Assert.Contains("-c", runner.Invocations[0].Arguments);
        Assert.Equal(["rcs", output], runner.Invocations[1].Arguments.Take(2));
        Assert.Contains("section \".oxmeta\"", File.ReadAllText(Path.Combine(temporary, "shapes.ll")));
    }

    [Theory]
    [InlineData("shapes", "shapes.a")]
    [InlineData("shapes.lib", "shapes.lib")]
    public void ALibraryWithoutAnExtensionGetsTheArchiveExtensionOfItsPlatform(string output, string expected) =>
        Assert.Equal(expected, NativeBuildPipeline.LibraryPath(output, "x86_64-unknown-linux-gnu"));

    [Fact]
    public void AWindowsLibraryWithoutAnExtensionGetsLib()
    {
        Assert.Equal("shapes.lib", NativeBuildPipeline.LibraryPath("shapes", "x86_64-pc-windows-msvc"));
    }

    [Fact]
    public void ALibraryDefinesItsOwnFunctionsAndOnlyDeclaresAnotherModulesFunctions()
    {
        const string other = "module other; public i32 helper() { return 1; }";
        const string shapes = "module shapes; import other; public i32 add() { return other.helper() + 1; }";
        var model = Analyze(shapes, other);

        using var library = new LlvmModuleGenerator().Generate("shapes", model, library: true);
        var ir = library.ToString();

        Assert.Contains("define i32 @_Ox_v1_m6shapes_f3add_p0_", ir);
        Assert.Contains("declare i32 @_Ox_v1_m5other_f6helper_p0_", ir);
        Assert.DoesNotContain("define i32 @_Ox_v1_m5other_f6helper_p0_", ir);
    }

    [Fact]
    public void AProgramStillDefinesEveryFunctionItGenerates()
    {
        const string other = "module other; public i32 helper() { return 1; }";
        var model = Analyze("module program; import other; public i32 main() { return other.helper(); }", other);

        using var program = new LlvmModuleGenerator().Generate("program", model);

        Assert.Contains("define i32 @_Ox_v1_m5other_f6helper_p0_", program.ToString());
    }

    [Fact]
    public void AGenericInstanceUsedByALibraryIsLinkOnceOdr()
    {
        const string other = "module other; public class Box<T> { T item; public T get() { return item; } }";
        const string shapes = "module shapes; import other; " +
            "public i32 read() { Box<i32> box = new Box<i32>(); return 0; }";
        var model = Analyze(shapes, other);

        using var library = new LlvmModuleGenerator().Generate("shapes", model, library: true);
        var ir = library.ToString();

        Assert.Contains("linkonce_odr", ir);
    }

    [Fact]
    public void AWindowsSharedLibraryExportsItsDefinitionsAndLinksTheSharedRuntime()
    {
        var model = new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics([Parse(Module)], null, null, requireMain: false).Model!;
        using var generated = new LlvmModuleGenerator(OxygenTarget.WindowsMsvcX64).Generate("shapes", model, library: true);
        var runner = new RecordingRunner();
        var temporary = Path.Combine(_directory, "work");

        new NativeBuildPipeline(runner).BuildLibrary(generated, new NativeBuildOptions
        {
            OutputPath = Path.Combine(_directory, "shapes"),
            Target = OxygenTarget.WindowsMsvcX64.Triple,
            TemporaryDirectory = temporary,
            KeepTemporaryFiles = true,
            Shared = true,
            Dependencies = [new NativeDependency(Path.Combine(_directory, "oxygenrt.lib"), NativeDependencyKind.Static)]
        });

        // The definitions are exported from the DLL, and the link takes the shared runtime's import library.
        Assert.Contains("dllexport", File.ReadAllText(Path.Combine(temporary, "shapes.ll")));
        Assert.Equal(["clang", "clang"], runner.Invocations.Select(invocation => invocation.Executable));
        var link = runner.Invocations[1].Arguments;
        Assert.Contains("-shared", link);
        Assert.Contains(Path.Combine(_directory, "oxygenrt.lib"), link);
        Assert.Equal(Path.Combine(_directory, "shapes.dll"), Path.GetFullPath(link.Skip(link.ToList().IndexOf("-o") + 1).First()));
    }

    [Fact]
    public void ALinuxSharedLibraryIsNamedBySonameAndExportsByDefaultVisibility()
    {
        var model = new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics([Parse(Module)], null, null, requireMain: false).Model!;
        using var generated = new LlvmModuleGenerator(OxygenTarget.LinuxGnuX64).Generate("shapes", model, library: true);
        var runner = new RecordingRunner();

        new NativeBuildPipeline(runner).BuildLibrary(generated, new NativeBuildOptions
        {
            OutputPath = Path.Combine(_directory, "libshapes"),
            Target = OxygenTarget.LinuxGnuX64.Triple,
            TemporaryDirectory = Path.Combine(_directory, "work"),
            KeepTemporaryFiles = true,
            Shared = true
        });

        Assert.DoesNotContain("dllexport", File.ReadAllText(Path.Combine(_directory, "work", "shapes.ll")));
        var link = runner.Invocations[1].Arguments;
        Assert.Contains("-Wl,-soname,libshapes.so", link);
        Assert.Equal(Path.Combine(_directory, "libshapes.so"), Path.GetFullPath(link.Skip(link.ToList().IndexOf("-o") + 1).First()));
    }

    [Theory]
    [InlineData("shapes", "x86_64-pc-windows-msvc", "shapes.dll")]
    [InlineData("shapes", "x86_64-unknown-linux-gnu", "shapes.so")]
    [InlineData("shapes.dll", "x86_64-pc-windows-msvc", "shapes.dll")]
    public void ASharedLibraryIsNamedWithTheSharedExtensionOfItsPlatform(string output, string target, string expected) =>
        Assert.Equal(expected, NativeBuildPipeline.SharedLibraryPath(output, target));

    [Fact]
    public void ALibraryOfTwoModulesHasAnObjectAndAMetadataSectionForEach()
    {
        const string alpha = "module alpha; public i32 one() { return 1; }";
        const string beta = "module beta; import alpha; public i32 two() { return alpha.one() + 1; }";
        var model = Analyze(beta, alpha);
        using var first = new LlvmModuleGenerator().Generate("alpha", model, library: true);
        using var second = new LlvmModuleGenerator().Generate("beta", model, library: true);
        var runner = new RecordingRunner();
        var temporary = Path.Combine(_directory, "work");

        new NativeBuildPipeline(runner).BuildLibrary([first, second], new NativeBuildOptions
        {
            OutputPath = Path.Combine(_directory, "both"),
            Target = OxygenTarget.LinuxGnuX64.Triple,
            TemporaryDirectory = temporary,
            KeepTemporaryFiles = true
        });

        // Each module is compiled to its own object, and the archive takes both objects.
        Assert.Equal(["clang", "clang", "llvm-ar"], runner.Invocations.Select(invocation => invocation.Executable));
        var archive = runner.Invocations[2].Arguments;
        Assert.Equal("rcs", archive[0]);
        Assert.Equal(Path.Combine(_directory, "both.a"), Path.GetFullPath(archive[1]));
        Assert.Equal([Path.Combine(temporary, "alpha.o"), Path.Combine(temporary, "beta.o")], archive.Skip(2));
        // Each object defines its own function and declares the other module's, and each carries its own metadata.
        var alphaIr = File.ReadAllText(Path.Combine(temporary, "alpha.ll"));
        var betaIr = File.ReadAllText(Path.Combine(temporary, "beta.ll"));
        Assert.Contains("define i32 @_Ox_v1_m5alpha_f3one_p0_", alphaIr);
        Assert.DoesNotContain("define i32 @_Ox_v1_m5alpha_f3one_p0_", betaIr);
        Assert.Contains("declare i32 @_Ox_v1_m5alpha_f3one_p0_", betaIr);
        Assert.Contains("define i32 @_Ox_v1_m4beta_f3two_p0_", betaIr);
        Assert.Contains("section \".oxmeta\"", alphaIr);
        Assert.Contains("section \".oxmeta\"", betaIr);
    }

    private static SemanticModel Analyze(string source, string dependency)
    {
        var tokens = new Tokenizer(Log.Logger);
        var units = new[] { new Parser(tokens.Tokenize(dependency), Log.Logger).Parse(), new Parser(tokens.Tokenize(source), Log.Logger).Parse() };
        var result = new SemanticAnalyzer(Log.Logger).AnalyzeWithDiagnostics(units, null, null, requireMain: false);
        Assert.Empty(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return result.Model!;
    }

    private static CompilationUnit Parse(string source)
    {
        var tokens = new Tokenizer(Log.Logger).Tokenize(source);
        return new Parser(tokens, Log.Logger).Parse();
    }

    // Records each tool run and writes nothing, so the build's steps can be checked without a toolchain.
    private sealed class RecordingRunner : INativeToolRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Invocations { get; } = [];

        public void Run(string executable, IReadOnlyList<string> arguments, string? workingDirectory) =>
            Invocations.Add((executable, arguments.ToList()));
    }
}
