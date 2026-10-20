using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs that use each form of import list, and checks the output of the native executable.
public sealed class ImportListIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public ImportListIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void ASymbolListBringsItsNamesIntoScopeUnqualified()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;
            import std.memory{alloc, free as release};

            i32 main() {
                i32* values = alloc<i32>(2);
                values[0] = 7;
                io.println(values[0]);
                release(values);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("7\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void AModuleGroupImportsEachModuleUnderItsLeafName()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.{io, memory};

            i32 main() {
                i32* values = memory.alloc<i32>(2);
                values[1] = 8;
                io.println(values[1]);
                memory.free(values);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("8\n", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void AModuleGroupWithAliasesQualifiesEachModuleByItsAlias()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.{io, memory} as {out, mem};

            i32 main() {
                i32* values = mem.alloc<i32>(1);
                values[0] = 9;
                out.println(values[0]);
                mem.free(values);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("9\n", run.Output.Replace("\r\n", "\n"));
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
