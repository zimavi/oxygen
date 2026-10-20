# Oxygen modules and imports

An Oxygen source file can declare a nested module:

```oxygen
public module math;

public i32 add(i32 left, i32 right) {
    return left + right;
}
```

Before semantic analysis, the CLI scans the source directories recursively for
`.oxy` files and resolves every declared module. File names do not need to
match module names, and multiple files may declare the same module; their
functions are combined into one module. Imports are checked against the
combined discovered module set before name lookup.

Nested modules are independent modules. For example, `math.float` can provide
floating-point operations:

```oxygen
public module math.float;

public f32 pow(f32 value, f32 exponent) {
    return value;
}
```

Functions declared directly by an imported module may be used without a
prefix. Descendant modules require only the portion of the module path below
the imported module. An import also makes descendants available through the
imported module's child segment:

```oxygen
import math;

i32 main() {
    var sum = add(10, 32);       // math.add
    return float.pow(20.4, 2.4); // math.float.pow
}
```

The fully qualified form remains valid, and the module-leaf form is accepted
for compatibility (`math.add` and `float.pow` above). Importing a nested
module directly exposes its functions in the same way:

```oxygen
import math.float;

i32 main() {
    return pow(20.4, 2.4); // math.float.pow
}
```

An import may define an explicit source-level alias. This is useful when two
imports expose the same unqualified function name:

```oxygen
import graphics.color as colors;
import document.color as document_colors;

i32 main() {
    return colors.parse("red") + document_colors.parse("red");
}
```

If multiple imported modules provide a matching unqualified function,
qualification or an alias is required; the compiler reports an ambiguity
instead of selecting one arbitrarily.

If `math` is not imported, use fully qualified names:

```oxygen
math.add(10, 32);
math.float.pow(20.4, 2.4);
```

Importing `math.float` directly permits `float.pow(...)` and unqualified
`pow(...)`.
Fully qualified names do not require an import when the corresponding module
is available to the compilation.

Functions and modules support `public` and `private` visibility. Both default
to `private`; private declarations are available only inside their declaring
module. Public declarations are the API exposed to importing modules.

Compiler-produced native modules carry versioned Oxygen metadata containing
qualified declarations, type encodings, visibility, native symbol names, and
link dependencies. The CLI writes this metadata beside the native output as
`<output>.oxmeta`, or to the path passed to `--emit-metadata`, and appends the
same versioned payload to the native artifact. Readers prefer the embedded
payload and fall back to the `.oxmeta` sidecar. Metadata is deterministically
ordered and version validated.

Native binaries without Oxygen metadata are opaque. They may be linked as
dependencies, but type-safe calls require an explicit ABI declaration or
manifest. When a native dependency has valid embedded or sidecar metadata,
the CLI imports its public declarations and native symbols for semantic
checking; the compiler never infers signatures by guessing from native
exports.

## Type names

Every struct, enum, union, class and interface belongs to the module that
declares it. Its name is qualified by that module, so two modules can each
declare a type with the same name. A type is referred to in one of three ways:

- By its bare name, when the name is declared in the current module or in an
  imported module. `import util;` makes `util`'s types visible under their own
  names.
- By its qualified name, `module.Type`. The qualified name needs no import when
  the module is part of the compilation.
- Through an import alias, `alias.Type`. `import util as u;` makes `u.Box` the
  name of `util.Box`. The alias adds a qualifier and does not hide the bare name.

```oxygen
import util;

class Box {
    i32 value = 5;
}

i32 main() {
    program.Box local = program.Box(); // this module's Box
    util.Box other = util.Box();       // the imported util.Box
    Box ambiguous = Box();             // error: ambiguous, see below
    return local.value;
}
```

Two visible types with one name are an error at the use (`OX0211`). The
diagnostic names both types and suggests a qualified name. The same rule covers
a type of this module and an imported type. A module declares each type name
once, so two declarations of one name in one module are an error at the
declaration.

A name that is not visible is not in the way. A program may declare its own
`Exception` while it does not import `std.exception`. Importing that module makes
the two names clash, and the clash is reported at the use.

In native output, each type has a symbol name that includes its module, so two
types with one name in different modules do not collide at link time.
