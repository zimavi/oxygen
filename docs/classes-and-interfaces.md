# Classes and interfaces

Oxygen classes are reference types. A class declaration may contain fields,
methods, constructors, and one destructor:

```oxygen
class Counter {
    i32 value;

    Counter(i32 initial) {
        value = initial;
    }

    i32 get() {
        return value;
    }

    ~Counter() {
    }
}
```

Classes may specify one base class and implemented interfaces:

```oxygen
interface Printable {
    void print();
}

class Counter : Printable {
    void print() {
    }
}
```

Instances are created with `new Type(arguments)`. A parameterless constructor
is generated when no constructor is declared. Constructors are overloadable by
parameter types. Interface and class contracts are checked during semantic
analysis, including missing methods, incompatible signatures, duplicate
methods, invalid base types, and inheritance cycles.

Methods and constructors may use `this` to refer to their current instance:

```oxygen
class Counter {
    i32 value;

    Counter(i32 value) {
        this.value = value;
    }

    i32 get(i32 value) {
        return this.value + value;
    }
}
```

Parameters and local variables may shadow fields. Unqualified field access
continues to work when no parameter or local has the same name; use `this.field`
to explicitly access the field when a name is shadowed. `this` is only valid in
class instance bodies, not in free functions.

The current LLVM backend represents a class instance as a heap pointer to an
object layout containing a vtable slot followed by class fields. It emits
allocation, field access, constructors, destructors, and direct class-method
calls. Interface values have a two-word object/table representation reserved
for dynamic dispatch.

## Reference conversions

Class references may be assigned to a base-class or implemented-interface
reference without an explicit cast. The same implicit conversion is used for
function arguments and return values:

```oxygen
class Animal { }
interface Printable { }
class Cat : Animal, Printable { }

Animal animal = new Cat();
Printable printable = new Cat();
```

The reverse conversion is a checked explicit cast:

```oxygen
Cat cat = cast<Cat>(animal);
Cat sameCat = cast<Cat>(printable);
```

Runtime type patterns use `is` and can optionally bind the checked value:

```oxygen
if (animal is Cat cat) {
    return cat.value();
}
```

The same syntax is available in switch cases (`case is Cat cat:`). A pattern
matches derived classes and implemented interfaces, and its binding is only
available in the matching branch.

An implicit downcast and a cast between unrelated reference types are compile
errors. When the compiler cannot prove an explicit downcast safe, the generated
code checks the runtime object type. A failed check reports an invalid-cast
diagnostic and terminates the process; it does not produce a value of the
requested type.

Destructor calls are emitted automatically for owned class locals. Locals
belong to their lexical scope, are destroyed once in reverse declaration
order, and are cleaned up on block exits, returns, `break`, `continue`, and
loop exits. Ownership escapes when a class reference is assigned to an outer
scope, returned from a function, or passed as a function argument; an escaped
reference is not destroyed by the inner scope. This is a transfer of the
reference's ownership, not a hidden allocation or `oxygen_free` call, and
manual pointer management remains unchanged.

Destructor dispatch is direct for the declared class. Virtual destructor
dispatch and partially initialized-constructor cleanup are not yet enabled.
Interface vtable population and indirect interface calls are also pending;
interface fat values must not be treated as directly callable class pointers.
