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
  part of that yet. The one exception is a generic declaration of the module that
  uses a global; see [across a library boundary](#across-a-library-boundary).

## Lifetime and storage

Each global is an internal LLVM global, except a global of a library module, which
the library exports (see [across a library boundary](#across-a-library-boundary)).
Its name is `module.name`, so modules can declare the same name. A global's storage is static: it is not allocated,
freed or tracked by the memory model, and taking its address with `&` is valid.

## Across a library boundary

A generic function or class is instantiated where it is used, so it can be
instantiated in a program that imports its module from a library. The body of a
generic declaration belongs to its module, so a name in it that refers to a
module-level variable refers to that variable, as it does in the module's
non-generic code. Privacy is a rule about names: the importing program's own
code still cannot name the variable.

- **One variable.** The variable has one storage for the whole program. The
  library that declares the module defines it and exports it, and a program that
  instantiates the generic declaration reads and writes that same storage. So a
  change made by the library's code is seen by the program, and the other way
  round.
- **What a library carries.** The generic declarations of a module carry the
  module's globals with them, with their types, `const` and constant initializers.
  A program declares them without their initializers, and their initial values
  are those the library defines.
- **Names.** The globals of an imported module are in that module's scope only.
  The importing program's functions do not see them, and an unqualified name in
  the program that matches one is an undefined name.
- **Older libraries.** A library built before this rule has no globals in its
  metadata. A generic declaration of it that uses a global is reported as before,
  and the library must be rebuilt.
