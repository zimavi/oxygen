using Xunit;

namespace Oxygen.Cli.Tests;

// Builds runtime/tests/runtime_tests.c against the built runtime archive and runs each of its cases. The cases cover runtime
// functions that Oxygen programs cannot call directly. A case that ends the process is checked by its exit code.
public sealed class RuntimeLibraryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void ConvertingANullCStringGivesAnEmptyStringWithNoData()
    {
        AssertCasePasses("c-string-from-null");
    }

    [SkippableFact]
    public void ConvertingACStringKeepsItsBytesWithoutCopying()
    {
        AssertCasePasses("c-string-from-text");
    }

    [SkippableFact]
    public void ConvertingAStringToACStringGivesANewTerminatedCopy()
    {
        AssertCasePasses("string-to-c-string-copies-and-terminates");
    }

    [SkippableFact]
    public void AStringWithAnEmbeddedZeroHasNoCStringAndGivesNull()
    {
        AssertCasePasses("string-with-embedded-zero-has-no-c-string");
    }

    [SkippableFact]
    public void ArraySizesWithinRangeAreTheirProduct()
    {
        AssertCasePasses("array-size-within-range");
    }

    [SkippableFact]
    public void AnArraySizeThatOverflowsStopsTheProgramWithExitCode101()
    {
        var run = RunCase("array-size-overflow-stops-the-program");

        Assert.Equal(101, run.ExitCode);
        Assert.Contains("is too large", run.Error);
    }

    [SkippableFact]
    public void CallocZeroesEveryByte()
    {
        AssertCasePasses("calloc-zeroes-every-byte");
    }

    [SkippableFact]
    public void CallocOfAnOverflowingSizeStopsTheProgramWithExitCode101()
    {
        var run = RunCase("calloc-overflow-stops-the-program");

        Assert.Equal(101, run.ExitCode);
        Assert.Contains("is too large", run.Error);
    }

    [SkippableFact]
    public void AZeroSizeAllocationIsNonNullAndCanBeFreed()
    {
        AssertCasePasses("zero-size-alloc-is-non-null-and-freeable");
    }

    [SkippableFact]
    public void TheTraceStackGrowsPastItsInitialCapacityAndRestoresDepths()
    {
        AssertCasePasses("trace-stack-grows-past-its-initial-capacity");
    }

    // The runtime keeps no state of its own, so two programs on two threads of one process have separate traces.
    [SkippableFact]
    public void EachThreadSeesOnlyItsOwnTrace()
    {
        AssertCasePasses("each-thread-sees-only-its-own-trace");
    }

    [SkippableFact]
    public void StandardOutputWritesEmbeddedZeroBytes()
    {
        var run = RunCase("write-stdout-writes-embedded-zero-bytes");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("a\0b", run.Output);
    }

    [SkippableFact]
    public void AbortDeliversStandardOutputWrittenBeforeIt()
    {
        var run = RunCase("abort-delivers-buffered-stdout");

        Assert.Equal(101, run.ExitCode);
        Assert.Equal("x", run.Output);
    }

    [SkippableFact]
    public void ANewCountedObjectHasOneReferenceBeforeItsPointer()
    {
        AssertCasePasses("object-new-counts-one-reference-before-the-object");
    }

    [SkippableFact]
    public void ReleasingTheLastReferenceRunsTheDestructorOnce()
    {
        AssertCasePasses("object-release-runs-the-destructor-once-at-zero");
    }

    [SkippableFact]
    public void OperationsOnANullObjectDoNothing()
    {
        AssertCasePasses("null-object-operations-do-nothing");
    }

    [SkippableFact]
    public void ReleasingAStackObjectDoesNothing()
    {
        AssertCasePasses("release-of-a-stack-object-does-nothing");
    }

    [SkippableFact]
    public void ReleasingAnObjectWithoutACountStopsTheProgram()
    {
        var run = RunCase("release-without-a-count-stops-the-program");

        Assert.Equal(101, run.ExitCode);
    }

    [SkippableFact]
    public void AFailureMessageIsSetOnTheExceptionObject()
    {
        AssertCasePasses("failure-message-is-set-on-the-exception-object");
    }

    [SkippableFact]
    public void ACastFailureNamesTheObjectAndItsTarget()
    {
        AssertCasePasses("cast-failure-names-the-object-and-its-target");
    }

    [SkippableFact]
    public void ACopyOfATypeRecordNamesTheSameType()
    {
        AssertCasePasses("a-copy-of-a-type-record-names-the-same-type");
    }

    private void AssertCasePasses(string name)
    {
        var run = RunCase(name);

        Assert.True(run.ExitCode == 0, $"Runtime case '{name}' exited with {run.ExitCode}:\n{run.Error}");
    }

    private (int ExitCode, string Output, string Error) RunCase(string name)
    {
        NativeProgram.RequireNativeToolchain();
        Directory.CreateDirectory(_directory);
        var executable = Path.Combine(_directory, "runtime_tests.exe");
        // The ELF runtime uses the Itanium exception functions, which the C++ runtime provides, and libm. The compiler links them
        // the same way for a program.
        var elfLibraries = OperatingSystem.IsWindows() ? Array.Empty<string>() : new[] { "-lm", "-lstdc++", "-pthread" };
        var build = NativeProgram.RunProcess("clang",
            ["-I", Path.Combine(NativeProgram.RepositoryRoot, "runtime", "include"),
                Path.Combine(NativeProgram.RepositoryRoot, "runtime", "tests", "runtime_tests.c"),
                NativeProgram.RuntimeLibrary, .. elfLibraries, "-o", executable]);
        Assert.True(build.ExitCode == 0, $"Building the runtime tests failed:\n{build.Error}");

        return NativeProgram.RunProcess(executable, [name]);
    }
}
