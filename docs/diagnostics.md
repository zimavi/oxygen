# Compiler diagnostics

The compiler reports source and build-input problems as structured diagnostics
instead of exposing implementation exceptions to Oxygen users. A diagnostic has
a phase, severity, message, source name, and source span. When a source span is
available, the CLI prints the surrounding source lines and an underline.

Diagnostics are produced during lexing, parsing, semantic analysis, LLVM
generation, native metadata loading, and native tool execution. Independent
front-end errors are collected where recovery is safe. Compilation does not
continue to LLVM generation when an error diagnostic exists.

Warnings are reported alongside errors and do not block compilation by
default. `--warnings-as-errors` makes warnings blocking diagnostics. Current
warnings include unused declarations and unreachable statements. Private
functions of the standard library (modules named `std` or `std.*`) are not
reported as unused, since a program uses only some of the overloads they
provide.

Exceptions remain reserved for invalid compiler API arguments, internal
invariants, and unrecoverable resource failures. They are not the normal
interface for invalid Oxygen source, malformed native metadata, unsupported
compiler input, or native tool failures.
