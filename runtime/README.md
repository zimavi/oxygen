# Oxygen runtime

The runtime is compiled as a static library and linked after LLVM has generated
the Oxygen object files. The archive exports the stable `__oxygen_runtime_initialize` ABI symbol used by
the generated native entry point, string output symbols
`oxygen_write_stdout` and `oxygen_write_stderr`, and the string marshaling
symbols `oxygen_string_to_c_string` and `oxygen_c_string_to_string`. It also
exports `__oxygen_runtime_array_bounds_failure`, which terminates execution
after reporting an invalid dynamic fixed-array index.
The generated entry point initializes an `oxygen_runtime_type_info` record for
each class and interface. Each record contains the stable type name, a
`oxygen_runtime_type_kind`, extensible flags, and nullable primary-parent
metadata. Class inheritance and interface relationships are registered with
`__oxygen_runtime_register_type`; the runtime type check helper then follows
those relationships for `is` patterns and checked casts. Object headers and
interface values store pointers to these records rather than raw type-name
pointers.

Runtime implementation files are organized by responsibility:

- `src/runtime.c` contains runtime initialization.
- `src/diagnostics.c` reports fatal runtime failures.
- `src/types.c` handles runtime type registration and relationship checks.
- `src/memory.c` provides allocation primitives.
- `src/io.c` provides byte output and primitive formatting.
- `src/strings.c` provides string/ C-string marshaling.

The output and marshaling helpers use the compiler's `{ ptr, i64 length }`
string layout. The output functions receive a pointer to this structure to
keep the C ABI explicit across platforms. Output writes exactly the supplied number of bytes, so embedded
NUL bytes are supported.
The Oxygen-to-C conversion allocates a NUL-terminated copy. Generic
`stdlib.memory` allocation uses `oxygen_alloc` and `oxygen_free`; ownership of
those allocations belongs to the Oxygen caller.

From the repository root:

```powershell
pwsh -File .\runtime\build.ps1
```

On POSIX-compatible platforms, use the shell script:

```sh
./runtime/build.sh
```

Both scripts produce the same GNU-compatible archive, including debug symbols:

- `runtime/build/liboxygenrt.a`

The runtime is compiled with `-O2 -g`. The archive format is intentionally
independent of the host platform so it can be embedded into the executable
linking pipeline. Both scripts use Clang for compilation and support overriding
the compiler, archiver, and output directory.

For example:

```sh
CLANG=/opt/llvm/bin/clang \
ARCHIVER=/opt/llvm/bin/llvm-ar \
OUTPUT_DIRECTORY=./artifacts/runtime \
./runtime/build.sh
```

The scripts require Clang and a compatible GNU archive tool (`llvm-ar` or `ar`).

The output is a static archive, not a complete executable. The CLI native
build passes this archive after the LLVM-generated object file and then links
any optional `--stdlib` paths as dynamic dependencies. The source standard
library includes `stdlib.io`, which provides `write_stdout`, `write_stderr`,
and overloaded `print`/`println` functions for strings and all primitive
numeric and boolean types. Primitive values use decimal formatting; booleans
are written as `true` or `false`. Use
Use `--debug` to generate debug symbols for the generated program and select
another optimization level with `--optimization O0`, `--optimization O1`, or
`--optimization O2`. The runtime archive always includes debug symbols.

To emit textual LLVM IR without compiling or linking a native executable, use
`--emit-ir`:

```powershell
dotnet run --project .\src\Oxygen.Cli -- build .\main.oxy `
  --emit-ir .\artifacts\program.ll
```

For example:

```powershell
dotnet run --project .\src\Oxygen.Cli -- build .\main.oxy `
  --debug --optimization O0 `
  --runtime .\runtime\build\liboxygenrt.a `
  --output .\program.exe
```

Additional native dependencies can be added to the pipeline as static or
dynamic entries without changing the object-generation stage.
