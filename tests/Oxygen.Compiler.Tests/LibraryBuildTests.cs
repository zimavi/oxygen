using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Oxygen.Compiler.Ast;
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
        Assert.Contains("section \".oxmeta\"", File.ReadAllText(Path.Combine(temporary, "library.ll")));
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
