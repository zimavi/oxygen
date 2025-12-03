
# Oxygen

A frankenstein compiler of C-like languages (C/C++) and Rust with its borrow system, using LLVM as backend.

> [!WARNING]
> This compiler is in early development stage, and I would not advice to use it at all.

## Requirements

The project rely on CMake and LLVM, specifically, CMake 3.31.2, LLVM 19 respectively.
It also uses C++ 23 standartd.

## Installation

First, ensure you have CMake, LLVM and any sutable C++ compiler installed.
Then clone repo and run CMake

```bash
  git clone ...
  cd oxygen
  mkdir build/
  cd build
  cmake ..
  make
```
    
## Usage/Examples

Well, as the compiler in earliest stage of development, there aren't much of an example to give,
nor what syntax or features it implies
