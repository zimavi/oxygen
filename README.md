# Oxygen

Oxygen is a compiled, statically typed, C-like programming language under active development.
The compiler is written in C#, LLVMSharp is used for LLVM integration, and the initial
runtime is implemented as a C static library.

The repository contains the compiler, CLI, runtime, tests, and language/reference docs.

## Repository layout

- `src/Oxygen.Cli/` — command-line compiler entry point and build pipeline.
- `src/Oxygen.Compiler/` — lexer, parser, AST, diagnostics, and semantic analysis.
- `src/Oxygen.Llvm/` — LLVM IR generation, metadata generation, and native build support.
- `runtime/` — C runtime library sources and build scripts.
- `tests/Oxygen.Compiler.Tests/` — automated tests for compiler behavior.
- `docs/` — language and implementation notes.
- `stdlib/` — standard-library source modules.

## Prerequisites

To build the repository you will typically need:

- .NET 10 SDK
- A Clang-based C toolchain for the runtime and native linking
- A compatible archiver such as `llvm-ar`, `ar`, or `llvm-lib`
- LLVM runtime libraries required by `LLVMSharp`

On Windows, the commands below use `pwsh`. On POSIX systems, use the equivalent shell commands.

## Build the solution

From the repository root:

```powershell
dotnet build .\Oxygen.slnx
```

## Run tests

```powershell
dotnet test .\tests\Oxygen.Compiler.Tests\Oxygen.Compiler.Tests.csproj
```

## Build the runtime

The runtime is built separately as a static archive.

### Windows / PowerShell

```powershell
pwsh -File .\runtime\build.ps1
```

### POSIX shell

```sh
./runtime/build.sh
```

The runtime build scripts build the archive for the host platform, at
`runtime/build/<triple>/liboxygenrt.a`. To build another supported target, pass its
triple (`-Target x86_64-pc-windows-msvc` or `-Target x86_64-unknown-linux-gnu` for
`build.ps1`, or the triple as an argument for `build.sh`). A cross build needs the
target's C headers, which the host may not have.
You can clean generated runtime artifacts with:

```powershell
pwsh -File .\runtime\build.ps1 -Clean
```

## Use the CLI

The CLI project provides a `build` command for compiling Oxygen source files.

Example: emit LLVM IR without producing a native executable:

```powershell
dotnet run --project .\src\Oxygen.Cli -- build .\main.oxy --emit-ir .\artifacts\program.ll
```

Example: build a native executable and link against the runtime archive:

```powershell
dotnet run --project .\src\Oxygen.Cli -- build .\main.oxy `
  --runtime .\runtime\build\x86_64-pc-windows-msvc\liboxygenrt.a `
  --output .\program.exe
```

Useful CLI options include:

- `--output`, `-o` — output path for the native executable
- `--emit-ir` — write textual LLVM IR instead of building a native executable
- `--emit-metadata` — write `.oxmeta` native-module metadata
- `--runtime`, `-r` — static Oxygen runtime archive to link
- `--stdlib` — optional dynamic standard-library dependencies
- `--clang`, `-c` — LLVM compiler driver used for object generation and linking
- `--debug` — generate debug symbols
- `--manual-memory`, `-mm` — manual memory mode for every function (C++ style)
- `--warnings-as-errors` — treat semantic warnings as errors
- `--optimization`, `-O` — optimization level passed to native code generation
- `--jobs`, `-j` — backend worker count / ThinLTO parallelism
- `--target` — target triple, `x86_64-pc-windows-msvc` or `x86_64-unknown-linux-gnu`; defaults to the host. The runtime archive must be built for the same target

Source imports are resolved from the directories containing the supplied source
files. The compiler also automatically discovers a `stdlib/` directory in an
ancestor of those directories, or under the directory specified by
`OXYGEN_PATH`, so repository standard-library modules do not need to be passed
individually.

## Documentation

The `docs/` directory contains language notes for arrays, classes, control flow,
diagnostics, enums, exceptions, iterators, memory, modules, pointers, strings,
structs, unions, native-module metadata, and C interop annotations.

Additional runtime details are documented in `runtime/README.md`.

## Status

Oxygen is evolving incrementally. The implementation is organized into clear stages:
lexer, parser, AST, semantic analysis, LLVM IR generation, runtime, and native build tooling.

If you are contributing new functionality, inspect the relevant source, tests, and docs first,
then keep changes focused and add tests alongside behavior changes.
