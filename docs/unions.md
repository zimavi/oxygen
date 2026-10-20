# Tagged unions

Oxygen supports tagged unions with named cases. A case may carry one `i32`
payload or no payload:

```oxygen
union Result {
    Ok(i32);
    Error(i32);
    Empty;
}

i32 main() {
    Result value = Result.Ok(42);
    switch (value) {
        case Ok(number): return number;
        case Error(code): return code;
        case Empty: return 0;
    }
}
```

Construction uses `Union.Case(...)` for payload cases and `Union.Case()` for
empty cases. Switches must cover every declared case exactly once. Payload
bindings are scoped to their case body.

The LLVM representation is a numeric case tag followed by one payload slot for
each case that carries a payload, in declaration order. A slot has the type of
its case, with the union's type arguments substituted, so `Result<T, E>` holds
a `T` and an `E` in separate slots. Empty cases leave their slots zeroed.

A non-generic union's payloads are still restricted to `i32`, and the compiler
reports other payload types. Generic instances take any payload type that their
type arguments give.

`std.collection` provides helpers for its two generic unions. For `Option<T>`:
`is_some`, `is_none` and `unwrap_or`. For `Result<T, E>`: `is_ok`, `is_err` and
`unwrap_or`. Each takes the union as its first argument, as in
`collection.is_some(value)`. `unwrap` is not provided yet, because it needs a way
to stop with a value of type `T` on the failure path.
