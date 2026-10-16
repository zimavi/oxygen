# Memory allocation

The standard library exposes generic allocation helpers:

```oxygen
import stdlib.memory;

i32 main() {
    i32* values = memory.alloc<i32>(4);
    memory.free<i32>(values);
    return 0;
}
```

`alloc<T>(count)` allocates `count * sizeof<T>()` bytes and returns `T*`.
`free<T>(pointer)` releases the allocation through the runtime. Allocation
does not initialize storage, and deallocation does not run type-specific
destructors.

`sizeof<T>()` and `sizeof(value)` are compile-time expressions that produce a
`u64` byte count. `void` and unspecialized generic types are not sized.
Ownership is manual; a pointer must be freed at most once and must not be used
after deallocation.
