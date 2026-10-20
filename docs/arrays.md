# Fixed-size arrays

Oxygen fixed-size arrays are inline value types written with a postfix type
suffix:

```oxygen
i32[3] values = { 10, 20, 30 };
```

The length is part of the type. Array literals must contain exactly as many
elements as the declared length. Where an array type is expected, such as a
declaration, assignment or return, the literal takes that element type, and each
element must convert to it implicitly: `i64[2] wide = { 1, 2 };` is valid, and
`u8[2] small = { 1, 300 };` is rejected. Without an expected array type, every
element must have the same type.
Array element types may be primitive types, pointers, strings, structs, or
other fixed-size arrays.

`{0}` as an array value sets every element to the zero value of the element
type, as it does for a struct. It is the only literal with one element that
fills an array of a different length:

```oxygen
i32[3] counts = {0};   // 0, 0, 0
i32[3] bad = {1};      // error: expects 3 elements, got 1
```

Indexing uses `array[index]` and can be used as either a value or an
assignment target:

```oxygen
values[1] = 42;
return values[1];
```

Constant indices are checked by the compiler. Dynamic indices are checked at
runtime; an invalid index reports an error and terminates the process through
the runtime bounds-failure primitive. `array.length` is a read-only member that
gives the array's fixed length as a `usize`, and does not allocate. It cannot be
assigned.

Arrays are not heap allocated, resized, or implicitly converted to pointers.
Passing or returning an array uses the array value type directly. Arrays must
be fully initialized before use.
## Iteration

Fixed-size arrays can be traversed with `for (var value in collection)`.
The loop variable is an element value and the collection is evaluated once.
See [iterators](iterators.md) for the current protocol boundary and
`std.collection.Option<T>`.
