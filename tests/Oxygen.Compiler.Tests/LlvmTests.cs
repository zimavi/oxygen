using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LLVMSharp.Interop;
using Oxygen.Compiler.Ast;
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
    public void MultiWorkerBuildsEnableThinLtoBackendJobsOnlyDuringLink()
    {
        var options = new NativeBuildOptions
        {
            OutputPath = "program",
            Jobs = 4
        };

        var compilationArguments = NativeBuildPipeline.CreateCompilationArguments(
            "program.ll",
            "program.o",
            options);
        var linkArguments = NativeBuildPipeline.CreateLinkArguments(
            "program.o",
            "program",
            options);

        Assert.Equal(["-O2", "-flto=thin"], compilationArguments.Take(2));
        Assert.Equal(["-O2", "-flto=thin", "-flto-jobs=4"], linkArguments.Take(3));
    }

    [Fact]
    public void RejectsNonPositiveWorkerCounts()
    {
        var options = new NativeBuildOptions
        {
            OutputPath = "program",
            Jobs = 0
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeBuildPipeline.CreateLinkArguments("program.o", "program", options));
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

    [Fact]
    public void ManglingIncludesPointeeTypes()
    {
        var model = Analyze("i32 consume(i32* value) { return *value; } i32 main() { return 0; }");
        var consume = model.Functions.Values.Single(function => function.Name == "consume");

        Assert.Equal("_Ox_v0_m7program_f7consume_p1_pi32", OxygenNameMangler.Mangle("program", consume));
    }

    [Fact]
    public void NativeMetadataSerializationIsVersionedAndDeterministic()
    {
        var metadata = new NativeModuleMetadata
        {
            ModuleName = "program",
            EntryPoint = "main",
            Functions =
            [
                new NativeFunctionMetadata
                {
                    SourceName = "z",
                    NativeName = "_Ox_z",
                    ReturnType = "i32"
                },
                new NativeFunctionMetadata
                {
                    SourceName = "a",
                    NativeName = "_Ox_a",
                    ReturnType = "void"
                }
            ],
            Dependencies =
            [
                new NativeDependencyMetadata { Path = "runtime.a", Kind = NativeDependencyKind.Static },
                new NativeDependencyMetadata { Path = "stdlib.so", Kind = NativeDependencyKind.Dynamic }
            ]
        };

        var serialized = NativeModuleMetadataSerializer.Serialize(metadata);

        Assert.Equal(serialized, NativeModuleMetadataSerializer.Serialize(metadata with
        {
            Functions = metadata.Functions.Reverse().ToArray(),
            Dependencies = metadata.Dependencies.Reverse().ToArray()
        }));
        Assert.Contains("\"formatVersion\": 1", serialized);
        var roundTripped = NativeModuleMetadataSerializer.Deserialize(serialized);
        Assert.Equal(metadata.ModuleName, roundTripped.ModuleName);
        Assert.Equal(metadata.EntryPoint, roundTripped.EntryPoint);
        Assert.Equal(metadata.Functions.OrderBy(function => function.NativeName).Select(function => function.NativeName),
            roundTripped.Functions.Select(function => function.NativeName));
        Assert.Equal(metadata.Dependencies.OrderBy(dependency => dependency.Kind).ThenBy(dependency => dependency.Path).Select(dependency => dependency.Path),
            roundTripped.Dependencies.Select(dependency => dependency.Path));
    }

    [Fact]
    public void NativeMetadataCarriesCInteropIntent()
    {
        var metadata = new NativeModuleMetadata
        {
            ModuleName = "program",
            EntryPoint = "main",
            Functions =
            [
                new NativeFunctionMetadata
                {
                    SourceName = "puts",
                    NativeName = "_Ox_puts",
                    ExternalName = "puts",
                    CallingConvention = "c",
                    ReturnType = "i32"
                }
            ]
        };

        var roundTripped = NativeModuleMetadataSerializer.Deserialize(
            NativeModuleMetadataSerializer.Serialize(metadata));

        Assert.Equal("puts", roundTripped.Functions.Single().ExternalName);
        Assert.Equal("c", roundTripped.Functions.Single().CallingConvention);
    }

    [Fact]
    public void NativeMetadataRejectsUnknownFormatVersions()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            NativeModuleMetadataSerializer.Deserialize(
                """{"formatVersion":2,"moduleName":"program","entryPoint":"main"}"""));

        Assert.Contains("Unsupported native metadata format version", exception.Message);
    }

    [Fact]
    public void EmitsImmutableStringAsPointerAndLengthAggregate()
    {
        var model = Analyze(
            "string echo(string value) { return value; } i32 main() { echo(\"hello\"); return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("{ ptr, i64 }", ir);
        Assert.Contains("hello", ir);
    }

    [Fact]
    public void EmitsConstructorChainCallsForThisAndBaseInitializers()
    {
        var model = Analyze(
            "class Base { i32 value = 1; Base() { } Base(i32 extra) : this() { value = extra; } } " +
            "class Child : Base { i32 own = 2; Child() : base(3) { } } " +
            "i32 main() { Child child = new Child(); return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        var baseClass = model.Classes["program.Base"];
        var parameterless = OxygenNameMangler.MangleConstructor(baseClass.ModuleName, baseClass, baseClass.Constructors[0]);
        var withValue = OxygenNameMangler.MangleConstructor(baseClass.ModuleName, baseClass, baseClass.Constructors[1]);
        Assert.Contains($"call void @{withValue}(", ir);
        Assert.Contains($"call void @{parameterless}(", ir);
    }

    [Fact]
    public void SynthesizedConstructorZeroInitializesOnlyItsOwnPropertyStorage()
    {
        var model = Analyze(
            "class Base { public i32 Level { get; private set; } } " +
            "class Account : Base { public i32 Balance { get; private set; } } " +
            "i32 main() { Account account = new Account(); return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("store i32 0, ptr %field.Balance", ir);
        Assert.Contains("store i32 0, ptr %field.Level", ir);
    }

    [Fact]
    public void EmitsSynthesizedConstructorAggregateZeroInitialization()
    {
        var model = Analyze(
            "struct Point { i32 x; bool visible; } " +
            "class Holder { i32 count; Point point; string name; } " +
            "i32 main() { Holder value = new Holder(); return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("store i32 0", ir);
        Assert.Contains("zeroinitializer", ir);
    }

    [Fact]
    public void EmitsMutuallyRecursiveClassFields()
    {
        var model = Analyze(
            "class StackTrace { u64 count; Exception exception_object; } " +
            "class Exception { string message; StackTrace stack_trace; } " +
            "i32 main() { return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("%class.program.StackTrace", ir);
        Assert.Contains("%class.program.Exception", ir);
    }

    [Fact]
    public void EmitsSizeOfForMutuallyRecursiveReferenceClasses()
    {
        var model = Analyze(
            "class StackTrace { Exception exception_object; } " +
            "class Exception { StackTrace stack_trace; } " +
            "u64 size_of_exception() { return sizeof<Exception>(); } " +
            "i32 main() { return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        // A class reference is a pointer, so its size is LLVM's pointer size under the module's data layout.
        Assert.Contains("ret i64 ptrtoint (ptr getelementptr (ptr, ptr null, i32 1) to i64)", ir);
    }

    [Fact]
    public void EmitsVirtualClassCallThroughVtableSlot()
    {
        var model = AnalyzeWithStdException(
            "class Base { virtual i32 value() { return 1; } } " +
            "class Child : Base { override i32 value() { return 2; } } " +
            "i32 main() { Base value = new Child(); return value.value(); }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        // Pointers are opaque, so the receiver needs no bitcast; the slot is loaded and called indirectly.
        Assert.Contains("vtable.slot", ir);
        Assert.Contains("load ptr, ptr %vtable.slot", ir);
        // The local is freed at scope end, so it is owned and the call can unwind: it is an invoke into the cleanup.
        Assert.Contains("invoke i32 %vtable.target", ir);
    }

    [Fact]
    public void EmitsArrayElementPassedToAFunction()
    {
        var model = Analyze(
            """
            void print(i32 value) { return; }
            i32 main() {
                i32[2] values = { 34, 35 };
                print(values[1]);
                return 0;
            }
            """);

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("getelementptr [2 x i32]", ir);
        Assert.Contains("array.element", ir);
    }

    [Fact]
    public void EmbedsAndReadsNativeMetadataFromAnArtifact()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oxygen-metadata-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var metadata = new NativeModuleMetadata
            {
                ModuleName = "foo.bar",
                EntryPoint = "main",
                Functions =
                [
                    new NativeFunctionMetadata
                    {
                        SourceName = "value",
                        NativeName = "_Ox_native_value",
                        ReturnType = "i32"
                    }
                ]
            };

            NativeModuleMetadataSerializer.Embed(path, metadata);

            Assert.Equal(metadata.ModuleName, NativeModuleMetadataSerializer.ReadArtifact(path).ModuleName);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void EmitsVoidMainAsVoidAndNativeMainReturnsZero()
    {
        var model = Analyze("void main() { return; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("define void @_Ox_v0_m7program_f4main()", ir);
        Assert.Contains("invoke void @_Ox_v0_m7program_f4main()", ir);
        Assert.DoesNotContain("%call = call void", ir);
        Assert.Contains("define i32 @main(i32 %argc, ptr %argv)", ir);
        Assert.Contains("ret i32 0", ir);
    }

    [Fact]
    public void EmitsVoidCallsWithoutAnAssignedResult()
    {
        var model = Analyze(
            "void notify() { return; } void main() { notify(); return; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("call void @_Ox_v0_m7program_f6notify()", ir);
        Assert.DoesNotContain("%call = call void @_Ox_v0_m7program_f6notify()", ir);
    }

    [Fact]
    public void EmitsClassAndInterfaceReferenceCasts()
    {
        var model = Analyze(
            """
            interface Printable { i32 value(); }
            class Base { }
            class Item : Base, Printable { i32 value() { return 1; } }
            i32 main() {
                Base baseValue = new Item();
                Printable printable = new Item();
                Item fromBase = cast<Item>(baseValue);
                Item fromInterface = cast<Item>(printable);
                return 0;
            }
            """);

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("__oxygen_runtime_is_instance", ir);
        Assert.Contains("__oxygen_runtime_invalid_cast", ir);
        Assert.Contains("oxygen.type.info.program.Item", ir);
        Assert.Contains("oxygen.type.info.program.Printable", ir);
        // The kind field is written through a constant address, which LLVM does not name, so the
        // assertion checks the stored value: 2 is the interface kind.
        Assert.Contains("store i32 2, ptr getelementptr", ir);
        Assert.Contains("interface.type", ir);
        Assert.Contains("cast.failure", ir);
    }

    [Fact]
    public void EmitsCallableInterfaceMethodTableAndIndirectCall()
    {
        var model = AnalyzeWithStdException(
            "interface Printable { i32 value(); } " +
            "class Item : Printable { i32 value() { return 7; } } " +
            "i32 main() { Printable printable = new Item(); return printable.value(); }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("oxygen.interface.table.program.Item.program.Printable", ir);
        Assert.Contains("interface.slot", ir);
        Assert.Contains("interface.call", ir);
        Assert.Contains("load ptr", ir);
    }

    [Fact]
    public void EmitsUnsignedDivisionComparisonAndFloatingPointOrdering()
    {
        var model = Analyze(
            "i32 unsignedTest(u64 value) { if (value < 10) { return 1; } return cast<i32>(value / 10 % 3); } " +
            "i32 floatTest(f64 value) { if (value < 0.0) { return 1; } return 0; } " +
            "i32 main() { return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("icmp ult i64", ir);
        Assert.Contains("udiv i64", ir);
        Assert.Contains("urem i64", ir);
        Assert.Contains("fcmp olt double", ir);
    }

    [Fact]
    public void EmitsUnorderedFloatingPointInequality()
    {
        // NaN is not equal to itself, so inequality must be the unordered predicate.
        var model = Analyze("i32 check(f64 value) { if (value != value) { return 1; } return 0; } i32 main() { return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("fcmp une double", ir);
    }

    [Fact]
    public void StoresIndexedValuesAsTheElementType()
    {
        var model = Analyze(
            "void clear(i8* bytes) { bytes[0] = 0; } i32 main() { return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("store i8 0", ir);
    }

    [Fact]
    public void PassesExternalStringResultsThroughAnOutPointer()
    {
        var model = Analyze(
            "@extern(\"oxygen_c_string_to_string\") extern string convert(i8* value); " +
            "i32 main() { return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("declare void @oxygen_c_string_to_string(ptr, ptr)", ir);
    }

    [Fact]
    public void PassesEveryExternalStringParameterByPointer()
    {
        var model = Analyze(
            "@extern(\"oxygen_string_to_c_string\") extern i8* to_c_string(string value); " +
            "@extern(\"oxygen_custom_takes_two\") extern void takes_two(string first, i32 count, string second); " +
            "i32 main() { return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("declare ptr @oxygen_string_to_c_string(ptr)", ir);
        Assert.Contains("declare void @oxygen_custom_takes_two(ptr, i32, ptr)", ir);
    }

    [Fact]
    public void EmitsBooleanNegationAndEquality()
    {
        // A parameter is not a compile-time constant, so the operations stay in the IR.
        var model = Analyze(
            "i32 check(bool value) { if (!value) { return 1; } " +
            "if (value == false) { return 2; } if (value != true) { return 3; } return 0; } " +
            "i32 main() { return check(true); }");

        // Generation verifies the module, so these checks only cover the operations themselves.
        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("xor i1", ir);
        Assert.Contains("icmp eq i1", ir);
        Assert.Contains("icmp ne i1", ir);
    }

    [Fact]
    public void EmitsCallableInterfacePropertyTableAndIndirectAccessors()
    {
        var model = Analyze(
            "interface Named { i32 Value { get; set; } } " +
            "class Item : Named { i32 Value { get; set; } } " +
            "i32 main() { Named named = new Item(); named.Value = 3; return named.Value; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("oxygen.interface.table.program.Item.program.Named", ir);
        Assert.Contains("interface.property.slot", ir);
        Assert.Contains("interface.property.call", ir);
    }

    [Fact]
    public void EmitsOneDestructorForAnInnerClassTransferredToAnOuterLocal()
    {
        var model = Analyze(
            "class Box { ~Box() { } } i32 main() { Box outer = new Box(); { Box inner = new Box(); outer = inner; } delete outer; return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();
        const string destructor = "%dispatch.destructor = load";

        Assert.Equal(1, ir.Split(destructor, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void EmitsNestedDestructorsInReverseScopeOrder()
    {
        var model = Analyze(
            "class Box { ~Box() { } } i32 main() { Box outer = Box(); { Box inner = Box(); } return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();
        const string destructor = "call void @_Ox_v0_m7program_d3Box";

        var first = ir.IndexOf(destructor, StringComparison.Ordinal);
        var second = ir.IndexOf(destructor, first + destructor.Length, StringComparison.Ordinal);
        // Each destructor runs on its scope-end path, and again in the unwind cleanup, which destroys flagged locals.
        Assert.Equal(4, ir.Split(destructor, StringSplitOptions.None).Length - 1);
        Assert.True(first >= 0 && second > first);
    }

    [Fact]
    public void EmitsDestructorAfterReturnedClassOwnershipIsReceived()
    {
        var model = Analyze(
            "class Box { ~Box() { } } Box make() { Box value = new Box(); return value; } i32 main() { Box result = make(); delete result; return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();
        const string destructor = "%dispatch.destructor = load";

        Assert.Equal(1, ir.Split(destructor, StringSplitOptions.None).Length - 1);
        Assert.Contains("@_Ox_v0_m7program_f4make", ir);
    }

    [Fact]
    public void TransfersClassArgumentOwnershipToTheCallee()
    {
        var model = Analyze(
            "class Box { ~Box() { } } void consume(Box value) { return; } i32 main() { Box value = new Box(); consume(value); delete value; return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();
        const string destructor = "%dispatch.destructor = load";

        Assert.Equal(1, ir.Split(destructor, StringSplitOptions.None).Length - 1);
        Assert.Contains("define void @_Ox_v0_m7program_f7consume_p1", ir);
    }

    [Fact]
    public void EmitsExplicitThisFieldAccessWhenParameterShadowsField()
    {
        var model = Analyze(
            "class Box { i32 value; Box(i32 value) { this.value = value; } i32 get(i32 value) { return this.value + value; } } i32 main() { return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("get", ir);
        Assert.Contains("field.value", ir);
    }

    [Fact]
    public void ReusesExternalRuntimeAllocationDeclarations()
    {
        var model = Analyze(
            """
            @extern("oxygen_alloc") extern void* allocate(u64 size);
            @extern("oxygen_free") extern void deallocate(void* pointer);
            i32 main() { return 0; }
            """);

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("declare ptr @oxygen_alloc(i64)", ir);
        Assert.Contains("declare void @oxygen_free(ptr)", ir);
        Assert.DoesNotContain("@oxygen_alloc.1", ir);
        Assert.DoesNotContain("@oxygen_free.2", ir);
    }

    [Fact]
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
        Assert.Contains("invoke i32 @_Ox_v0_m7program_f4main()", ir);
    }

    [Fact]
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

    [Fact]
    public void EmitsControlFlowBlocksAndLoopBranches()
    {
        var model = Analyze(
            """
            i32 main() {
                i32 value = 0;
                while (value < 3) {
                    if (value == 1) {
                        continue;
                    }
                    value = value + 1;
                    if (value == 2) {
                        break;
                    }
                }
                for (i32 index = 0; index < 2; index = index + 1) {
                    value = value + index;
                }
                return value;
            }
            """);

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("while.cond", ir);
        Assert.Contains("while.body", ir);
        Assert.Contains("while.end", ir);
        Assert.Contains("for.cond", ir);
        Assert.Contains("for.increment", ir);
        Assert.Contains("br i1", ir);
    }

    [Fact]
    public void EmitsIntegerSwitchInstructionAndFallthrough()
    {
        var model = Analyze(
            "i32 main() { i32 value = 1; switch (value) { case 0: return 10; case 1: return 20; default: return 30; } }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("switch i32", ir);
        Assert.Contains("switch.body", ir);
    }

    [Fact]
    public void SwitchWhoseCasesAllReturnEndsWithUnreachableMergeBlock()
    {
        var model = Analyze(
            "i32 main() { i32 value = 1; switch (value) { case 0: return 10; default: return 30; } }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Matches(@"switch\.end:[^\n]*\n\s+unreachable", ir);
    }

    [Fact]
    public void CodeAfterSwitchWithBreakIsKept()
    {
        var model = Analyze(
            "i32 main() { i32 value = 1; switch (value) { case 1: break; default: return 2; } return 3; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("ret i32 3", ir);
    }

    [Fact]
    public void ShortCircuitOperatorsBranchOnTheLeftOperandAndJoinWithAPhi()
    {
        var model = Analyze(
            "i32 main() { i32 n = 0; if (n == 0 || 5 / n > 0) { return 4; } return 5; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("or.rhs", ir);
        Assert.Contains("phi i1", ir);
    }

    [Fact]
    public void IntegerDivisionChecksZeroDivisorAndSignedOverflowBeforeDividing()
    {
        var model = Analyze(
            "i32 main() { i32 a = 7; i32 b = 2; return a / b + a % b; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("div.zero", ir);
        Assert.Contains("div.overflow", ir);
        Assert.Contains("call void @__oxygen_runtime_arithmetic_failure", ir);
    }

    [Fact]
    public void UnsignedIntegersConvertToFloatingPointWithUnsignedInstructions()
    {
        var model = Analyze(
            "i32 main() { u32 value = 4294967295; f64 converted = cast<f64>(value); return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("uitofp i32", ir);
        Assert.DoesNotContain("sitofp i32", ir);
    }

    [Fact]
    public void IfWhoseArmsBothReturnEndsItsMergeBlockWithUnreachable()
    {
        var model = Analyze(
            "i32 main() { i32 x = 1; if (x == 1) { return 7; } else { return 8; } }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Matches(@"if\.end:[^\n]*\n\s+unreachable", ir);
    }

    [Fact]
    public void StringLiteralsKeepEmbeddedZeroBytes()
    {
        var model = Analyze(
            "i32 main() { string text = \"a\\u0000b\"; return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("[3 x i8] c\"a\\00b\"", ir);
    }

    [Fact]
    public void ShadowingDeclarationGetsItsOwnSlotAndTheOuterOneIsRestored()
    {
        var model = Analyze(
            "i32 main() { i32 a = 1; { i32 a = 2; } return a; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("%a.addr1 = alloca i32", ir);
        Assert.Contains("ret i32 %a", ir);
    }

    [Fact]
    public void EmitsAFreeWhenAnAutomaticFunctionsAllocationGoesOutOfScope()
    {
        var model = Analyze(
            "class Box { i32 value = 1; } void f() { Box b = new Box(); i32 v = 1; } i32 main() { f(); return 0; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("call void @oxygen_free", ir);
    }

    [Fact]
    public void EmitsConditionalExpressionControlFlow()
    {
        var model = Analyze("i32 main() { return true ? 1 : 2; }");

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("conditional.true", ir);
        Assert.Contains("conditional.false", ir);
        Assert.Contains("conditional.end", ir);
    }

    [Fact]
    public void StackSlotsAreAllocatedInTheEntryBlockNotInsideLoops()
    {
        var model = Analyze(
            """
            class Pair {
                i32 first;
            }

            i32 main() {
                i32 total = 0;
                i32 index = 0;
                while (index < 4) {
                    total = total + (index == 1 ? 1 : 2);
                    Pair pair = new Pair();
                    index = index + 1;
                }
                for (i32 step = 0; step < 2; step = step + 1) {
                    total = total + step;
                }
                while (index < 6) {
                    i32[2] values = { 1, 2 };
                    for (var value in values) {
                        total = total + value;
                    }
                    index = index + 1;
                }
                return total;
            }
            """);

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Contains("alloca", ir);
        Assert.Contains("for.in.index", ir);
        Assert.Empty(AllocasOutsideEntryBlock(ir));
    }

    // Returns the alloca lines that appear after a block label in the same function. The first block of a function is
    // its entry block, so an alloca is allowed only before the second label.
    private static List<string> AllocasOutsideEntryBlock(string ir)
    {
        var violations = new List<string>();
        var blockIndex = -1;
        foreach (var rawLine in ir.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("define ", StringComparison.Ordinal))
                blockIndex = -1;
            else if (System.Text.RegularExpressions.Regex.IsMatch(rawLine, @"^[\w.$-]+:"))
                blockIndex++;
            else if (line.Contains("= alloca ") && blockIndex > 0)
                violations.Add(line);
        }
        return violations;
    }

    [Fact]
    public void CastPairSemanticAnalysisDoesNotAdmitIsRejectedByTheBackend()
    {
        // Semantic analysis admits only the cast pairs the backend lowers. This forces an impossible one (i32 to bool)
        // into the model to check that the backend reports it instead of passing the operand through.
        var tokenizer = new Tokenizer(Log.Logger);
        var unit = new Parser(tokenizer.Tokenize("i32 main() { return cast<i32>(1); }"), Log.Logger).Parse();
        var model = new SemanticAnalyzer(Log.Logger).Analyze([unit]);
        var cast = (CastExpression)((ReturnStatement)unit.Functions[0].Body.Statements[0]).Value!;
        ((Dictionary<Expression, SemanticType>)model.ExpressionTypes)[cast] =
            SemanticType.FromPrimitive(PrimitiveType.Bool);

        var exception = Assert.Throws<InvalidOperationException>(() => new LlvmModuleGenerator().Generate("program", model));
        Assert.Contains("no lowering for cast from 'i32' to 'bool'", exception.Message);
    }

    [Fact]
    public void ArgumentCoercionWithNoLoweringIsReportedInsteadOfPassingTheValueThrough()
    {
        // Semantic analysis admits only the coercions the backend lowers. This forces an impossible one (bool to i32)
        // on an argument to check that the backend reports it instead of passing the value through.
        var tokenizer = new Tokenizer(Log.Logger);
        var unit = new Parser(tokenizer.Tokenize(
            "i32 f(i32 x) { return x; } i32 main() { i32 v = 1; return f(v); }"), Log.Logger).Parse();
        var model = new SemanticAnalyzer(Log.Logger).Analyze([unit]);
        var call = (CallExpression)((ReturnStatement)unit.Functions[1].Body.Statements[1]).Value!;
        var argument = call.Arguments[0];
        ((Dictionary<Expression, SemanticType>)model.ExpressionTypes)[argument] = SemanticType.FromPrimitive(PrimitiveType.Bool);

        var exception = Assert.Throws<InvalidOperationException>(() => new LlvmModuleGenerator().Generate("program", model));
        Assert.Contains("no coercion from 'bool' to 'i32'", exception.Message);
    }

    [Fact]
    public void AFailedGenerationDoesNotChangeTheNextOne()
    {
        const string valid = "class Box { i32 value; } i32 main() { Box box = new Box(); return 0; }";
        var expected = new LlvmModuleGenerator().Generate("program", Analyze(valid)).ToString();

        // The forced cast fails inside a try, while main owns a local, which leaves the generator partway through both.
        var tokenizer = new Tokenizer(Log.Logger);
        var unit = new Parser(tokenizer.Tokenize(
            "class Box { i32 value; } i32 main() { Box box = new Box(); try { return cast<i32>(1); } finally { } }"),
            Log.Logger).Parse();
        var broken = new SemanticAnalyzer(Log.Logger).Analyze([unit]);
        var tryStatement = (TryStatement)unit.Functions[0].Body.Statements[1];
        var cast = (CastExpression)((ReturnStatement)tryStatement.Body.Statements[0]).Value!;
        ((Dictionary<Expression, SemanticType>)broken.ExpressionTypes)[cast] = SemanticType.FromPrimitive(PrimitiveType.Bool);
        Assert.Throws<InvalidOperationException>(() => new LlvmModuleGenerator().Generate("program", broken));

        var actual = new LlvmModuleGenerator().Generate("program", Analyze(valid)).ToString();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ExternWithTheSameSignatureAsARuntimeFunctionSharesItsDeclaration()
    {
        var model = Analyze(
            """
            @extern("memcmp") extern i32 compare(i8* left, i8* right, u64 count);
            i32 main() { return 0; }
            """);

        using var generated = new LlvmModuleGenerator().Generate("program", model);
        var ir = generated.ToString();

        Assert.Single(ir.Split('\n'), line => line.StartsWith("declare ", StringComparison.Ordinal)
            && line.Contains("@memcmp(", StringComparison.Ordinal));
    }

    [Fact]
    public void ExternNamedLikeARuntimeFunctionWithADifferentSignatureIsRejectedNotRenamed()
    {
        var model = Analyze(
            """
            @extern("__oxygen_runtime_initialize") extern i32 initialize(i32 value);
            i32 main() { return 0; }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => new LlvmModuleGenerator().Generate("program", model));
        Assert.Contains("'__oxygen_runtime_initialize'", exception.Message);
    }

    [Fact]
    public void ExternNamedMainWithADifferentSignatureIsRejectedNotRenamed()
    {
        var model = Analyze(
            """
            @extern("main") extern i32 entry(i32 value);
            i32 main() { return 0; }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => new LlvmModuleGenerator().Generate("program", model));
        Assert.Contains("'main'", exception.Message);
    }

    [Fact]
    public void VerifierAcceptsAWellFormedModuleAndRejectsABlockWithoutATerminator()
    {
        using var context = LLVMContextRef.Create();
        using var builder = context.CreateBuilder();
        using var valid = context.CreateModuleWithName("valid");
        var validFunction = valid.AddFunction("f", LLVMTypeRef.CreateFunction(context.VoidType, [], false));
        builder.PositionAtEnd(validFunction.AppendBasicBlock("entry"));
        builder.BuildRetVoid();

        LlvmModuleVerifier.Verify(valid);

        using var broken = context.CreateModuleWithName("broken");
        broken.AddFunction("f", LLVMTypeRef.CreateFunction(context.VoidType, [], false))
            .AppendBasicBlock("entry");

        var exception = Assert.Throws<LlvmVerificationException>(() => LlvmModuleVerifier.Verify(broken));
        Assert.Contains("does not have terminator", exception.Message);
    }

    // The generator needs std.exception's NullReferenceException for checks on method calls and member access. Unit
    // tests do not load the standard library, so these tests declare the part of it that the checks use.
    private const string NullReferenceClasses =
        "class Exception { public string message; public Exception(string message) { this.message = message; } } " +
        "class RuntimeException : Exception { public RuntimeException(string message) : base(message) {} } " +
        "class NullReferenceException : RuntimeException { public NullReferenceException(string message) : base(message) {} } ";

    private static SemanticModel Analyze(string source)
    {
        var tokenizer = new Tokenizer(Log.Logger);
        var unit = new Parser(tokenizer.Tokenize(source), Log.Logger).Parse();
        return new SemanticAnalyzer(Log.Logger).Analyze([unit]);
    }

    // The declared part of std.exception is its own unit, so its classes have their qualified names, as the checks require.
    private static SemanticModel AnalyzeWithStdException(string source)
    {
        var tokenizer = new Tokenizer(Log.Logger);
        var standard = new Parser(tokenizer.Tokenize("module std.exception; " + NullReferenceClasses), Log.Logger).Parse();
        var unit = new Parser(tokenizer.Tokenize(source), Log.Logger).Parse();
        return new SemanticAnalyzer(Log.Logger).Analyze([standard, unit]);
    }

    private sealed class RecordingToolRunner : INativeToolRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Invocations { get; } = [];

        public void Run(string executable, IReadOnlyList<string> arguments, string? workingDirectory) =>
            Invocations.Add((executable, arguments));
    }
}
