using System.ComponentModel;
using System.Diagnostics;
using LLVMSharp.Interop;

namespace Oxygen.Llvm;

public enum NativeDependencyKind
{
    Static,
    Dynamic
}

public sealed record NativeDependency(string Path, NativeDependencyKind Kind);

public sealed record NativeBuildOptions
{
    public required string OutputPath { get; init; }
    public string CompilerPath { get; init; } = "clang";
    // The archiver of a library build. It takes the rcs arguments of llvm-ar, which GNU ar also accepts.
    public string ArchiverPath { get; init; } = "llvm-ar";
    // A library build writes a shared library instead of a static archive. Its dependencies are then linked into it.
    public bool Shared { get; init; }
    public string OptimizationLevel { get; init; } = "O2";
    // The target triple passed to clang for both compiling and linking. Null leaves clang to the host.
    public string? Target { get; init; }
    // Whether to link with lld (-fuse-ld=lld). Null uses lld when it is found on PATH, and the default linker otherwise.
    public bool? UseLld { get; init; }
    // The directory for the intermediate IR and object files. Null creates a new directory under the system temporary directory.
    public string? TemporaryDirectory { get; init; }
    // Whether to keep the intermediate files after the build. Otherwise the temporary directory is deleted when the build ends.
    public bool KeepTemporaryFiles { get; init; }
    public int Jobs { get; init; } = 1;
    public string? WorkingDirectory { get; init; }
    public IReadOnlyList<NativeDependency> Dependencies { get; init; } = [];
    public IReadOnlyList<string> AdditionalLinkerArguments { get; init; } = [];
}

public interface INativeToolRunner
{
    void Run(string executable, IReadOnlyList<string> arguments, string? workingDirectory);
}

public sealed class NativeToolException(string executable, int exitCode, string output)
    : InvalidOperationException(
        $"Native tool '{executable}' failed with exit code {exitCode}.{Environment.NewLine}{output}")
{
    public string Executable { get; } = executable;
    public int ExitCode { get; } = exitCode;
    public string Output { get; } = output;
}

// Runs a native tool and waits for it. Both output streams are read while the tool runs: reading one to its end first would block
// the tool once the other pipe was full. The tool is stopped when it runs longer than the timeout, or when the cancellation
// token is cancelled, such as by Ctrl+C.
public sealed class ProcessNativeToolRunner(TimeSpan? timeout = null, CancellationToken cancellation = default) : INativeToolRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public void Run(string executable, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"Unable to start native tool '{executable}'. Ensure it is installed and on PATH.",
                exception);
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        stop.CancelAfter(_timeout);
        try
        {
            process.WaitForExitAsync(stop.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            if (cancellation.IsCancellationRequested)
                throw new OperationCanceledException($"The native tool '{executable}' was cancelled.", cancellation);
            throw new InvalidOperationException(
                $"Native tool '{executable}' did not finish within {_timeout.TotalSeconds:0} seconds and was stopped.");
        }

        var text = output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new NativeToolException(executable, process.ExitCode, text);
    }
}

public sealed class NativeBuildPipeline(INativeToolRunner? toolRunner = null)
{
    private readonly INativeToolRunner toolRunner = toolRunner ?? new ProcessNativeToolRunner();

    public void Build(GeneratedLlvmModule module, NativeBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(options);
        options = options with { OutputPath = ExecutablePath(options.OutputPath, options.Target) };
        Build(module.ToString(), options);
    }

    // The file that the link writes for an output path. On the Windows target an executable needs the .exe extension, which is
    // added when the output has none.
    public static string ExecutablePath(string outputPath, string? target) =>
        IsWindowsTarget(target) && string.IsNullOrEmpty(Path.GetExtension(outputPath)) ? outputPath + ".exe" : outputPath;

    public void Build(string llvmIr, NativeBuildOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(llvmIr);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputPath);
        options = options with { OutputPath = ExecutablePath(options.OutputPath, options.Target) };

        var outputPath = Path.GetFullPath(options.OutputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException($"Output path '{options.OutputPath}' has no directory.");
        Directory.CreateDirectory(outputDirectory);

        InTemporaryDirectory(options, temporaryDirectory =>
        {
            var irPath = Path.Combine(temporaryDirectory, "program.ll");
            var objectPath = Path.Combine(temporaryDirectory, "program.o");
            File.WriteAllText(irPath, llvmIr);

            toolRunner.Run(
                options.CompilerPath,
                CreateCompilationArguments(irPath, objectPath, options),
                options.WorkingDirectory);

            var linkArguments = CreateLinkArguments(objectPath, outputPath, options);

            toolRunner.Run(options.CompilerPath, linkArguments, options.WorkingDirectory);
        });
    }

    // Builds a static or a shared library from one or more modules. Each module is compiled to its own object, with its metadata in
    // the .oxmeta section, and the objects are archived together (or linked together into the shared library). A library is not
    // linked as a program, so it needs neither the runtime nor a native entry point. A shared library links the runtime it depends on.
    public void BuildLibrary(IReadOnlyList<GeneratedLlvmModule> modules, NativeBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputPath);
        if (modules.Count == 0)
            throw new ArgumentException("A library needs at least one module.", nameof(modules));

        foreach (var module in modules)
        {
            // The section is added once per module, so the library carries the metadata of each of its modules.
            module.AddMetadataSection();
            if (options.Shared && IsWindowsTarget(options.Target))
                ExportDefinitions(module.Module);
        }
        var outputPath = Path.GetFullPath(options.Shared
            ? SharedLibraryPath(options.OutputPath, options.Target)
            : LibraryPath(options.OutputPath, options.Target));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException($"Output path '{options.OutputPath}' has no directory."));

        InTemporaryDirectory(options, temporaryDirectory =>
        {
            var objectPaths = new List<string>();
            foreach (var module in modules)
            {
                var name = module.Metadata.ModuleName;
                var irPath = Path.Combine(temporaryDirectory, $"{name}.ll");
                var objectPath = Path.Combine(temporaryDirectory, $"{name}.o");
                File.WriteAllText(irPath, module.ToString());
                toolRunner.Run(
                    options.CompilerPath,
                    CreateCompilationArguments(irPath, objectPath, options),
                    options.WorkingDirectory);
                objectPaths.Add(objectPath);
            }

            // The archiver adds members to an existing archive, and the linker would keep an old file's parts, so the old library is
            // removed first.
            if (File.Exists(outputPath))
                File.Delete(outputPath);
            if (options.Shared)
                toolRunner.Run(options.CompilerPath, CreateSharedLinkArguments(objectPaths, outputPath, options),
                    options.WorkingDirectory);
            else
                toolRunner.Run(options.ArchiverPath, ["rcs", outputPath, .. objectPaths], options.WorkingDirectory);
        });
    }

    // The single-module form of BuildLibrary.
    public void BuildLibrary(GeneratedLlvmModule module, NativeBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(module);
        BuildLibrary([module], options);
    }

    // The file that a library build writes for an output path. A library gets .lib on the Windows target and .a elsewhere, when the
    // output has no extension.
    public static string LibraryPath(string outputPath, string? target) =>
        string.IsNullOrEmpty(Path.GetExtension(outputPath))
            ? outputPath + (IsWindowsTarget(target) ? ".lib" : ".a")
            : outputPath;

    // The file that a shared library build writes: a .dll on the Windows target, which also gets an import library next to it, and an
    // .so elsewhere. The extension is added when the output has none.
    public static string SharedLibraryPath(string outputPath, string? target) =>
        string.IsNullOrEmpty(Path.GetExtension(outputPath))
            ? outputPath + (IsWindowsTarget(target) ? ".dll" : ".so")
            : outputPath;

    // Links a shared library from the modules' objects and its dependencies, which are the shared runtime and nothing else. The
    // dependencies are resolved when the library is loaded, or by the linker through the import library on Windows.
    public static IReadOnlyList<string> CreateSharedLinkArguments(
        IReadOnlyList<string> objectPaths,
        string outputPath,
        NativeBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(objectPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(options);

        var arguments = CreateCodeGenerationArguments(options, includeLtoJobs: false);
        arguments.Add("-shared");
        if (!IsWindowsTarget(options.Target))
            arguments.Add($"-Wl,-soname,{Path.GetFileName(outputPath)}");
        arguments.AddRange(objectPaths);
        arguments.AddRange(options.Dependencies.Select(dependency => dependency.Path));
        arguments.AddRange(RunPathArguments(options));
        arguments.Add("-o");
        arguments.Add(outputPath);
        if (options.UseLld ?? IsLldOnPath(options.Target))
            arguments.Add("-fuse-ld=lld");
        return arguments;
    }

    // On Windows a shared library exports the functions it defines, so that the programs that link it can call them. An instance of a
    // generic declaration is linkonce_odr and stays private to the object, so it is not exported.
    // Exports the functions and the data of the module with external linkage. Data is exported too, since a program that uses a class of
    // the library refers to the class's type information, which the library defines.
    private static void ExportDefinitions(LLVMModuleRef module)
    {
        for (var function = module.FirstFunction; function.Handle != IntPtr.Zero; function = function.NextFunction)
        {
            if (!function.IsDeclaration && function.Linkage == LLVMLinkage.LLVMExternalLinkage)
                function.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
        }
        for (var global = module.FirstGlobal; global.Handle != IntPtr.Zero; global = global.NextGlobal)
        {
            if (!global.IsDeclaration && global.Linkage == LLVMLinkage.LLVMExternalLinkage)
                global.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
        }
    }

    // Runs a build in a temporary directory, which is deleted afterwards unless the options keep it.
    private static void InTemporaryDirectory(NativeBuildOptions options, Action<string> build)
    {
        var temporaryDirectory = options.TemporaryDirectory ?? Path.Combine(Path.GetTempPath(), $"oxygen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            build(temporaryDirectory);
        }
        finally
        {
            try
            {
                if (!options.KeepTemporaryFiles)
                    Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Temporary build artifacts must not hide the compiler result.
            }
            catch (UnauthorizedAccessException)
            {
                // Temporary build artifacts must not hide the compiler result.
            }
        }
    }

    public static IReadOnlyList<string> CreateCompilationArguments(
        string irPath,
        string objectPath,
        NativeBuildOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(irPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectPath);
        ArgumentNullException.ThrowIfNull(options);

        var arguments = CreateCodeGenerationArguments(options, includeLtoJobs: false);
        arguments.AddRange(["-x", "ir", "-c", irPath, "-o", objectPath]);
        return arguments;
    }

    public static IReadOnlyList<string> CreateLinkArguments(
        string objectPath,
        string outputPath,
        NativeBuildOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(options);

        var arguments = CreateCodeGenerationArguments(options, includeLtoJobs: true);
        arguments.Add(objectPath);
        arguments.AddRange(options.Dependencies
            .Where(dependency => dependency.Kind == NativeDependencyKind.Static)
            .Select(dependency => dependency.Path));
        arguments.AddRange(options.Dependencies
            .Where(dependency => dependency.Kind == NativeDependencyKind.Dynamic)
            .Select(dependency => dependency.Path));
        arguments.AddRange(RunPathArguments(options));
        // The Itanium exception functions come from the C++ runtime, and the runtime and the standard library call libm. ELF
        // targets link both, after the objects that need them. The Windows target has them in its own C runtime.
        if (!IsWindowsTarget(options.Target))
            arguments.AddRange(["-lm", "-lstdc++"]);
        arguments.AddRange(options.AdditionalLinkerArguments);
        arguments.Add("-o");
        arguments.Add(outputPath);
        if (options.UseLld ?? IsLldOnPath(options.Target))
            arguments.Add("-fuse-ld=lld");
        return arguments;
    }

    // On ELF targets the program and the shared libraries it links record the folders of their shared dependencies as their RUNPATH, so
    // they load without LD_LIBRARY_PATH or a copy in a system folder. The folder is a separate argument: -Wl, would split a comma in it.
    // Windows finds the DLLs through PATH, so it records nothing.
    private static IEnumerable<string> RunPathArguments(NativeBuildOptions options)
    {
        if (IsWindowsTarget(options.Target))
            return [];
        var folders = options.Dependencies
            .Where(dependency => dependency.Kind == NativeDependencyKind.Dynamic)
            .Select(dependency => Path.GetDirectoryName(Path.GetFullPath(dependency.Path)) ?? string.Empty)
            .Where(folder => folder.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (folders.Any(folder => folder.Contains(',', StringComparison.Ordinal)))
            throw new ArgumentException("A shared dependency's folder has a comma in its path, so the program cannot record it as a run path.");
        return folders.SelectMany(folder => new[] { "-Wl,-rpath", $"-Wl,{folder}" });
    }

    private static bool IsWindowsTarget(string? target) =>
        target is null ? OperatingSystem.IsWindows() : target.Contains("-windows-", StringComparison.Ordinal);

    // clang finds lld by its program name, so lld is used when that program is on PATH: lld-link for the Windows target, and
    // ld.lld for the others.
    private static bool IsLldOnPath(string? target)
    {
        var linker = IsWindowsTarget(target) ? "lld-link" : "ld.lld";
        var fileNames = OperatingSystem.IsWindows() ? new[] { linker + ".exe", linker } : new[] { linker };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var fileName in fileNames)
            {
                if (File.Exists(Path.Combine(directory, fileName)))
                    return true;
            }
        }
        return false;
    }

    private static List<string> CreateCodeGenerationArguments(
        NativeBuildOptions options,
        bool includeLtoJobs)
    {
        if (string.IsNullOrWhiteSpace(options.OptimizationLevel) ||
            options.OptimizationLevel.Any(char.IsWhiteSpace) ||
            options.OptimizationLevel.Any(character => !char.IsLetterOrDigit(character)))
            throw new ArgumentException(
                "OptimizationLevel must contain only letters and digits.",
                nameof(options));
        if (options.Jobs < 1)
            throw new ArgumentOutOfRangeException(nameof(NativeBuildOptions.Jobs), options.Jobs,
                "Jobs must be greater than zero.");

        var arguments = new List<string> { $"-{options.OptimizationLevel}" };
        if (!string.IsNullOrWhiteSpace(options.Target))
            arguments.Add($"--target={options.Target}");
        if (options.Jobs > 1)
        {
            arguments.Add("-flto=thin");
            if (includeLtoJobs)
                arguments.Add($"-flto-jobs={options.Jobs}");
        }
        return arguments;
    }
}
