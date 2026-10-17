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
class. It contains the exception message and a `StackTrace` value. The trace
records Oxygen function names and source locations captured while the
exception propagates and can be formatted by the standard library.

Exception propagation is implemented by the runtime's handler frames. A
function that does not handle an exception propagates it to its caller. Class
destructors for locals leaving a scope are run during normal control flow and
while an exception unwinds that scope.

If an exception reaches the generated native entry point without a matching
handler, the runtime prints the exception type, message, and stack trace to
standard error, flushes the stream, and terminates with a non-zero exit status.
This is distinct from compiler diagnostics, which are reported before native
code is generated.
