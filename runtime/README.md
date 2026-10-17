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
- `src/exceptions.c` provides exception handler registration, dispatch, and
  unhandled-exception termination.
- `src/stack_trace.c` formats runtime-provided stack trace frames.
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

Exceptions are represented by `oxygen_runtime_exception`, which contains a
type name, message, and an optional array of
`oxygen_runtime_stack_trace_frame` values. Generated code can register a
callback with `__oxygen_runtime_push_exception_handler` and remove it with
`__oxygen_runtime_pop_exception_handler`. Raising an exception dispatches to
the current handler; without one, `__oxygen_runtime_unhandled_exception`
prints the type, message, and stack trace to standard error, flushes the
stream, and aborts with a non-zero status. Handler state is thread-local, so
independent generated programs running on different threads do not share
exception chains.

The exception ABI is versioned by
`OXYGEN_RUNTIME_EXCEPTION_ABI_VERSION` and
`__oxygen_runtime_exception_abi_version()`. Generated code should initialize
records with `__oxygen_runtime_exception_init`, attach borrowed trace frames
with `__oxygen_runtime_exception_set_stack_trace`, and use the accessor
functions when it needs to inspect a record. Trace frame and record pointers
are borrowed; their storage must remain alive until dispatch returns. The
`__oxygen_runtime_stack_trace_frame_at` accessor performs bounds checking and
returns null for an invalid index.

The public `std.exception` standard-library module contains the
`Exception`, `StackTrace`, and `Frame` value classes. It intentionally keeps
formatting and presentation in Oxygen while the C runtime only provides the
stable record and fatal diagnostic ABI.

LLVM exception lowering currently uses the POSIX/Itanium C++ unwinder
(`__gxx_personality_v0` and the `__cxa_*` bridge) for native `throw`,
`try/catch`, and `finally` control flow. Windows/SEH lowering is not yet
implemented: the Windows bridge reports an unhandled exception rather than
pretending that Itanium landing pads are compatible with SEH. Cross-compiling
exception-enabled programs for Windows therefore requires a future
`__C_specific_handler`/LLVM funclet implementation.

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
are written as `true` or `false`. Use `--debug` to generate debug symbols for
the generated program and select
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
