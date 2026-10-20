# Enums

Enums are closed sets of named values with a fixed-width integer
representation. If the underlying type is omitted, it defaults to `i32`:

```oxygen
enum Status {
    Ready,
    Done
}
```

An explicit underlying type may still be selected:

```oxygen
enum Color : i32 {
    Red,
    Green = 4,
    Blue
}
```

Implicit member values start at zero and increment from the previous member,
so `Red` is `0` and `Blue` is `5`. Member names are qualified by the enum
name:

```oxygen
i32 main() {
    Color color = cast<Color>(4);
    return cast<i32>(color);
}
```

Enum types remain distinct from their underlying integer types. Conversions
must be explicit. A constant integer-to-enum conversion is checked at
compile time. A conversion of a runtime integer checks that the value is a
declared member and terminates through the runtime failure handler if it is
invalid. Enum-to-integer conversions produce the declared underlying type.

Member values must fit the underlying type, which includes implicit values. A
`u64` enum holds values up to 18446744073709551615, and `enum Small : u8 { A = 255, B }`
is an error, because `B` would be 256. Member names and values must be unique within an enum. Payload-bearing
variants and pattern matching are not part of the current enum model.

## Flags enums

An enum annotated with `@flags` holds bit flags. Its values combine with bitwise
operators:

```oxygen
@flags enum Access : i32 {
    Empty = 0,
    Read = 1,
    Write = 2,
    Exec = 4
}

i32 main() {
    Access a = Access.Read | Access.Write;
    if ((a & Access.Read) != 0) {
        return 1;
    }
    return 0;
}
```

- Each member is `0` or a single bit: a value with exactly one bit set. Any other
  value is a compile-time error (`OX0210`). Member `0` is allowed, for a name such
  as `Empty` or `None`.
- `|` combines two values of the same flags enum, `&` tests or intersects them, and
  `^` toggles bits. Each result has the enum type.
- `~` is the complement. It keeps only the bits that the members use, so
  `~Access.Read` is `Write | Exec` and names no bit outside the members.
- A flags value is compared with the integer literal `0` for equality, as in
  `(a & Access.Read) != 0`. The literal takes the enum's type. Any other integer
  comparison is an error; use the members instead.
- Bitwise operators on an enum that is not `@flags`, or applying `~` to a
  non-flags enum, are compile-time errors. Combining values of two different
  enums, or a flags value with an integer, is `OX0213`; an integer converts to
  the enum with `cast<E>(value)`, as in `Access.Read | cast<Access>(1)`.
- Other enum operations are unchanged. An enum without `@flags` keeps only its
  declared members and compares for equality.
- `@flags` is the only annotation an enum accepts. An unknown enum annotation is an
  error.

The underlying type is still a fixed-width integer, `i32` by default. Flags enums
do not change the representation, so `cast<i32>(a)` gives the bit pattern.
