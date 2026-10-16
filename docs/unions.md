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

The current implementation uses a two-`i32` LLVM aggregate: a numeric case
tag followed by an `i32` payload slot. Consequently, union payloads are
currently restricted to `i32`; empty cases leave the payload slot zeroed.
