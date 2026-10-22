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
- clang 22.x for the runtime and native linking. Its major version must match the LLVM libraries, which are LLVM 22.1.8. Other major versions are not part of the test matrix.
- A compatible archiver such as `llvm-ar`, `ar`, or `llvm-lib`
- The LLVM libraries used by `LLVMSharp`. NuGet restores them, pinned to LLVM 22.1.8 in `Directory.Build.props`. Keep `LLVMSharpVersion` and `LibLLVMVersion` changed together.

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

## Build the standard library

`stdlib/build.ps1` (Windows) and `stdlib/build.sh` (Linux) build the shared runtime, then the
standard library as one shared library, into `stdlib/build/<triple>/`. The Windows build gives
`std.dll` and its import library `std.lib`; the Linux build gives `std.so`. The compiler is
published into the same folder, since the LLVM library is copied only by a publish. The scripts
take a target and `clean`, like `runtime/build.ps1` and `build.sh`.

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

- `--output`, `-o` — output path for the native executable, or for the library with `--library`. Defaults to `program`, or to the module name for a library. On the Windows target `.exe` is added when the path has no extension
- `--library` — build a static library instead of an executable. A library has no `main` function, and it is not linked, so `--runtime` cannot be used with it. Each module that the given sources declare is compiled to its own object, which carries that module's metadata, and the objects are archived together. A module that the sources import is not built. The default name is the first module's, and `--emit-ir` is not supported with `--library` yet. A library that uses another library's classes names it with `--stdlib`, and a shared library links it
- `--dynamic` — with `--library`, build a shared library: `<name>.dll` with its import library `<name>.lib` on Windows, and `<name>.so` elsewhere. It links the shared runtime, which `--runtime` names (`oxygenrt.lib` or `liboxygenrt.so`); the default is the one that `runtime/build.ps1` or `build.sh` builds
- `--emit-ir` — write textual LLVM IR instead of building a native executable
- `--runtime`, `-r` — Oxygen runtime to link: the static archive (`liboxygenrt.a`), or the shared runtime (`oxygenrt.lib` on Windows, `liboxygenrt.so` elsewhere). A program built with `--stdlib`, and a `--library --dynamic` build, link the shared runtime, so they take it by default and reject the static archive. A static archive is linked into the program, and a shared runtime at run time needs `oxygenrt.dll` beside the program on Windows, or its directory on the library path elsewhere
- `--stdlib` — a native library to build against, such as the shared standard library made by `build stdlib/*.oxy --library --dynamic --no-std`. Its modules are declared from its metadata, so their source is not loaded, and the standard library source does not need to be present. Its non-generic functions, its generic functions and unions, and its interfaces can be used. Its classes are imported with their fields and methods, and their constructors and destructors are the library's code. Static fields of imported classes are shared with the library that defines them. Generic structs are not imported yet. On Windows, name the DLL: its import library beside it is linked
- `--no-std` — do not load the standard library. The sources are built alone, so `import std.*` is an error unless the module is in the sources, and `RuntimeException` and `NullReferenceException` are not available. This is how the standard library itself is built (`build stdlib/*.oxy --library --dynamic --no-std`). A null class reference in such a program stops it with exit code 101
- `--clang`, `-c` — LLVM compiler driver used for object generation and linking
- `--unoptimized`, `-u` — build without optimization, the same as `--optimization O0`. It does not add debug information, and it cannot be combined with `--optimization`
- `--manual-memory`, `-mm` — manual memory mode for every function (C++ style)
- `--warnings-as-errors` — treat semantic warnings as errors
- `--optimization`, `-O` — optimization level passed to native code generation
- `--jobs`, `-j` — backend worker count / ThinLTO parallelism

The native link uses `lld` (`-fuse-ld=lld`) when `lld-link` (Windows target) or `ld.lld` (other targets) is on `PATH`, and the default linker otherwise. A native tool that runs longer than 10 minutes is stopped, and Ctrl+C stops the build and exits with code 130.
- `--target` — target, `x86_64_pc_windows_msvc` (triple `x86_64-pc-windows-msvc`) or `x86_64_unknown_linux_gnu` (triple `x86_64-unknown-linux-gnu`); defaults to the host. The shell suggests the names and rejects others. The runtime archive must be built for the same target

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

## Versioning

The version is in `VERSION` at the repository root, and it is written nowhere else.
`Directory.Build.props` reads it into the assembly version, and `oxygenc --version`
reports it. The project follows [Semantic Versioning](https://semver.org). It starts at
`0.1.0`, and while the major version is 0 nothing is released.

A commit that changes behavior changes `VERSION` in the same commit, by the bump for its
conventional commit type:

- `feat`: minor bump, `0.1.0` to `0.2.0`.
- `fix` and `perf`: patch bump, `0.1.0` to `0.1.1`.
- A breaking change (a `!` after the type, or a `BREAKING CHANGE:` footer): minor bump
  while the major version is 0, since semver lets `0.y.z` change in any minor release.
- `refactor`, `test`, `docs`, `build`, `ci` and `chore`: no bump. A refactor that changes
  behavior is a `feat` or `fix` instead.

The first release sets the major version to 1. That is a decision for the release, not for
a single commit. Nightly builds add a prerelease suffix such as `0.1.0-nightly.20261021`
and do not change `VERSION`.
