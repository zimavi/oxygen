# Control flow

Oxygen supports `if`/`else`, `while`, and C-style `for` statements:

```oxygen
if (ready) { start(); } else { stop(); }
while (count < 10) { count = count + 1; }
for (i32 i = 0; i < 10; i = i + 1) { process(i); }
```

Switch statements support union patterns and constant value cases:

```oxygen
switch (value) {
    case 0: return 10;
    case 1: return 20;
    default: return 0;
}
```

Case bodies are one statement (use a block for multiple statements). Cases
fall through implicitly; `break` exits the nearest switch or loop. Integer and
enum switches are emitted using an integer jump table when possible. Boolean,
string, and pointer selectors use ordered equality checks. Value cases must be
compile-time constants and a value switch may contain at most one `default`.
Union switches retain named payload patterns and must cover every union case.

Class and interface switches also support runtime type patterns. The `is`
keyword may be used with or without a binding:

```oxygen
if (value is Child child) { return child.get(); }
switch (value) {
    case is Child child: return child.get();
    case is Labeled: return 1;
    default: return 0;
}
```

Type patterns are valid only for class and interface references. They follow
C#-style assignability: derived class instances match base-class patterns and
implementing classes match interface patterns. A binding is scoped to the
successful branch body.

Conditions must have type `bool`. The initializer, condition, and increment
clauses of a `for` loop are optional (`for (;;)` is valid). A declaration in a
`for` initializer is scoped to that loop and its body.

`break` exits the innermost loop and `continue` proceeds to the innermost loop's
next condition check (or increment clause for `for`). Both statements require a
semicolon and are errors outside a loop. Do-while loops are not supported.
