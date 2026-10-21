using System.Linq;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Parsing;
using Serilog;
using Xunit;

namespace Oxygen.Compiler.Tests;

// The generic declarations of a unit, which a library carries in the binary form.
public sealed class GenericSyntaxTests
{
    private const string Source = """
        module shapes;
        import std.memory{alloc};
        enum Mode { Read, Write }
        i32 plain() { return 0; }
        i32 twice<T>(T value) { return 2; }
        struct Box<T> { T item; }
        struct Pair { i32 first; }
        class Holder<T : Named> { T item; }
        interface Named { }
        union Maybe<T> { Some(T); None; }
        """;

    [Fact]
    public void ExtractKeepsTheModuleImportsAndGenericDeclarationsOnly()
    {
        var unit = Parse(Source);

        var generic = GenericSyntax.Extract(unit);

        Assert.Equal("shapes", generic.ModuleName);
        Assert.Equal(["std.memory"], generic.Imports.Select(import => import.Name));
        Assert.Equal(["twice"], generic.Functions.Select(function => function.Name));
        Assert.Equal(["Box"], generic.Structs.Select(structure => structure.Name));
        Assert.Equal(["Holder"], generic.Classes.Select(declaration => declaration.Name));
        Assert.Empty(generic.Interfaces);
        Assert.Equal(["Maybe"], generic.Unions.Select(declaration => declaration.Name));
        Assert.Empty(generic.Enums);
        Assert.Empty(generic.Globals);
        Assert.True(GenericSyntax.IsGenericOnly(generic));
    }

    [Fact]
    public void ANonGenericClassIsLeftOutEvenWhenItHasMethods()
    {
        var unit = Parse("module shapes; class Plain { i32 count = 3; public i32 size() { return count; } }");

        Assert.Empty(GenericSyntax.Extract(unit).Classes);
        Assert.False(GenericSyntax.HasDeclarations(unit));
    }

    [Fact]
    public void AUnitWithANonGenericDeclarationIsNotGenericOnly()
    {
        Assert.False(GenericSyntax.IsGenericOnly(Parse(Source)));
        Assert.False(GenericSyntax.IsGenericOnly(Parse("enum Mode { Read } struct Box<T> { T item; }")));
    }

    private static CompilationUnit Parse(string source)
    {
        var tokens = new Tokenizer(Log.Logger).Tokenize(source);
        var result = new Parser(tokens, Log.Logger).ParseWithDiagnostics("shapes.oxy");
        Assert.Empty(result.Diagnostics);
        return result.Unit;
    }
}
