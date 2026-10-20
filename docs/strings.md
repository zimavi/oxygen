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

A string has two read-only members and an element type:

- `text.length` is the byte length, as a `usize`.
- `text[i]` is the byte at index `i`, as a `u8`. The index can be any integer
  type. It is checked against the length at run time: an index outside
  `0 .. length` stops the program through the bounds-failure primitive, and a
  negative index is outside the range.
- A string is immutable, so `text[i] = x` and `text.length = x` are rejected at
  compile time.
- Two strings compare with `==` and `!=`, by their bytes. Ordering operators are
  not defined on strings.

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

The `stdlib.io` module provides `write_stdout(string)`, `write_stderr(string)`,
and the generic `print<T : Printable>(T)` and `println<T : Printable>(T)`. The
first two functions call the runtime's `oxygen_write_stdout` and
`oxygen_write_stderr` primitives, which are the only output primitives in the
runtime. They write the string's exact byte length, including embedded zero
bytes. `println` appends a newline after the supplied value.

`print` and `println` accept any type that implements `Printable`. Strings and
the primitive types implement it by a built-in rule; each one is written by a
`write` function in `stdlib.io`, so formatting is ordinary Oxygen code:

- Integers are written in decimal, with a leading `-` when negative.
- `bool` is written as `true` or `false`.
- Floating-point values are written with six digits after the decimal point,
  rounded half up, with trailing zeros removed (`0.1 + 0.2` is `0.3`, `3.0` is
  `3`). NaN is written as `nan`, and infinities as `inf` or `-inf`. A finite
  value of 2^64 or more is written as its exact integer value, so `2^96` prints
  all 29 digits.

A class prints by implementing `Printable` with a `void write()` method:

```oxygen
import std.io;

class Point : Printable {
    i32 x;
    i32 y;
    Point(i32 x, i32 y) { this.x = x; this.y = y; }
    void write() {
        io.print(x);
        io.print(",");
        io.print(y);
    }
}

i32 main() {
    io.println(new Point(3, -4)); // 3,-4
    return 0;
}
```
