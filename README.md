# Oxygen

A frankenstein compiler of C-like languages (C/C++) and Rust with its borrow system, using LLVM as backend.

> [!WARNING]
> This compiler is in early development stage, and I would not advice to use it at all.

## Requirements

The project rely on CMake and LLVM, specifically, CMake 3.31.2, LLVM 19 respectively.
It also uses C++ 23 standartd.

Other dependencies are downloaded using CMake.

## Building

First, ensure you have CMake, LLVM and any sutable C++ compiler installed.
Then clone repo and run CMake

```bash
  mkdir build/
  cd build
  cmake ..
  make
```

## Usage/Examples

The compiler accepts input file, as well as output file with `-o` flag.
Warnings can be ignored using `--Wignore` or produce errors instead, with `--Werror` flags.

If defined `DBG_OXY_DUMP_ARGS` in source, or `-Dinclude_dump_args_flag=1` in CMake via shell,
the `-dArgs` flag will be availabe, to dump options structure.

The compiler in earliest stage of development, so there aren't much of an example to give,
nor what syntax or features it implies
