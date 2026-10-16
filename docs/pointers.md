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

`void*` is an opaque pointer type. A typed pointer can be converted to or from
`void*` only with an explicit `cast<T*>(value)`, and a `void*` cannot be
dereferenced until it has been cast to a typed pointer.

`usize` and `isize` are pointer-sized integer types. Converting between a
pointer and either type requires an explicit cast:

```oxygen
usize address = cast<usize>(pointer);
i32* restored = cast<i32*>(address);
```

Numeric literal suffixes such as `1u64`, `1f32`, and `1usize` select the
literal type. Implicit numeric conversions are accepted when the compiler can
prove they are safe: lossless widening conversions and representable
compile-time constants are allowed. Other runtime numeric conversions require
an explicit cast.
