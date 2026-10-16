# Enums

Enums are closed sets of named values with an explicit fixed-width integer
representation:

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
    Color color = (Color) 4;
    return (i32) color;
}
```

Enum types remain distinct from their underlying integer types. Conversions
must be explicit. A constant integer-to-enum conversion is checked at
compile time. A conversion of a runtime integer checks that the value is a
declared member and terminates through the runtime failure handler if it is
invalid. Enum-to-integer conversions produce the declared underlying type.

Member names and values must be unique within an enum. Payload-bearing
variants and pattern matching are not part of the current enum model.
