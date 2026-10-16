# Control flow

Oxygen supports `if`/`else`, `while`, and C-style `for` statements:

```oxygen
if (ready) { start(); } else { stop(); }
while (count < 10) { count = count + 1; }
for (i32 i = 0; i < 10; i = i + 1) { process(i); }
```

Conditions must have type `bool`. The initializer, condition, and increment
clauses of a `for` loop are optional (`for (;;)` is valid). A declaration in a
`for` initializer is scoped to that loop and its body.

`break` exits the innermost loop and `continue` proceeds to the innermost loop's
next condition check (or increment clause for `for`). Both statements require a
semicolon and are errors outside a loop. Do-while loops are not supported.
