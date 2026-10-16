using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Parsing;
using Oxygen.Compiler.Semantic;
using Oxygen.Llvm;
using Serilog;
using Xunit;

namespace Oxygen.Compiler.Tests;

public sealed class LlvmTests
{
    [Fact]
    public void ManglesModuleAndFunctionNamesWithLengthPrefixes()
    {
        var model = Analyze("i32 main() { return 0; }");

        var mangled = OxygenNameMangler.Mangle("program", model.MainFunction!);

        Assert.Equal("_Ox_v0_m7program_f4main", mangled);
    }

    [Fact]
    public void LinkArgumentsPlaceStaticDependenciesBeforeDynamicDependencies()
    {
        var arguments = NativeBuildPipeline.CreateLinkArguments(
            "program.o",
            "program",
            new NativeBuildOptions
            {
                OutputPath = "program",
                Dependencies =
                [
                    new NativeDependency("stdlib.so", NativeDependencyKind.Dynamic),
                    new NativeDependency("runtime.a", NativeDependencyKind.Static),
                    new NativeDependency("support.a", NativeDependencyKind.Static)
                ],
                AdditionalLinkerArguments = ["-pthread"]
            });

        Assert.Equal(
            ["-O2", "program.o", "runtime.a", "support.a", "stdlib.so", "-pthread", "-o", "program", "-v", "-fuse-ld=lld"],
            arguments);
    }

    [Fact]
    public void CompilesPassedIrThenLinksConfiguredDependencies()
    {
        var runner = new RecordingToolRunner();
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"oxygen-test-{Guid.NewGuid():N}");
        try
        {
            new NativeBuildPipeline(runner).Build(
                "; module",
                new NativeBuildOptions
                {
                    CompilerPath = "clang-test",
                    OutputPath = Path.Combine(outputDirectory, "program"),
                    Dependencies =
                    [
                        new NativeDependency("runtime.a", NativeDependencyKind.Static),
                        new NativeDependency("stdlib.so", NativeDependencyKind.Dynamic)
                    ]
                });

            Assert.Equal(2, runner.Invocations.Count);
            Assert.Contains("-O2", runner.Invocations[0].Arguments);
            Assert.Equal(["-x", "ir", "-c"], runner.Invocations[0].Arguments.Skip(1).Take(3));
            Assert.Equal(
                ["runtime.a", "stdlib.so", "-o", Path.Combine(outputDirectory, "program")],
                runner.Invocations[1].Arguments.Skip(2).Take(4));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void AddsDebugSymbolsAndIndependentOptimizationToBothStages()
    {
        var runner = new RecordingToolRunner();
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"oxygen-test-{Guid.NewGuid():N}");
        try
        {
            new NativeBuildPipeline(runner).Build(
                "; module",
                new NativeBuildOptions
                {
                    OptimizationLevel = "O0",
                    GenerateDebugSymbols = true,
                    OutputPath = Path.Combine(outputDirectory, "program")
                });

            Assert.Equal(2, runner.Invocations.Count);
            Assert.Equal(["-O0", "-g"], runner.Invocations[0].Arguments.Take(2));
            Assert.Equal(["-O0", "-g"], runner.Invocations[1].Arguments.Take(2));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void ManglingIncludesFloatingPointParameterTypes()
    {
        var model = Analyze("i32 main() { return 0; } f64 scale(f64 value) { return value; }");

        var scale = model.Functions.Values.Single(function => function.Name == "scale");

        Assert.Equal("_Ox_v0_m7program_f5scale_p1_f64", OxygenNameMangler.Mangle("program", scale));
    }

    [Fact(Skip = "LLVMSharp native library is not installed in this test environment.")]
    public void EmitsVoidMainAsVoidAndNativeMainReturnsZero()
    {
        var model = Analyze("void main() { return; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("define void @_Ox_v0_m7program_f4main()", ir);
        Assert.Contains("call void @_Ox_v0_m7program_f4main()", ir);
        Assert.Contains("define i32 @main(i32 %argc, ptr %argv)", ir);
        Assert.Contains("ret i32 0", ir);
    }

    [Fact(Skip = "LLVMSharp native library is not installed in this test environment.")]
    public void EmitsFloatingPointArithmeticAndNativeMainStub()
    {
        var model = Analyze(
            """
            f64 calculate(f64 value) { return value * 2.5; }
            i32 main() { return 0; }
            """);

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("define double @_Ox_v0_m7program_f9calculate_p1_f64", ir);
        Assert.Contains("fmul", ir);
        Assert.Contains("declare void @__oxygen_runtime_initialize()", ir);
        Assert.Contains("define i32 @main(i32 %argc, ptr %argv)", ir);
        Assert.Contains("call void @__oxygen_runtime_initialize()", ir);
        Assert.Contains("call i32 @_Ox_v0_m7program_f4main()", ir);
    }

    [Fact(Skip = "LLVMSharp native library is not installed in this test environment.")]
    public void EmitsOverloadedCallsToTheMangledFunction()
    {
        var model = Analyze(
            """
            i32 add(i32 left) { return left + 1; }
            f64 add(f64 left) { return left; }
            i32 main() { return add(41); }
            """);

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("@_Ox_v0_m7program_f3add_p1_i32", ir);
        Assert.Contains("@_Ox_v0_m7program_f3add_p1_f64", ir);
    }

    private static SemanticModel Analyze(string source)
    {
        var tokenizer = new Tokenizer(Log.Logger);
        var unit = new Parser(tokenizer.Tokenize(source), Log.Logger).Parse();
        return new SemanticAnalyzer(Log.Logger).Analyze([unit]);
    }

    private sealed class RecordingToolRunner : INativeToolRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Invocations { get; } = [];

        public void Run(string executable, IReadOnlyList<string> arguments, string? workingDirectory) =>
            Invocations.Add((executable, arguments));
    }
}
