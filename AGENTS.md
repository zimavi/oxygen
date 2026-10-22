# AGENTS.md — Oxygen Programming Language

## 1. Project Overview

Oxygen is a compiled, statically typed programming language designed around a modern C-like syntax and native code generation.

The project aims to build a complete programming language, including:

- A lexer and parser.
- A type system and semantic analysis.
- A compiler written in C#.
- LLVM IR generation using LLVMSharp.
- Native executable generation through LLVM's backend and toolchain.
- A runtime library, initially implemented in C.
- A standard library, eventually implemented primarily in Oxygen itself.
- A command-line compiler and development toolchain.

Oxygen should have a straightforward syntax, explicit semantics, predictable compilation, and direct access to native capabilities.

It is not intended to be a Rust clone, a C++ clone, or a language that hides its implementation behind excessive abstractions.

When contributing to Oxygen, prioritize a coherent language design and a maintainable compiler architecture over implementing features as quickly as possible.

## 2. Core Design Principles

All contributions must follow these principles.

### 2.1. Modern C-like syntax

Oxygen uses C-style syntax with braces, semicolons, and familiar control-flow constructs.

The language should feel familiar to programmers who know C, C#, or similar languages, without inheriting every historical limitation of C.

Examples of intended syntax:

```oxygen
i32 main() {
    i32 answer = 42;
    return answer;
}
```

A variable can also use type inference:

```oxygen
i32 main() {
    var answer = 42;
    return answer;
}
```

An immutable variable can use `const`:

```oxygen
const i32 answer = 42;
```

Design constraints:

- Do not introduce `let` as a variable declaration keyword.
- Use `var` for type-inferred variable declarations.
- Use explicit types where the programmer specifies a variable's type.
- Use braces for blocks and C-style control flow.
- Require semicolons for statements where appropriate.
- Prefer return-type-first function declarations.
- Do not introduce Rust-specific syntax or semantics merely because they are common in modern language design.

The examples above describe the intended language direction. Verify the current parser and implementation before assuming every example is already supported.

### 2.2. Static typing

Oxygen is statically typed.

The compiler must determine the types of expressions and validate their correctness before generating executable code.

Semantic analysis should handle:

- Variable declarations and references.
- Type inference.
- Function declarations and calls.
- Return-type checking.
- Operator compatibility.
- Scope resolution.
- Argument and parameter compatibility.
- Invalid conversions.
- Duplicate declarations and ambiguous references.

Do not rely on LLVM to perform language-level type checking. LLVM IR generation should receive semantically validated, typed program representations.

### 2.3. Explicit language semantics

Every language feature must have documented, deterministic behavior.

Avoid silently introducing:

- Implicit conversions between unrelated types.
- Implicit changes to variable mutability.
- Unspecified overload resolution rules.
- Undefined behavior as a shortcut for an incomplete implementation.
- Backend-dependent language semantics.

When a language feature is not yet defined, identify the missing design decision instead of inventing an arbitrary rule.

## 3. Compiler Architecture

The compiler is implemented in C#.

LLVMSharp is the intended interface for generating LLVM IR and interacting with LLVM's compilation infrastructure.

The compiler should use distinct stages with clearly defined responsibilities.

The intended compilation pipeline is:

```text
Source Files
    |
    v
Lexer
    |
    v
Parser
    |
    v
Abstract Syntax Tree (AST)
    |
    v
Name Resolution
    |
    v
Semantic Analysis and Type Checking
    |
    v
Typed AST / Intermediate Representation
    |
    v
LLVM IR Generation (LLVMSharp)
    |
    v
LLVM Verification
    |
    v
Native Code Generation and Linking
    |
    v
Native Executable
```

The precise boundaries between stages can evolve, but their responsibilities must remain separate.

### 3.1. Lexer

The lexer converts source text into tokens.

It should be responsible for:

- Recognizing keywords and identifiers.
- Recognizing numeric and string literals.
- Recognizing operators and punctuation.
- Tracking source locations.
- Reporting invalid tokens.

The lexer must not perform semantic analysis or generate LLVM IR.

### 3.2. Parser

The parser converts tokens into an AST.

It should:

- Implement Oxygen's grammar.
- Enforce syntactic structure.
- Produce useful syntax errors.
- Preserve source locations for diagnostics.
- Avoid performing backend-specific work.

Do not embed LLVM API calls in parser code.

### 3.3. AST

The AST represents the syntactic structure of an Oxygen program.

Keep it independent of LLVMSharp.

AST nodes should represent language constructs such as:

- Functions and parameters.
- Variable declarations.
- Blocks and statements.
- Literals and identifiers.
- Unary and binary expressions.
- Function calls.
- Return statements.
- Conditional statements and loops.

Only introduce nodes for language features that are implemented or intentionally being developed.

Do not make the AST a thin wrapper around LLVM instructions. The AST must represent Oxygen semantics, not LLVM implementation details.

### 3.4. Semantic analysis

Semantic analysis validates the AST and resolves language-level meaning.

It should determine:

- Which declaration an identifier refers to.
- The type of each expression.
- Whether a function call is valid.
- Whether return statements match the enclosing function's return type.
- Whether declarations conflict.
- Whether operations are valid for their operand types.
- Whether a program satisfies the language's current rules.

LLVM IR generation must not be responsible for discovering ordinary semantic errors.

Prefer diagnostics that point to the relevant source location and explain the actual problem.

### 3.5. Typed intermediate representation

Use a typed AST or another suitable compiler-internal representation between semantic analysis and LLVM IR generation.

This layer must preserve the information needed by the backend without forcing the backend to repeat semantic analysis.

It should make types, resolved declarations, and relevant conversion decisions explicit.

Keep this representation independent of LLVMSharp wherever practical.

### 3.6. LLVM backend

Use LLVMSharp for LLVM API access and IR generation.

The backend is responsible for translating validated Oxygen constructs into LLVM IR.

It should handle:

- Mapping Oxygen types to LLVM types.
- Creating LLVM functions and global entities.
- Generating basic blocks and instructions.
- Generating control flow.
- Generating function calls.
- Generating return instructions.
- Managing LLVM modules and contexts.
- Verifying generated IR.
- Preparing modules for native compilation.

Do not mix LLVMSharp API details into the lexer, parser, or language's type system.

Use a backend abstraction where it provides a real architectural benefit. Avoid adding excessive interfaces and abstractions that do not solve an actual problem.

### 3.7. LLVMSharp version and compatibility

LLVMSharp is the intended LLVM integration, but its APIs and supported LLVM versions must be verified against the repository's actual package configuration.

Before implementing backend functionality:

1. Inspect the existing project files and NuGet package references.
2. Identify the installed LLVMSharp version and corresponding LLVM version.
3. Use APIs supported by those versions.
4. Follow the repository's existing interop and resource-management conventions.
5. Avoid copying examples written for incompatible LLVMSharp versions.

Do not invent LLVMSharp method names or assume that APIs from LLVM's C++ interface are directly available through LLVMSharp.

Keep LLVM resources such as contexts, modules, builders, and target machines under clear ownership. Dispose of owned native resources appropriately and avoid invalid lifetimes or double disposal.

## 4. Types

Oxygen is intended to support explicit, fixed-width integer types.

The initial type system should remain small and understandable.

Examples of intended type names include:

- `i8`, `i16`, `i32`, `i64`
- Unsigned integer counterparts, such as `u8`, `u16`, `u32`, and `u64`
- Boolean values
- Floating-point types
- `void` for functions that do not return a value

These names describe the intended design direction. Check the current implementation before treating the entire list as finalized.

When implementing types:

- Define a canonical internal representation for each type.
- Specify how each type maps to LLVM.
- Keep signedness explicit where applicable.
- Validate operators and conversions before code generation.
- Avoid relying on host-language numeric defaults to define Oxygen semantics.
- Do not assume that every LLVM type corresponds directly to an Oxygen type.

The language's type system must remain independent of the C# types used to implement the compiler.

For example, an Oxygen `i32` is a language-level type, not simply a C# `int` reused throughout the implementation.

## 5. Variables and Scopes

Oxygen supports explicitly typed declarations and is intended to support type inference through `var`.

Illustrative examples:

```oxygen
i32 count = 10;
var result = count;
const i32 limit = 100;
```

The compiler must distinguish between:

- A declared type.
- An inferred type.
- A variable's mutability.
- The value assigned to the variable.

Do not implement `var` as a separate runtime type. It is a declaration form whose type is determined during compilation.

Variable lookup must respect lexical scopes.

A declaration in an inner scope should not accidentally overwrite the symbol associated with an outer scope.

Define duplicate-declaration and shadowing rules explicitly before changing scope resolution.

## 6. Functions

Functions are first-class elements of the language's compilation model, although function values and first-class function support are not assumed to be implemented yet.

An intended function declaration looks like this:

```oxygen
i32 add(i32 a, i32 b) {
    return a + b;
}
```

A function call looks like this:

```oxygen
i32 result = add(20, 22);
```

The compiler should maintain function metadata, including:

- Source-level name.
- Parameter types and order.
- Return type.
- Declaring module or namespace, if applicable.
- Resolved symbol identity.
- Generated native symbol name.

Function declarations and calls must be validated semantically before code generation.

### 6.1. Function overloading

Oxygen is intended to support a limited form of static function overloading.

Overloads may differ by parameter types.

For example:

```oxygen
void print(i32 value);
void print(f64 value);
void print(bool value);
```

These declarations illustrate the intended direction; they are not a guarantee that the syntax or feature is already implemented.

Initial overload-resolution rules should be simple:

1. Resolve the function name and its visible overload candidates.
2. Compare the number of arguments with each candidate's parameter count.
3. Check argument types against parameter types.
4. Select a uniquely matching overload.
5. Report an error if no overload matches.
6. Report an ambiguity error if multiple candidates match equally well.

Initially, do not introduce implicit conversions merely to make an overload match.

Return type alone must not distinguish overloads. Two functions with the same name and parameter types cannot be distinguished by their return types.

Overload resolution belongs in semantic analysis, not in the LLVM backend.

### 6.2. Function overloading and native symbols

Different Oxygen overloads must receive different native symbol names.

Do not use the source-level function name directly as the unique linker symbol for every overload.

Use a deterministic, versioned mangling scheme. The proposed scheme is:

```text
ox_v1_m<module>_f<function>_p<count>_<parameter-types>
```

This is a conceptual format, not a requirement to concatenate ambiguous raw names literally.

The implementation must define:

- Unambiguous identifier encoding, preferably with length prefixes.
- Canonical type encodings.
- Parameter count encoding.
- Module or namespace encoding where relevant.
- A stable encoding for every supported parameter type.

Return types are excluded from overload identity.

For example, the mangling implementation must ensure that overloads such as:

```oxygen
void print(i32 value);
void print(f64 value);
```

produce distinct symbols.

Do not claim the proposed scheme is finalized if the repository already contains a different documented scheme. Inspect existing code and reconcile the implementation before changing it.

### 6.3. ABI separation

Keep three naming domains distinct:

1. Oxygen's source-level identifiers.
2. Oxygen's mangled native symbols.
3. External ABI symbols, including C runtime functions and the native executable entry point.

An Oxygen function should not need to use C++ linkage syntax merely to avoid symbol collisions.

External functions should be represented explicitly in the compiler's symbol and ABI model. Do not treat ordinary Oxygen functions as C functions by default.

## 7. Program Entry Point

The intended source-level entry point is:

```oxygen
i32 main() {
    return 0;
}
```

The final native executable must expose the entry point expected by the selected platform and toolchain.

Do not assume that the Oxygen source-level `main` must itself be exported under the native symbol `main`.

Instead, use a generated native entry-point wrapper.

Conceptually:

```text
Operating System / C Runtime
             |
             v
       Native main
             |
             v
    Oxygen initialization
             |
             v
     Mangled Oxygen main
             |
             v
      Return exit code
```

The exact initialization and shutdown sequence can evolve as the runtime develops.

The compiler or linker integration should ensure that:

- The native executable has the expected entry point.
- The Oxygen function is called using its mangled symbol.
- The Oxygen return value is propagated correctly.
- Runtime initialization occurs before user code when required.
- Runtime shutdown occurs according to the defined lifecycle.

Do not solve entry-point naming collisions by requiring the user to declare Oxygen's `main` as an external C function.

Keep platform-specific startup and linking logic separate from ordinary function compilation.

## 8. Runtime Architecture

The initial runtime should be implemented in C.

This provides a practical bootstrap layer for operations that depend on the host operating system or native ABI.

The runtime may eventually be partially or primarily implemented in Oxygen itself.

This transition must be incremental. Do not introduce a circular dependency in which the compiler requires the completed Oxygen runtime to compile the runtime itself.

### 8.1. C bootstrap runtime

The initial C runtime should provide a small set of primitives that Oxygen code can call through a defined ABI.

Examples include:

- Writing bytes to standard output.
- Writing bytes to standard error.
- Process termination, if required.
- Low-level allocation, if required by supported language features.
- Other platform-dependent operations as the language develops.

Keep the runtime small. Do not duplicate language-level abstractions in C if they can be implemented in Oxygen.

C runtime symbols must have stable, explicitly defined names and must not collide with generated Oxygen symbols.

### 8.2. Standard output and printing

The eventual public printing API should be a language-level standard library feature, rather than a collection of unrelated C functions exposed directly to every Oxygen program.

A desirable architecture is:

```text
Oxygen program
      |
      v
Oxygen standard library: print(...)
      |
      v
Formatting and type-specific helpers
      |
      v
C runtime: oxygen_write_stdout(...)
      |
      v
Operating system
```

The exact public syntax and module organization for printing remain subject to the language's evolving standard-library design.

The low-level C output primitive should accept a pointer to bytes and an explicit length.

Conceptually:

```c
void oxygen_write_stdout(const unsigned char *data, size_t length);
```

The actual declaration must use the project's chosen ABI types and header conventions.

Prefer length-based output over `puts` because a length-based interface does not require every string to be null-terminated and can support embedded zero bytes.

The compiler and standard library must define how Oxygen strings are represented before implementing the final string-printing API.

### 8.3. Type-specific formatting

Different values may require different formatting logic.

The standard library can provide overloaded `print` functions or internal formatting helpers for different types.

For example:

- Integers can be formatted as decimal text.
- Floating-point values require a defined formatting policy.
- Booleans can be formatted as textual values.
- Strings can be written directly when their representation permits it.

Do not assume that LLVM automatically supplies printing or formatting facilities. LLVM generates code; the runtime and standard library implement the required behavior.

During early development, C helpers using facilities such as `snprintf` may be appropriate for numeric formatting. These are implementation details, not permanent language semantics.

Keep formatting logic in the standard library when practical, and keep platform-dependent output primitives in the C runtime.

## 9. Strings, Memory, and Native Interoperability

Strings, pointers, arrays, structs, and other compound types should be introduced deliberately.

Do not assume that the initial language already has a finalized memory model.

Before implementing a feature that depends on memory layout, define the relevant semantics.

For strings, decide and document:

- The internal representation.
- Whether lengths are stored explicitly.
- Whether embedded zero bytes are supported.
- How string literals are represented.
- Ownership and lifetime rules, where applicable.
- How strings cross the C ABI boundary.

For pointers and structs, define:

- Type representation.
- Layout and alignment requirements.
- Mutability and aliasing rules, if applicable.
- ABI compatibility requirements.
- Memory ownership and allocation responsibilities.

Do not implement a garbage collector, reference counting, borrow checker, or another ownership system simply because it is common in other languages. Such features require explicit design decisions.

Use the simplest implementation that satisfies the language's documented semantics.

## 10. Initial Feature Priorities

Develop the language incrementally, keeping each milestone compilable and testable.

The intended progression is:

### Phase 1: Compiler foundation

- Project structure and compiler command-line entry point.
- Lexer and parser.
- AST.
- Diagnostics and source locations.
- Primitive types.
- Variable declarations and scopes.
- Semantic analysis.

### Phase 2: Functions and native execution

- Function declarations and calls.
- Parameter and return-type checking.
- Typed intermediate representation.
- LLVMSharp integration.
- LLVM IR generation and verification.
- Native executable generation.
- Native entry-point wrapper.
- Basic control flow.

### Phase 3: Runtime and basic standard library

- C bootstrap runtime.
- Standard output and error.
- Basic string support.
- Printing and formatting.
- Runtime initialization where necessary.

### Phase 4: Richer language features

- Structs.
- Arrays.
- Pointers.
- Enums.
- Function overloading.
- Improved module and namespace support.

### Phase 5: Toolchain and standard library growth

- Imports and separate compilation.
- A more complete standard library.
- Improved diagnostics.
- Cross-platform native compilation.
- Build-system integration.
- Incremental compilation and other developer tooling, when justified.

This roadmap is a planning guide, not a requirement to implement features in a particular order regardless of the existing codebase.

Preserve working functionality and adapt the sequence to the repository's current state.

## 11. Error Handling and Diagnostics

Compiler errors should be reported to the programmer, not exposed as unhandled C# exceptions.

Examples of errors that should produce useful diagnostics:

- Unexpected tokens.
- Undefined identifiers.
- Duplicate declarations.
- Invalid operand types.
- Missing function overloads.
- Ambiguous overload resolution.
- Incorrect function arguments.
- Incompatible return types.
- Unsupported language constructs.
- LLVM verification failures.

Include source locations whenever available.

Distinguish between:

1. User errors in Oxygen source code.
2. Compiler implementation bugs.
3. LLVM integration or toolchain failures.

Do not silently ignore errors or generate misleading fallback IR.

Internal exceptions may be appropriate for compiler invariants, but ordinary invalid source programs should result in controlled diagnostics.

## 12. Testing Requirements

Every language feature must have appropriate tests.

Prefer a layered testing strategy.

### Lexer tests

Test token recognition, keywords, operators, literals, and invalid input.

### Parser tests

Test valid syntax, precedence, associativity, block structure, and syntax errors.

### Semantic tests

Test type checking, name resolution, scopes, function calls, overload resolution, and invalid programs.

### LLVM tests

Test IR generation for supported constructs and verify that generated modules are valid.

### Integration tests

Compile Oxygen programs and execute the resulting native binaries.

Examples of important integration tests include:

- A program that returns zero.
- Integer arithmetic.
- Variable declarations.
- Function calls.
- Conditional branches.
- Loops.
- Runtime output.
- Overloaded function calls, once implemented.
- Native entry-point and exit-code handling.

Test invalid programs separately and verify that they fail with useful diagnostics.

Do not rely solely on snapshots of generated LLVM IR. Tests should validate program behavior as well as intermediate output.

## 13. Coding Conventions and Repository Changes

Before making changes, inspect the existing repository.

Determine:

- The solution and project structure.
- The C# target framework.
- The LLVMSharp and LLVM versions.
- Existing compiler stages and abstractions.
- Current naming conventions.
- Existing tests.
- Existing runtime declarations and ABI assumptions.

Follow the established conventions unless there is a concrete reason to change them.

When modifying the codebase:

- Keep changes focused.
- Avoid unrelated refactoring.
- Do not duplicate existing functionality.
- Avoid introducing unnecessary dependencies.
- Preserve existing public interfaces unless a change is intentional.
- Keep the compiler buildable after each meaningful change.
- Add tests alongside new functionality.
- Update documentation when language behavior changes.

Do not create placeholder implementations that appear functional but silently omit important behavior.

If a feature cannot yet be implemented correctly, report the limitation and explain what is missing.

## 14. Rules for AI Coding Agents

Agents working on Oxygen must follow these rules.

### 14.1. Inspect before implementing

Never assume that the repository is empty or that the latest feature discussed is already implemented.

Inspect the relevant source files, project files, tests, and documentation before proposing or applying changes.

Do not rewrite existing components merely because a different design is possible.

### 14.2. Distinguish decisions from proposals

Not every idea in this document is a finalized language specification.

Treat the following as established project direction:

- Oxygen is a statically typed compiled language.
- Its syntax is modern-C-like.
- The compiler is written in C#.
- LLVMSharp is the intended LLVM integration.
- The initial runtime is implemented in C.
- The standard library should provide higher-level functionality.
- Oxygen functions require a clear distinction between source-level and native symbols.
- The compiler should separate semantic analysis from LLVM IR generation.

Treat exact syntax for unfinished features, the final mangling format, string representation, memory model, ABI details, and other unspecified semantics as decisions that may require further design.

Do not silently convert a proposal into a permanent language rule.

### 14.3. Prefer minimal, complete implementations

Implement the smallest complete version of a feature that fits the architecture.

For example, implementing integer addition should include parsing, type checking, LLVM generation, and tests where appropriate. It should not require implementing structs, generics, or a complete standard library first.

Avoid broad rewrites and speculative abstractions.

### 14.4. Do not bypass compiler stages

Do not implement type checking inside the LLVM backend.

Do not perform overload resolution by selecting an arbitrary function with the correct name.

Do not use LLVM types as the sole representation of Oxygen's semantic types.

Do not emit IR for invalid source programs merely to make compilation continue.

### 14.5. Verify changes

After implementing a feature:

1. Build the relevant projects.
2. Run the relevant tests.
3. Add tests for the new behavior.
4. Inspect generated IR when relevant.
5. Verify that LLVM accepts the IR.
6. Run native integration tests when the toolchain is available.

If a command cannot be run because a dependency or toolchain is missing, state that limitation explicitly.

Never claim a build or test succeeded unless it was actually executed successfully.

### 14.6. Explain architectural trade-offs

When a change affects language semantics, ABI, symbol mangling, memory representation, compiler stages, or runtime boundaries, explain the consequences.

Prefer designs that keep future extensions possible without implementing speculative functionality prematurely.

If a requested implementation conflicts with these instructions or an existing language decision, identify the conflict and recommend a coherent resolution rather than silently choosing one.

## 15. Definition of Done

A language feature is not complete merely because its syntax parses.

Where applicable, a feature is complete when:

- Its syntax is defined.
- Its semantics are documented.
- Its AST representation is correct.
- Its types and name resolution are validated.
- Its LLVM IR generation is implemented.
- Generated IR passes verification.
- Native code can be generated.
- Runtime interactions follow the documented ABI.
- Valid programs behave as expected.
- Invalid programs produce useful diagnostics.
- Automated tests cover the behavior.

Some features may require fewer stages, but completion must be judged against their actual responsibilities.

## 16. Final Guideline

Oxygen should evolve as a coherent programming language, not merely as a collection of syntax features that happen to produce LLVM IR.

Keep language semantics, compiler architecture, LLVM integration, runtime functionality, and the standard library clearly separated.

When uncertain, inspect the implementation, identify the missing design decision, explain the trade-offs, and choose the simplest design consistent with Oxygen's established direction.

# Oxygen Engineering and Maintainability Policy

## 1. Purpose

Oxygen is a compiled programming language implemented in C#. Its compiler produces LLVM IR and uses native toolchain processes to compile and link native binaries.

The goal is to develop Oxygen through specification-driven development while preserving a codebase that is understandable, testable, reviewable, and safe to extend by human contributors.

Correct output is necessary but not sufficient. Every implementation must also respect architectural boundaries, make its behavior understandable, and provide sufficient evidence for human review.

Maintainability is a design constraint, not a cleanup task to postpone until later.

## 2. Core principles

### 2.1 Explicitness over cleverness

Prefer clear control flow, descriptive names, explicit data flow, and visible failure handling.

Do not introduce clever abstractions, implicit behavior, hidden global state, or complicated generic frameworks when a simpler implementation communicates the intent more clearly.

Avoid both giant multipurpose classes and excessive fragmentation into tiny classes with no meaningful independent responsibility.

### 2.2 Cohesion and limited coupling

Each component must have a clear responsibility and a defined boundary.

Components should depend on stable contracts rather than unrelated implementation details.

A change should affect only the components that genuinely need to understand it. If a feature requires changes across several compiler stages, explain why each stage is affected.

Do not duplicate language rules, serialization rules, type-conversion logic, or other authoritative behavior across multiple components.

### 2.3 Abstractions must earn their existence

Introduce an abstraction when it provides a concrete benefit, such as enforcing an invariant, isolating an external dependency, eliminating meaningful duplication, enabling independent testing, or representing a stable domain concept.

Do not introduce interfaces, factories, registries, service locators, generic frameworks, or additional projects merely to make the architecture appear more sophisticated.

Do not generalize for hypothetical future requirements without a concrete architectural reason.

### 2.4 Human-readable implementation

A reviewer must be able to understand why the implementation works without reconstructing the entire compiler.

Use domain terminology consistently. Keep functions focused on a coherent operation. Make important assumptions, invariants, side effects, and failure conditions explicit.

Comments should explain intent, non-obvious constraints, invariants, and reasons behind design decisions. Do not use comments as a substitute for understandable code.

Avoid unexplained magic values, unnecessary indirection, deeply nested control flow, and methods that perform several unrelated operations.

Do not optimize for the smallest possible diff or lowest line count at the expense of comprehension.

### 2.5 Explicit failure behavior

Failures must be represented and handled consistently with existing Oxygen conventions.

Do not silently discard errors, convert failures into success-shaped values, use broad exception handling to conceal defects, or fall back to misleading defaults.

Validate untrusted inputs at appropriate boundaries, especially source files, serialized native library metadata, external tool arguments, and compiler-generated artifacts.

Distinguish invalid user input, invalid source programs, malformed library metadata, unavailable toolchain dependencies, and internal compiler defects.

Follow existing error and result types unless the specification explicitly justifies a change.

## 3. Mandatory compiler architecture

The logical compilation pipeline is:

1. CLI argument processing and command dispatch.
2. Source acquisition and reading.
3. Lexical analysis.
4. Syntactic parsing and source AST construction.
5. Native library parsing and metadata ingestion, including Oxygen type information, generic AST trees, and other serialized compiler metadata.
6. Semantic analysis.
7. LLVM IR generation.
8. Native toolchain invocation through Clang for compilation and linking.

The exact execution order may accommodate dependency loading, incremental compilation, or other documented requirements. Do not assume that every stage must be a separate project or process.

Every stage must have an explicit responsibility, defined inputs and outputs, and documented failure behavior.

### 3.1 CLI and orchestration

Own argument parsing, command selection, configuration, and coordination of compilation tasks.

Do not implement language syntax parsing, semantic rules, or LLVM lowering in the CLI.

### 3.2 Source acquisition

Own access to source files and the representation of source content and identity.

Keep filesystem access separate from lexical and syntactic analysis so those stages can be tested using in-memory inputs.

### 3.3 Lexer

Convert source characters into tokens according to the language specification.

Own lexical errors and token source locations. Do not perform semantic analysis or emit LLVM IR.

### 3.4 Parser

Convert tokens into the source AST according to the language grammar.

Own syntax errors, source locations, and any specified parser recovery behavior. Do not implement type checking, LLVM lowering, or native process execution.

### 3.5 Native library and metadata ingestion

Decode, validate, and represent serialized Oxygen library metadata.

Handle type information, generic definitions and AST trees according to the metadata format specification.

Do not trust malformed or incompatible metadata. Report meaningful diagnostics for invalid formats, unsupported versions, missing required information, and structurally invalid data.

Keep serialization and deserialization rules separate from language semantic rules. Shared AST or type representations are acceptable where they preserve clear ownership and invariants.

### 3.6 Semantic analysis

Own name resolution, type checking, overload resolution, generic validation, and other compile-time language rules.

Represent semantic information explicitly. LLVM generation must not need to rediscover facts that semantic analysis is responsible for establishing.

Semantic analysis may consume source ASTs and validated library metadata through defined contracts.

### 3.7 LLVM IR generation

Translate semantically valid constructs into LLVM IR.

Follow the language's established lowering rules and runtime ABI. Do not silently invent semantic behavior or use IR generation to bypass diagnostics that belong to semantic analysis.

If the current architecture permits IR generation to receive invalid or partially analyzed nodes, document and enforce the relevant invariants explicitly.

### 3.8 Native toolchain integration

Own invocation of Clang, command construction, process lifecycle, exit status, and native build diagnostics.

Do not place compiler language rules or AST transformations in the toolchain integration layer.

Handle paths and process arguments safely and avoid relying on the current working directory unless the behavior is explicit.

## 4. Contracts and data ownership

For each component, define:

- Its responsibility and explicit non-responsibilities.
- The types and invariants of its inputs.
- The types and guarantees of its outputs.
- The errors or diagnostics it can produce.
- The resources or state it owns.
- Its dependencies on other components.
- How it can be tested independently.

Do not allow downstream stages to depend on undocumented internal details of upstream implementations.

Do not add redundant representations of the same information unless each representation has a clearly justified purpose.

Where source ASTs, semantic information, generic AST trees, and serialized metadata overlap, establish which representation is authoritative and who owns transformations between representations.

## 5. Spec-driven development procedure

Before implementing a non-trivial change:

1. Inspect the relevant source code, existing specifications, tests, and established conventions.
2. Identify the responsible components and the architectural contracts affected.
3. Write or update a specification describing the behavior, design, failure modes, and acceptance criteria.
4. Identify affected public APIs, language rules, metadata formats, runtime ABI, and compatibility requirements.
5. Define a testing strategy before writing the implementation.
6. Implement the smallest coherent change that satisfies the specification.
7. Run the relevant tests and available build or formatting checks.
8. Review the final diff against the specification and this policy.
9. Report the implementation, design decisions, tests performed, and remaining limitations.

For a small, localized change, keep the specification lightweight. Do not create unnecessary documents or procedural overhead for trivial fixes.

If the specification conflicts with the existing architecture or an established language rule, stop and explain the conflict before silently choosing a direction.

Do not implement unrelated improvements while working on a feature. Propose independent refactors separately unless they are required for correctness or a clearly defined architectural boundary.

## 6. Implementation quality requirements

- Follow established naming, formatting, and project conventions.
- Reuse existing domain models and utilities where they fit the requirement.
- Avoid duplicate implementations of the same rule.
- Avoid global mutable state and hidden dependencies.
- Keep side effects at explicit boundaries.
- Avoid unnecessary reflection, runtime type-name inspection, or other mechanisms that conflict with the project's NativeAOT requirements.
- Avoid unnecessary allocations and repeated work in compiler hot paths, but do not sacrifice clarity for unmeasured performance gains.
- Keep configuration, language semantics, metadata formats, and toolchain behavior separate.
- Preserve backward compatibility unless a breaking change is explicitly specified.
- Do not introduce dependencies without explaining their purpose and maintenance cost.
- Do not claim that code, tests, or builds succeeded unless they were actually run and their results observed.

When a function or class grows beyond a comfortable scope, evaluate whether its responsibilities should be separated. Do not apply arbitrary line-count limits without considering cohesion and readability.

## 7. Testing requirements

Test behavior at the narrowest useful boundary and verify integration behavior where components interact.

Depending on the change, tests should cover:

- Valid inputs and expected outputs.
- Invalid inputs and meaningful failure behavior.
- Boundary conditions and malformed data.
- Relevant regression cases.
- Interactions with adjacent pipeline stages.
- User-visible diagnostics and exit status where applicable.

Use lexer, parser, semantic, metadata, LLVM generation, and end-to-end tests according to the component being changed.

Prefer assertions about language semantics, observable behavior, documented contracts, and stable IR properties over assertions about private implementation details.

Tests must not merely reproduce the implementation's assumptions. Their expected behavior must be traceable to the specification.

## 8. Human review requirements

Before presenting a change for review, verify:

- The implementation satisfies every applicable acceptance criterion.
- Each modified component has a justified responsibility.
- Architectural boundaries remain intact or changes to them are explicitly justified.
- No unrelated changes or accidental duplication were introduced.
- Errors and edge cases are handled intentionally.
- Tests provide evidence for the behavior being changed.
- Public API, metadata, language semantics, runtime ABI, and compatibility implications are documented where relevant.
- The diff can be understood without reading unrelated parts of the repository.

Summarize the design rationale, not just the list of files modified.

If the implementation is correct but introduces significant complexity, explain that complexity and why it is necessary.

## 9. Stop conditions

Do not proceed with a major design decision when essential information is missing and a wrong assumption could cause architectural damage.

Instead, identify the uncertainty, present the smallest set of reasonable alternatives, explain the trade-offs, and request clarification when necessary.

Do not invent requirements, silently weaken acceptance criteria, bypass failing tests, suppress diagnostics to make a build pass, or perform broad refactoring under the label of a localized feature.

## 10. Definition of maintainable completion

A change is complete only when:

1. The specified behavior is implemented.
2. Relevant automated checks have been executed, with failures reported honestly.
3. The architectural boundaries are preserved or deliberately revised.
4. The implementation and tests are understandable to another contributor.
5. Relevant compatibility and failure behavior are addressed.
6. The final change is traceable to its specification.

Correctness, maintainability, and reviewability are independent requirements. None substitutes for the others.
