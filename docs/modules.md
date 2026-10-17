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
