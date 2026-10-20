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
- `src/exceptions.c` provides exception handler registration, dispatch,
  throw-site trace capture, and unhandled-exception termination.
- `src/types.c` handles runtime type registration and relationship checks.
- `src/memory.c` provides allocation primitives.
- `src/io.c` provides byte output. Formatting of primitive values is done by the
  standard library, not the runtime.
- `src/strings.c` provides string/ C-string marshaling.

The output and marshaling helpers use the compiler's `{ ptr, i64 length }`
string layout. The output functions receive a pointer to this structure to
keep the C ABI explicit across platforms. A string result of an external
function is written through a trailing out-pointer parameter, which
`oxygen_c_string_to_string` uses. Output writes exactly the supplied number of bytes, so embedded
NUL bytes are supported.
The Oxygen-to-C conversion allocates a NUL-terminated copy. Generic
`std.memory` allocation uses `oxygen_alloc` and `oxygen_free`; ownership of
those allocations belongs to the Oxygen caller.

The public `std.exception` standard-library module contains the
`Exception`, `StackTrace`, and `Frame` representations. Callers construct
exceptions with user data only (`Exception(message)`); runtime trace state is
initialized by the class itself. Generated functions
push and pop source frames through `__oxygen_runtime_trace_push` and
`__oxygen_runtime_trace_pop`; `__oxygen_runtime_throw_object` copies the
bounded active frame list into owned storage associated with the thrown
object. The ABI exposes that storage through
`__oxygen_runtime_exception_trace`. Unhandled exceptions include the message
when the type metadata identifies its message field, followed by the captured
frames.

The source-level `std.exception.StackTrace` exposes the captured frame count
through its `StackTrace(void* exception_object)` constructor and provides
`frame_at(index)`. The latter uses bounds-checked runtime accessors for the
opaque frame pointer, C-string names, line, and column, avoiding a foreign
struct-return ABI. The runtime owns the frame storage until the matching catch
or unhandled-exception path ends.

LLVM exception lowering currently uses the POSIX/Itanium C++ unwinder
(`__gxx_personality_v0` and the `__cxa_*` bridge) for native `throw`,
`try/catch`, and `finally` control flow. On Windows, the runtime bridge uses
the Clang/LLVM SEH exception record format instead: throwing raises the
private `OXYGEN_RUNTIME_SEH_EXCEPTION_CODE` with the Oxygen object pointer as
its payload, and catch handlers recover that pointer from
`EXCEPTION_POINTERS`. Type matching follows the object's existing
`oxygen_runtime_type_info` parent chain. `rethrow` preserves the active
payload and SEH flags. The bridge does not install a second personality
routine; Windows LLVM funclets and the platform `__C_specific_handler` own
stack unwinding.

From the repository root:

```powershell
pwsh -File .\runtime\build.ps1
```

To remove all generated runtime objects and archives:

```powershell
pwsh -File .\runtime\build.ps1 -Clean
./runtime/build.sh clean
```

On POSIX-compatible platforms, use the shell script:

```sh
./runtime/build.sh
```

Each script builds both exception ABIs and writes platform-specific artifacts,
including debug symbols:

- `runtime/build/posix/liboxygenrt.a` (Itanium C++ EH)
- `runtime/build/windows/liboxygenrt.a` (Windows SEH)

The runtime is compiled with `-O2 -g` using an explicit x86_64 target triple.
Both scripts use Clang for compilation and support overriding the compiler,
archiver, and output directory.

For example:

```sh
CLANG=/opt/llvm/bin/clang \
ARCHIVER=/opt/llvm/bin/llvm-ar \
OUTPUT_DIRECTORY=./artifacts/runtime \
./runtime/build.sh
```

Each script produces both platform libraries in one invocation. The scripts
require Clang and a compatible archive tool (`llvm-ar`/`ar` or `llvm-lib`).

The output is a static archive, not a complete executable. The CLI native
build passes this archive after the LLVM-generated object file and then links
any optional `--stdlib` paths as dynamic dependencies. The source standard
library includes `std.io`, which provides `write_stdout`, `write_stderr`,
and the generic `print`/`println` functions. Values of primitive types, strings,
and classes implementing `Printable` can be printed. Formatting is written in
Oxygen. Use `--debug` to generate debug symbols for
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
  --runtime .\runtime\build\windows\liboxygenrt.a `
  --output .\program.exe
```

Additional native dependencies can be added to the pipeline as static or
dynamic entries without changing the object-generation stage.
