using Xunit;

namespace Oxygen.Cli.Tests;

// Builds programs whose objects the ownership pass frees or keeps, and checks how the native executable ends. The leak report is
// printed at exit when OXYGEN_LEAK_REPORT is set, so a freed program reports no allocation left.
public sealed class OwnershipIntegrationTests : IDisposable
{
    private const string NoLeaks = "Oxygen: 0 allocation(s) were not freed";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oxygenc-tests", Guid.NewGuid().ToString("N"));

    public OwnershipIntegrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    // Every object the program creates is released, so nothing is left allocated at exit.
    [SkippableTheory]
    [MemberData(nameof(FreedPrograms))]
    public void EveryObjectTheProgramCreatesIsFreed(string name, string source, int exitCode)
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(name, source);

        Assert.Equal(exitCode, run.ExitCode);
        Assert.Contains(NoLeaks, run.Error);
    }

    // The object outlives the scope of the local that names it, through a parameter, a field, an alias, or a method that stores `this`.
    // The owner must not free it while it is reachable, which the exit code shows. The leak report is not checked, because these
    // objects are kept on purpose and may be reported as not freed.
    [SkippableTheory]
    [MemberData(nameof(KeptPrograms))]
    public void AnObjectKeptElsewhereIsNotFreedWhileReachable(string name, string source, int exitCode)
    {
        NativeProgram.RequireNativeToolchain();

        var run = BuildAndRun(name, source);

        Assert.Equal(exitCode, run.ExitCode);
    }

    public static TheoryData<string, string, int> FreedPrograms => new()
    {
        { "discarded_iter", DiscardedIter, 0 },
        { "temp_argument", TempArgument, 0 },
        { "discarded_new", DiscardedNew, 0 },
        { "borrowed_local_argument", BorrowedLocalArgument, 0 },
        { "call_result_local", CallResultLocal, 0 },
        { "call_receiver_temporary", CallReceiverTemporary, 0 },
        { "comparison_only", ComparisonOnly, 1 },
        { "interface_local", InterfaceLocal, 0 },
        { "owned_interface_from_call", OwnedInterfaceFromCall, 0 },
        { "owned_interface_return", OwnedInterfaceReturn, 16 },
        { "returned_owned_local", ReturnedOwnedLocal, 7 },
        { "recursive_owned_return", RecursiveOwnedReturn, 3 },
        { "loop_owned_local", LoopOwnedLocal, 6 },
        { "break_out_of_loop_owned", BreakOutOfLoopOwned, 3 },
        { "delete_then_scope", DeleteThenScope, 2 },
        { "exception_unwinds_owned", ExceptionUnwindsOwned, 10 },
        { "list_for_in", ListForIn, 3 },
        { "for_in_break", ForInBreak, 0 },
        { "for_in_return", ForInReturn, 4 },
        { "manual_iter", ManualIter, 0 },
        { "manual_walk", ManualWalk, 7 },
        { "parameter_walk", ParameterWalk, 3 },
        { "this_walk", ThisWalk, 8 },
        { "this_returned_holder", ThisReturnedHolder, 4 },
        { "stored_object_outlives_its_owner_scope", StoredObjectOutlivesItsOwnerScope, 9 },
        { "overwrite_releases_the_old_value", OverwriteReleasesTheOldValue, 0 },
        { "returned_field_alias_is_retained", ReturnedFieldAliasIsRetained, 5 },
        { "self_assignment_keeps_the_object", SelfAssignmentKeepsTheObject, 4 },
        { "union_payload_is_released_with_its_holder", UnionPayloadIsReleasedWithItsHolder, 0 },
        { "list_elements_are_released_with_the_list", ListElementsAreReleasedWithTheList, 0 },
        { "derived_class_fields_are_released_with_it", DerivedClassFieldsAreReleasedWithIt, 0 },
        { "interface_field_is_released_with_its_holder", InterfaceFieldIsReleasedWithItsHolder, 4 },
    };

    public static TheoryData<string, string, int> KeptPrograms => new()
    {
        { "kept_by_parameter", KeptByParameter, 5 },
        { "kept_temporary_by_assignment", KeptTemporaryByAssignment, 6 },
        { "alias_copy", AliasCopy, 3 },
        { "returned_field_alias", ReturnedFieldAlias, 8 },
        { "this_kept_by_method", ThisKeptByMethod, 9 },
        { "interface_alias", InterfaceAlias, 9 },
        { "returned_walk", ReturnedWalk, 3 },
        { "parameter_kept_by_returned_iterator", ParameterKeptByReturnedIterator, 3 },
        { "temporary_receiver_kept", TemporaryReceiverKept, 4 },
        { "parameter_receiver_kept", ParameterReceiverKept, 4 },
    };

    private (int ExitCode, string Output, string Error) BuildAndRun(string name, string source)
    {
        var directory = Path.Combine(_directory, name);
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "main.oxy");
        var executable = Path.Combine(directory, "main.exe");
        File.WriteAllText(sourcePath, source);

        // Programs that import nothing are built without the standard library, as the rest of the native tests build them.
        var arguments = new List<string> { "build", sourcePath, "-o", executable, "-r", NativeProgram.RuntimeLibrary };
        if (!source.Contains("import std", StringComparison.Ordinal))
            arguments.Add("--no-std");
        var build = NativeProgram.RunProcess(NativeProgram.CompilerExecutable, arguments);
        Assert.True(build.ExitCode == 0, $"Build failed with exit code {build.ExitCode}:\n{build.Error}");

        return NativeProgram.RunProcess(executable, [], new Dictionary<string, string> { ["OXYGEN_LEAK_REPORT"] = "1" });
    }

    private const string ListForIn = """
        import std.collection;
        i32 main() {
            List<i32> items = new List<i32>();
            items.append(1);
            items.append(2);
            i32 total = 0;
            for (i32 value in items) { total = total + value; }
            return total;
        }
        """;

    private const string ForInBreak = """
        import std.collection;
        i32 main() {
            List<i32> items = new List<i32>();
            items.append(1);
            items.append(2);
            for (i32 value in items) { break; }
            return 0;
        }
        """;

    private const string ForInReturn = """
        import std.collection;
        i32 find(List<i32> items) {
            for (i32 value in items) { return value; }
            return 0;
        }
        i32 main() {
            List<i32> items = new List<i32>();
            items.append(4);
            return find(items);
        }
        """;

    // The iterator refers to the list, so the list is freed after the iterator, which is declared after it.
    private const string ManualIter = """
        import std.collection;
        i32 main() {
            List<i32> items = new List<i32>();
            items.append(3);
            var it = items.iter();
            return 0;
        }
        """;

    private const string ManualWalk = """
        import std.collection;
        i32 main() {
            List<i32> items = new List<i32>();
            items.append(3);
            items.append(4);
            var it = items.iter();
            i32 total = 0;
            total = total + collection.unwrap_or(it.next(), 0);
            total = total + collection.unwrap_or(it.next(), 0);
            total = total + collection.unwrap_or(it.next(), 0);
            return total;
        }
        """;

    private const string DiscardedIter = """
        import std.collection;
        i32 main() {
            List<i32> items = new List<i32>();
            items.append(3);
            items.iter();
            return 0;
        }
        """;

    // The returned iterator refers to a list that the callee would otherwise free, so the list is kept and the caller still reads it.
    private const string ReturnedWalk = """
        import std.collection;
        Iterator<i32> make() {
            List<i32> items = new List<i32>();
            items.append(3);
            items.append(4);
            Iterator<i32> it = items.iter();
            return it;
        }
        i32 main() { Iterator<i32> it = make(); return collection.unwrap_or(it.next(), 0); }
        """;

    // A list that a parameter names is iterated by a local that does not escape, so the caller's list is freed after the call.
    private const string ParameterWalk = """
        import std.collection;
        i32 total(List<i32> items) { var it = items.iter(); return collection.unwrap_or(it.next(), 0); }
        i32 main() { List<i32> items = new List<i32>(); items.append(3); return total(items); }
        """;

    private const string ParameterKeptByReturnedIterator = """
        import std.collection;
        Iterator<i32> keep(List<i32> items) { Iterator<i32> it = items.iter(); return it; }
        i32 main() { List<i32> items = new List<i32>(); items.append(3); Iterator<i32> it = keep(items); return collection.unwrap_or(it.next(), 0); }
        """;

    // `this.peek()` gives its holder a reference to this box, and the holder does not escape, so twice does not keep this box.
    private const string ThisWalk = """
        class Box {
            public i32 value = 4;
            public Peek peek() { return new Peek(this); }
            public i32 twice() { Peek p = this.peek(); return p.read() * 2; }
        }
        class Peek {
            public Box box;
            public Peek(Box box) { this.box = box; }
            public i32 read() { return box.value; }
        }
        i32 main() { Box b = new Box(); return b.twice(); }
        """;

    // The holder is returned, so keep does keep this box. In main the box is declared before the holder, so the holder is freed first,
    // and the box is freed after it; the program reads the box while both are alive.
    private const string ThisReturnedHolder = """
        class Box {
            public i32 value = 4;
            public Peek peek() { return new Peek(this); }
            public Peek keep() { Peek p = this.peek(); return p; }
        }
        class Peek {
            public Box box;
            public Peek(Box box) { this.box = box; }
            public i32 read() { return box.value; }
        }
        i32 main() { Box b = new Box(); Peek p = b.keep(); return p.read(); }
        """;

    // The receiver is a temporary, which the holder cannot outlive, so the box is kept.
    private const string TemporaryReceiverKept = """
        class Box {
            public i32 value = 4;
            public Peek peek() { return new Peek(this); }
            public Peek keep() { Peek p = this.peek(); return p; }
        }
        class Peek {
            public Box box;
            public Peek(Box box) { this.box = box; }
            public i32 read() { return box.value; }
        }
        i32 main() { Peek p = new Box().keep(); return p.read(); }
        """;

    // The parameter names the box that the caller passes, and the result of keep refers to it, so the caller's box is kept.
    private const string ParameterReceiverKept = """
        class Box {
            public i32 value = 4;
            public Peek peek() { return new Peek(this); }
            public Peek keep() { Peek p = this.peek(); return p; }
        }
        class Peek {
            public Box box;
            public Peek(Box box) { this.box = box; }
            public i32 read() { return box.value; }
        }
        Peek viaParam(Box b) { return b.keep(); }
        i32 main() { Box bx = new Box(); Peek p = viaParam(bx); return p.read(); }
        """;

    // A holder keeps the object that its owner's scope releases, so the object outlives the owner and is still read.
    private const string StoredObjectOutlivesItsOwnerScope = """
        class Box { public i32 value = 0; }
        class Holder { public Box item; }
        i32 main() {
            Holder keep = new Holder();
            {
                Box b = new Box();
                b.value = 9;
                keep.item = b;
            }
            return keep.item.value;
        }
        """;

    // The first object is released by the overwrite, so only the second one is still held.
    private const string OverwriteReleasesTheOldValue = """
        class Box { public i32 value = 0; }
        class Holder { public Box item; }
        i32 main() {
            Holder h = new Holder();
            h.item = new Box();
            h.item = new Box();
            return 0;
        }
        """;

    // The returned alias is retained by the return, so the caller's local releases its own count and the holder still has one.
    private const string ReturnedFieldAliasIsRetained = """
        class Box { public i32 value = 0; }
        class Holder { public Box item; }
        Box get(Holder h) { return h.item; }
        i32 main() {
            Holder h = new Holder();
            h.item = new Box();
            h.item.value = 5;
            Box x = get(h);
            return x.value;
        }
        """;

    // The new value is retained before the old one is released, so storing the object into itself keeps it alive.
    private const string SelfAssignmentKeepsTheObject = """
        class Box { public i32 value = 0; }
        class Holder { public Box item; }
        i32 main() {
            Holder h = new Holder();
            h.item = new Box();
            h.item.value = 4;
            h.item = h.item;
            return h.item.value;
        }
        """;

    // The holder's union releases the payload it holds when the holder is freed.
    private const string UnionPayloadIsReleasedWithItsHolder = """
        import std.collection;
        class Box { public i32 value = 3; }
        class Holder { public Option<Box> item; }
        i32 main() { Holder h = new Holder(); h.item = Option.Some(new Box()); return 0; }
        """;

    // The list releases each element it holds when it is freed.
    private const string ListElementsAreReleasedWithTheList = """
        import std.collection;
        class Box { public i32 value = 3; }
        i32 main() { List<Box> boxes = new List<Box>(); boxes.append(new Box()); boxes.append(new Box()); return 0; }
        """;

    // A derived object releases the fields of its base class and its own, and both destructors run as part of the release.
    private const string DerivedClassFieldsAreReleasedWithIt = """
        class Inner { public i32 v = 1; }
        class Base { public Inner a; }
        class Derived : Base { public Inner b; }
        i32 main() { Derived d = new Derived(); d.a = new Inner(); d.b = new Inner(); return 0; }
        """;

    // An interface-typed field is released through its object, with the object's own destructor.
    private const string InterfaceFieldIsReleasedWithItsHolder = """
        interface Shape { i32 area(); }
        class Square : Shape { public i32 side = 2; public i32 area() { return side * side; } }
        class Holder { public Shape s; }
        i32 main() { Holder h = new Holder(); h.s = new Square(); return h.s.area(); }
        """;

    private const string TempArgument = """
        class Box { public i32 value; }
        i32 use(Box box) { return box.value; }
        i32 main() { return use(new Box()); }
        """;

    private const string DiscardedNew = """
        class Box { public i32 value; }
        i32 main() { new Box(); return 0; }
        """;

    private const string BorrowedLocalArgument = """
        class Box { public i32 value; }
        i32 use(Box box) { return box.value; }
        i32 main() { Box box = new Box(); return use(box); }
        """;

    private const string CallResultLocal = """
        class Box { public i32 value; }
        Box make() { return new Box(); }
        i32 main() { Box box = make(); return box.value; }
        """;

    private const string CallReceiverTemporary = """
        class Box { public i32 value; }
        Box make() { return new Box(); }
        i32 main() { return make().value; }
        """;

    private const string ComparisonOnly = """
        class Box { public i32 value; }
        i32 main() { Box box = new Box(); if (box != null) { return 1; } return 0; }
        """;

    private const string InterfaceLocal = """
        interface Shape { i32 area(); }
        class Square : Shape { public i32 side; public i32 area() { return side * side; } }
        i32 main() { Shape shape = new Square(); return shape.area(); }
        """;

    private const string OwnedInterfaceFromCall = """
        interface Shape { i32 area(); }
        class Square : Shape { public i32 side; public i32 area() { return side * side; } }
        Shape make() { return new Square(); }
        i32 main() { Shape shape = make(); return shape.area(); }
        """;

    private const string OwnedInterfaceReturn = """
        interface Shape { i32 area(); }
        class Square : Shape { public i32 side = 4; public i32 area() { return side * side; } }
        Shape make() { Shape shape = new Square(); return shape; }
        i32 main() { Shape shape = make(); return shape.area(); }
        """;

    private const string ReturnedOwnedLocal = """
        class Box { public i32 value; }
        Box make(i32 value) { Box box = new Box(); box.value = value; return box; }
        i32 main() { Box box = make(7); return box.value; }
        """;

    private const string RecursiveOwnedReturn = """
        class Box { public i32 value; }
        Box chain(i32 depth) {
            Box box = new Box();
            box.value = depth;
            if (depth == 0) { return box; }
            Box deeper = chain(depth - 1);
            return box;
        }
        i32 main() { Box top = chain(3); return top.value; }
        """;

    private const string LoopOwnedLocal = """
        class Box { public i32 value; }
        i32 main() {
            i32 total = 0;
            i32 i = 0;
            while (i < 4) { Box box = new Box(); box.value = i; total = total + box.value; i = i + 1; }
            return total;
        }
        """;

    private const string BreakOutOfLoopOwned = """
        class Box { public i32 value; }
        i32 main() {
            i32 found = 0;
            i32 i = 0;
            while (i < 10) { Box box = new Box(); box.value = i; if (i == 3) { found = box.value; break; } i = i + 1; }
            return found;
        }
        """;

    private const string DeleteThenScope = """
        class Box { public i32 value = 2; }
        i32 main() { Box box = new Box(); i32 result = box.value; delete box; return result; }
        """;

    private const string ExceptionUnwindsOwned = """
        import std.exception;
        class Box { public i32 value = 1; }
        i32 fail() { Box box = new Box(); throw new Exception("boom"); }
        i32 main() { try { return fail(); } catch (Exception error) { return 10; } }
        """;

    private const string KeptByParameter = """
        class Box { public i32 value; }
        class Holder { public Box item; }
        void store(Holder holder, Box box) { holder.item = box; }
        i32 main() { Holder holder = new Holder(); Box box = new Box(); box.value = 5; store(holder, box); return holder.item.value; }
        """;

    private const string KeptTemporaryByAssignment = """
        class Box { public i32 value; }
        class Holder { public Box item; }
        i32 main() { Holder holder = new Holder(); holder.item = new Box(); holder.item.value = 6; return holder.item.value; }
        """;

    private const string AliasCopy = """
        class Box { public i32 value; }
        i32 main() { Box first = new Box(); Box second = first; second.value = 3; return first.value; }
        """;

    private const string ReturnedFieldAlias = """
        class Box { public i32 value; }
        class Holder { public Box item; }
        Box peek(Holder holder) { return holder.item; }
        i32 main() { Holder holder = new Holder(); holder.item = new Box(); holder.item.value = 8; Box seen = peek(holder); return seen.value; }
        """;

    private const string ThisKeptByMethod = """
        class Box { public i32 value; }
        class Node {
            public i32 value = 9;
            public void remember(Holder holder) { holder.item = this; }
        }
        class Holder { public Node item; }
        i32 main() { Holder holder = new Holder(); Node node = new Node(); node.remember(holder); return holder.item.value; }
        """;

    private const string InterfaceAlias = """
        interface Shape { i32 area(); }
        class Square : Shape { public i32 side = 3; public i32 area() { return side * side; } }
        i32 main() { Shape shape = new Square(); Shape other = shape; return other.area(); }
        """;
}
