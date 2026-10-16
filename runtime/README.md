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

The scripts produce both optimized and debug-symbol archives. The PowerShell
script uses the MSVC archive format by default on Windows:

- `runtime\build\oxygen_runtime.lib`
- `runtime\build\oxygen_runtime_debug.lib`

The shell script produces GNU archives:

- `runtime/build/liboxygen_runtime.a`
- `runtime/build/liboxygen_runtime_debug.a`

The release runtime uses `-O2`; the debug runtime uses `-O0 -g`. Both scripts
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

The output is a static archive, not a complete executable. The CLI native
build passes this archive after the LLVM-generated object file and then links
any optional `--stdlib` paths as dynamic dependencies. The standard library
is not present yet, so the dynamic dependency list is empty by default. Use
`--debug` to generate debug symbols, use the debug runtime by default, and
select another optimization level with `--optimization O0`, `--optimization
O1`, or `--optimization O2`.

For example:

```powershell
dotnet run --project .\src\Oxygen.Cli -- build .\main.oxy `
  --debug --optimization O0 `
  --runtime .\runtime\build\oxygen_runtime_debug.lib `
  --output .\program.exe
```

Additional native dependencies can be added to the pipeline as static or
dynamic entries without changing the object-generation stage.
