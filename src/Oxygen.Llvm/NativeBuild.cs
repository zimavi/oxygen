using System.ComponentModel;
using System.Diagnostics;

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
    public string? MetadataPath { get; init; }
    public string CompilerPath { get; init; } = "clang";
    public string OptimizationLevel { get; init; } = "O2";
    // The target triple passed to clang for both compiling and linking. Null leaves clang to the host.
    public string? Target { get; init; }
    public int Jobs { get; init; } = 1;
    public bool GenerateDebugSymbols { get; init; }
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

public sealed class ProcessNativeToolRunner : INativeToolRunner
{
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

        var output = process.StandardOutput.ReadToEnd();
        output += process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new NativeToolException(executable, process.ExitCode, output);
    }
}

public sealed class NativeBuildPipeline(INativeToolRunner? toolRunner = null)
{
    private readonly INativeToolRunner toolRunner = toolRunner ?? new ProcessNativeToolRunner();

    public void Build(GeneratedLlvmModule module, NativeBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.MetadataPath))
        {
            var metadata = module.Metadata with
            {
                Dependencies = options.Dependencies
                    .Select(dependency => new NativeDependencyMetadata
                    {
                        Path = dependency.Path,
                        Kind = dependency.Kind
                    })
                    .ToArray()
            };
            NativeModuleMetadataSerializer.Write(options.MetadataPath, metadata);
            Build(module.ToString(), options);
            EmbedMetadataIfOutputExists(options.OutputPath, metadata);
            return;
        }
        Build(module.ToString(), options);
    }

    private static void EmbedMetadataIfOutputExists(string outputPath, NativeModuleMetadata metadata)
    {
        var fullPath = Path.GetFullPath(outputPath);
        if (File.Exists(fullPath))
            NativeModuleMetadataSerializer.Embed(fullPath, metadata);
    }

    public void Build(string llvmIr, NativeBuildOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(llvmIr);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputPath);

        var outputPath = Path.GetFullPath(options.OutputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException($"Output path '{options.OutputPath}' has no directory.");
        Directory.CreateDirectory(outputDirectory);

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"oxygen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
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
        }
        finally
        {
            try
            {
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
        arguments.AddRange(options.AdditionalLinkerArguments);
        arguments.Add("-o");
        arguments.Add(outputPath);
        arguments.Add("-v");
        arguments.Add("-fuse-ld=lld");
        return arguments;
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
        if (options.GenerateDebugSymbols)
            arguments.Add("-g");
        if (options.Jobs > 1)
        {
            arguments.Add("-flto=thin");
            if (includeLtoJobs)
                arguments.Add($"-flto-jobs={options.Jobs}");
        }
        return arguments;
    }
}
