# Reference counting for escaping objects

Status: specification (M6a). Steps 0, 1, 2a and 2b are implemented, with object layout version 3, and all of it is uncommitted on `feat/refcount`. The limits at the end of Step 2b rules are the open part. Unique results (step 4) and the benchmark are not done.

The ownership analysis (`docs/classes-and-interfaces.md`) frees a class object at the end of the scope that owns it, when the object does not escape. An object that escapes (into a field, a container element, or a value that outlives its owner) is currently never freed. This document specifies how such objects are counted and freed when their last reference goes away.

## Decisions

These were made by the project owner and are fixed:

1. **Hybrid.** Objects that the analysis proves local keep the scope-end free, with no counting. Objects that escape are reference counted.
2. **Count width is `u64`.**
3. **Threads.** A count operation is non-atomic while the object is used by one thread, and atomic otherwise. Until native threads exist (M6b), every count is non-atomic.
4. **Cycles leak.** Objects that refer to each other are never freed. This is documented; there is no cycle collector and no weak reference in this milestone.
5. **`delete` force-frees.** `delete` runs the destructor and frees the memory whatever the count is. Other references then dangle. This is the programmer's responsibility under manual memory control.
6. **Object layout: prefix count.** The count is a prefix of the allocation, and the object pointer does not change (see Object layout).
7. **Base destructors are chained.** A derived class's destructor runs its base class's destructor, and the release glue runs both. Done in step 2b.
8. **No legacy support.** The project is not stable. Libraries and metadata written by earlier builds are not read, and no code exists only for them. Removing the legacy code is M6a-0 in the milestone plan, and it comes before step 1.

## Scope

Counted objects are the instances that `new` creates on the heap. A class instance built without `new` (a value class, or a construction with `C(...)`) lives on the stack, and is never counted. The analysis must not count those, and the counting code must not touch them.

## Object layout

The count is a prefix of the allocation, not a field of the object:

```
allocation:  [ count : u64 ][ runtime type info ][ virtual slots ][ fields ... ]
                            ^ the object pointer
```

The object pointer is unchanged, so every existing field and slot index, every vtable, every interface value, and every C-side pointer keeps its meaning. Only the allocator changes:

- `oxygen_alloc` for a counted object allocates `8 + size` bytes, writes 1 to the first word, and returns the address after it.
- `oxygen_free` for a counted object frees the address 8 bytes before the object pointer.

The count is 8-byte aligned. The alignment of the object is unchanged.

Rejected alternative: a count after the type-info pointer. This moves every field and slot index, and every backend place that computes one, for no gain.

## Invariant

A count is the number of references that hold the object: owning locals, fields, elements, and interface values stored in such places. A count is never kept for a borrow. Parameters, and temporaries that the analysis classifies as borrowed, do not hold a count.

A borrow is safe because the owner outlives it. For a local owner, the existing scope rules already guarantee that. For a counted object, the owner may release its count early, and the object then stays alive through the other holders, which are counted. An object is freed exactly when its count reaches zero.

## Operations

The runtime provides three operations on a counted object. Each is a no-op for a null pointer.

- `oxygen_object_new(size)`: allocates a counted object with count 1, and returns it.
- `oxygen_retain(object)`: adds 1 to the count.
- `oxygen_release(object)`: subtracts 1. When the count reaches 0, it runs the object's destructor through its type info, and frees the allocation.

The live-block counter (`OXYGEN_LEAK_REPORT`) counts allocations, not references, and is unchanged.

### Which operations the compiler emits

- **Creation.** `new C()` calls `oxygen_object_new`. The creator's owned local holds the one count.
- **Scope end.** An owned local whose object did not escape is freed directly, as today. An owned local whose object may have been counted is released (`oxygen_release`), not freed directly.
- **Retain.** A store of a counted reference into a field, an element, a variable outside the owner's scope, or a global retains the object. The store is an `Assign` or `Escape` flow in the analysis.
- **Overwrite.** Assigning a new value to a field or element releases the old value and retains the new one. Today a field assignment does not release the old value, so this is a new rule.
- **Return.** Returning an owned local moves its count to the caller and does not release it. A returned value always carries one count, and the caller releases it at its own scope end. The caller frees it directly only when the callee proved the object unique (see below).
- **Unwinding.** Exception cleanup releases the counted locals it leaves, as scope cleanup does.
- **Delete.** `delete` frees the object without checking the count, and without touching the other references.

### Unique results

A function returns a unique object when the object was created in that function and never escaped there. Its caller may then free it directly, with no count operation. This is an optimization of the rule above, not a change of meaning: a caller that releases a unique object frees it, and a caller that frees it directly gets the same result. It needs a property beside `ReturnsOwned`, computed in the same fixpoint.

### Destructors and drop glue

When a counted object reaches zero, the runtime runs its destructor, then releases the references that it holds. The compiler generates that release for each class:

- every field whose type is a class or an interface is released;
- a union field whose payload is a class or an interface is released in the case that holds it.

The chain is part of the release glue (step 2b): a derived object's glue runs its own destructor, then the base class's glue, which runs the base destructor and releases the base's counted fields, and then releases its own counted fields. So the derived destructor runs first, and each destructor runs once.

Elements of a buffer are the open case. The standard library's `List<T>` owns a raw buffer of `T`, and raw pointers do not own what they point to. Its destructor must release the elements when `T` is a counted type. The mechanism is an intrinsic (see the open questions).

### Step 2a rules

These are the rules that step 2a implements. They keep the hybrid decision: an object that never escapes is still freed directly at its owner's scope end, with no count operation.

- **Direct free.** A local initialized by `new`, and not escaping, is freed with `oxygen_object_free` at scope end, or at the end of its statement for a temporary. This is the only path that frees without a count, and it is valid because nothing ever retained the object.
- **Counted release.** Every other owned value is released with `oxygen_release` at its owner's scope end: a local initialized by a call that returns a class, a local from `new` that escapes, a for-in iterator, and a temporary from a call. The owner's count is the one the creator or the callee handed over.
- **Moves.** A class value that is returned, or stored, and that is owned (`new`, a call result, or an owned local) transfers its count. A returned owned local is not released by its scope; the caller owns the count.
- **Retains.** A class value stored into a field, a global, an element, or a variable that is not owned, and any class value that is returned without being owned (a parameter, a field read, an alias local), is retained first. The receiving slot then owns one count.
- **Overwrite.** A field or global store releases the value it replaces, after the new value is retained, so a self-assignment is safe. Element stores do not release the old value: buffers are not zeroed, so the old slot may hold garbage. Until the element release intrinsic exists (step 2b), an overwritten element stays counted.
- **Zeroed objects.** `oxygen_object_new` zeroes the object, so every field holds null until it is stored, and an overwrite of a field never releases garbage.
- **Calls that keep an argument.** A callee that stores a parameter retains it, so the caller's owned value is released at its own scope end and survives while the callee's slot holds it.

Until drop glue (step 2b), a field, element or global that still holds a count at the owner's death leaks it. That is the intermediate state the leak tests describe.

### Step 2b rules

- **Release glue.** Every class of the program has a release glue, `_r` in its mangled name (`docs/mangling.md`). The runtime type's destructor is the glue, so both the release of an object's last count and a direct free run it. The glue runs the class's own destructor, then its base class's glue, then releases the counted references in the class's own fields. Inherited fields are not released twice: the base class's glue releases them.
- **Counted fields and unions.** A field of a class or interface type is released by count. A union field releases the payload of the case it holds.
- **Elements.** `std.memory.release_elements<T>(pointer, count)` is an intrinsic: the compiler implements it, since a generic body cannot tell whether `T` is counted. For a class or an interface `T` it releases each of the first `count` elements. For any other `T` it does nothing. `List<T>` releases its elements in its destructor, before it frees its buffer.
- **Stack objects.** A class instance built without `new` is not counted, but a holder can still store it. Its address has a word before it holding the stack count (`UINT64_MAX`), and the runtime's counts do nothing for it. Its destructor still runs at its owner's scope end, and the counted fields it holds are not released.
- **Temporaries given to calls.** An owned temporary given to a call, or to a constructor, is released when its statement ends, whether or not the callee keeps it: a callee that keeps it has retained it. A `new` temporary that a callee may keep is released by count rather than freed directly.
- **Limits.** A struct, a fixed array, or a value class stored in a field is not released, so the counted objects it holds leak. Those are the cases that a later step still has to cover.

## Threads

A count operation has two forms: a plain add or subtract, and an atomic one. The form is chosen per operation site, from whether the object can be reached from another thread. Until threads exist, every site is plain.

The choice must be per object, not per reference: once a thread can reach an object, every operation on it must be atomic. The thread-sharing analysis that picks the form is part of M6b. The object carries no flag; the form is decided by the compiler.

## Interface values

An interface value holds its object pointer in its first word. Storing an interface value into a field, element, or outer variable retains that object. Releasing an interface value releases its object pointer, and the destructor is found through the object's type info, as it is now.

## Unions

A union value holding a counted reference copies that reference. Storing a union into a field or element retains the payload, and overwriting or destroying the union releases it. The union rules follow the same paths as the class rules above.

## Cost

Counts add work only on the paths that escape: a retain at each store of an escaping reference, and a release at each overwrite and destruction. The objects that the analysis keeps local keep their current cost, which is one free at scope end. A unique result adds no count operation.

Before the design is final, measure a loop-heavy benchmark that allocates escaping objects, with and without the counts. The result decides whether unique-result elision and atomic forms are worth their complexity.

## Compatibility

There is no compatibility with earlier builds (decision 8). The object layout version in the native module metadata already rejects a library built with another layout, and this change bumps it. Nothing else is kept for older libraries.

## Tests

- Runtime (C): retain and release, the zero case, a null argument, and the destructor run once at zero.
- Native: a container element is freed with its container (the leak report is zero); a field store retains the object, and the object is freed when the last holder goes away; an overwrite releases the old value; a returned value transfers its count; an exception releases a counted local.
- `delete` frees a counted object whose count is greater than one, and the test documents the dangling reference rather than checking it.
- Cycles: a test that two objects referring to each other are reported as not freed, which documents the leak.
- Benchmark: the measurement above, recorded in the milestone plan.

## Steps

0. This specification, and the open questions below settled.
M6a-0. Legacy cleanup, as recorded in the milestone plan. It comes before step 1, so the new layout is added to code that has no older form.
1. The layout prefix, `oxygen_object_new`, `oxygen_retain`, `oxygen_release`, with the C tests.
2a. **Done.** The owner protocol: counted release for owners that may be shared, direct free only for a `new` object that nothing retains, retains on every store into memory, retain on return unless the count moves, and the zeroed object. The lifetime pairs of the earlier model are gone, since a retain now does their job. Tests: the held-count cases, and the rule tests in `OwnershipIntegrationTests`.
2b. **Done.** Drop glue for counted fields and unions, the base destructor chain, and the `release_elements` intrinsic (see Step 2b rules). Tests: list elements, union fields, derived classes, and interface fields are released with their holders; the base destructor runs after the derived one.
3. Covered by 2a for the escape points and the overwrite rule. An element that is overwritten is still not released, because a buffer's slots are not known to hold a value, so its old value is counted until the owner is freed.
4. Unique results, and the benchmark. The atomic form waits for M6b.
5. `delete` force-free, with its tests. The delete path frees a counted object through its release glue, so the other references dangle, as decided.

## Open questions

1. **Element release intrinsic.** Settled: `std.memory.release_elements<T>(pointer, count)`, implemented by the compiler for an `@intrinsic` function (step 2b).
2. **Resolved (user): no legacy support.** Older libraries are rejected by the layout version, and nothing else is read.
3. **Resolved (user): base destructors are chained** (decision 7).
4. **Resolved (user): value classes in fields** are inline, and the containing object's drop glue releases their class-typed fields, recursively.
