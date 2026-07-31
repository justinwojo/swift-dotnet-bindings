# ObjC emitter gap worklist — MapLibre/Facebook validation pass (2026-07-31)

Six generator gaps found by running the MapLibre and Facebook bindings end-to-end (build →
sim → device) in the consumer repo. **These are planned, fundable work items** — they were
briefly registered in `not-planned.md` and have been *moved* here because they are queued for
the first 0.18.1 ObjC pass; `not-planned.md` now just points at this doc. When an item is
fixed, delete its section; when the doc is empty, delete the doc (per `README.md`, completed
work is documented by code and tests).

Related but **not** in this worklist (still in `not-planned.md`, reopen-triggered):
foreign-owned ObjC types omitted rather than resolved against the sibling assembly
(TypeOwnershipManifestEmitter direction), never-defined `@class` forward decls emitting empty
shells, and the synthesized auto-dep package identity. Read those rows before touching
cross-assembly type resolution — they border items 3 and 4 below.

## Where the code lives

| Concern | Path |
|---|---|
| ObjC AST parsing | `src/Swift.Bindings/src/ObjC/Parser/ClangAstParser.cs` |
| ApiDefinition emission (classes, categories, protocols, dedup) | `src/Swift.Bindings/src/ObjC/Emitter/ApiDefinitionEmitter.cs` |
| Type mapping (pointers, value types, system types) | `src/Swift.Bindings/src/ObjC/Emitter/ObjCTypeMapper.cs` |
| Enum/struct emission | `src/Swift.Bindings/src/ObjC/Emitter/StructsAndEnumsEmitter.cs` |
| System type vocabulary | `src/Swift.Bindings/src/Data/objc-type-mappings.json` |
| Throwing-tombstone precedent (item 6) | `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureParamTombstoneEmitter.cs` |

Repo gates: `./build.sh UnitTests` must stay green — the pass floor is
`build/baselines/validation-baseline.json` → `swift_bindings_unit_pass_floor` (currently
16379). The floor is a **minimum**: raise it when you add tests; never lower it. Every fix
here should land with emitter unit tests (see `src/Swift.Bindings/tests/UnitTests/`), and the
repo's standing posture is **fail closed**: an unbindable member must drop *with a recorded
skip* (visible in `binding-report.json`), never silently, and never as a callable that
misbehaves at runtime (`roadmap.md` § Surface loss is a distinct failure mode).

## How to validate against the consumer repo

The live repro for every item is `/Users/wojo/Dev/swift-dotnet-packages` (the NuGet packages
monorepo). Its working tree currently carries **uncommitted** test hardening + Facebook csproj
fixes that these baselines depend on — do not stash, revert, or commit it (the owner reviews
it separately).

Relevant layout there:

| What | Path |
|---|---|
| MapLibre binding project | `libraries/MapLibre/SwiftBindings.MapLibre.csproj` (vendored `MapLibre.xcframework` beside it) |
| MapLibre test app | `libraries/MapLibre/tests/Program.cs` — 63 patterns; **GAP-PROBE for item 2 at `:1337`**, **for item 5 at `:1383`** |
| Facebook binding projects | `libraries/Facebook/{FBSDKCoreKit_Basics,FBSDKCoreKit,FBAEMKit,FBSDKLoginKit,FBSDKShareKit}/SwiftBindings.Facebook.{CoreBasics,Core,AEM,Login,Share}.csproj` |
| Facebook test app | `libraries/Facebook/tests/Program.cs` — SB0001 fault documented as a first-class SKIP around `:519` |

Current sim baselines (must not regress): **MapLibre 57 pass / 0 fail / 6 skip**, **Facebook
32 pass / 0 fail / 4 skip**. The test apps' success gate requires `fail == 0 && pass > 0`
before printing `TEST SUCCESS`.

The loop for testing a patched generator end-to-end:

```bash
# 1. In swift-bindings — pack the SDK at a dev version
#    (nupkgs land in $TMPDIR/swift-nuget by default; --output-dir to override)
./build.sh Pack --version 0.18.1-dev.1 --apple-version 26.2.10-dev.1

# 2. Stage into the consumer repo's preferred local feed
cp "$TMPDIR"/swift-nuget/SwiftBindings.{Sdk,Runtime}.0.18.1-dev.1.nupkg \
   /Users/wojo/Dev/swift-dotnet-packages/local-packages/

# 3. In swift-dotnet-packages — point every csproj at the dev SDK and wipe obj/
#    so bindings actually regenerate
dotnet nuke BumpSdkVersion --version 0.18.1-dev.1

# 4. Rebuild the library under test (Facebook is multi-product two-pass;
#    pass-1 wrapper failures there are expected)
dotnet nuke BuildLibrary --library MapLibre
dotnet nuke BuildLibrary --library Facebook --all-products

# 5. Sim-validate (watches stdout for TEST SUCCESS / crash)
dotnet nuke BootSim
dotnet nuke ValidateSim --library MapLibre --timeout 30
dotnet nuke ValidateSim --library Facebook --timeout 30
```

**Run `dotnet nuke` invocations serially** — concurrent runs in that repo silently skip
(build.log lock collision). Generated bindings to inspect land under each binding project's
`obj/**/swift-binding/*.cs`; per-member skip records in `binding-report.json` next to them.
When a fix makes a previously-dropped member appear (items 1, 3, 4) or changes a member's
shape (items 2, 5, 6), extend the test app to exercise it and flip the corresponding
GAP-PROBE/SKIP into a real pass/fail test — the probes exist precisely to be converted.

---

## 1. Foreign-category *instance* properties are dropped with no skip record — 33 MapLibre `NSValue` unboxers

A foreign category is emitted as a bgen `[Category]` static extension class, so
`ApiDefinitionEmitter.EmitCategory` filters properties to `p.IsClass` at two sites (`:491` for
the emptiness test, `:549` for emission) — "static classes cannot have instance properties
(CS0708)". The `:494` all-empty path *does* record `EmptyCategory`; a category with at least
one emittable **method** does not — it emits, and its instance properties vanish at `:549`
with no `diagnostics?.RecordSkip`, so they are absent from the binding **and** from
`binding-report.json`. MapLibre's `NSValue (MLNAdditions)` is exactly that shape: the boxing
half is class methods (`+valueWithMLNCoordinate:` …) and emits; the unboxing half is **33
readonly instance properties** (`MLNCoordinateValue`, …) and disappears silently — so a
consumer can box a value into an `NSValue` but has no path to read one back (KVO payloads,
notification `userInfo`, style-layer enum expressions). **The silence is arguably the worse
half**: an unreported drop makes the coverage report overclaim.

**Fix shape — two independent halves:** (i) record a skip at the `:549` filter so the gap
self-reports (cheap; do this regardless of (ii)); (ii) recover the surface by projecting a
category instance property as an instance **getter method**
(`[Export("MLNCoordinateValue")] CLLocationCoordinate2D GetMLNCoordinateValue();`), which *is*
legal in a static extension class — unverified, and it changes emitted names, so it needs its
own fixture.

**Validate:** after (i), `binding-report.json` for MapLibre lists the 33 dropped properties.
After (ii), the generated `Swift.MapLibre` surface exposes the unboxers; add a MapLibre test
that boxes a `CLLocationCoordinate2D` via `NSValue.ValueWithMLNCoordinate` and reads it back,
asserting equality.

## 2. A C-array parameter binds as a single `out` scalar whose generated body zeroes the caller's data

`ObjCTypeMapper.IsValueTypePointerParameter` (`:554-600`) answers "is this a pointer to a
value type" and structurally cannot distinguish a pointer to **one** value from a pointer to
the **first element of an array**: `CLLocationCoordinate2D *` reaches the Apple-value-type arm
(`:593`) and returns true, so `+polylineWithCoordinates:count:` emits
`out CLLocationCoordinate2D coords` — and C# `out` semantics assign `coords = default`
*before* the native call, so the caller's coordinates are zeroed rather than passed, and only
one element could ever have been carried anyway. Runtime-confirmed by the MapLibre test app's
GAP-PROBE ((0,0) readback). Affects `polylineWithCoordinates:count:`,
`polygonWithCoordinates:count:`, `MLNMultiPoint` set/append/insert, and
`MLNMapView.SetVisibleCoordinates`. **This is a silent wrong-data shape, not a compile
error** — no gate in the pipeline can see it, which ranks it above the rest of this cohort.

**Fix shape:** the model discards constness (`ObjCTypeRef` has no `IsConst`;
`ClangAstParser` never reads the qualType qualifier), and a `const T *` is by definition an
input, never an out-param — recovering that one flag rejects the whole family and is the
fail-closed half. Binding it *usefully* additionally needs the `count:` sibling parameter
correlated so the pair projects as a single array parameter.

**Validate:** GAP-PROBE at `libraries/MapLibre/tests/Program.cs:1337` currently *confirms*
the zeroed readback. Fail-closed half done → those members drop as recorded skips and the
probe no longer compiles (update it to assert absence-with-skip-record). Full fix done →
convert the probe into a real test: create an `MLNPolyline` from an array of coordinates and
— together with item 3 — read the vertices back and assert them.

## 3. `_NSRange` (the struct tag) is unresolvable where `NSRange` (the typedef) resolves

`Data/objc-type-mappings.json` registers `NSRange` in both `objcValueTypes` and
`systemStructs`, but clang's AST prints the underlying record tag `_NSRange` for these
parameters and the mapper has no de-sugaring step, so every member taking one drops as
`ObjCUnresolvableType`: MapLibre's `getCoordinates:range:`, `replaceCoordinatesInRange:`,
`removeCoordinatesInRange:`, plus 3 `MLNMapViewDelegate` glyph callbacks. **Combined with
item 2, the consequence is that there is no working path at all to read a polyline's or
polygon's vertices from C#** — the two items are one consumer-visible hole and should be
closed together.

**Fix shape:** register the tag spellings alongside the typedefs (audit for siblings —
`_NSZone`, `_NSRange`, …), or strip a single leading `_` when the de-underscored name
resolves and the node is a record decl.

**Validate:** after the fix, `getCoordinates:range:` and the two range mutators appear in the
generated MapLibre binding (check `obj/**/swift-binding/*.cs` and the shrunken
`ObjCUnresolvableType` skip list in `binding-report.json`); the item-2 round-trip test is the
end-to-end proof.

## 4. No system-enum vocabulary: a member typed by another framework's enum is always unresolvable

`objc-type-mappings.json` carries tables for pointer types, CoreFoundation refs, primitives,
ObjC value types and system structs, but **no enum table** — and `enumNames` only ever holds
enums declared in the bound module's own translation unit. So any ObjC member typed by a
system enum resolves against nothing and drops: `CLAuthorizationStatus`,
`CLAccuracyAuthorization`, `CLActivityType`, `CLDeviceOrientation`,
`NSFormattingUnitStyle`. Costs MapLibre the whole `MLNLocationManager` authorization surface
and the formatter `UnitStyle`.

**Fix shape:** a `systemEnums` table shaped like the existing `systemStructs` one, mapping
each name to its Microsoft.iOS managed spelling; the emitted binding then needs the owning
framework's `using` and the consumer a `NativeReference` on it, so the table is the small
half of the work.

**Note — one name in the reported set is a different bug:** `MLNPluginLayerPropertyType` is
MapLibre's *own* enum, so its unresolvability is not this row; check it against the
anonymous-enum and umbrella-header rows in `not-planned.md` § ObjC & mixed bindings before
folding it in.

**Validate:** after the fix, the `MLNLocationManager` authorization members appear in the
MapLibre binding; add a test that reads an auth-status-typed member (type-level assertion is
enough — sim grants no location permission).

## 5. A class re-declaring an ancestor's property narrows it and warns CS0108

Class emission seeds `emittedPropertyNames` / accessor selectors from the class's own
properties, and protocols additionally seed from inherited protocols — but nothing walks the
`SuperclassName` chain (`ApiDefinitionEmitter.cs:396` records that classes have no inherited
seeding). So when a subclass's headers re-declare a property to satisfy a protocol
conformance — MapLibre's `MLNPolyline` re-declares `title` **read-only** for
`MLNAnnotation`/`MLNOverlay` while its base `MLNShape` declares it read-write — the emitted
subclass member hides the base member: `CS0108` in every consumer build, and the setter is
unreachable through a subclass-typed variable. Not fatal (upcasting to the base type works,
verified), but it is a real surface *narrowing* plus permanent warning noise.

**Fix shape:** seed the class walk from the resolved superclass chain and then either skip an
identical re-declaration or emit it `new` with the widest accessor set on the chain.

**Validate:** GAP-PROBE at `libraries/MapLibre/tests/Program.cs:1383` documents the shadow
and the upcast workaround. After the fix: MapLibre consumer build emits no CS0108 for
`MLNPolyline.Title`, and the probe converts to a test that sets `Title` through a
polyline-typed variable and reads it back.

## 6. SB0001-marked members are emitted as ordinary callables, and calling one faults the process

`SB0001` marks a member whose P/Invoke goes direct-CallConvSwift with non-blittable types and
no `@_cdecl` wrapper — i.e. exactly `roadmap.md`'s confirmed-upstream block #2 — via an
`[Obsolete]`-style "no `@_cdecl` wrapper" annotation. That mark is **advisory only**: the
member still emits as an ordinary callable, and invoking it does not throw a managed
exception, it **kills the process**. Confirmed on 5 `AEMReporter` members in the Facebook
validation pass (`AEMReporter.IsContentOptimized(null)` faults; the packages-repo test app
carries it as a first-class documented SKIP). That is the one outcome the repo's fail-closed
posture exists to prevent — a consumer following IntelliSense reaches a member the generator
already knew was unbridgeable, and gets a crash instead of a diagnosable failure.

**Decided (owner, 2026-07-31): real fix + tombstone floor.** First trace why each of the five
shapes is `CannotWrap` (`MethodWrapperEmitter.EvaluateWrapperEligibility` records the rejecting
guard) and extend `@_cdecl` wrapper eligibility where it is sound — the sibling
`recordAndUpdate`, with a near-identical `[String: Any]?` parameter, already gets a working
wrapper (`SBW_FBAEMKit_AEMReporter_recordAndUpdate_*`), so the wrappable/unwrappable wall runs
*through* this family and at least some of the five are expected to be genuinely fixable.
Whatever remains `CannotWrap` + non-blittable becomes a **throwing tombstone** like the
unsupported-closure family (`ClosureParamTombstoneEmitter`) — still visible, self-describing,
and safe; advisory-only emission of a fatal callable retires. Tombstones keep the declaration,
which preserves the protocol-conformance constraint that blocked outright suppression
(CS0535, noted at `WrapperValidation.cs:1963`).

**Validate:** the Facebook test app documents the fault as a SKIP around
`libraries/Facebook/tests/Program.cs:519` (the skip text deliberately avoids signal-name
strings — `ValidateSim`'s crash scanner greps stdout for them; keep it that way). If the
tombstone policy is adopted: convert the skip into a test asserting that calling
`AEMReporter.IsContentOptimized(null)` throws the tombstone's managed exception instead of
faulting.

---

**Minor observation, deliberately not a work item:** MapLibre narrows `NSExpression` paint
constants to `float` (0.42 → 0.41999998688697815) — likely inherent to the native
representation, handled with tolerance in the test app's assertions. Only revisit if a
consumer reports precision loss that tolerance can't paper over.
