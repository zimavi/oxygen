using Xunit;

namespace Oxygen.Cli.Tests;

// Compiles generic code whose type parameters are constrained by an interface, runs it, and checks the result. The body of a
// generic is checked once, with its type parameter opaque, so it calls only the members of its constraints.
public sealed class GenericConstraintIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-constraints", Guid.NewGuid().ToString("N"));

    public GenericConstraintIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [SkippableFact]
    public void ConstrainedGenericsCallTheirInterfaceAndConvertToIt()
    {
        NativeProgram.RequireNativeToolchain();
        var run = NativeProgram.BuildAndRun(_directory,
            """
            interface Shape {
                i32 area();
            }

            class Square : Shape {
                i32 side;

                Square(i32 side) {
                    this.side = side;
                }

                public i32 area() {
                    return side * side;
                }
            }

            class Rect : Shape {
                i32 w;
                i32 h;

                Rect(i32 w, i32 h) {
                    this.w = w;
                    this.h = h;
                }

                public i32 area() {
                    return w * h;
                }
            }

            i32 total<T : Shape>(T value) {
                Shape shape = value;
                return value.area() + shape.area();
            }

            class Pair<T : Shape> {
                T first;

                Pair(T first) {
                    this.first = first;
                }

                i32 doubled() {
                    return first.area() * 2;
                }
            }

            i32 main() {
                i32 a = total(new Square(3));
                Pair<Rect> pair = new Pair<Rect>(new Rect(2, 5));
                return a + pair.doubled();
            }
            """);

        // total(Square(3)) is 9 + 9 = 18, and the pair doubles the 10 of Rect(2, 5): 18 + 20 = 38.
        Assert.Equal(38, run.ExitCode);
    }
}
