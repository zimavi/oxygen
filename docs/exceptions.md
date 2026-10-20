# Exceptions

Oxygen exceptions are class values. A program raises an exception with
`throw`, and handles exceptions with a `try` statement followed by one or more `catch` clauses and/or a `finally`
clause:

```oxygen
try {
    risky_operation();
} catch (FileException error) {
    print(error.message);
} catch {
    print("unexpected failure");
} finally {
    close_resources();
}
```

Typed catches match the thrown class or any class derived from it. A catch-all
clause matches every exception and must be last. A `finally` clause is optional,
but a `try` must have at least one handler or a `finally` clause. Catch
variables are scoped to their handler body. `finally` runs after normal
completion, before a return leaves the construct, and during exception
unwinding; if it throws or returns, that outcome replaces the pending one.

`throw;` inside a catch clause continues the unwinding of the exception that the
clause caught. It is the same object, with the trace captured at its original
throw, so no new exception is created and no new trace is captured. Outer
catches and `finally` clauses see the original exception. `throw;` is valid only
inside a catch clause.

The standard library provides `std.exception.Exception` as the root exception
class. Construct it with the user-facing `Exception(message)` constructor, or with
`Exception()` for an empty message; the runtime trace is not a constructor
argument. It contains the exception message
and a `StackTrace` value. The native runtime keeps a copy of every frame that is
on the trace stack at the throw site, with no limit on depth. Each frame contains
the Oxygen function, module/source name, line, and column.

`StackTrace` is a value class that refers to its exception, so creating an
exception allocates no trace object. `count` reads the number of frames, and
`frame_at(index)` returns a new `Frame`, which the caller owns. Frames are
numbered from zero, outermost first. `frame_at` stops the program when the index
is not less than `count`. Runtime plumbing is internal. The standard `Exception`
constructors create the trace, and a derived exception class reaches them through
its constructor's `base(...)` call, or through the implicit parameterless base
call.

The runtime keeps the trace of a caught exception until the catch body that
handles it completes, so the body can read `count` and the frames. After that,
`count` is zero. A catch body that leaves early, by `return`, `break`,
`continue` or a new `throw`, keeps its trace until the program ends. Each `try`
saves the depth of the trace stack, and each catch restores it, because the
functions that an exception left never popped their frames. The trace of an
uncaught exception is printed and released when it reaches the entry point.

Exception propagation is implemented by the runtime's handler frames. A
function that does not handle an exception propagates it to its caller. Class
destructors for locals leaving a scope are run during normal control flow and
while an exception unwinds that scope.

If an exception reaches the generated native entry point without a matching
handler, the runtime prints the exception type, message, and stack trace to
standard error, flushes the stream, and terminates with a non-zero exit status.

Runtime failures are different. Indexing out of bounds, integer division by zero,
an invalid shift or enum value, a failed allocation, a failed write to standard
output or error, and a stack trace frame out of range each print a message and end the program with exit code 101. A runtime
failure does not raise an exception that a catch can handle.

On Windows, a hardware fault is a runtime failure too: an access violation, a
divide error or a stack overflow. The runtime prints the exception code and the
address, and ends the program with exit code 101. No catch clause and no
`finally` block runs for it, even when the fault happens inside a call. POSIX
targets do not report hardware faults; the operating system ends the program
with the signal, as it does for any native program.

Dereferencing a null raw pointer is not defined yet. Without optimization, the
store or load faults and is reported as above. With optimization, the compiler
may treat the null access as undefined behaviour and emit a trap instead, which
Windows ends without the report. Tests that fault on purpose build with
`--optimization O0`.

This is distinct from compiler diagnostics, which are reported before native
code is generated. Compiler diagnostics are structured reports for invalid
source, malformed native metadata, unsupported backend inputs, and native
tool failures; see [Compiler diagnostics](diagnostics.md).

On Windows targets produced by LLVM/Clang, exception propagation uses native
SEH funclets (`catchswitch`, `catchpad`, and `cleanuppad`). The runtime bridges
Oxygen object pointers through a private SEH exception code while preserving the
version 1 Oxygen exception ABI. POSIX/Itanium targets continue to use the
existing landing-pad implementation. MinGW/Itanium Windows targets are not
covered by the native SEH lowering and must not be treated as equivalent to
MSVC-compatible Windows COFF targets.
