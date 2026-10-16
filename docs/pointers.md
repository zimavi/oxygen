# Pointers

Oxygen supports raw, typed pointers using a C-like `T*` syntax:

```oxygen
i32 main() {
    i32 value = 41;
    i32* pointer = &value;
    *pointer = 42;
    return *pointer;
}
```

`&` takes the address of an addressable variable or dereferenced pointer.
`*` dereferences a pointer and can be used both as an expression and as an
assignment target. Pointer types are exact: implicit conversions between
pointee types are not performed.

The integer literal `0` is accepted as a null pointer value when a pointer is
expected. Dereferencing a compile-time-known null literal is rejected. Other
validity and lifetime rules are the programmer's responsibility; pointers to
local variables are allowed, but must not outlive the storage they reference.

Pointer arithmetic supports `pointer + integer` and `pointer - integer`.
The integer offset is scaled by the size of the pointee type. Integer-plus-
pointer, pointer-pointer arithmetic, and pointer comparisons other than
`==` and `!=` are not supported.

Pointers are lowered as native LLVM pointers. They are not managed, reference
counted, or garbage-collected.
