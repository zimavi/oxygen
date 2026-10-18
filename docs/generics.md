# Generics

Classes, interfaces, unions, structs, and functions may declare type
parameters:

```oxygen
class Box<T> {
    T value;
    Box(T value) { this.value = value; }
    T get() { return value; }
}

T identity<T>(T value) {
    return value;
}
```

Type arguments are supplied explicitly, or inferred for a function call or a
constructor call from the argument types:

```oxygen
i32 main() {
    Box<i32> box = new Box<i32>(42);
    var copy = identity(box.get()); // identity<i32>
    return copy;
}
```

Generics are resolved entirely at compile time. There is no runtime type
argument, no boxing, and no shared code between instantiations.

## Constraints

A type parameter may require that its type argument implements an interface:

```oxygen
void show<T : Printable>(T value) {
    value.write();
}
```

Each instantiation is checked against the constraint. A type that does not
implement the interface is an error at the point of instantiation:

```text
main.oxy:4:5: error: Type 'Plain' does not satisfy the constraint 'Printable' of type parameter 'T'
```

Primitive types and `string` implement `std.io.Printable` by a built-in rule,
and classes implement the interfaces they declare, including interfaces
inherited from base classes. Inside an instantiation the body is checked with
the concrete type, so `value.write()` on an `i32` resolves to the `write`
function for `i32`.

A method call on a primitive, string, or struct receiver names a free function
whose first parameter has exactly the receiver's type. The receiver is not
converted, so `value.write()` on an `i32` never selects a `write(i64)`
overload through widening. For primitive and string receivers the candidates
also include the private `write` functions of `std.io`, which implement
`Printable` for those types; ordinary calls such as `io.write(value)` cannot
name them.

## Compilation model

Oxygen compiles generics by monomorphization: each distinct set of type
arguments produces its own concrete declaration, and only those concrete
declarations reach the backend.

```text
Declarations collected (generic and non-generic)
    |
    v
Non-generic bodies type-checked
    |
    v
Instantiation worklist, until no new instantiation appears:
  concrete type arguments
    -> clone the declaration's members
    -> type-check the clone with the parameters bound
    -> queue the generic types that checking revealed
    |
    v
Instantiations lowered to concrete named declarations
    |
    v
LLVM IR generation
```

A generic declaration's body is type-checked once per instantiation, with its
type parameters bound to concrete types. This matters because a body may use a
generic type that appears nowhere in a signature:

```oxygen
class List<T> {
    Iterator<T> iter() {
        return new ListIterator<T>(this); // ListIterator<T> is only used here
    }
}
```

Checking `List<i32>` discovers `ListIterator<i32>`, which is then queued and
instantiated in turn. The worklist repeats until it reaches a fixed point, so
instantiation chains of any depth are followed, including generic functions
called from generic bodies (`memory.alloc<T>` inside `List<T>` becomes
`memory.alloc<i32>`).

Each instantiation is a separate set of syntax nodes. Semantic facts —
expression types, resolved calls, method receiver types — are recorded per node
instance, so `Box<i32>.get` and `Box<i64>.get` never share a type.

## Specialized names

An instantiated aggregate is given a generated name derived from its type
arguments, in the form `<name>__ox_<arguments>`. Aggregate arguments are encoded
with a length prefix, so different argument groupings cannot collide:

| Oxygen type           | Specialized name              |
| --------------------- | ----------------------------- |
| `Box<i32>`            | `Box__ox_i32`                 |
| `Box<i32*>`           | `Box__ox_pi32`                |
| `Pair<One<i32>, i64>` | `Pair__ox_11One__ox_i32_i64`  |
| `One<Pair<i32, i64>>` | `One__ox_16Pair__ox_i32_i64`  |

These names are a compiler-internal encoding, not a stable ABI. They are
distinct from the native symbol mangling applied to functions, which is
documented in `modules.md`.

## Diagnostics

A generic declaration is checked once on its own, so errors that do not depend
on the type arguments are reported against the generic declaration itself.

An error that only arises for particular type arguments is reported when that
instantiation is checked. The message names the instantiation and the location
points into the generic declaration, which may be in a different file from the
code that requested it:

```text
util.oxy:1:51: error: The 'Minus' operator requires a numeric operand (in instantiation 'util.negate<bool>')
```

Unused-declaration warnings are reported for the generic declaration only, not
repeated for each instantiation.
