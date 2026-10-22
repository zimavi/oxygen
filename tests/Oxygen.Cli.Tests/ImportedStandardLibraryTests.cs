using Xunit;

namespace Oxygen.Cli.Tests;

// --stdlib takes the modules of a shared standard library from the library's metadata. A program declares the library's functions and
// instantiates its generic functions and unions, and it does not compile the standard library's source. It then links the library and the
// shared runtime, and runs with both beside it.
public sealed class ImportedStandardLibraryTests : IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();
    private static readonly string TargetDirectory = Path.Combine(NativeProgram.RepositoryRoot, "runtime", "build",
        Windows ? "x86_64-pc-windows-msvc" : "x86_64-unknown-linux-gnu");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public ImportedStandardLibraryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void AProgramDeclaresTheFunctionsOfASharedStandardLibraryAndRunsWithIt()
    {
        var (library, runtime, runtimeFile) = PrepareSharedLibraries();
        // write_stdout is an extern of the library, and print_char and println_char are its functions with bodies.
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source, """
            import std.io;

            i32 main() {
                io.write_stdout("h");
                io.println_char('i');
                return 0;
            }
            """);

        var ir = Path.Combine(_directory, "main.ll");
        var emit = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "--stdlib", library, "--emit-ir", ir]);
        Assert.True(emit.ExitCode == 0, $"Emitting IR failed with exit code {emit.ExitCode}:\n{emit.Error}");
        var text = File.ReadAllText(ir);
        // The library defines the functions, so the program declares them, and its own IR has no definition of them.
        foreach (var name in new[] { "write_stdout", "println_char" })
        {
            Assert.Matches($"declare [^\\n]*{name}", text);
            Assert.DoesNotMatch($"define [^\\n]*{name}", text);
        }

        var run = BuildAndRun(source, library, runtime, runtimeFile);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("hi\n", run.Output.Replace("\r\n", "\n"));
    }

    // The generic functions of memory and io are instantiated in the program, and so is a generic union of collection, whose functions
    // the program uses. Their bodies are checked where they are instantiated, and the library's own check is not repeated.
    [SkippableFact]
    public void AProgramInstantiatesTheGenericFunctionsAndUnionsOfASharedStandardLibrary()
    {
        var (library, runtime, runtimeFile) = PrepareSharedLibraries();
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source, """
            import std.collection;
            import std.io;
            import std.memory;

            i32 main() {
                i32* values = alloc<i32>(2);
                values[0] = 7;
                io.println(values[0]);
                free<i32>(values);
                Option<i32> value = Option.Some(9);
                if (is_some(value)) {
                    return 3;
                }
                return 4;
            }
            """);

        var run = BuildAndRun(source, library, runtime, runtimeFile);

        Assert.Equal(3, run.ExitCode);
        Assert.Equal("7\n", run.Output.Replace("\r\n", "\n"));
    }

    // A class of the library is declared here and defined there, so a program throws and catches the library's RuntimeException.
    [SkippableFact]
    public void AProgramThrowsAndCatchesAnExceptionClassOfASharedStandardLibrary()
    {
        var (library, runtime, runtimeFile) = PrepareSharedLibraries();
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source, """
            import std.exception;

            i32 main() {
                try {
                    throw new RuntimeException("boom");
                } catch (RuntimeException error) {
                    return 6;
                }
                return 0;
            }
            """);

        var run = BuildAndRun(source, library, runtime, runtimeFile);

        Assert.Equal(6, run.ExitCode);
    }

    // A field that a class inherits is read at its position in the library's layout: the message is Exception's, read through RuntimeException.
    [SkippableFact]
    public void AProgramReadsAnInheritedFieldOfAClassOfASharedStandardLibrary()
    {
        var (library, runtime, runtimeFile) = PrepareSharedLibraries();
        var source = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(source, """
            import std.exception;

            i32 main() {
                RuntimeException error = new RuntimeException("abc");
                if (error.message.length == 3) {
                    return 7;
                }
                return 8;
            }
            """);

        var run = BuildAndRun(source, library, runtime, runtimeFile);

        Assert.Equal(7, run.ExitCode);
    }

    // Builds the standard library and returns it with the runtime that a program links and the runtime file that it needs beside it.
    // The test is skipped when the toolchain or the shared runtime is missing.
    private (string Library, string Runtime, string RuntimeFile) PrepareSharedLibraries()
    {
        NativeProgram.RequireLibraryToolchain();
        // The program links the runtime's import library on Windows, and its DLL is copied beside the program to run.
        var runtime = Path.Combine(TargetDirectory, Windows ? "oxygenrt.lib" : "liboxygenrt.so");
        var runtimeFile = Path.Combine(TargetDirectory, Windows ? "oxygenrt.dll" : "liboxygenrt.so");
        Skip.IfNot(File.Exists(runtime) && File.Exists(runtimeFile),
            "The shared runtime is not built. Run runtime/build.ps1 or runtime/build.sh to build it.");
        return (BuildStandardLibrary(), runtime, runtimeFile);
    }

    // Builds the program against the library and the runtime, then runs it. The library is built beside the program, and the runtime is
    // copied there too. The program finds both: on Windows through the DLL search, and on Linux through the library path, which is given to
    // the child process only.
    private (int ExitCode, string Output, string Error) BuildAndRun(string source, string library, string runtime, string runtimeFile)
    {
        var executable = Path.Combine(_directory, Windows ? "main.exe" : "main");
        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", source, "--stdlib", library, "-r", runtime, "-o", executable]);
        Assert.True(build.ExitCode == 0, $"Build failed with exit code {build.ExitCode}:\n{build.Error}");

        File.Copy(runtimeFile, Path.Combine(_directory, Path.GetFileName(runtimeFile)), overwrite: true);
        var environment = Windows
            ? null
            : new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = _directory };
        return NativeProgram.RunProcess(executable, [], environment);
    }

    // Builds the standard library as a shared library in the test's folder, and returns the path of the shared library.
    private string BuildStandardLibrary()
    {
        var output = Path.Combine(_directory, "std");
        var arguments = new List<string> { "build" };
        arguments.AddRange(Directory.GetFiles(Path.Combine(NativeProgram.RepositoryRoot, "stdlib"), "*.oxy"));
        arguments.AddRange(["--library", "--dynamic", "--no-std", "-o", output]);

        var run = NativeProgram.RunProcess(NativeProgram.CompilerExecutable, arguments);

        Assert.True(run.ExitCode == 0, $"Building the standard library failed with exit code {run.ExitCode}:\n{run.Error}");
        return output + (Windows ? ".dll" : ".so");
    }
}
