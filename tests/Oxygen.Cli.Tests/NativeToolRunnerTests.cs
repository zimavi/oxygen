using Oxygen.Llvm;
using Xunit;

namespace Oxygen.Cli.Tests;

// Runs native tools through ProcessNativeToolRunner: output that fills a pipe, a tool that runs past its timeout, and a cancelled build.
public sealed class NativeToolRunnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    // The compiler finds the standard library under OXYGEN_PATH. RunProcess sets it for the processes it starts, and the runners
    // started here need the same value.
    public NativeToolRunnerTests()
    {
        Directory.CreateDirectory(_directory);
        Environment.SetEnvironmentVariable("OXYGEN_PATH", NativeProgram.RepositoryRoot);
    }

    // A program with thousands of unused variables. The compiler reports a warning for each, so its error output is large and it runs for a while.
    private string WriteLargeSource()
    {
        var source = Path.Combine(_directory, "main.oxy");
        var lines = Enumerable.Range(0, 3000).Select(index => $"    i32 value{index} = 0;");
        File.WriteAllText(source, "i32 main() {\n" + string.Join("\n", lines) + "\n    return 0;\n}\n");
        return source;
    }

    private string[] CompileArguments(string source) =>
        ["build", source, "-o", Path.Combine(_directory, "program.exe"), "--emit-ir", Path.Combine(_directory, "program.ll")];

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void ALargeErrorOutputDoesNotBlockTheTool()
    {
        NativeProgram.RequireNativeToolchain();
        // Thousands of unused variables give warnings that together fill the standard error pipe several times over. Reading
        // standard error only after standard output had ended would leave the compiler blocked on a full pipe.
        var runner = new ProcessNativeToolRunner(timeout: TimeSpan.FromMinutes(2));
        runner.Run(NativeProgram.CompilerExecutable, CompileArguments(WriteLargeSource()), _directory);
    }

    [SkippableFact]
    public void ATimeoutStopsTheToolAndReportsIt()
    {
        NativeProgram.RequireNativeToolchain();
        // The compiler takes well over 100 milliseconds on the large source, so the tool is still running when the timeout ends it.
        var runner = new ProcessNativeToolRunner(timeout: TimeSpan.FromMilliseconds(100));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            runner.Run(NativeProgram.CompilerExecutable, CompileArguments(WriteLargeSource()), _directory));

        Assert.Contains("did not finish within", exception.Message);
    }

    [SkippableFact]
    public void ACancelledRunStopsTheToolAndThrowsCancellation()
    {
        NativeProgram.RequireNativeToolchain();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new ProcessNativeToolRunner(cancellation: cancellation.Token);

        Assert.Throws<OperationCanceledException>(() =>
            runner.Run(NativeProgram.CompilerExecutable, ["--help"], _directory));
    }
}
