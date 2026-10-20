# Functions and default parameters

A function declaration gives its return type first, then its name and its
parameters:

```oxygen
i32 scale(i32 value, i32 factor = 2) {
    return value * factor;
}
```

## Default values

A parameter may have a default value, written after `=`. A call that omits that
argument uses the default:

```oxygen
scale(5);      // 10, factor is 2
scale(5, 3);   // 15
```

The rules are:

- **Defaults come last.** A parameter without a default cannot follow one with
  a default. The required parameters are the leading ones.
- **A default is a constant.** It is a bool literal, a floating-point literal
  with an optional sign, or an integer constant expression, such as `-1` or
  `2 * 8`. A name, a call, or a string is not a constant.
- **A default has the parameter's type.** It must convert implicitly to the
  parameter's type, so `u8 a = 300` is rejected.
- **Extern functions cannot have defaults**, because C has no default arguments.
- **A parameter whose type uses a generic parameter cannot have a default.**
  Generic functions and classes can still default their non-generic parameters.

A call passes at least the required parameters and at most all of them. A
function with defaults therefore accepts several argument counts, and the call
matches a function when its count is in that range.

Constructors and methods follow the same rules. In `new Box()`, `Box()`, and
`box.get()`, the omitted arguments take the defaults of the constructor or method
that the call resolves to.

## Overloads and defaults

Overloads of one name are told apart by their parameter types. A return type
never distinguishes them.

Two overloads conflict when one call could match both. That happens when some
argument count is accepted by both, and their first parameters of that count
have the same types. A conflicting pair is rejected at declaration:

```oxygen
i32 process(i32 code) { return code; }
i32 process(i32 code, i32 extra = 0) { return code + extra; } // error: process(5) matches both
```

Overloads that differ in a parameter type at every position both can be called
with do not conflict:

```oxygen
i32 pair(i32 a, i32 b) { return a - b; }
i32 pair(i32 a, i64 b = 0) { return a; }   // fine: they differ at the second parameter
```

A call that matches several overloads chooses the closest one, as described in
the overload rules of the compiler: an exact match beats a conversion, so
`pair(1, cast<i32>(2))` calls the first.

## Named arguments

An argument can name the parameter it fills, with `name: value`:

```oxygen
i32 scale(i32 value, i32 factor = 2) {
    return value * factor;
}

scale(factor: 3, value: 5); // 15
scale(value: 4);            // 8, factor takes its default
```

The rules are:

- **Positional arguments come first**, then named ones. A positional argument
  after a named one is an error.
- **Each name must be a parameter** of the function, and each parameter is filled
  once. Filling a parameter both by position and by name is an error.
- **A parameter without a default must be filled.** A named call that leaves one
  out is an error, and the message names the parameter.
- **Overloads are chosen among those the names fit.** An overload that has no
  parameter with a given name, or that leaves a required parameter unfilled, is
  not a candidate. The usual ranking then applies to the arguments in parameter
  order.
- **Named arguments work everywhere a call does**: free functions, methods,
  receiver calls, `new`, class construction, and the `this(...)` and `base(...)`
  initializers of a constructor.
- **Union case payloads are given by position only**, as `Option.Some(value)`.

A label is recognized only at the start of an argument, so the `:` of a
conditional expression inside an argument is still that operator:
`scale(flag ? 1 : 2)` passes a conditional value.
