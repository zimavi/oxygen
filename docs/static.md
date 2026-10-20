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
- A static field of a class or interface type holds an object or `null`. It starts
  as `null`, and its only initializer is `null`. Storing a stack object in it is an
  error, because the field outlives the function.
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

## Base classes

A static member of a base class is reached through a subclass name, as
`Child.n` and `Child.twice()`, and through an object of the subclass, as
`child.n`. The two mean the same member. A static member of the object's own
class is not reached through an object: write its class name.

A static member of a generic base class is not reached through a subclass yet.
Write the base's own name with its type arguments.

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
field of a generic class is named by its bare name inside the class. From outside,
`Box<i32>.touches` names the field of that instance, so `Box<i32>` and
`Box<string>` keep separate fields:

```oxygen
i32 main() {
    Box<i32>.touches = Box<i32>.touches + 10;
    return Box<string>.touches;
}
```

## Static properties

A static property is declared with `static` before its type, and has a `get`
and a `set` accessor, each with a body. Its getter and setter are static methods
of the class, which have no receiver:

```oxygen
class Counter {
    static i32 seen = 0;

    static i32 Seen {
        get { return seen; }
        set { seen = value; }
    }
}

i32 main() {
    Counter.Seen = 4;
    Counter.Seen += 3;
    return Counter.Seen;
}
```

Reading `Counter.Seen` calls its getter, and writing it calls its setter. A
compound assignment does both. A property with only a `get` is read-only, and one
with only a `set` is write-only. Its accessors follow the visibility rules of
static methods. Auto-accessors (`get;`) are not allowed on a static property.

## Rules that are errors

- `static` on an interface member: an interface's contract is per instance.
- `static` with `virtual` or `override`.
- `static` on a constructor or a destructor.
- A static property with an `init` accessor, or an accessor without a body.
- A static field whose initializer is not a constant, or whose type is not
  numeric, `bool`, a class, or an interface.
- A static field of a class or interface type initialized with anything but
  `null`.
- A static field of the object's own class named through an object, or an instance
  field named through its class.
- `this` in a static method.
- A static method of the object's own class called through an object. Call it through
  its class.
- Assignment to a `static const` field.

## Limits

- A static property of a generic class belongs to one instance, so it is named
  with type arguments: `Box<i32>.Seen`. Without them, `Box.Seen` is an error.
  Inside the class, the property is still named with its type arguments.
