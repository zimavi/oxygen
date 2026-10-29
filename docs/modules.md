# Oxygen modules and imports

An Oxygen source file can declare a nested module:

```oxygen
public module math;

public i32 add(i32 left, i32 right) {
    return left + right;
}
```

The CLI reads the module each `.oxy` file declares, from its header. Files are
found by the module they declare, not by their names, so file names do not need
to match module names, and multiple files may declare the same module; their
functions are combined into one module. The compiled files are the files that
were given on the command line, the files that declare a module the program
needs, and, transitively, the files that declare each module that a compiled
file imports. A file that the program does not need is not compiled, so a
syntax error in it does not stop the build. The files are searched in the
folders below the given files, and a folder that cannot be read is skipped with
a warning; a module that the program needs from such a folder is then reported
as missing at its import. Imports are checked against the set of compiled
modules before name lookup.

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

A call can name a function in three ways, and each resolves to the same
declaration:

- **Unqualified**, for a function the program sees without a prefix: a function
  of the importing module, a function of a module imported directly (`pow(...)`
  after `import math.float;`), or a name listed in a symbol import.
- **Module-leaf**, the last segment of an imported module, followed by the name.
  After `import math;`, `float.pow(...)` means `math.float.pow(...)`. After
  `import stdlib.io;`, `io.print(...)` means `stdlib.io.print(...)`.
- **Fully qualified**, the module's full name followed by the name:
  `math.add(...)` and `math.float.pow(...)` above.

The module-leaf form is intended, for two reasons. It makes a call explicit
about the module that declares the function, so a reader sees which module
`io.print` comes from. It also resolves an ambiguous name. When two imported
modules declare a function with the same name, an unqualified call is an
error (`is ambiguous; use a module prefix`). The programmer then writes a
prefix that names one module. The leaf form is enough when the leaves differ,
as with `graphics.color` and `document.text`. When the leaves are the same, as
in the example below, the full module name is needed:

```oxygen
import graphics.color;
import document.color;

i32 main() {
    // color.parse("red") is ambiguous here: both modules declare parse.
    return graphics.color.parse("red") + document.color.parse("red");
}
```

Importing a nested module directly exposes its functions in the same way:

```oxygen
import math.float;

i32 main() {
    return pow(20.4, 2.4); // math.float.pow
}
```

An import may define an explicit source-level alias. This is useful when two
imports expose the same unqualified function name, or the same leaf. Each alias
names its module, so the calls below are not ambiguous:

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
link dependencies. Library builds embed the metadata in a `.oxmeta` object
section (see [native module metadata](native-module-metadata.md)). Metadata is
deterministically ordered and version validated.

Native binaries without Oxygen metadata are opaque. They may be linked as
dependencies, but type-safe calls require an explicit ABI declaration or
manifest. When a native dependency has valid `.oxmeta` section metadata,
the CLI imports its public declarations and native symbols for semantic
checking; the compiler never infers signatures by guessing from native
exports.

## Import lists

One import statement can name several modules, or several names of one module:

```oxygen
import std.{memory, strings};              // std.memory and std.strings, each under its leaf name
import std.{memory, strings} as {mem, str}; // the same, qualified by mem and str
import std.memory{alloc, free as release}; // only these two declarations, unqualified
```

A group is the same as one import for each module it lists, so `memory.alloc`
and the other names of each module work as they do after a plain import. A list of aliases is matched to the
modules by position, and the two lists must have the same length.

A symbol list brings only the listed public declarations into scope, by their
names: `alloc<i32>(4)` and `release(pointer)`. Other declarations of the module
are not visible through the import, and the leaf qualifier is not added, so
`memory.alloc` needs a plain `import std.memory;`. An entry may name a function
or a type, and `as` gives it a different local name.

The following are errors:

- A group inside a group, as in `import std.{memory{alloc}};`.
- An alias list with a different number of names from the group.
- An alias for a whole symbol list, as in `import std.memory{alloc} as m;`.
- An empty symbol list, and a dotted name inside a symbol list.
- A listed name that the module does not declare as `public`. It is reported at
  the import.
- A listed name that clashes with a declaration of the same kind in the
  importing module. It is reported at the import, not at a use.
- Two imports that provide one name are an ambiguity at the use, as for plain
  imports.

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

A type alias (see [type aliases](type-aliases.md)) is a type name too. It follows
the same rules, so an alias and a type of one name, or two aliases of one name
that two imports provide, are ambiguous at the use.

A name that is not visible is not in the way. A program may declare its own
`Exception` while it does not import `std.exception`. Importing that module makes
the two names clash, and the clash is reported at the use.

In native output, each type has a symbol name that includes its module, so two
types with one name in different modules do not collide at link time.
