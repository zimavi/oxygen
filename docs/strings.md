# Immutable strings

Oxygen string literals and values are immutable UTF-8 byte sequences. A string
has an explicit byte length, so embedded zero bytes do not terminate the value.
The `string` type can be written explicitly or inferred with `var`:

```oxygen
string greeting = "hello";
var message = "hello, Oxygen";
const string name = "Oxygen";
```

String contents cannot be modified. `const` bindings also cannot be
reassigned, using the same rule as other Oxygen values.

Inside the Oxygen LLVM backend, a string is represented as `{ ptr, i64 }`: a
pointer to immutable UTF-8 bytes and the byte length. String literals are
stored in private module globals. This is an internal representation, not yet a
C ABI contract.

The standard library exposes `stdlib.strings.to_c_string` and
`stdlib.strings.from_c_string` for C interop. `to_c_string` copies the UTF-8
bytes into a newly allocated, NUL-terminated buffer and returns an `i8*`.
That buffer is owned by the runtime and there is intentionally no deallocator
yet; callers must treat it as temporary until the ownership API is defined.
The conversion rejects embedded zero bytes by returning a null pointer.

`from_c_string` accepts an `i8*` and creates an Oxygen string view whose length
is measured up to the first NUL byte. The C pointer must remain valid for the
result's lifetime, and a null pointer is not a valid input. This conversion
does not perform IO.

## I/O

The `stdlib.io` module provides `write_stdout(string)`,
`write_stderr(string)`, `print(string)`, and `println(string)`. The first two
functions call the runtime's `oxygen_write_stdout` and
`oxygen_write_stderr` primitives. They write the string's exact byte length,
including embedded zero bytes; `println` appends a newline after the supplied
value.
