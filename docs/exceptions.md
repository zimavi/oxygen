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

A caught exception is released when the catch that handles it is finished with it.
The catch frees the exception object, after running its destructor, when the
binding of the catch is not kept elsewhere. The binding is kept elsewhere when the
catch body uses it other than to read a member (`error.message`) or to call a
method on it: assigning it, returning it, passing it to a function, storing it in
a field or rethrowing it with `throw error;`. A kept exception is not freed by
that catch; it is left to the place that holds it, as an escaping object is.
Releasing the exception clears the runtime's references to it, releases its trace,
and frees the text of a runtime failure.

The release happens when the catch body completes normally, and also on each exit
of the body by `return`, `break` or `continue`. An exception that leaves the catch
body by a new `throw` is not released, so it is leaked. Each `try` saves the depth
of the trace stack, and each catch restores it, because the functions that an
exception left never popped their frames. The trace of an uncaught exception is
printed and released when it reaches the entry point.

A stack object (`T(...)`) cannot be thrown, since the exception would outlive the
function that built it.

Exception propagation is implemented by the runtime's handler frames. A
function that does not handle an exception propagates it to its caller. Class
destructors for locals leaving a scope are run during normal control flow and
while an exception unwinds that scope.

If an exception reaches the generated native entry point without a matching
handler, the runtime prints the exception type, message, and stack trace to
standard error, flushes the stream, and terminates with a non-zero exit status.

Some failures are raised as exceptions that a catch can handle. These are detected
by the compiled code, and each one throws an instance of its class:

| Failure | Class |
| --- | --- |
| Indexing an array or a string out of bounds | `std.exception.IndexOutOfRangeException` |
| Integer division by zero | `std.exception.DivideByZeroException` |
| Signed division that overflows (the minimum value divided by -1) | `std.exception.RuntimeException` |
| A shift count out of range for the shifted type | `std.exception.RuntimeException` |
| An enum value that is not one of the enum's cases | `std.exception.RuntimeException` |

The message describes the failure and gives its values: the index and the length of
an array or a string, the shift count, or the enum value. An enum value is shown as
an unsigned number. The exception captures the stack
trace where the failure happened, as a `throw` does. A build without
`std.exception` (`--no-std`) has no class to throw, so these failures end the
program with exit code 101, as they always did.

Other runtime failures end the program with exit code 101 and do not raise an
exception that a catch can handle: a failed allocation, a failed write to standard
output or error, and a stack trace frame out of range. Out of memory cannot build
an exception, and a failed write has nowhere to report one.

An uncaught exception, of either kind, is reported as described above, and ends the
program with exit code 101, as the other runtime failures do.

On Windows, a hardware fault is a runtime failure too: an access violation, a
divide error or a stack overflow. The runtime prints the exception code and the
address, and ends the program with exit code 101. No catch clause and no
`finally` block runs for it, even when the fault happens inside a call. POSIX
targets do not report hardware faults; the operating system ends the program
with the signal, as it does for any native program.

Dereferencing a null raw pointer is a hardware fault. Every generated function
allows an access through address zero (LLVM's `null_pointer_is_valid`), so an
optimized build keeps the access and the fault is reported as above, instead of
treating the access as undefined behaviour. A stack overflow is reported the same
way.

## The `@noreturn` annotation

`@noreturn` on a function or method promises that it never returns to its caller. Every path of its
body must end in a `throw`, in a call to another `@noreturn` function, or in a loop that never ends.
The compiler checks this the way it checks that a non-void function returns, so such a function needs
no `return` statement. A `return` in it is an error, and a body that can reach its end is an error.

A statement that calls a `@noreturn` function ends the path, so the statements after it are
unreachable and get the same warning as code after a `break`. The emitted function carries LLVM's
`noreturn` attribute, and the call is followed by an `unreachable` terminator, so the optimizer may
drop the code after it.

`@noreturn` and `@nothrow` are independent. A `@noreturn` function may throw: a throw leaves the
function and does not return to the caller, so its caller can catch the exception. A function that
is both may only abort. A C function declared with `@extern` can be `@noreturn` too, for example one
that ends the program. The standard library's `std.process.abort()` is one: it ends the program with exit
code 101, and the message, if any, is written by the caller first.

## Without exceptions

`--no-exceptions` is rejected with an error, since the feature is work in progress. The compiler
code for it exists, and it is planned as follows: a throw prints the exception as an uncaught one
and ends the program with exit code 101, without unwinding; a `try` is its body, with its `finally`
clause running on every way out; a `catch` clause is type-checked, is not emitted, and gets a
warning that it never runs; and a runtime failure ends the program with its message. The runtime is
told the mode at startup, through the flags of `__oxygen_runtime_initialize`.

## The `@nothrow` annotation

`@nothrow` on a function or method promises that it never throws. The compiler checks the promise
at compile time, and the code it emits for such a function is smaller, as described below.

The body of a `@nothrow` function may not:

- `throw` (including `throw;`);
- call a function that may throw. A call is allowed when the callee is `@nothrow`, or is a C function
  declared with `@extern`, since C does not throw Oxygen exceptions;
- call through a function pointer, or call a function that the compiler cannot resolve (a union case, a
  construction, or a call of a function value), since its target is not known;
- create an object whose construction may throw: a constructor that is not `@nothrow`, or the constructor
  of a base class that is not `@nothrow`. A class without declared constructors is constructed by a
  synthesized one, which runs its field initializers, so that is allowed only when every initializer is a literal;
- read or write a property, since its accessor may throw;
- `delete` an object, or use `for`-in, since destructors and iterator methods may throw;
- declare a local whose class has a destructor, since the destructor runs at the end of the scope.

A constructor may be `@nothrow`, and the same rules apply to it. Its field initializers are checked as
expressions, and its body as a function. It may not chain to another constructor with `this(...)` or
`base(...)`, and it may not run a base class's constructor that may throw.

A dispatched call (a virtual method or an interface method) is allowed only when the method it
names is `@nothrow`. An implementation of a `@nothrow` interface method, and an override of a
`@nothrow` method, must be `@nothrow` too. A `try` statement inside a `@nothrow` function is a
warning, since nothing in the function can throw to be caught.

A runtime failure inside a `@nothrow` function stops the program with exit code 101, as it does without
exceptions. It is not thrown, so a catch in the caller cannot see it.

A `@nothrow` function is emitted as plain calls. Nothing in its body can throw, so it sets up no
unwinding (no personality, no cleanup of its locals and no landing pads), and a `try` statement in it
is lowered as a plain block with its `finally` clause. It also has no trace frame, since no exception
that is reported can have it on its trace. The one exception is a C function that calls back into an
Oxygen function that throws: that frame is then missing from the trace, as the C frames are.

Limits of the check, which is conservative: a `@nothrow` annotation is not recorded in library
metadata, so a function of a library is treated as one that may throw, even if it is marked
`@nothrow` in its own module. A temporary object whose class has a destructor is not checked: only the
locals of such a class are.

This is distinct from compiler diagnostics, which are reported before native
code is generated. Compiler diagnostics are structured reports for invalid
source, malformed native metadata, unsupported backend inputs, and native
tool failures; see [Compiler diagnostics](diagnostics.md).

For the `x86_64-pc-windows-msvc` target, exception propagation uses native SEH
funclets (`catchswitch`, `catchpad`, and `cleanuppad`). The runtime bridges
Oxygen object pointers through a private SEH exception code while preserving the
version 1 Oxygen exception ABI. The `x86_64-unknown-linux-gnu` target uses the
existing landing-pad implementation. The exception model follows from the target
triple, which `--target` sets. Other triples, including MinGW Windows, are not
supported targets.
