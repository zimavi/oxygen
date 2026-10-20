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

### Inference

A generic function call, a constructor call, or a union case takes each type
parameter from the types of its arguments, through pointers, arrays, and type
arguments as well as plain parameters. It then takes an open parameter from
the expected type, such as the declared type of a variable or the return type
of the enclosing function. Arguments are not converted for inference: a
parameter of a concrete type, such as `u64`, accepts an integer literal by the
usual conversion rules.

- A parameter bound to two types that are not classes or interfaces is an
  error. Two classes or interfaces bind to the wider type when one converts
  implicitly to the other, and otherwise to their nearest common supertype.
  So `both(new Dog(), new Animal())` binds `T` to `Animal` in either order, and
  two unrelated classes that share an interface bind to that interface.
- An expected type binds a parameter that no argument reached. It also widens a
  class or interface binding to the expected type when the bound type converts
  to it, so `Option<Iterable<i32>> it = Option.Some(items);` binds `T` to
  `Iterable<i32>`. An expected type of another shape binds nothing, and a value
  of a different primitive type is reported where it is used.
- A parameter that nothing binds is an error. Write its type arguments
  explicitly, such as `Option<string>.None()`. A union case without a payload is
  written with parentheses, `Option.None()`.

```oxygen
Option<i32> none() {
    return Option.None(); // T is i32 from the return type
}

var named = Option.Some("hello"); // Option<string>
```

## Constraints

A type parameter can require several interfaces with `&`, for example `void foo<T : A & B, R : C>()`.
Every listed interface must be implemented by the type argument.

A type parameter may require that its type argument implements an interface:

```oxygen
void show<T : Printable>(T value) {
    value.write();
}
```

A generic body is checked once, when it is declared, with each type parameter
left opaque. Inside the body a type parameter has only the members of its
constraints:

- A method of a constraint interface can be called on a value of the type
  parameter, and a property of a constraint interface can be read from it.
- A value of the type parameter converts implicitly to each of its constraint
  interfaces, as a class converts to an interface it implements.
- Any other member, and any operator the type parameter does not define, is an
  error, even if no instantiation would reach it.

```oxygen
interface Shape {
    i32 area();
}

i32 total<T : Shape>(T value) {
    Shape shape = value;        // T converts to its constraint
    return value.area() + shape.area();
}

i32 broken<T : Shape>(T value) {
    return value.perimeter();   // error at the declaration: T has no 'perimeter'
}
```

Each instantiation is checked against the constraint too. A type that does not
implement the interface is an error at the point of instantiation:

```text
main.oxy:4:5: error: Type 'Plain' does not satisfy the constraint 'Printable' of type parameter 'T'
```

Primitive types and `string` implement `std.io.Printable` by a built-in rule,
except `void`, which has no value to write. Classes implement the interfaces
they declare, including interfaces inherited from base classes. Each instantiation is also checked with the
concrete type, so `value.write()` on an `i32` resolves to the `write` function
for `i32`.

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
Bodies type-checked, generic ones with opaque type parameters
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

A generic declaration's body is checked once with its type parameters opaque
(see [Constraints](#constraints)), and then once per instantiation, with its
type parameters bound to concrete types. The instantiation check matters because
a body may use a generic type that appears nowhere in a signature:

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

A generic declaration is checked once on its own, with its type parameters
opaque, so an error that no type argument could avoid is reported against the
generic declaration itself. An operator that the type parameter does not support
is one such error, whatever the type arguments are:

```text
util.oxy:1:51: error: The 'Minus' operator requires a numeric operand
```

An error that only arises for particular type arguments is reported when that
instantiation is checked. The message names the instantiation and the location
points into the generic declaration, which may be in a different file from the
code that requested it.

Unused-declaration warnings are reported for the generic declaration only, not
repeated for each instantiation.
