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
    public void ManglingIncludesFloatingPointParameterTypes()
    {
        var model = Analyze("i32 main() { return 0; } f64 scale(f64 value) { return value; }");

        var scale = model.Functions.Values.Single(function => function.Name == "scale");

        Assert.Equal("_Ox_v0_m7program_f5scale_p1_f64", OxygenNameMangler.Mangle("program", scale));
    }

    [Fact(Skip = "LLVMSharp native library is not installed in this test environment.")]
    public void EmitsVoidMainAsI32ReturningZero()
    {
        var model = Analyze("void main() { return; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("define i32 @_Ox_v0_m7program_f4main()", ir);
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
}
