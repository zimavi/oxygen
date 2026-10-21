using System.Linq;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Parsing;
using Serilog;
using Xunit;

namespace Oxygen.Cli.Tests;

// The generic entries that a build puts into the metadata of the module it builds.
public sealed class BuildCommandGenericsTests
{
    [Fact]
    public void TheGenericDeclarationsOfTheBuiltModuleAreEntriesNamedByTheirFiles()
    {
        var units = new[]
        {
            Parse("module program; struct Box<T> { T item; } i32 main() { return 0; }"),
            Parse("module other; struct Pair<T> { T first; }")
        };
        var sources = new[]
        {
            new SourceText(Path.Combine("src", "main.oxy"), "module program; ..."),
            new SourceText(Path.Combine("src", "other.oxy"), "module other; ...")
        };

        var entry = Assert.Single(BuildCommand.GenericsOf(units, sources, "program"));

        Assert.Equal("program", entry.ModuleName);
        Assert.Equal("main.oxy", entry.SourceName);
        Assert.Equal(["Box"], SyntaxTreeCodec.Read(entry.Syntax).Unit.Structs.Select(structure => structure.Name));
    }

    [Fact]
    public void AFileWithoutAModuleHeaderBelongsToTheProgramModule()
    {
        var units = new[] { Parse("struct Box<T> { T item; } i32 main() { return 0; }") };
        var sources = new[] { new SourceText("main.oxy", "struct Box<T> { T item; } ...") };

        var entry = Assert.Single(BuildCommand.GenericsOf(units, sources, "program"));

        Assert.Equal("program", entry.ModuleName);
    }

    [Fact]
    public void AModuleWithoutGenericDeclarationsHasNoEntries()
    {
        var units = new[] { Parse("module program; i32 main() { return 0; }") };
        var sources = new[] { new SourceText("main.oxy", "module program; ...") };

        Assert.Empty(BuildCommand.GenericsOf(units, sources, "program"));
    }

    private static CompilationUnit Parse(string source)
    {
        var tokens = new Tokenizer(Log.Logger).Tokenize(source);
        var result = new Parser(tokens, Log.Logger).ParseWithDiagnostics("main.oxy");
        Assert.Empty(result.Diagnostics);
        return result.Unit;
    }
}
