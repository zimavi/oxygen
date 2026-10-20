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

## Stack objects

`T(...)` builds an object of a reference class on the stack of the current
function, and `new T(...)` builds it on the heap. A stack object is destroyed when
its function returns or its block ends, so it must not outlive that scope. The
compiler rejects a stack object that is:

- returned, directly or through a local that holds it;
- stored in a field;
- assigned to a variable of an outer scope.

Use `new T(...)` for an object that must outlive its scope. `T(...)` names a
class when a class has that name. When no class has the name but a function does,
`T(...)` is a call of that function, whatever the capitalization.
