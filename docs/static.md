# Static members

A class can have static fields and static methods. A static member belongs to
the class, not to an instance. Every instance and all code of the class share
the same static field, and a static method is called without an object.

```oxygen
class Counter {
    static i32 count = 0;
    static const i32 limit = 3;
    i32 id;

    Counter() {
        Counter.count = Counter.count + 1;
        this.id = Counter.count;
    }

    static i32 next(i32 step = 1) {
        count = count + step;
        return count;
    }
}

i32 main() {
    Counter a = new Counter();
    return Counter.next(10);
}
```

## Static fields

- A static field is a numeric or `bool` value, declared with `static` before its
  type. It is zero unless it has an initializer.
- Its initializer must be a constant, the same kind a module-level variable
  accepts: a bool literal, a floating-point literal with an optional sign, or an
  integer constant expression. A name, a call, or a string is not a constant.
- `static const` fields need an initializer and cannot be assigned.
- Inside the class, a static field is named by its bare name, from its methods,
  its constructors, and its field initializers. Elsewhere it is named as
  `Class.field`.
- A static field is not part of an object's layout. An object does not hold it.

## Static methods

- A static method has no receiver. It has no `this`, and it cannot name an
  instance field or call an instance method without an object.
- It is called as `Class.method(...)`. A bare name does not reach a class method,
  whether it is static or not.
- An instance method may call a static method of its class or of another class.
- A static method may use the static fields and the static methods of its class,
  by name or through `Class.`.
- Static methods are not virtual. `virtual` and `override` are errors on a static
  member.
- Overloads follow the usual rules, including default parameters and named
  arguments.

## Visibility

`public` and `private` apply to static members as they do to instance members.
A private static member is visible only inside its class.

## Generic classes

A generic class has one set of static fields per instantiation. `Box<i32>` and
`Box<string>` keep separate counters:

```oxygen
class Box<T> {
    static i32 touches = 0;

    i32 touch() {
        touches = touches + 1;
        return touches;
    }

    static i32 total() {
        return touches;
    }
}

Box<i32>.total(); // the total of Box<i32> only
```

Inside a generic class, `Box.total()` names the current instance's method.
Static methods of a generic class are called as `Box<i32>.total()`. A static
field of a generic class is named by its bare name inside the class, so it is not
yet reachable from outside the class.

## Rules that are errors

- `static` on an interface member: an interface's contract is per instance.
- `static` with `virtual` or `override`.
- `static` on a constructor, a destructor, or a property. Static properties are
  not supported yet.
- A static field whose initializer is not a constant, or whose type is not
  numeric or `bool`.
- A static field named through an object, or an instance field named through its
  class.
- `this` in a static method.
- A static method called through an object. Call it through its class.
- A static field or method named through a subclass that does not declare it.
  `Child.n` is an error when only `Base` declares `n`; write `Base.n`.
- Assignment to a `static const` field.

## Limits

- Static fields of class type, and static properties, are not supported.
- A static field of a generic class is reached by its bare name inside the class
  only.
- A static method is not inherited for lookup through a subclass name, as
  described above.
