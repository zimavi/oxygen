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

The keyword `null` is the null pointer value. It converts to any pointer type. The integer
literal `0` is not a null pointer. Dereferencing a null literal is rejected, and so is dereferencing a
pointer variable that is null on every path to the dereference. A pointer that is null on only some paths
is accepted, because the compiler does not track it further. A loop or a `try` statement forgets the
pointers it assigns, since they may hold a value on the path that leads back into it. Other
validity and lifetime rules are the programmer's responsibility; pointers to
local variables are allowed, but must not outlive the storage they reference.

Pointer arithmetic supports `pointer + integer` and `pointer - integer`.
The integer offset is scaled by the size of the pointee type. Prefix and
postfix `++` and `--`, and compound `+=` and `-=`, apply the same scaled
one-element or integer-offset movement. Prefix operators produce the updated
pointer; postfix operators produce the previous pointer value. Integer-plus-
pointer, pointer-pointer arithmetic, multiplication, division, bitwise
operations, shifts, and pointer comparisons other than `==` and `!=` are not
supported.

Typed pointers can also be indexed with `pointer[index]`. This applies the
scaled index offset and dereferences the resulting address:

```oxygen
i32 value = pointer[2];
pointer[2] = value + 1;
```

Pointer indexing has no bounds metadata or runtime bounds check. The pointed-to
type must be typed rather than `void`; validity and lifetime remain the
programmer's responsibility.

Integers support prefix and postfix `++` and `--`, compound arithmetic and
bitwise assignment, and the binary bitwise (`&`, `|`, `^`) and shift (`<<`,
`>>`) operators. Increment and decrement require mutable addressable values.

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
