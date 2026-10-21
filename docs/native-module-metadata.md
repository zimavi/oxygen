# Native module metadata

The metadata document is UTF-8 JSON and has a versioned `formatVersion` field.
The build no longer writes it as a sidecar file; the `--emit-metadata` flag was
removed, and library builds embed it in an object section (see below). Format version `3` is the current one. It
contains the source module name, the platform entry point, generated function
symbols (named as described in [native symbol names](mangling.md)), link
dependencies, and two arrays that version 2 does not have: `types` and
`generics`. Format version `2` is still read, and such a document has neither
array. Format version `1` used the earlier symbol scheme, so readers reject it.

## Types and generics (version 3)

`types` describes the structs, classes and enums that the module declares, and
that are not generic, with what an importing program needs to lay them out and
to call their methods: the kind, the module, the name and the visibility, the
size and alignment of a struct or class (the size is a multiple of the alignment,
and the alignment is a power of two), the fields in declaration order with their
byte offsets, the methods with their native names, and the cases, underlying
type and flags of an enum. A `@value` mark is allowed only on a class. Union and
interface types are not described yet.

The layouts are the ones the generated code uses: a reference class starts with
its vtable pointer and one slot per virtual method, and a `@value` class has no
vtable. The offsets and sizes come from the LLVM target data of the build. A
generic instantiation (a name with the `__ox_` suffix, see
[generics](generics.md)) is not a type of the module.

A method and a property accessor are listed under their class, so they are not
in `functions`. An enum case's value is written in decimal text, since a 64-bit
value can be outside the range of a JSON number.

`generics` carries the generic declarations of the module, not its source text.
Each entry is a unit in the binary syntax form (`SyntaxTreeCodec`, format version
1, see `Oxygen.Compiler.Ast`): the module header, the imports, and the classes,
structs, unions, interfaces and functions that have type parameters. The form
has no comments, formatting or original text. The entry's module and source name
must match the names inside the form, and the form must hold only generic
declarations. A damaged form is reported with the source it came from.

The language has no type parameters on methods, so each entry is a whole
top-level generic declaration, and nothing else of its unit comes with it. The
entries of a build are the generic declarations of the module being built, one
entry per source file, with the file name as the source name.

Both arrays are validated when the document is read, and a native name may
appear only once in a module, for a function or for a method. Entries are written
in a fixed order: types by module and name, methods by symbol, and generics by
module and source name. Fields and enum cases keep their declaration order.

Types and generics are read and checked, but the loader does not import them
yet. A library that has them gets a warning that says so, instead of having them
ignored silently.

`Oxygen.Llvm.NativeModuleMetadataSerializer` provides deterministic
serialization and read/write helpers. Functions are ordered by generated
native symbol and dependencies by kind and path, so equivalent metadata
produces byte-identical output. Readers reject unsupported format versions
rather than silently interpreting a newer schema.

When a native library is imported, the compiler reads its embedded metadata,
or the sidecar when the library has no embedded copy. A library without either
is opaque and contributes no declarations. The metadata must name the target
that the library was built for, and a library for another target is rejected.

Only primitive types, `string`, pointers and fixed-size arrays are described by
the metadata. A function with a class, struct, enum, union, interface or generic
parameter, or with such a result, is not imported. The compiler reports it as a
warning that names the function. The sidecar and the embedded copy are written
only after the link succeeds, so a failed build leaves neither behind.

The generated module exposes its metadata through `GeneratedLlvmModule.Metadata`
and writes it when `NativeBuildOptions.MetadataPath` is supplied. Metadata
describes the generated module; dependency paths are populated by a future
build invocation and are not inferred from LLVM IR.

## The metadata section

`GeneratedLlvmModule.AddMetadataSection()` writes the same JSON document into a
`.oxmeta` section of the module's object file. The section is kept through
`llvm.used`, so optimization does not remove it. Adding the section twice is an
error. `NativeModuleSection.ReadPayloads(path)` reads the payload of every object
in an object file or an `ar` archive that has the section. Archives are read in
the GNU and BSD name forms by the tests. An archive written by `llvm-ar` (a COFF
`.lib` with a long member name, and an ELF `.a`) was also read, in a manual
check. Archives written by `lib.exe` have not been read yet. Other archive members
are skipped, and a file that is neither an object nor an archive is invalid input.

`--library` writes the section: the module is compiled without a native `main`,
its object carries the metadata, and the object is archived with `llvm-ar rcs`
into a static library. A library on the Windows target gets the `.lib` extension
when `-o` has none, and `.a` elsewhere. Its name defaults to the module name. The
payloads are not turned into declarations yet, so a library cannot be imported
by its metadata until the importer (slice 2b) is done.
