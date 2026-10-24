# Native symbol names

Every Oxygen function has a native symbol, which the linker and the native
metadata use. Symbols are built by `Oxygen.Llvm.OxygenNameMangler`. The current
scheme is version 1, which every symbol starts with: `_Ox_v1_`. The version
changes whenever an encoding changes, so symbols from different versions never
match, and native metadata of another version is rejected.

A symbol is built from these parts:

```
symbol         := "_Ox_v1_" "m" name(module) [ "_c" name(type) | "_i" name(type) ] member
member         := "_f" name(function) signature [ specialization ]     function or method
                | "_k" signature                                       constructor
                | "_d"                                                 destructor
                | "_r"                                                 release glue of a class (what an object releases when it is freed)
signature      := "_p" count "_" type*
specialization := "_g" count "_" type*
name(x)        := length(x) x
```

- `name(...)` is an identifier with its length in front. A module name may
  contain dots, and the length covers them.
- `_c` gives the class that declares a method, a constructor or a destructor,
  and `_i` gives the interface that declares a method. The class's module comes
  before it. A method of a class is therefore never the same symbol as a
  function of a module with the same name as the class.
- Every list has its count in front: the parameters of a signature, and the
  arguments of a specialization. A function without parameters has `_p0_`.

## Type codes

| Type | Code |
| --- | --- |
| `i8` `i16` `i32` `i64` | `i8` `i16` `i32` `i64` |
| `u8` `u16` `u32` `u64` | `u8` `u16` `u32` `u64` |
| `usize` `isize` | `us` `is` |
| `f32` `f64` `bool` `void` | `f32` `f64` `b` `v` |
| `string` | `str` |
| pointer to `T` | `p` followed by the code of `T` |
| array of `N` elements of `T` | `a` `N` `_` followed by the code of `T` |
| generic parameter `T` | `g` name(`T`) |
| enum | `E` name(qualified name) |
| union, struct, class, interface | `U`, `S`, `C`, `I`, then name(qualified name), then `_a` count `_` followed by the type arguments when there are any |
| function pointer returning `R` with parameters `P1..Pn` | `F` followed by the code of `R`, then the parameter list written as in a signature: `_p` `n` `_` followed by the codes of `P1..Pn` |

The qualified name of a type includes its module, so two types with one name in
different modules encode differently.

## Examples

| Declaration | Symbol |
| --- | --- |
| `i32 main()` in module `program` | `_Ox_v1_m7program_f4main_p0_` |
| `f64 scale(f64 x)` in module `program` | `_Ox_v1_m7program_f5scale_p1_f64` |
| `i32 add(i32 a)` and `i32 add(f64 a)` | `_Ox_v1_m7program_f3add_p1_i32`, `_Ox_v1_m7program_f3add_p1_f64` |
| `i32 value()` in class `A` of module `program` | `_Ox_v1_m7program_c1A_f5value_p0_` |
| `i32 value()` in module `program.A` | `_Ox_v1_m9program.A_f5value_p0_` |
| constructor `A(i32 x)` of class `A` | `_Ox_v1_m7program_c1A_k_p1_i32` |
| destructor of class `A` | `_Ox_v1_m7program_c1A_d` |
| release glue of class `A` | `_Ox_v1_m7program_c1A_r` |

The last two rows of the method examples show why methods have a class segment:
`A.value` and the function `value` of module `program.A` would otherwise be
the same symbol.

## Guarantees

- Decoding a symbol gives one result, because every name is length-prefixed,
  every list has its count, and each type code begins with a tag that no other
  code begins with.
- Symbols of different functions differ. The compiler's test
  `AMethodAndAFunctionOfAModuleNamedLikeTheClassGetDifferentSymbols` checks
  one such pair, and LLVM would rename a duplicate symbol, so a duplicate shows
  up in the IR as a `.1` suffix.
- External functions keep the name they are given with `@extern`. They are not
  mangled.

## Versions

A change to any encoding is a new version. The version is written into every
symbol and into the native metadata's `formatVersion` (see
[native module metadata](native-module-metadata.md)). Metadata of another
version is rejected, because its symbols do not match this compiler's output.
