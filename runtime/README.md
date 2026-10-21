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
`oxygen_runtime_type_kind`, extensible flags, nullable primary-parent
metadata, and the list of supertypes: every base class and interface, emitted
as a constant table. The runtime type check helper compares addresses against
the object's type and that list for `is` patterns and checked casts, so no
relationships are registered at startup. Object headers and interface values
store pointers to these records rather than raw type-name pointers.

Runtime implementation files are organized by responsibility:

- `src/runtime.c` contains runtime initialization.
- `src/diagnostics.c` reports fatal runtime failures.
- `src/exceptions.c` provides exception handler registration, dispatch,
  throw-site trace capture, and unhandled-exception termination.
- `src/types.c` provides the runtime type check, which compares type records
  with the supertype tables the compiler emits.
- `src/memory.c` provides allocation primitives.
- `src/io.c` provides byte output. Formatting of primitive values is done by the
  standard library, not the runtime.
- `src/strings.c` provides string/ C-string marshaling.

`tests/runtime_tests.c` runs native cases for functions that Oxygen programs
cannot call directly. It takes one case name on the command line, so a case that
stops the process is checked by its exit code. The integration test
`RuntimeLibraryTests` in `tests/Oxygen.Cli.Tests` builds it with clang against
the runtime archive and runs each case.

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

Each script builds the host target by default and writes its archive, with debug
symbols, to a directory named after the target triple:

- `runtime/build/x86_64-unknown-linux-gnu/liboxygenrt.a` (Itanium landing pads)
- `runtime/build/x86_64-pc-windows-msvc/liboxygenrt.a` (Windows SEH)

The Windows target also gets a shared runtime, `oxygenrt.dll` with its import
library `oxygenrt.lib`, in the same directory. The DLL exports every entry point
of `oxygen_runtime.h` (`OXYGEN_RUNTIME_API` marks them). A program linked with
`--runtime .../oxygenrt.lib` needs `oxygenrt.dll` next to it when it runs. The
ELF shared runtime is not built yet.

Process setup (the exit handler, the console code page and the hardware fault
handler) runs once per process, so a second program started in the same process
does not install the handlers again. The runtime keeps no other file-scope state:
trace frames, exception records and the in-flight exception are thread-local.

Pass a triple to build another target: `-Target` for `build.ps1`, or the triple as
an argument for `build.sh`. The compiler's `--target` selects the archive that
matches it. A cross build needs the target's C headers, which the host may not
have. Position-independent code (`-fPIC`) is used for the ELF target only.

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

Each script requires Clang and a compatible archive tool (`llvm-ar`/`ar` or
`llvm-lib`). Without `--runtime`, the compiler reads the archive for its target
from `OXYGEN_PATH/runtime/build/<triple>/`, and reports a missing archive with
the command that builds it.

The output is a static archive, not a complete executable. The CLI native
build passes this archive after the LLVM-generated object file and then links
any optional `--stdlib` paths as dynamic dependencies. The source standard
library includes `std.io`, which provides `write_stdout`, `write_stderr`,
and the generic `print`/`println` functions. Values of primitive types, strings,
and classes implementing `Printable` can be printed. Formatting is written in
Oxygen. Select the optimization level with `--optimization O0`, `--optimization O1`,
or `--optimization O2`, or use `--unoptimized` for `O0`. The compiler does not
generate debug information yet. The runtime archive is built with `-g`.

To emit textual LLVM IR without compiling or linking a native executable, use
`--emit-ir`:

```powershell
dotnet run --project .\src\Oxygen.Cli -- build .\main.oxy `
  --emit-ir .\artifacts\program.ll
```

For example:

```powershell
dotnet run --project .\src\Oxygen.Cli -- build .\main.oxy `
  --unoptimized `
  --runtime .\runtime\build\windows\liboxygenrt.a `
  --output .\program.exe
```

Additional native dependencies can be added to the pipeline as static or
dynamic entries without changing the object-generation stage.
