using Xunit;

namespace Oxygen.Cli.Tests;

// A shared library that uses another library's classes is built against that library with --stdlib. A program that uses both libraries
// throws and catches the exception classes of each: by the library's own class, and by the standard library's base class.
public sealed class LibraryDependencyTests : IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();
    private static readonly string TargetDirectory = Path.Combine(NativeProgram.RepositoryRoot, "runtime", "build",
        Windows ? "x86_64-pc-windows-msvc" : "x86_64-unknown-linux-gnu");

    // The library defines an exception class that derives from the standard library's RuntimeException, and a function that throws it.
    private const string AppLibrary = """
        module mylib;
        import std.exception;

        public class AppException : RuntimeException {
            public AppException(string message) : base(message) {}
        }

        public void fail() {
            throw new AppException("from mylib");
        }
        """;

    // The library has a module-level variable, which its own functions and a generic function of the module use.
    private const string CountLibrary = """
        module mylib;

        i32 count = 3;

        public void tick() {
            count += 1;
        }

        public i32 current() {
            return count;
        }

        public T mark<T>(T value) {
            count += 1;
            return value;
        }
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public LibraryDependencyTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void AProgramCatchesAnExceptionOfAnotherLibraryAsItsStandardBaseClass()
    {
        var run = BuildAndRunProgram(
            """
            import mylib;
            import std.exception;

            i32 main() {
                try {
                    fail();
                } catch (RuntimeException error) {
                    return 9;
                }
                return 0;
            }
            """);

        Assert.Equal(9, run.ExitCode);
    }

    // A static field of a library's class has one storage, which the library owns: the program's call changes the value that the program reads.
    // A generic struct and a struct that it holds are declared by the library, and a program instantiates the generic struct with its own type
    // arguments and reads its fields.
    [SkippableFact]
    public void AProgramUsesAGenericStructOfAnotherLibrary()
    {
        const string shapesLibrary = """
            module mylib;

            public struct Pair {
                i32 first;
                i32 second;
            }

            public struct Box<T> {
                T item;
                Pair pair;
            }
            """;

        var run = BuildAndRunProgram(
            """
            import mylib;

            i32 main() {
                Box<i32> box = Box { item: 5, pair: Pair { first: 1, second: 2 } };
                return box.item + box.pair.second;
            }
            """, shapesLibrary);

        Assert.Equal(7, run.ExitCode);
    }

    // A generic function of a library uses the library's module-level variable. The program's instantiation changes the same storage that the
    // library's own code reads, so the program sees the library's change and the library sees the program's.
    [SkippableFact]
    public void AGenericFunctionOfAnotherLibraryUsesTheGlobalsOfItsModule()
    {
        var run = BuildAndRunProgram(
            """
            import mylib;

            i32 main() {
                tick();
                mark<i32>(0);
                return current();
            }
            """, CountLibrary);

        Assert.Equal(5, run.ExitCode);
    }

    // The privacy of a module's variables is about names: a program that imports the library cannot name its variable.
    [SkippableFact]
    public void AProgramCannotNameTheModuleVariableOfAnotherLibrary()
    {
        var build = BuildProgram(
            """
            import mylib;

            i32 main() {
                return count;
            }
            """, CountLibrary);

        Assert.Equal(ExitCodes.SourceError, build.ExitCode);
        Assert.Contains("'count'", build.Error);
    }

    // A function of a library takes a function pointer, which the program fills with its own function's address. The library's signature
    // names the function pointer type through its metadata, so the program's call matches the library's function.
    [SkippableFact]
    public void AProgramPassesItsFunctionToAFunctionPointerParameterOfAnotherLibrary()
    {
        const string applyLibrary = """
            module mylib;

            public i32 apply(fn i32 op(i32, i32), i32 a, i32 b) {
                return op(a, b);
            }
            """;

        var run = BuildAndRunProgram(
            """
            import mylib;

            i32 add(i32 a, i32 b) {
                return a + b;
            }

            i32 main() {
                return apply(&add, 2, 3);
            }
            """, applyLibrary);

        Assert.Equal(5, run.ExitCode);
    }

    // A public alias of a library is carried with the library's declarations, so a program names the library's type by the alias.
    [SkippableFact]
    public void AProgramUsesAnAliasOfAnotherLibrary()
    {
        const string countLibrary = """
            module mylib;

            public type Count = i32;

            public Count bump(Count value) {
                return value + 1;
            }
            """;

        var run = BuildAndRunProgram(
            """
            import mylib;

            i32 main() {
                Count value = 4;
                return bump(value);
            }
            """, countLibrary);

        Assert.Equal(5, run.ExitCode);
    }

    [SkippableFact]
    public void AProgramAndAnotherLibraryShareTheStaticFieldsOfAClass()
    {
        const string counterLibrary = """
            module mylib;

            public class Counter {
                public static i32 count = 3;

                public static i32 next(i32 step) {
                    count = count + step;
                    return count;
                }
            }
            """;

        var run = BuildAndRunProgram(
            """
            import mylib;

            i32 main() {
                Counter.next(4);
                return Counter.count;
            }
            """, counterLibrary);

        Assert.Equal(7, run.ExitCode);
    }

    [SkippableFact]
    public void AProgramCatchesAnExceptionOfAnotherLibraryAsItsOwnClass()
    {
        var run = BuildAndRunProgram(
            """
            import mylib;

            i32 main() {
                try {
                    fail();
                } catch (AppException error) {
                    return 10;
                }
                return 0;
            }
            """);

        Assert.Equal(10, run.ExitCode);
    }

    // A program built without exceptions has no catch clauses that run, but a throw from a library built with exceptions still reaches the
    // program's entry point, which reports it and ends the program with exit code 101 (docs/exceptions.md).
    [SkippableFact]
    public void AProgramBuiltWithoutExceptionsEndsWhenALibraryThrows()
    {
        var run = BuildAndRunProgram(
            """
            import mylib;

            i32 main() {
                try {
                    fail();
                } catch (AppException error) {
                    return 10;
                }
                return 0;
            }
            """, AppLibrary, "--no-exceptions");

        Assert.Equal(101, run.ExitCode);
        Assert.Contains("from mylib", run.Error);
    }

    // println of a string calls the standard library's string overload. An imported function takes a string by pointer, and the call must too.
    [SkippableFact]
    public void AProgramPrintsAStringThroughTheSharedStandardLibrary()
    {
        var run = BuildAndRunProgram(
            """
            import std.io;

            i32 main() {
                io.println("hello");
                return 3;
            }
            """);

        Assert.Equal(3, run.ExitCode);
        Assert.Equal("hello\n", run.Output.Replace("\r\n", "\n"));
    }

    // Builds the standard library, then the library that uses it, then the program that uses both, and runs the program.
    private (int ExitCode, string Output, string Error) BuildAndRunProgram(string source, string librarySource = AppLibrary,
        params string[] programFlags)
    {
        var build = BuildProgram(source, librarySource, programFlags);
        Assert.True(build.ExitCode == 0, $"Building the program failed with exit code {build.ExitCode}:\n{build.Error}");

        // The program finds the libraries and the runtime beside it: on Windows through the DLL search, and on Linux through the library
        // path, given to the child process only.
        var environment = Windows
            ? null
            : new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = _directory };
        return NativeProgram.RunProcess(Path.Combine(_directory, Windows ? "main.exe" : "main"), [], environment);
    }

    // Builds the standard library, then the library that uses it, then the program that uses both, and returns the program's build result.
    // The libraries and the runtime are built beside the program, and the runtime is copied there too.
    private (int ExitCode, string Output, string Error) BuildProgram(string source, string librarySource, params string[] programFlags)
    {
        NativeProgram.RequireLibraryToolchain();
        var runtime = Path.Combine(TargetDirectory, Windows ? "oxygenrt.lib" : "liboxygenrt.so");
        var runtimeFile = Path.Combine(TargetDirectory, Windows ? "oxygenrt.dll" : "liboxygenrt.so");
        Skip.IfNot(File.Exists(runtime) && File.Exists(runtimeFile),
            "The shared runtime is not built. Run runtime/build.ps1 or runtime/build.sh to build it.");

        var standardLibrary = BuildStandardLibrary();
        var appSource = Path.Combine(_directory, "mylib.oxy");
        File.WriteAllText(appSource, librarySource);
        var appLibrary = Path.Combine(_directory, "mylib");
        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", appSource, "--library", "--dynamic", "--stdlib", standardLibrary, "-r", runtime, "-o", appLibrary]);
        Assert.True(build.ExitCode == 0, $"Building the library failed with exit code {build.ExitCode}:\n{build.Error}");

        var programSource = Path.Combine(_directory, "main.oxy");
        File.WriteAllText(programSource, source);
        var executable = Path.Combine(_directory, Windows ? "main.exe" : "main");
        var program = NativeProgram.RunProcess(NativeProgram.CompilerExecutable,
            ["build", programSource, "--stdlib", standardLibrary, "--stdlib", appLibrary + (Windows ? ".dll" : ".so"),
                "-r", runtime, .. programFlags, "-o", executable]);

        File.Copy(runtimeFile, Path.Combine(_directory, Path.GetFileName(runtimeFile)), overwrite: true);
        return program;
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
