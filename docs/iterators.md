# Iteration

Fixed-size arrays support the `for-in` statement:

```oxygen
i32[3] values = [1, 2, 3];
i32 total = 0;
for (var value in values) {
    total += value;
}
```

The iteration variable is declared with `var`, is scoped to the loop, and is
mutable. Iteration proceeds from index zero through the last array element.
The collection expression is evaluated once before the loop starts.
Fixed arrays are an intrinsic exception to the method protocol below; they do
not require user-visible `iter()`/`next()` methods.

The compiler recognizes a user-defined protocol on class collections:
`collection.iter()` must return an iterator, either a class or an interface, and
the iterator must provide `next()` returning `Option<T>`. An interface iterator's
`next()` is called through the interface, as any interface method is. `Some` is tag zero and `None` is tag one.
The LLVM representation uses a tag plus a type-aware payload slot, so
primitive, pointer, class, and other currently supported payload types use
their native LLVM representation. `std.collection` provides the canonical
generic tagged union `Option<T>` (`Some(T)` and `None`).

`std.collection` also provides `List<T>`, a growable list. `append` doubles the
storage when it is full, and `get(index)` and `pop()` return an `Option<T>`,
since an index past the end and an empty list are expected outcomes. Its
`iter()` returns the interface `Iterator<T>`, and `List<T>` implements
`Iterable<T>`. The loop does not free the iterator that `iter()` returns, so
each loop over a list leaves one small iterator object allocated.
