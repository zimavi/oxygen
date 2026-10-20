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

The standard library exposes `std.strings.to_c_string` and
`std.strings.from_c_string` for C interop. `to_c_string` returns a new
NUL-terminated copy of the UTF-8 bytes as an `i8*`. The caller owns the copy and
frees it with `std.memory.free`. A string with an embedded zero byte has no C
string, so `to_c_string` returns `null` for it. Check the result before using it:

```oxygen
import std.io;
import std.memory;
import std.strings;

i8* copy = to_c_string(text);
if (copy != null) {
    io.write_stdout(from_c_string(copy));
    memory.free(copy);
}
```

`from_c_string` accepts an `i8*` and creates an Oxygen string view whose length
is measured up to the first NUL byte. The C pointer must remain valid for the
result's lifetime, and a null pointer is not a valid input. This conversion
does not perform IO.

## I/O

## Characters

A character literal is one ASCII character between single quotes, or one escape
sequence. It has type `u8`, and its value is that byte. The escapes are the
string escapes plus `\'`:

```oxygen
u8 letter = 'a';      // 97
u8 newline = '\n';    // 10
u8 quote = '\'';      // 39
```

A character literal holds exactly one ASCII character, so `''`, `'ab'`, and
`'é'` are rejected. Other bytes are written as numbers with the `u8` suffix,
such as `233u8`.

A `u8` is a number, so `print` and `println` write its value in decimal:
`io.print(97u8)` writes `97`. To write the byte as a character, use
`io.print_char` or `io.println_char`. Those functions write a single byte as is,
so a value of 128 or more is not a complete UTF-8 character on its own.

## I/O

The `std.io` module provides `write_stdout(string)`, `write_stderr(string)`,
and the generic `print<T : Printable>(T)` and `println<T : Printable>(T)`. The
first two functions call the runtime's `oxygen_write_stdout` and
`oxygen_write_stderr` primitives. They write the string's exact byte length,
including embedded zero bytes. `print_char` and `println_char` write one byte
through the runtime's `oxygen_write_stdout_byte` primitive. `println` appends a newline after the supplied value.

Printing a number builds its digits in local arrays, so it does not allocate.

Standard output is buffered. The runtime writes the buffer when the program
returns from `main`, before any message to standard error, and before a runtime
failure stops the program, so the two streams show the output in the order it
was written. A program that is stopped by a signal can lose buffered output. A
failed write to either stream stops the program with exit code 101; see
[Exceptions](exceptions.md).

`print` and `println` accept any type that implements `Printable`. Strings and
the primitive types implement it by a built-in rule; each one is written by a
`write` function in `std.io`, so formatting is ordinary Oxygen code:

- Integers are written in decimal, with a leading `-` when negative.
- `bool` is written as `true` or `false`.
- Floating-point values are written with six digits after the decimal point,
  rounded half up, with trailing zeros removed (`0.1 + 0.2` is `0.3`, `3.0` is
  `3`). NaN is written as `nan`, and infinities as `inf` or `-inf`. A finite
  value of 2^64 or more is written as its exact integer value, so `2^96` prints
  all 29 digits. A negative value keeps its sign even when it rounds to zero, and
  negative zero is written as `-0`, so `-0.0000001` prints `-0`. Positive zero is
  written as `0`.

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
