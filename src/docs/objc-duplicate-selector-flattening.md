# ObjC binding — duplicate `[Export]` selector on a flattened registered type

**Status:** FIXED (root-caused, runtime-confirmed). Both shapes are implemented in `ApiDefinitionEmitter` with emitter unit tests and a launch-time runtime gate (the mixed PackGate fixture / `--mixed-direct` leg); this doc is retained for the diagnosis and the false-positive caveats.
**Discovered:** binding a real-world third-party mixed (ObjC + Swift) xcframework set on current main; the app aborts at launch on the iOS Simulator (Mono JIT).
**Layer:** ObjC API-definition emitter (`src/Swift.Bindings/src/ObjC/Emitter/ApiDefinitionEmitter.cs`).

---

## 1. Symptom

A consuming app that links the generated ObjC bgen binding aborts during managed↔ObjC
registration, before any test code runs:

```
System.AggregateException: One or more errors occurred.
 (Could not register the selector 'init' of the member '<Module>.<Proto>..ctor'
  because the selector is already registered on the member 'Init'.)
 (Could not register the selector 'setFoo:' of the member '<Module>.<Class>.set_Foo'
  because the selector is already registered on the member 'SetFoo'.)
 ---> ObjCRuntime.RuntimeException: ...
   at Registrar.Registrar.RegisterAssembly(Assembly assembly)
   at ObjCRuntime.Runtime.register_assembly(...)
*** Terminating app due to uncaught exception 'System.AggregateException' ... abort()
```

This is **duplicate *selector* registration within one managed type**. It is distinct from
issue #40 ("Class X is implemented in both …"), which is duplicate native *class*
registration across two linked binaries. Here a single registered type carries two managed
members that both `[Export(...)]` the same ObjC selector, and the .NET registrar refuses it.

### The registrar contract (the oracle)

For each registered type, every `[Export("sel")]` member must have a selector unique within
that type's **instance** method-list (and separately within its **class** method-list). Three
things are NOT collisions and must not be treated as such:

- **`[Static]` vs instance** sharing a selector — they register on the metaclass vs the class
  (`+sel` vs `-sel`), separate lists. Benign.
- **`[Wrap("...")]` properties** carry no `[Export]` at all — only the backing weak property
  does — so a `[Wrap]` accessor never participates in selector registration. Benign.
- **A `[Protocol]` requirement satisfied by a concrete conformer's constructor** — bgen
  reconciles the conformer's `init`-as-ctor with the abstract `init` requirement; no duplicate
  on the concrete class.

The registrar collects *all* violations across one assembly into an `AggregateException` and
aborts in `RegisterAssembly`, so the inner-exception list is the authoritative defect set for
that assembly. Registration is per-assembly: a multi-module binding can hide further
duplicate-selector defects in modules whose assembly is registered *after* the first that
aborts.

---

## 2. The two confirmed shapes

A static "selector → ≥2 distinct C# member identities on a flattened type" audit *over*-predicts
(it flags the three benign categories above). The runtime registrar is the real oracle. On the
discovering corpus the static audit flagged six candidate sites across two modules; the runtime
confirmed **exactly two**, each a different emitter shape.

### Shape A — `[Protocol]`/Model synthesized default ctor vs an `init` requirement emitted as a method

**Structure.** An ObjC protocol declares a parameterless `-init` (or `-initWith…:`) requirement.
`EmitProtocol` emits it as an abstract method:

```csharp
[Protocol(Name = "...")]
[BaseType(typeof(NSObject))]
partial interface SomeProtocol
{
    [Abstract]
    [Export("init")]
    NSObject Init();
    ...
}
```

bgen's generated **Model class** for that protocol *also* synthesizes a default constructor that
exports `init`. So the Model class registers `init` twice — once for `..ctor`, once for `Init` —
and the registrar aborts:
`selector 'init' of 'SomeProtocol..ctor' already registered on 'Init'`.

Only the **protocol's Model class** hits this. Concrete conformers do not (their own `init`
becomes a constructor and the abstract requirement is not separately re-emitted as a method on
the conformer).

**Root cause.** `EmitProtocol` has **no `[DisableDefaultCtor]` handling**. `EmitClass` computes
`disableDefaultCtor` from any parameterless `init`/`initWith…` member and emits
`[DisableDefaultCtor]` (so bgen suppresses the synthesized default ctor); the protocol path never
does, so the Model's synthesized ctor collides with the abstract `init` method.

**Fix (verified — full EmitClass mirror).** `[DisableDefaultCtor]` alone is *not* sufficient: bgen
compiles the kept `[Abstract] [Export("init")] NSObject Init();` to a `public virtual NSObject Init()`
on a generated concrete class deriving from `NSObject`, which hides `NSObject.Init()` (CS0108 — a
benign warning in a lenient `dotnet build`, fatal under warnings-as-errors). `EmitClass` already does
*both* steps for classes — emit `[DisableDefaultCtor]` **and** suppress the parameterless `init`
method in its method loop (the "Fix #6" filter). `EmitProtocol` must mirror **both**:

1. When the protocol declares a parameterless `init`/`initWith…` requirement, emit
   `[DisableDefaultCtor]` so bgen does not synthesize the adapter's default ctor that exports `init`.
2. Suppress the parameterless `init` method itself (filter `Selector == "init" && Parameters.Count
   == 0` inside the method loop). This removes the second registrar entry *and* the
   `NSObject.Init()` shadow.

Keep the filter exactly to parameterless selector `init` — a parameterized `initWith…:` exports a
distinct selector, does not collide, and must stay emitted. Dropping the parameterless `init` is the
least-harmful projection: a parameterless ObjC `init` is construction/lifecycle, not a useful
protocol-polymorphic managed operation, and NSObject-derived conformers already inherit native
`init`. A protocol whose only member is `init` becomes a valid empty marker interface.

### Shape B — class method whose selector equals a *conformed-protocol* property accessor (protocol flattening)

**Structure.** A class conforms to a protocol AND declares a method whose `[Export]` selector
equals a settable property's accessor selector contributed by that protocol:

```csharp
[Protocol] partial interface SomeProtocol
{
    [Abstract] bool Foo { [Bind("isFoo")] get; [Export("setFoo:")] set; }
}

[BaseType(typeof(NSObject))]
partial interface SomeClass : SomeProtocol
{
    [Export("setFoo:")] void SetFoo(bool disable);   // collides with the flattened setter
}
```

bgen flattens the conformed protocol's accessors into `SomeClass`'s registration, so `SomeClass`
registers `setFoo:` twice (the flattened property setter + the method).

**Root cause.** `EmitClass` builds `propertyAccessorSelectors` from **`cls.Properties` only** and
drops methods colliding with *those* via `CollidesWithPropertyAccessor`. It does **not** seed
accessor selectors from conformed `[Protocol]` properties — the in-code comment in `EmitClass`
explicitly notes "classes don't have inherited-protocol seeding." The existing within-class guard
(added for the plain property-vs-method case — the `setURL:`-style fix) therefore misses the
protocol-flattened case.

**Fix.** Seed the class's `propertyAccessorSelectors` (and the member-name / signature sets) with
accessor selectors flattened from conformed `[Protocol]` properties, **transitively** — mirroring
the existing `SeedInheritedProtocolSignatures` used in `EmitProtocol` for the protocol→protocol
case. Then `CollidesWithPropertyAccessor` drops the class method in favor of the inherited
property accessor, exactly as it already does within a single class.

---

## 3. Existing machinery to extend (don't reinvent)

All in `ApiDefinitionEmitter.cs`:

- `BuildPropertyAccessorSelectors` / `CollidesWithPropertyAccessor` — the within-type
  method-vs-property-accessor guard. Already used by both `EmitClass` and `EmitProtocol`. Shape B
  is "feed it the *inherited* protocol property accessors too."
- `SeedInheritedProtocolSignatures` / `ComputeProtocolEmissionSet` — already flatten
  transitively-inherited protocol method signatures and member names for the **protocol→protocol**
  case (so `EmitProtocol` avoids CS0111/CS0102 in the bgen-flattened `*.g.cs`). Shape B is the
  **class→protocol** analog of this same flattening, extended to *accessor selectors*; Shape A is
  the missing `disableDefaultCtor` step on the protocol path.
- `EmitClass`'s `disableDefaultCtor` computation (parameterless `init`/`initWith…` →
  `[DisableDefaultCtor]`) is the template for Shape A's fix on `EmitProtocol`.

---

## 4. Audit caveats / false positives (so a fix validates against the right oracle)

A static selector-collision audit is useful for *finding candidate shapes* but must not be the
pass/fail oracle — it lacks `[Static]` and `[Wrap]` awareness and cannot model bgen's
ctor↔abstract-init reconciliation. Of six static candidates on the discovering corpus, four were
false positives:

- a `setDelegate:` pair where the public property is `[Wrap("WeakDelegate")]` (no export) and only
  the backing `WeakDelegate` carries `[Export("setDelegate:")]` — single registration;
- a `disable` pair where one member is `[Static]` (class method) and one is instance — separate
  selector namespaces;
- two concrete-class `init`/`initWith…` candidates that bgen reconciles (the ctor satisfies the
  requirement; no duplicate on the concrete class).

The real defects were one Shape A and one Shape B.

---

## 5. Test plan (BindingTests + unit)

- **Unit (emitter):** a fixture protocol with an `init`/`initWith…:` requirement → assert the
  emitted `[Protocol]` carries `[DisableDefaultCtor]` (Shape A). A fixture class conforming to a
  protocol that declares a settable property whose setter selector equals a class method →
  assert the colliding class method is dropped (a `DuplicateSelector` skip is recorded) and the
  property accessor is kept (Shape B). Assert behavior (attribute present / method absent), not
  exact strings.
- **BindingTests (runtime, the real gate):** add ObjC source reproducing both shapes; the gate is
  that the app launches past `RegisterAssembly` without the duplicate-selector `AggregateException`.
  This is exactly the kind of ABI/registration failure unit tests cannot catch.
- **Coverage caveat:** because registration aborts per-assembly, after fixing A+B re-run the
  full multi-module binding to surface any next-layer duplicate-selector defects in modules that
  the first abort prevented from registering.
