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

The standard library provides `std.exception.Exception` as the root exception
class. Construct it with the user-facing `Exception(message)` constructor, or with
`Exception()` for an empty message; the runtime trace is not a constructor
argument. It contains the exception message
and a `StackTrace` value. The native
runtime owns a bounded, address-stable trace copy captured at the throw site;
each frame contains the Oxygen function, module/source name, line, and column.
The runtime-backed `StackTrace` exposes the count and `frame_at(index)`
constructs a `Frame` through bounds-checked accessors. Runtime plumbing is
internal. The standard `Exception` constructors create the trace, and a derived
exception class reaches them through its constructor's `base(...)` call, or
through the implicit parameterless base call. The runtime owns the frame storage
until the matching catch or unhandled-exception path ends.

Exception propagation is implemented by the runtime's handler frames. A
function that does not handle an exception propagates it to its caller. Class
destructors for locals leaving a scope are run during normal control flow and
while an exception unwinds that scope.

If an exception reaches the generated native entry point without a matching
handler, the runtime prints the exception type, message, and stack trace to
standard error, flushes the stream, and terminates with a non-zero exit status.
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
