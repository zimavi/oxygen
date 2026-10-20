using Xunit;

namespace Oxygen.Cli.Tests;

// Compiles programs that compare class and interface references, runs them, and checks the result. A reference compares by
// identity: two references are equal when they name the same object, and a null reference is equal only to null.
public sealed class ReferenceComparisonIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-refs", Guid.NewGuid().ToString("N"));

    public ReferenceComparisonIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [SkippableFact]
    public void ClassReferencesCompareByIdentityAndWithNull()
    {
        NativeProgram.RequireNativeToolchain();
        var run = NativeProgram.BuildAndRun(_directory,
            """
            class Node {
                i32 v;

                Node(i32 v) {
                    this.v = v;
                }
            }

            i32 main() {
                Node a = new Node(1);
                Node b = new Node(1);
                Node n = null;
                i32 r = 0;
                if (a == a) {
                    r = r + 1;
                }
                if (a != b) {
                    r = r + 2;
                }
                if (n == null) {
                    r = r + 4;
                }
                if (a != null) {
                    r = r + 8;
                }
                return r;
            }
            """);

        Assert.Equal(15, run.ExitCode);
    }

    [SkippableFact]
    public void InterfaceReferencesCompareWithTheObjectTheyName()
    {
        NativeProgram.RequireNativeToolchain();
        var run = NativeProgram.BuildAndRun(_directory,
            """
            interface Named {
                i32 id();
            }

            class Node : Named {
                i32 v;

                Node(i32 v) {
                    this.v = v;
                }

                i32 id() {
                    return v;
                }
            }

            i32 main() {
                Node a = new Node(1);
                Named x = a;
                Named y = null;
                i32 r = 0;
                if (x == a) {
                    r = r + 1;
                }
                if (y == null) {
                    r = r + 2;
                }
                if (x != null) {
                    r = r + 4;
                }
                return r;
            }
            """);

        Assert.Equal(7, run.ExitCode);
    }
}
