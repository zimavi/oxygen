# Type aliases

A type alias gives another name to a type:

```oxygen
type UserId = u64;
type IntPointer = i32*;

UserId next(UserId id) {
    return id + 1;
}
```

## Meaning

An alias is not a new type. `UserId` and `u64` are the same type, so a value of one
passes where the other is written, with no conversion and no diagnostic. This
follows the rule that values of unrelated types never convert implicitly.

The target can be any type: a primitive, a pointer, an array, a string, a struct,
a class, a union, an enum or an interface. An alias of a nominal type (a class,
struct, union, enum or interface) can be used wherever that type can, including
the forms that name a type in an expression: a construction (`new Tally(5)`,
`Point { x: 1 }`, `Option.Some(...)`), a static member (`Counter.count`) and a
base type.

An alias of a type that is not nominal (a primitive, a pointer, an array or a
string) can be used only where a type is written, such as a declaration, a
parameter, a return type or a type argument. Using it in an expression is an
error.

## Syntax and scope

`type Name = Target;` is a declaration at module scope. A `public` alias is
exported like the other type declarations, and a private one is only visible in
its module (see [modules](modules.md#type-names)). The target is written as any type
is written.

An alias may name another alias. A cycle of aliases (`type A = B; type B = A;`)
is an error at the first alias of the cycle.

An alias shares its name with no other type of its module: a struct, class, union,
enum or interface of the same name is an error at the alias.

## Names from imports

An alias is a type name, so the module-scoped rules apply to it (see
[modules](modules.md#type-names)). When two visible types, aliases included, have one
name, the name is ambiguous at the use, and a qualified name or an import alias
picks one.

## Libraries

A library carries all of its aliases with its generic declarations, since a
declaration it carries may use one. A program that imports the library can then
use the alias. A library's aliases are resolved only when the program uses them, so
an alias of an enum, which a library does not carry yet, is an error only in a
program that uses that alias.

## Not in this version

- Generic aliases (`type Pair<T> = ...`) are not supported. An alias names a type
  with no type parameters of its own. An alias of a generic type's instance,
  such as `type IntBox = Box<i32>;`, names that instance and can be used only as a
  type, not with type arguments.
- Aliases of function pointer types wait for function pointers.
