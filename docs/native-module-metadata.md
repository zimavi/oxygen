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

The generated module exposes its metadata through `GeneratedLlvmModule.Metadata`
and writes it when `NativeBuildOptions.MetadataPath` is supplied. Metadata
describes the generated module; dependency paths are populated by a future
build invocation and are not inferred from LLVM IR.
