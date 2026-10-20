# Structs

Oxygen structs are data-only aggregate types. They contain fields and cannot
declare methods or functions.

```oxygen
struct Point {
    i32 x;
    i32 y;
}

struct Segment {
    Point start;
    Point end;
}
```

A struct value is built with a named literal, which must set every field exactly
once, or with `{0}`, which zero-fills every field:

```oxygen
i32 main() {
    Point point = Point { x: 10, y: 20 };
    point.x = 11;
    return point.y;
}
```

`{0}` sets each field, nested structs included, to the zero value of its type.
It is the only brace literal that a struct accepts:

```oxygen
Point origin = {0};
```

A local struct without an initializer can be assigned one field at a time. Each
field is unassigned until it is written, and a whole struct is unassigned until
every field has been. Reading an unassigned field is a compile error, and so is
reading the whole struct while any of its fields is unassigned. A whole-value
assignment assigns every field:

```oxygen
i32 main() {
    Point p;
    p.x = 1;
    p.y = 2;          // every field is assigned now
    Point q = {0};
    return p.x + q.y;
}
```

Fields can contain primitive types, strings, pointers, or other structs.
Whole-value assignment and field reads/writes are supported. Struct equality
and other comparisons are not supported.

Structs use natural LLVM layout by default. `@packed` removes inter-field
padding. `@align(<power-of-two>)` accepts an alignment between 1 and 4096 bytes; its
layout effect is not emitted yet, so it currently has no effect on generated code:

```oxygen
@packed struct PackedPoint {
    i8 tag;
    i32 value;
}
```

By-value recursive layouts are rejected. Recursive data structures must use a
pointer field instead.
