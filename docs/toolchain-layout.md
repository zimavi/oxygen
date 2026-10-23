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
| Standard library, read for its metadata and loaded by programs | `bin\oxygenstd.dll` | `lib/liboxygenstd.so` |
| Standard library, linked | `lib\oxygenstd.lib` | `lib/liboxygenstd.so` |

On Windows a program finds `oxygenrt.dll` and `oxygenstd.dll` through `PATH`, so they sit in
`bin` beside the compiler. On Linux the loader needs `<prefix>/lib` on its library path.
The Linux standard library is `liboxygenstd.so`. Its SONAME is its file name, which programs ask the loader
for, and `ldconfig` caches only `lib*.so` names, so a name without the `lib` prefix is never found.

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

## Installing

`scripts/install.ps1` (Windows) and `scripts/install.sh` (Linux x86_64) build the toolchain
and copy it into the layout above. The default prefix is `%LOCALAPPDATA%\Oxygen` on Windows and
`~/.local/oxygen` on Linux. Pass `-Prefix` or `--prefix` to choose another folder.

```text
scripts/install.ps1                          the default steps
scripts/install.ps1 -AddToPath               also add <prefix>\bin to the user PATH
scripts/install.ps1 -Only bundle,path        run only the named steps
scripts/install.ps1 -SkipBuild               reuse the outputs that already exist, and copy them again
scripts/install.ps1 -Update                  rebuild the installation at the prefix
```

`-Update` (`--update` on Linux) rebuilds every step into an installation that already exists
at the prefix, and prints the version it replaced. It fails when there is no installation there,
and it cannot be combined with `-SkipBuild`. It does not change PATH unless `-AddToPath` is
also given. On Linux it does not ask about sudo. It refreshes the system links only when they
already point at the prefix, and `--sudo` links them anyway.

The Linux script takes the same options with two spellings: `--only`, `--skip-build` and
`--add-to-path`. The steps run in this order, whatever order `-Only` names them in:

| Step | What it does |
|---|---|
| `check-dotnet` | Checks for the .NET 10 SDK and stops with the install command if it is missing. It does not install .NET. |
| `publish-compiler` | Publishes the compiler into `artifacts/install/stage/compiler`. |
| `build-runtime` | Runs `runtime/build.ps1` or `build.sh` for the host. |
| `build-stdlib` | Runs `stdlib/build.ps1` or `build.sh`, which builds the runtime it links and publishes its own compiler copy. |
| `bundle` | Replaces the prefix's `bin`, `lib` and `share/oxygen/stdlib` folders with the built files. Other files in the prefix are left alone. |
| `link-libraries` | Linux only: with sudo, symlinks `liboxygenrt.so` and `liboxygenstd.so` into `/usr/local/lib` and runs `ldconfig`. Runs after `bundle`. |
| `path` | Windows: adds `<prefix>\bin` to the user PATH, once. Linux: links `oxygenc` into `~/.local/bin` and the two libraries into `~/.local/lib`, without sudo. Either runs only with `-AddToPath` or `--add-to-path`, or when named. |

Each step is also a script of its own in `scripts/`, so any step can be rerun alone.

### Run path on Linux

On Linux the compiler records the prefix's `lib` folder as the run path of every program and
shared library that it links against a shared library. The program then loads `liboxygenrt.so` and
`liboxygenstd.so` from there, with no `LD_LIBRARY_PATH`, no sudo links and no copy in a system
folder. The run path is the folder as it was when the program was built, so moving the prefix
breaks programs built before the move, and they must be built again. Windows finds the DLLs through
`PATH` instead, and records nothing.

### Checking an installation

`scripts/check-installed.sh` (or `.ps1`) builds a program with the installed compiler, from a
folder of its own, with no `OXYGEN_PATH` and no `LD_LIBRARY_PATH`. It checks that the build prints
no warning, and that the program prints `i` and exits with 7. CI runs it after each install.

## Releases

A release is made when `VERSION` changes on `master`. `.github/workflows/release.yml` then builds
an installation on a Windows runner and on a Linux runner, checks each one, and packs it:

- `oxygen-<version>-windows-x86_64.zip`
- `oxygen-<version>-linux-x86_64.tar.gz`

Each archive holds one folder named after the archive, with `bin`, `lib` and `share` inside it, and
an installer beside them: `install.ps1` on Windows, `install.sh` on Linux. The workflow unpacks each
archive, runs its installer into a prefix, refuses a second install, updates the prefix, checks it,
and uninstalls it. The installers install into a prefix (`%LOCALAPPDATA%\Oxygen` and `~/.local/oxygen`
by default). Install copies the installer to `share/oxygen`, so the prefix can be uninstalled after
the archive is deleted. `-Update` or `--update` replaces the folders that an installation owns: `bin`,
`lib`, `share/oxygen/stdlib` and the copied installer. `-Uninstall` or `--uninstall` (run from
`share/oxygen`, or with `--prefix`) removes those folders, and the prefix when nothing else is in it.
Other files in the prefix are kept. `-AddToPath` (Windows) puts `bin` on the user PATH, which uninstall
removes again. `--add-to-path` (Linux) links `oxygenc` into `~/.local/bin`, and uninstall removes that
link when it points into the prefix. `--sudo` (Linux) links the libraries into `/usr/local/lib`, and
with `--uninstall` it removes those links. The release is named `v<version>`, and the workflow creates
its tag at the commit it built, only after every archive has built. The release gets both archives and a `SHA256SUMS` file, which lists the
SHA-256 hash of each archive. The release notes are generated from the commits since the previous
release.

While the major version is 0, the release is a pre-release, since nothing is stable yet. A version
that already has a release is not built again, so a run that repeats does nothing. A run
that failed after the tag was made builds the tagged commit, not the one it started from. The
workflow can also be started by hand from the Actions tab, on `master` only.

Nightly builds (`.github/workflows/nightly.yml`) run every night at 03:17 UTC. They build the same
archives, named `oxygen-<version>-nightly.<yyyymmdd>-<platform>`, and publish a prerelease named
`nightly-<yyyymmdd>`. The release notes are a changelog of the commits since the previous nightly,
grouped by conventional commit type: breaking changes, features, fixes, and other changes. A night
is skipped when no commit has come since the previous nightly, or when its prerelease exists.
`VERSION` does not change for a nightly.

Both workflows call `.github/workflows/build-archives.yml`, which holds the matrix.

CI (`.github/workflows/ci.yml`) runs the same install scripts on each push to `master` and on each
pull request into `master`, then the test suites, then the check of the installed prefix.

On Linux, a default run asks whether to link the libraries with sudo, and only when it has a
terminal:

```text
Link the libraries into /usr/local/lib with sudo, so programs find them without LD_LIBRARY_PATH? [y/N]
```

A yes runs `link-libraries`. Its sudo calls ask for the password on the terminal. Without a
terminal they use `sudo -n`, which fails at once instead of waiting. If sudo cannot get
permission, the step prints `But you promised ;(`, and the install carries on as it does
without sudo. A no, or no answer, links nothing into `/usr/local/lib`, and programs then need
`LD_LIBRARY_PATH=<prefix>/lib`. The symlinks point into the prefix, so they break if the
prefix is moved or removed. Naming the step with `--only link-libraries` runs it without the
question. `--sudo` answers yes without asking, for scripts.

The Linux install exits with status 1 when the libraries were asked for and could not be linked.
The install itself is complete then, with the prefix's own libraries, so the message says what to
set. A default install that answers no exits 0.

What is not done yet:

- **Target.** The scripts build the host target only. The release matrix has two targets, and
  ARM64 targets need a backend target entry, a runtime and standard library build, and a runner.
- **Uninstall of a source install.** `scripts/install.*` has no uninstall yet, so a prefix built from the source is removed by hand. Archives are uninstalled with their installer.
