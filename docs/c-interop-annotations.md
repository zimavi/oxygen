# C interop annotations

Oxygen has a small, declarative annotation vocabulary for recording C ABI
intent while the foreign-call and struct-layout implementations are still
being developed:

```oxygen
@extern("puts") extern i32 puts(i32 value);

i32 main() {
    return puts(0);
}
```

Annotations use `@name` or `@name(arguments)` and are attached to the
following function declaration. The compiler currently recognizes:

- `@extern("native_name")` — records the native symbol name for an external
  declaration.
- `@calling_convention(c)` or `@calling_convention("c")` — records the C
  calling convention.
- `@packed` and `@align(bytes)` — apply to struct declarations (see `structs.md`).

An external declaration uses `extern` in place of a function body and may be
called like an ordinary function. Primitive arguments and return values are
lowered directly using the current LLVM primitive mappings. The external
symbol is emitted with its declared C name rather than an Oxygen-mangled name.
The declaration itself does not generate a function body.

The initial implementation does not parse C headers. String marshaling is
provided by `std.strings`: `to_c_string(string)` returns a new NUL-terminated
`i8*` that the caller owns and frees with `std.memory.free`, or `null` for a
string with an embedded zero byte (see [strings](strings.md)). `from_c_string(i8*)` returns an Oxygen string view
whose length ends at the first NUL. Null pointers are invalid inputs to the
reverse conversion.

The string ABI is one rule for every external function. Each `string` parameter
is passed as a pointer to an `oxygen_string`, which the C side declares as
`const oxygen_string *`. A `string` result is returned through a trailing
out-pointer, `oxygen_string *`, which the C side fills in. Strings are never
passed by value, so the call does not depend on how each platform classifies a
two-word struct. Structs, variadic functions, and platform-specific ABI details
remain unsupported until their Oxygen representations are defined.

`@packed` and `@align` are parsed and validated for struct declarations. `@align`
accepts power-of-two byte values from 1 through 4096. The layout effect of `@align`
(alignment of struct storage) is not emitted yet; only `@packed` changes the layout today.
The eventual implementation must define target layout, alignment validity,
marshaling, ownership, and the distinction between Oxygen-mangled symbols and
external ABI symbols before emitting foreign calls.
