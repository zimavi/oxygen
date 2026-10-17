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

Destructor syntax and symbols are recognized, but automatic destruction at
scope exit and virtual destructor dispatch are not enabled yet. They require
ownership and lifetime information in the semantic model plus cleanup-aware
lowering for returns, branches, loop exits, and partially initialized
constructors. Interface vtable population and indirect interface calls are
also pending; interface fat values must not be treated as directly callable
class pointers.
