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

Struct values use named literals. Every field must be initialized exactly once:

```oxygen
i32 main() {
    Point point = Point { x: 10, y: 20 };
    point.x = 11;
    return point.y;
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
