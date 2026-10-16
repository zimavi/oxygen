# Oxygen runtime

The runtime is compiled as a static library and linked after LLVM has generated
the Oxygen object files. The archive exports the stable
`__oxygen_runtime_initialize` ABI symbol used by the generated native entry
point.

From the repository root:

```powershell
pwsh -File .\runtime\build.ps1
```

On POSIX-compatible platforms, use the shell script:

```sh
./runtime/build.sh
```

The PowerShell script produces `runtime\build\oxygen_runtime.lib` using the
MSVC archive format by default on Windows. The shell script produces
`runtime/build/liboxygen_runtime.a` using the GNU archive format. Both scripts
use Clang for compilation and support overriding the compiler, archiver, and
output directory.

For example:

```sh
CLANG=/opt/llvm/bin/clang \
ARCHIVER=/opt/llvm/bin/llvm-ar \
OUTPUT_DIRECTORY=./artifacts/runtime \
./runtime/build.sh
```

The scripts require Clang and a compatible static-library archiver
(`llvm-lib`/`lib` on Windows, or `llvm-ar`/`ar` on POSIX-compatible systems).

The output is a static archive, not a complete executable. The eventual
linking step should pass the archive together with the LLVM-generated object
file(s) and the platform C runtime libraries.
