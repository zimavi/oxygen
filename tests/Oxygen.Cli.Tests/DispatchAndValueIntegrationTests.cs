using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs whose behaviour depends on overload selection, virtual and interface dispatch, default field values
// and unsigned arithmetic, and checks the output of the native executable.
public sealed class DispatchAndValueIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public DispatchAndValueIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SkippableFact]
    public void OverloadsCallTheFunctionMatchingTheArgumentType()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            i32 add(i32 left) { return left + 1; }
            f64 add(f64 left) { return left; }

            i32 main() {
                io.print(add(41));
                io.print(cast<i64>(add(1.5)));
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("421", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void VirtualCallRunsTheDerivedOverride()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class Base {
                virtual i32 value() { return 1; }
            }

            class Child : Base {
                override i32 value() { return 2; }
            }

            i32 main() {
                Base item = new Child();
                io.print(item.value());
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("2", run.Output.Replace("\r\n", "\n"));
    }

    // A derived class with its own virtual slot and its own field: the base class's fields and slot must keep their positions when the
    // object is seen through the base class, and the derived class's slot and field must be where its own code expects them.
    [SkippableFact]
    public void DerivedClassKeepsTheBaseClassFieldsAndSlotsInPlace()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class Base {
                i32 first;
                virtual i32 value() { return first; }
            }

            class Derived : Base {
                i32 second;
                virtual i32 extra() { return second; }
                override i32 value() { return first + second; }
            }

            i32 main() {
                Derived derived = new Derived();
                derived.first = 3;
                derived.second = 4;
                Base item = derived;
                io.print(item.value());
                io.print(item.first);
                io.print(derived.extra());
                io.print(derived.second);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("7344", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void InterfaceCallRunsTheImplementingClassMethod()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            interface Numbered {
                i32 value();
            }

            class Item : Numbered {
                i32 value() { return 7; }
            }

            i32 main() {
                Numbered numbered = new Item();
                io.print(numbered.value());
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("7", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void PropertiesWithoutInitializersStartAsZero()
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(
            """
            import std.io;

            class Account {
                public i32 Balance { get; private set; }
            }

            i32 main() {
                Account account = new Account();
                io.print(account.Balance);
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("0", run.Output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void UnsignedValuesCompareAndDivideAsUnsigned()
    {
        NativeProgram.RequireNativeToolchain();

        // As an unsigned value, u64 max is larger than 10, and dividing it by 10 gives a value whose remainder by 3 is 1.
        // Read as a signed value it would be -1, which is less than 10 and divides to 0.
        var run = BuildAndRun(
            """
            import std.io;

            i32 main() {
                u64 big = 18446744073709551615;
                if (big > 10) {
                    io.print("gt");
                }
                io.print(cast<i64>(big / 10 % 3));
                return 0;
            }
            """);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("gt1", run.Output.Replace("\r\n", "\n"));
    }

    private (int ExitCode, string Output, string Error) BuildAndRun(string source) =>
        NativeProgram.BuildAndRun(_directory, source);
}
