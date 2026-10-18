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
interface Labeled {
    void label();
}

class Counter : Labeled {
    void label() {
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

Class methods can opt into vtable dispatch with `virtual`. An overriding
method must use `override`; overriding a non-virtual method, or omitting
`override` when replacing a virtual method, is a semantic error:

```oxygen
class Animal {
    virtual i32 value() { return 1; }
}

class Cat : Animal {
    override i32 value() { return 2; }
}
```

Calls through a class reference use the object's vtable and select the
most-derived override.

## Properties

Classes and interfaces may declare C#-style properties.  Auto accessors use
the class storage layout (and therefore have the same native behavior as a
backing field). The backend emits explicit callable getter/setter functions
for both auto and body-backed accessors:

```oxygen
class Account {
    public i32 Balance { get; private set; }
    public i32 Id { get; init; }
}

interface Named {
    string Name { get; }
}
```

An accessor may be an auto accessor, terminated by `;`, or may contain an
explicit body:

```oxygen
class Account {
    i32 balance;

    public i32 Balance {
        get { return balance; }
        private set { balance = value; }
    }
}
```

The supported accessors are `get`, `set`, and constructor-only `init`. A
property may declare at most one accessor of each kind. `set` and `init` are
alternative write accessors; a property cannot declare both. An `init`
accessor may be assigned from a constructor, but not from an ordinary method
or after construction.

An interface property declares an accessor contract and never contributes
storage. An implementing class must provide compatible accessors, either as
auto accessors or with explicit bodies:

```oxygen
interface Named {
    string Name { get; }
}

class Person : Named {
    public string Name { get; }
}
```

Property reads use the getter and assignments use the setter or constructor
initializer. This remains true for explicit bodies, inherited properties,
base-typed references, and interface-typed references.

Accessor visibility may be specified independently with `public`, `private`,
`protected`, or `internal`.  Property names participate in class/interface
contract checking and auto properties are lowered as backing storage. A
property with `init` may only be assigned from a constructor; a property with
only `get` is read-only. Accessor visibility is checked at each use site.
Virtual properties follow the same `virtual`/`override` rules as virtual
methods, including replacement of inherited backing storage. Virtual accessor
calls share the object dispatch slots used by virtual methods.

The current LLVM backend represents a class instance as a heap pointer to an
object layout containing a vtable slot followed by class fields. It emits
allocation, field access, constructors, destructors, and direct class-method
calls. Interface values use a two-word object/table representation. The first word
is the object pointer and the second points at an internal per-class,
per-interface callable table. Method calls and property getter/setter calls
load their implementation from that table; class-to-interface casts and
pattern bindings install the appropriate table pointer.

## Value classes and storage

Class construction without `new` uses the current function's stack/local
storage:

```oxygen
class Point {
    i32 x;
    i32 y;
}

i32 main() {
    Point point = Point();
    return point.x;
}
```

`new Point(...)` continues to allocate a heap object. A stack-created ordinary
class is still a reference value and is destroyed at the end of its owning
scope.

Annotating a class with `@value` gives it value-passing semantics:

```oxygen
@value class Point {
    i32 x;
    i32 y;
}

i32 sum(Point point) {
    return point.x + point.y;
}
```

Value-class parameters and return values use aggregate, field-wise copies
instead of the class-reference ABI. This remains true when the source value was
created with `new`; the heap object is read into the value passed to the
function. Copies are shallow for the initial implementation. Value classes may
declare fields, methods, constructors, and destructors, but cannot inherit,
implement interfaces, or declare virtual/override methods.

## Reference conversions

Class references may be assigned to a base-class or implemented-interface
reference without an explicit cast. The same implicit conversion is used for
function arguments and return values:

```oxygen
class Animal { }
interface Labeled { }
class Cat : Animal, Labeled { }

Animal animal = new Cat();
Labeled printable = new Cat();
```

The reverse conversion is a checked explicit cast:

```oxygen
Cat cat = cast<Cat>(animal);
Cat sameCat = cast<Cat>(printable);
```

When a class has no explicit constructor, the compiler synthesizes a public
parameterless constructor. It zero-initializes every declared and inherited
field using a valid type-specific zero value: numeric zero, `false`, null
references and pointers, or a zero aggregate.

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
Interface method and property declarations use an explicit receiver ABI.
Interface calls extract the object word from the fat value before invoking the
selected callable. Table entries contain the generated class implementation
symbols, including synthesized auto-property accessors and explicit accessor
functions. The table is selected by the class-to-interface conversion, so
calls through an interface reference use the implementation belonging to the
runtime class.
