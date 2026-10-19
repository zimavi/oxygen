# Const

A `const` variable cannot be assigned after its declaration. A local `const`
needs an initializer, as a module-level `const` does (see [globals](globals.md)).

```oxygen
const i32 limit = 100;
```

## Where an assignment is rejected

An assignment is checked along its whole target: the variable, then each member
or element it reaches.

- **Value parts are fixed.** A field of a struct or value class, or an element
  of an array, is part of its variable. Assigning to it is rejected when the
  variable is const.
- **Reference objects change only inside their class.** A field of a class
  object reached through a const variable can be assigned only from a member of
  that class, so the class can update its own state. From outside the class the
  assignment is rejected, however deep the path is.
- **Pointers are the explicit escape.** The walk stops at a pointer, so a
  mutation through a pointer is not blocked by a const variable that holds it.

```oxygen
class Inner { i32 x; }

class Outer {
    Inner inner;

    void touch() {
        const Outer self = this;
        self.inner = new Inner();   // allowed: inside Outer
    }
}

i32 main() {
    const Outer o = new Outer();
    o.inner = new Inner();          // rejected: outside class 'Outer'
    o.inner.x = 5;                  // rejected: outside class 'Inner'
    return 0;
}
```

Assigning to a const variable itself is always rejected, and so is assigning
to a const field outside the constructors that declare it.
