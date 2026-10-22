# Toolchain layout

An installed Oxygen toolchain is one folder, the prefix. The compiler finds the runtime and
the standard library from the folder it runs from, so it needs no `OXYGEN_PATH`.

```text
<prefix>/bin/oxygenc                  the compiler
<prefix>/lib/                         the runtime and the standard library, for the linker
<prefix>/share/oxygen/stdlib/*.oxy    the standard library sources, for reading only
```

| File | Windows | Linux |
|---|---|---|
| Compiler | `bin\oxygenc.exe` | `bin/oxygenc` |
| Shared runtime, loaded by programs | `bin\oxygenrt.dll` | `lib/liboxygenrt.so` |
| Shared runtime, linked | `lib\oxygenrt.lib` | `lib/liboxygenrt.so` |
| Static runtime | `lib\liboxygenrt.a` | `lib/liboxygenrt.a` |
| Standard library, read for its metadata and loaded by programs | `bin\std.dll` | `lib/std.so` |
| Standard library, linked | `lib\std.lib` | `lib/std.so` |

On Windows a program finds `oxygenrt.dll` and `std.dll` through `PATH`, so they sit in
`bin` beside the compiler. On Linux the loader needs `<prefix>/lib` on its library path.
The Linux standard library is `std.so`, not `libstd.so`, because the library records its own
file name as the name that programs ask the loader for.

## Finding the installation

The compiler is part of an installation when the folder it runs from is called `bin` and
`<prefix>/share/oxygen/stdlib` exists. A compiler that runs from a build folder, such as
`src/Oxygen.Cli/bin/Debug/net10.0`, is not, and it keeps using `OXYGEN_PATH` and
`--runtime` as before.

## What an installed compiler does

- **Runtime.** `--runtime` defaults to the installation's runtime for the target: the static
  archive, or the shared runtime when the build links the standard library or builds a shared
  library.
- **Standard library.** The installed `std` is linked as if `--stdlib` named it, and the
  program does not compile the standard library sources. `--no-std` leaves it out, and
  `--stdlib` adds other libraries to it.
- **A missing piece is an error.** When the installation lacks the standard library for the
  target, the build stops with exit code 3 and names the file it looked for. It does not fall
  back to `OXYGEN_PATH` or to the sources, which could mix an old installation with a new tree.
  An installation is for one platform, so `--target` with another platform's triple reports the
  missing `std` file this way.

The sources in `share/oxygen/stdlib` are not compiled. They are there for tools that show
the standard library to the programmer, such as a language server.
