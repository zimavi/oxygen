# Native module metadata

Oxygen native builds can emit a sidecar `.oxmeta` file with
`oxygen build --emit-metadata <path>`. The document is UTF-8 JSON and has a
versioned `formatVersion` field. Format version `2` contains the source module
name, the platform entry point, generated function symbols (named as described
in [native symbol names](mangling.md)) and link dependencies. Format version `1`
used the earlier symbol scheme, so readers reject it.

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
