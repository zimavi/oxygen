# Module-level variables

A variable declared outside every function and type is a module-level variable.
It lives for the whole program, and its name is visible to the functions of its
module:

```oxygen
i32 counter = 10;
const i32 step = 5;
f64 scale = 2.5;
bool enabled = true;
i32 zeroed;

void bump() {
    counter += step;
}
```

## Rules

- **Type.** A global has an explicit numeric or `bool` type. `var` is not allowed
  at module level yet, and other types (strings, pointers, arrays, classes) are
  rejected with a diagnostic.
- **Initializer.** The initializer must be a constant: a `bool` literal, a
  floating-point literal with an optional sign, or an integer constant expression
  such as `2 * 1024`. A name, a call or any other expression is rejected.
  The constant must convert to the declared type under the usual implicit rules,
  so `u8 small = 300;` and `f32 x = 0.5;` are rejected.
- **No initializer.** A global without an initializer starts as zero (`0`,
  `0.0` or `false`). This is defined behaviour, unlike an uninitialized local.
- **`const`.** A `const` global cannot be assigned, and it must have an initializer.
- **Mutability.** A non-`const` global can be read and assigned by any function of
  its module, including compound assignments and `++`/`--`.
- **Scope.** A local, a parameter or, in a method, a field with the same name
  shadows the global within its scope. A module cannot declare two globals with
  the same name.
- **Visibility.** A global is private to its module. Globals have no visibility
  keyword yet, so access from an importing module is not supported. Type names
  are module-scoped and import-aware (see [modules](modules.md)); globals are not
  part of that yet.

## Lifetime and storage

Each global is an internal LLVM global. Its name is `module.name`, so modules
can declare the same name. A global's storage is static: it is not allocated,
freed or tracked by the memory model, and taking its address with `&` is valid.
