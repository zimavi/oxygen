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

Class storage is selected by the construction expression. `Type(arguments)`
constructs an instance in stack/local storage, while `new Type(arguments)`
constructs a heap instance. Ordinary classes remain reference values regardless
of where the instance was created.

Use `@value class Type` when class values must be passed to functions and
returned from functions by value. Value-class copies are shallow field-wise
copies. Value classes are currently restricted to classes without inheritance,
interfaces, or virtual/override methods.
