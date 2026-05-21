# Session 6c — `sort(by:)` per-Value-type specialization + `KeyPath<any P, V>` existential admission

**Status:** code shipped (commit `62ec673e`). Phases 1, 2, 3 all landed; review-finding fixes (frozen-struct gate, variant-loss in distinct-V collection, NRT-erasure overload normaliser, param-signature dedup, bag/property availability merge) folded in. Doc-sync + verification items still outstanding — see "Outstanding after code ship" below.
**Parent session:** 6 (closed via 6a/6b at `af26a62d`).
**Branch:** `keypath-subsystem`.
**Driving deferrals:**
1. **1× `sort(by:)`** — the final tombstoned `MusicLibraryRequest<T>` surface from Session 6b's a-priori split (`06b-csm-method-own-generic-machinery.md` §Blocker C).
2. **`KeyPath<any P, V>` direct-parameter admission** — the `04-typed-singleton-emission.md:17` follow-up. The protocol-bag BindingTests fixture currently works around the rejection by typing the consumer parameter as `Swift.AnyKeyPath` and `as!`-casting on the Swift side (see `BindingTests/Sources/SwiftBindingsTestLib/KeyPath/KeyPathProtocolBag.swift:140-153` for the inline rationale).

After 6c lands, `MusicLibraryRequest<T>`'s full 11-member surface emits per-conformer (filter ×7, sort ×N per bag, response, filter(text:), limit/offset/includeOnlyDownloadedContent) and Session 6 closes.

## Real signature, verified from the iOS 26.2 swiftinterface

```swift
public struct MusicLibraryRequest<MusicItemType> where MusicItemType : MusicKit.MusicLibraryRequestable {
    ...
    public mutating func sort<Value>(
        by keyPath: Swift.KeyPath<MusicItemType.LibrarySortProperties, Value>,
        ascending: Swift.Bool)
}
```

Two corrections to the shape Session 6b assumed:

| 6b's assumption | Actual (verified) |
|---|---|
| `Value : Comparable` constraint | **`Value` is unconstrained** |
| Root is `Item` (the conformer) | Root is `Item.LibrarySortProperties` (a PAT-rooted nested bag) |

The first correction matters: an unconstrained method-own `Value` cannot be enumerated by walking conformers of a constraint protocol (there is no constraint protocol). The second correction matters in the opposite direction: it lifts a chunk of would-be 6c work into existing Session 4 infrastructure — Session 4's typed-singleton emitter already walks PAT-rooted associated-type bags. `Album.LibrarySortProperties` properties get typed-singleton fields exactly the way `Album.LibraryFilter` properties do, **without any new bag-walker emitter machinery**. The new work is per-Value-type Sort overload emission consuming that same bag walk.

## Route history (why this doc is a rewrite)

This doc was originally written around **Route A** — an `AnyKeyPath` C# boundary plus a Swift wrapper that did `unsafeDowncast(anyKP, to: KeyPath<Bag, Any>.self)` and called `request.sort(by: typedKP, ascending:)`. Route A is **dead**. Empirically verified on Swift 6.2.4: the cast site succeeds, but any subsequent `b[keyPath: cast]` subscript traps with `Fatal error: invalid unsafeDowncast` because the runtime keypath's Value-type metadata no longer matches `Any`. The trap fires inside the callee; Apple's `sort` body is closed-source, so we cannot prove it never subscripts, and even if today's iOS doesn't subscript, a future iOS might.

```swift
// Empirical evidence (Swift 6.2.4)
struct Bag { var title: String; var year: Int }
let kp: KeyPath<Bag, String> = \.title
let any: AnyKeyPath = kp
let cast = unsafeBitCast(any, to: KeyPath<Bag, Any>.self)  // OK at cast site
let b = Bag(title:"x", year:1)
_ = b[keyPath: cast]                                       // CRASH: invalid unsafeDowncast
```

**Route C — per-Value-type enumeration** — replaces Route A. For each closed conformer of `MusicLibraryRequestable`, walk its `LibrarySortProperties` bag, collect the **distinct projectable Value types** of the bag's stored properties, and emit one C# Sort overload per (conformer × distinct-projectable-Value-type). Each Swift wrapper does `unsafeDowncast` to the **exact** `KeyPath<Bag, ConcreteV>` — V matches the runtime keypath's metadata, no trap.

Route B (preserve method-own generic end-to-end) was considered and rejected: it requires generalizing CSM's "specializable" definition (which today is PAT-conformer based) and a runtime metadata witness for method-own generics on `@_cdecl` boundaries — far out of scope for 6c.

Estimated MusicKit surface count under Route C: 9 conformers × ~3–5 distinct projectable Value types per `LibrarySortProperties` bag ≈ **27–45 Sort overloads** (vs. 1 broken Route A surface).

## Architecture

### Blocker D — `KeyPath<any P, V>` existential admission

#### Site

`src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs:247-284` — `IsContainerWithSupportedDirectExistential`. Today the switch admits three shapes whose generic arguments are existential:
- `Optional<any P>`
- `Array<any P>`
- `Dictionary<K, any P>` (value-position existential)

Plus their Optional-wrapped variants by recursion.

KeyPath-family containers (`KeyPath`, `PartialKeyPath`, `WritableKeyPath`, `ReferenceWritableKeyPath`) are not in the switch. A method/property typed `KeyPath<any P, V>` falls through to `return false`, and the caller (`MemberEmissionValidator.cs:303,580`; `MethodHandler.cs:187,834`; `PropertyHandler.cs:279`; `ExistentialBypassEmitter.cs:61,408,1470`) rejects with `UnsupportedExistential`.

#### Fix

Add a new branch admitting `KeyPath<any P, V>` family containers when:
- Arity matches `TypeProjectionFactory.GetKeyPathArity(name)` (uses the family-membership single source of truth — rejects `AnyKeyPath` because arity 0).
- Slot 0 (Root) is a supported existential per `IsValidExistentialForContainer`.
- Slot 1 (Value, where present) is **not itself existential**.
- Slot 1 (Value, where present) **is projectable** via `TypeProjectionFactory.Project(valueSpec, …)` (i.e., `Project(…) != null`). This is the F4/F6 reviewer refinement from the paired review: "Value is not existential" is too weak — the projection must succeed so the emitted C# `KeyPath<Root, TValue>` type actually resolves to a real C# type. The check mirrors the per-prop projection skip already in `KeyPathSingletonEmitter.cs:524`.

```cs
// New branch in IsContainerWithSupportedDirectExistential, immediately after the Dictionary case.
//
// KeyPath family — Root (slot 0) may be existential. AnyKeyPath has arity 0 (no
// generic params) and is rejected here. PartialKeyPath<Root> has arity 1 (Root
// only). KeyPath / WritableKeyPath / ReferenceWritableKeyPath have arity 2
// (Root + Value).
if (TypeProjectionFactory.IsKeyPathFamily(outerNamedType.Name))
{
    var arity = TypeProjectionFactory.GetKeyPathArity(outerNamedType.Name);
    if (arity != outerNamedType.GenericParameters.Count) return false;
    if (arity < 1) return false;

    // Root (slot 0) must be a supported existential.
    if (!_existentialHandler.IsExistential(outerNamedType.GenericParameters[0])) return false;
    if (!IsValidExistentialForContainer(outerNamedType.GenericParameters[0])) return false;

    // Value (slot 1, where present) must not itself be existential, and must
    // project to a real C# type (otherwise the emitted KeyPath<Root, TValue>
    // would not compile).
    if (arity >= 2)
    {
        var valueSpec = outerNamedType.GenericParameters[1];
        if (_existentialHandler.IsExistential(valueSpec)) return false;
        if (TypeProjectionFactory.Project(valueSpec, /*ctx*/ ...) == null) return false;
    }
    return true;
}
```

Approximate cost: ~20 LOC in `BoundGenericsHandler.cs`. (Up from the original ~12 because of the arity gate + projection check.)

#### Why not Optional-wrap recursion

`Optional<KeyPath<any P, V>>` is a vanishingly-rare shape in Apple frameworks and the existing recursion already handles it generically as long as the inner is admitted. No extra code.

#### BindingTests fixture lift

`KeyPathProtocolBag.swift:150-211` currently routes through `Swift.AnyKeyPath`. After Blocker D, the natural signature `kp: KeyPath<ProtocolBag_BookFilter, Swift.String>` is admitted. Switch the fixture to the natural shape and drop the parameter-level `Swift.AnyKeyPath` boxing along with the `as!` cast in the Swift body. The receiver-side upcast `(filter as ProtocolBag_BookFilter)[keyPath: kp]` stays: Swift's typed-KeyPath subscript requires the receiver to match the KeyPath Root, and the Root is an existential — so reading through a `KeyPath<any P, V>` against a concrete witness needs the receiver upcast as a language invariant, not a workaround. Phase 1 implementation discovered this rule and kept the upcast; the design's original claim that the upcast would "simplify to `filter[keyPath: kp]`" was wrong.

The `samePath(_:_:)` helper at line 187 stays on `Swift.AnyKeyPath` (it tests the type-erased equality, not the typed access — that's deliberate, not a workaround).

### Blocker E — `sort(by:)` per-Value-type specialization (Route C)

#### The shape after parent-generic substitution

For `MusicLibraryRequest<Album>` (closed conformer), the sort method substitutes to:

```swift
mutating func sort<Value>(
    by keyPath: KeyPath<Album.LibrarySortProperties, Value>,
    ascending: Bool)
```

`Album.LibrarySortProperties` is a closed type (Session 4 already walks it for `LibraryFilter`-precedent typed singleton emission). `Value` is method-own and unconstrained — CSM's existing enumeration machinery (PAT-conformer cartesian only) cannot reach it. Route C **enumerates a new dimension**: the distinct projectable Value types of the bag's stored properties.

#### Why a sibling emitter, not a CSM extension

Both reviewers agreed (Codex F3 + Grok F3): Route C is *not* a CSM relaxation; it is an orthogonal axis.

- CSM's `ConcreteSpecializationEngine.FindSpecializableMethods` (line 548-785) only admits method-own generics that are **PAT-constrained** (calls `FindSpecializableProtocolConstraint(param)`; `continue` if `null`). Sort's `<Value>` is unconstrained → it never enters `SpecializableParam`.
- `ConcreteProtocolSpecializationEmitter.Sync.cs:34-92`'s `IsCsmSyncEligibleForGenericParent` enforces ownParamCount parity ("every method-own generic param must be specializable") — same gate, same reject.
- Forcing unconstrained `<V>` into `SpecializableParam` would corrupt the model and create cross-coupling between two unrelated specialization axes.

Instead, Route C is a **sibling emitter** parallel to `KeyPathSingletonEmitter`, invoked from the same three handler post-body sites that already call the singleton emitter:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs:417,422`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs:444,448`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs:362,366`

The sibling emitter:
1. Re-uses CSM's `engine.GetConformers(protocolName)` to enumerate the parent's closed conformers (no new conformer logic).
2. For each conformer, resolves the bag declaration (e.g., `Album.LibrarySortProperties`) via the shared bag walker (see below).
3. Collects distinct projectable Value types from the bag's stored properties.
4. Emits one C# Sort overload per (conformer × distinct projectable V).
5. Marks the original open-generic sort surface as `WasEmitted` so `MethodHandler` skips the parent-body emission (same discipline as `KeyPathSingletonEmitter` and CSM).

Working name: `KeyPathBagValueSpecializationEmitter` (or `RouteCSortEmitter` — final name TBD during impl). Lives next to `KeyPathSingletonEmitter.cs` for proximity to the bag walker it shares.

#### Shared bag walker (extracted from `KeyPathSingletonEmitter`)

Codex F2 + Grok F2: Session 4 already contains the bag walker; do not duplicate.

`KeyPathSingletonEmitter.cs` is the sole file that walks PAT-rooted associated-type bags:
- `FindBagDecl(...)` (around line 324) — resolves bag decl from conformer + associated-type name.
- `IsEmittableBag(...)` (around line 395) — bag-level admission gate.
- `IsEmittableProperty(...)` (around line 467) — per-prop admission gate.
- `foreach (var prop in bagDecl.Properties)` (around line 508) — property walk.
- Per-prop projection skip (line 524) — `TypeProjectionFactory.Project(...)` returns null → skip.

Extract a small shared internal helper:

```cs
// In KeyPathSingletonEmitter (or a sibling helper file alongside).
internal static class KeyPathBagWalker
{
    /// <summary>
    /// Resolves the bag decl for (conformer × associatedTypeName) and returns
    /// (bagDecl, projectableProperties) where projectableProperties is the
    /// subset whose declared Value type passes TypeProjectionFactory.Project(...).
    /// Returns null when the bag is unresolvable, not emittable, or has zero
    /// projectable properties.
    /// </summary>
    public static BagWalkResult? TryResolveProjectableBagProps(
        TypeDeclaration conformer,
        string assocTypeName,
        TypeDatabase typeDatabase,
        ...);

    public readonly record struct BagWalkResult(
        TypeDeclaration BagDecl,
        IReadOnlyList<(PropertyDecl Prop, TypeProjection Projection)> ProjectableProps);

    /// <summary>
    /// Distinct projection of bag props onto their public projected C# Value
    /// type. Two properties with the same projected PublicType collapse to one
    /// entry. Used by the Route C emitter to decide the per-V Sort overload set.
    /// </summary>
    public static IReadOnlyList<TypeProjection> DistinctProjectedValueTypes(
        BagWalkResult bag);
}
```

The existing singleton emitter calls this helper for its per-prop emission. The new Route C emitter calls the same helper, then projects onto distinct Value types and emits one overload per distinct V.

#### Predicate (V-erasure-safe shape)

Route C only applies to methods matching a tightly-scoped shape. Both reviewers reinforced (Codex F3 + Grok F3 + the original predicate sketch):

A method qualifies for per-V specialization iff **all** of:

1. Has a parent generic constrained to a PAT (so CSM's conformer enumeration applies).
2. Has **exactly one** method-own generic parameter `V`.
3. `V` has **zero constraints** (unconstrained — Route C handles this *by design*; constraint-bearing V would need Route B).
4. `V` appears **exclusively** in the Value slot of **exactly one** KeyPath-family parameter. Not in the return type, not in any other parameter, not in any where-clause, not transitively inside a nested generic.
5. The KeyPath-family parameter's Root type spec references the parent generic's associated-type bag (e.g., `Parent.LibrarySortProperties`) — the bag must resolve and be projectable through the shared walker.
6. The method is **not async**, **not throws-with-typed-error**, **not actor-isolated** (these add witness-table complications that are out of scope; the simple synchronous mutating shape is what MusicKit's `sort(by:)` is).

The predicate lives in a single source-of-truth helper used by both the suppression path (so the open-generic surface is marked `WasEmitted`) and the Route C emitter (so it knows what to emit). Working name: `IsRouteCSortShapeEligible(MethodDecl, ...)`. Mirrors 6b's "single source of truth" discipline (Grok F1 from prior review).

#### C# emitted surface (per closed conformer × per projectable Value type)

```cs
public static class MusicLibraryRequestAlbumCsmExtensions
{
    public static void Sort(
        this Swift.MusicKit.MusicLibraryRequest<Swift.MusicKit.Album> req,
        Swift.KeyPath<Swift.MusicKit.Album.LibrarySortProperties, string> keyPath,
        bool ascending)
    {
        // P/Invoke into SBW_..._sort_String_..._ using req's swift handle + keyPath.DangerousGetHandle()
    }

    public static void Sort(
        this Swift.MusicKit.MusicLibraryRequest<Swift.MusicKit.Album> req,
        Swift.KeyPath<Swift.MusicKit.Album.LibrarySortProperties, nint> keyPath,
        bool ascending)
    {
        // P/Invoke into SBW_..._sort_Int_..._
    }

    // ...one Sort overload per distinct projectable Value type in Album.LibrarySortProperties.
}
```

Overload key collision analysis (Codex F4 + Grok F4): the existing dedup uses the **full projected PublicType** of each parameter (`DefaultParameterOverloadEmitter.cs:657` `GetProjectedOverloadKey`; `IHandler.cs:521` `GetProjectedCSharpMethodKey`). `KeyPath<Album.LibrarySortProperties, string>` and `KeyPath<Album.LibrarySortProperties, nint>` produce distinct key strings (different `TValue` substring). **No collision.** The Route C emitter must use this exact projected-public-type key — never raw Swift spellings like `Swift.String` / `Swift.Int` if the emitted signature uses `string` / `nint`.

#### Swift `@_cdecl` wrapper shape (per emitted Sort surface)

```swift
@_cdecl("SBW_<Module>_<Parent>_<Conformer>_sort_<ProjectedV>_<signature-hash>")
public func sbw_..._sort_String(
    _ requestPtr: UnsafeMutablePointer<MusicLibraryRequest<Album>>,
    _ kpHandle: UnsafeRawPointer,
    _ ascending: Bool,
    _ outPtr: UnsafeMutablePointer<MusicLibraryRequest<Album>>
) {
    let anyKP = Unmanaged<AnyKeyPath>.fromOpaque(kpHandle).takeUnretainedValue()
    // Exact concrete downcast — V matches runtime keypath's Value metadata exactly,
    // no trap. This is the entire reason Route C works where Route A fails.
    let typedKP = unsafeDowncast(anyKP, to: KeyPath<Album.LibrarySortProperties, String>.self)
    var request = requestPtr.pointee
    request.sort(by: typedKP, ascending: ascending)
    outPtr.initialize(to: request)
}
```

No `precondition(rootType == …)` guard is needed: the C# signature already statically constrains both Root *and* Value to the exact concrete types. A C# caller cannot construct a `KeyPath<Album.LibrarySortProperties, string>` rooted in some other type — Swift's keypath type system enforces it at construction. (If we ever admit existential-rooted `KeyPath<any P, V>` *into a Sort call* the picture changes — but the Route C surface emits closed-conformer-rooted overloads, so the existential admission path doesn't reach this wrapper.)

#### Per-V tombstoning (Codex F5 + Grok F5)

A bag's stored properties may have a mix of projectable and unprojectable Value types (e.g., `MyOpaqueStruct` that doesn't bind). Tombstoning is **per-overload (per-V)**, not per-conformer:

- For each conformer × distinct V: emit the overload iff `TypeProjectionFactory.Project(valueSpec)` succeeded for at least one bag property with that V.
- Skip individual unprojectable Vs — don't drop the whole conformer.
- The skipped Vs surface in CSM's per-method skip-reason log so they're explainable.

This mirrors `KeyPathSingletonEmitter.cs:524`'s existing per-prop skip discipline.

#### TypeDB substitution

The substituted Root (`MusicItemType.LibrarySortProperties` → `Album.LibrarySortProperties`) reuses 6b's `SubstitutePairingGenericsInTypeSpec` threading (`CPSE.cs:556,904,1242`) for the Root render in the Swift wrapper. No new threading. The Value substitution is direct (the concrete projectable V is computed by the Route C emitter, not by CSM substitution).

## Predicate ↔ emitter contract (D's lesson, re-stated)

The Route C eligibility predicate (`IsRouteCSortShapeEligible`) must match the emitter's per-pairing dry-run exactly. Same single-source-of-truth helper consulted by:

- The Route C sibling emitter (deciding whether to emit overloads at all).
- The CSM open-generic-suppression path (deciding whether to mark the method `WasEmitted` so it doesn't also emit as an open-generic parent surface).
- The CSM eligibility predicate (`IsCsmSyncEligibleForGenericParent` and the async sibling) — these need to know that "this method has a method-own generic V" is OK *iff* Route C will pick it up. Otherwise the existing parity gate would reject the parent class, masking the per-conformer Route C output.

Drift between any of these three is the bug shape that bit 6b ("D's lesson"). Same defense: one helper, one set of facts, asserted from three call sites.

## Phased implementation plan

Three phases. Per-phase = one commit, gated by per-phase validation.

### Phase 1 (commit 1) — `KeyPath<any P, V>` admission + AnyKeyPath workaround lift

**Goal:** admit `KeyPath<any P, V>` (and KeyPath-family variants) into `IsContainerWithSupportedDirectExistential` with the strengthened projectability gate. Lift the BindingTests workaround in `KeyPathProtocolBag.swift`. Add generator-side unit tests on the new branch.

**Files (estimated):**
- `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs` — new KeyPath-family branch with arity + projectability gates (~20 LOC).
- `src/Swift.Bindings/test/SwiftBindings.Tests/BoundGenericsHandlerTests.cs` (or wherever the existing handler tests live) — admission cases (`KeyPath<any P, V>`, `PartialKeyPath<any P>`, `WritableKeyPath<any P, V>`, `ReferenceWritableKeyPath<any P, V>`) + rejection cases (Value-existential rejected, AnyKeyPath rejected because arity 0, Value-unprojectable rejected, malformed-arity rejected). ~40 LOC.
- `BindingTests/Sources/SwiftBindingsTestLib/KeyPath/KeyPathProtocolBag.swift` — switch the 6 consumer methods (`ProtocolBag_BookConsumer.readTitle/readYear/readIsFiction/readRating`, `ProtocolBag_MovieConsumer.readTitle/readRuntimeMinutes`) from `Swift.AnyKeyPath` to typed `KeyPath<ProtocolBag_*Filter, V>` parameters. Drop the `as!` cast on the KeyPath; keep the `(filter as ProtocolBag_*Filter)[keyPath: kp]` receiver upcast (Swift typed-KeyPath subscript requires receiver = Root, and Root is existential — language invariant). ~30 LOC delta (net reduction).
- `BindingTests/RuntimeTestsApp/KeyPath/KeyPathProtocolBagTests.cs` — adjust C# tests to call the new typed Swift signatures directly (no behavioural change; cleanup). The `samePath(_:_:)` test stays as-is. ~20 LOC delta.

**Phase-1 gates:**
- `nuke test` green (new unit tests).
- `nuke binding-tests --compile-only` (regen + compile-check) green.
- `nuke binding-tests --skip-regen` sim green (existing tests still pass with the typed signature).
- Device + validate deferred to phase 3.

**Phase-1 r1 review:** paired Codex + Grok mandatory before commit.

### Phase 2 (commit 2) — Route C sibling emitter + shared bag walker extraction

**Goal:** extract the bag-walker helper from `KeyPathSingletonEmitter`, add the Route C sibling emitter, wire predicate ↔ emitter contract, exercise via a generic BindingTests fixture (no MusicKit dependency yet).

**Files (estimated):**
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/KeyPathBagWalker.cs` (new) — shared `TryResolveProjectableBagProps` + `DistinctProjectedValueTypes` helpers (~80 LOC, including doc comments + the BagWalkResult record).
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/KeyPathSingletonEmitter.cs` — refactor the existing bag-walk to call `KeyPathBagWalker`. Net delta should be small (extraction, not addition). ~20 LOC delta.
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/KeyPathBagValueSpecializationEmitter.cs` (new) — Route C sibling emitter. For each (parent generic, conformer) pair: resolve bag → walk distinct projectable Vs → emit per-V Sort overload (C# extension + Swift `@_cdecl` wrapper with exact `unsafeDowncast`). Re-uses `ConcreteSpecializationEngine.GetConformers(...)` and existing CSM rendering helpers (`BuildKeyPathPublicCSharpType`, `SubstitutePairingGenericsInTypeSpec`). ~120 LOC.
- `src/Swift.Bindings/src/Marshaler/RouteCSortShapeEligibility.cs` (new) — `IsRouteCSortShapeEligible(MethodDecl, …)` single-source-of-truth predicate. ~50 LOC.
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.Sync.cs` — augment `IsCsmSyncEligibleForGenericParent` to consult `IsRouteCSortShapeEligible`: if the method-own generic is Route-C-eligible, allow the parent class to specialize without rejecting on ownParamCount parity. ~10 LOC.
- `src/Swift.Bindings/src/Marshaler/ConcreteSpecializationEngine.cs` — `FindSpecializableMethods` (~548) and `ParentTupleSatisfiesMethodConstraints` (~1139) need awareness that Route-C-eligible method-own generics are not blockers. ~10 LOC.
- Call sites in handler post-body emission (`ClassHandler.cs`, `FrozenStructHandler.cs`, `NonFrozenStructHandler.cs`) — invoke the new Route C emitter alongside the existing singleton emitter call. ~3 LOC per file × 3 files = 9 LOC.
- `BindingTests/Sources/SwiftBindingsTestLib/Generics/RouteCSortByKeyPath.swift` (new) — fixture mirroring MusicKit's shape without depending on MusicKit. `protocol SortBagProto { associatedtype SortBag }` with 2 concrete conformers (`ConformerA`, `ConformerB`), each defining a nested `SortBag` with properties of mixed projectable types (e.g., `title: String`, `year: Int`, `rating: Double`, `releaseDate: Date`). Parent struct `RouteCRequest<T: SortBagProto>` with `mutating func sortBy<V>(_ keyPath: KeyPath<T.SortBag, V>, ascending: Bool)`. Body reads `_kvcKeyPathString` via reflection into a published `lastSortKey` property so tests can observe the boundary worked. (~80 LOC.)
- `BindingTests/RuntimeTestsApp/Generics/RouteCSortByKeyPathTests.cs` (new) — assertions: per-conformer extension class exists; per-V overloads exist for each distinct projectable V in each conformer's bag; passing a Session-4 typed singleton round-trips and `lastSortKey` matches. Negative: a property with an unprojectable Value type has no corresponding Sort overload. ~100 LOC.

**Tripwire reminder:** Phase 2 is the largest commit. If it crosses **300 LOC or 8 files** during impl, pause + SendMessage team-lead. (Higher tripwire than 6b's because Route C is intrinsically more machinery — the sibling emitter + shared helper extraction + predicate helper are all new files.)

**Phase-2 gates:**
- `nuke test` green (new predicate + helper + emitter unit tests).
- `nuke binding-tests --compile-only` green.
- `nuke binding-tests --skip-regen` sim green; new fixture passes.
- Device + validate deferred to phase 3.

**Phase-2 r1 review:** paired Codex + Grok mandatory before commit. Highest-stakes phase — review focus: predicate↔emitter coupling, helper extraction correctness, per-V tombstoning.

### Phase 3 (commit 3) — MusicKit `sort(by:)` wiring + full validation sweep

**Goal:** regen MusicKit and confirm `sort(by:)` emits per (closed conformer × distinct projectable V) across 9 conformers. Update validation baseline. Run cross-cutting gates.

**Files (estimated):**
- Regen-only — no generator-side code changes.
- `.validation-baseline.json` — bump `cs_compile` + `swift_compile` to reflect new MusicKit emission (expected surface delta: ~27–45 new Sort overloads, depending on per-conformer bag shapes).
- `src/docs/keypath-subsystem/00-overview.md` — status section updated; 6c done.
- `src/docs/keypath-subsystem/06-musiclibraryrequest-re-enablement.md` — mark exit criteria fully met.
- `BindingTests/RuntimeTestsApp/SmokeTests/MusicKitSmokeTests.cs` — add a `Sort` smoke test exercising `req.Sort(AlbumLibrarySortPropertiesKeyPaths.Title, ascending: true)` (or whichever property Session 4 emits for the `Album.LibrarySortProperties` bag). ~25 LOC.

**Phase-3 gates (full sweep):**
- `nuke test` green.
- `nuke binding-tests --skip-regen` sim green; all 6b + 6c fixtures pass; no green→red.
- `nuke binding-tests --device --skip-regen` green; **0 crashes per device flake-vs-regression memory; if crash count > 0, rerun once fresh before drawing conclusions**.
- `nuke validate --filter MusicKit` — MusicKit must stay 4/4 pass; surface count rises (27–45 new sort surfaces expected); per-conformer extension classes confirmed for all 9 conformers.
- `nuke validate` (full sweep) — baseline holds; if `cs_compile` or `swift_compile` drops below baseline on any non-MusicKit consumer, regression — escalate before committing.

**Phase-3 r1 review:** paired Codex + Grok mandatory before commit. **r2 on Critical/High findings only.**

## Risks

| # | Risk | Phase | Mitigation |
|---|---|---|---|
| R1 | A bag with zero projectable Value types emits zero Sort overloads for that conformer, silently | 2 | Per-V tombstoning is deliberate; the empty case logs a per-conformer skip reason ("bag has no projectable properties"). Phase-3 MusicKit emission asserts every conformer has ≥1 overload (sanity check — every `LibrarySortProperties` bag has a `title: String` or similar at minimum). |
| R2 | A bag has two properties with the same projected Value type (e.g., two `String` props) — distinct-V collapsing drops one Sort overload by design, but the test would surface a one-keypath-per-V mismatch | 2 | The Route C emitter emits one *overload* per distinct V — the C# user can pass any Session-4 singleton with that V to that one overload. The mapping is (one overload) → (N keypaths sharing V). Test asserts overload count = distinct V count, not property count. |
| R3 | Predicate-↔-emitter drift: the V-erasure-safety check in `IsRouteCSortShapeEligible` falls behind a new Route C emitter gate added later | all | Same `IsRouteCSortShapeEligible` helper called from emitter, suppression path, AND sync predicate. Function-doc-level note documenting the three-way contract. |
| R4 | A method-own generic with constraint (e.g., `<V: SomeProto>`) slips through and gets Route-C-treated when it shouldn't | 2 | Helper explicitly requires zero constraints on V. Add a negative unit test for `<V: Comparable>` and `<V: Equatable>`. |
| R5 | Lifting the `KeyPathProtocolBag` AnyKeyPath workaround (typed `KeyPath<any P, V>` parameter + dropped `as!` cast) trips an unrelated bug in proxy emission or existential-parameter marshalling for the new typed signature | 1 | Phase-1 retired this risk: the lifted fixture ran the full sim suite at 2201 pass / 0 fail with baseline parity. The receiver-side `(filter as ProtocolBag_*Filter)[keyPath: kp]` upcast was kept intentionally (Swift language invariant, not a workaround). Mitigation reference only — no further action. |
| R6 | Phase-2 helper accidentally over-accepts a non-sort method that happens to have a method-own unconstrained `V` in a KeyPath Value slot but also uses `V` elsewhere | 2 | Helper requires V appears EXCLUSIVELY in ONE KeyPath Value slot. Add a negative unit test for `<V>(_: KeyPath<Root, V>, other: V) -> V` (V in 3 positions). |
| R7 | A method matches the Route-C predicate but is async / throws-typed-error / actor-isolated, and the simple `@_cdecl` wrapper template doesn't compose with those wrappers | 2 | Helper rejects async / throws-typed-error / actor-isolated up front (see predicate condition #6). Negative unit tests for each. If MusicKit ever exposes such a sort variant, a follow-up session designs the composition. |
| R8 | MusicKit conformer-set regression — Apple's iOS 26.x might add/remove `MusicLibraryRequestable` conformers; `LibrarySortProperties` shape might also shift per-conformer | 3 | Phase-3 captures actual conformer count + per-conformer bag shapes post-regen. Update `MusicKitSmokeTests.cs` baseline. Per 6b-Risk-A pattern. |
| R9 | Device-gate flake on cold device first run mis-attributed to Phase 3 regression | 3 | Per memory `feedback_device_gate_flake_vs_regression.md`: rerun device gate fresh if `CrashCount > 0` before drawing conclusion. |
| R10 | `unsafeDowncast(_, to: KeyPath<Bag, ConcreteV>.self)` produces a Swift compiler warning at the wrapper generation site for some V projection (e.g., a rare numeric alias) | 2 | If warning appears, use `unsafeBitCast` instead — same runtime behavior, fewer compiler smarts. The 6b emitter has precedent (`BoundGenericsHandler.cs:203` does `unsafeBitCast` for `[any P.Type]`). |
| R11 | Distinct-V collection produces a V that projects to the same C# type as another (e.g., `Int` and `Int32` both → `nint`) — overload key collision | 2 | Distinct collection runs **after** projection (on the projected `PublicType` string), not on raw Swift type names. Two Swift Vs that project identically collapse to one Sort overload by design — that's the correct C# overload-resolution outcome. |
| R12 | Phase-2 LOC exceeds the bumped 300/8 tripwire — Route C machinery turns out wider than estimated | 2 | Tripwire-driven pause + SendMessage. Spillover lands as 6d follow-up. Most likely overrun: `KeyPathBagValueSpecializationEmitter` or shared helper exceeds estimate. |

## Out-of-scope (explicit)

- Method-own constraint-bearing generics (e.g. `<V: Comparable>` at the boundary). The 06b assumption that sort was constraint-bearing turned out wrong; if a future Apple SDK adds a sort variant with `<V: Comparable>`, that needs its own design pass.
- Method-own generics with V appearing in MORE THAN one position (e.g., return type + parameter). The V-erasure helper rejects.
- Conformer-rooted KeyPath construction from C# (e.g., `KeyPath<Album, String>` for `\Album.title`). Session 4 emits bag-rooted singletons only; conformer-rooted singletons would need a separate emitter session. Not blocking 6c — MusicKit's sort is bag-rooted.
- Sessions 7-10 consumer productionization (depends on this but is independent work).
- Any `Swift.Runtime` SafeHandle changes beyond what's already in `keypath-subsystem`.
- Existential-rooted Sort calls (`req.Sort(kp: KeyPath<any P, V>, …)`). The Route C surface emits closed-conformer-rooted overloads; existential admission (Blocker D) covers the *receive-side* generic shape only.
- Generalizing Route C to non-sort method shapes in other Apple frameworks. The predicate is structural (one unconstrained method-own V in KeyPath Value slot of one parameter), so other future surfaces matching the shape would benefit automatically — but no current in-scope framework other than MusicKit has been audited for matching surfaces.

## Internal split criterion (6c → follow-up) during impl

PAUSE + SendMessage team-lead BEFORE proceeding if any of:
- Phase 2 crosses **300 LOC or 8 files** in CSM/Route-C emitter machinery (bumped from 6b's 150/5 — Route C is intrinsically more machinery).
- New Swift wrapper or runtime work surfaces beyond what's design-doc'd here.
- The `KeyPathBagValueSpecializationEmitter` extraction reveals an unexpected coupling to a CSM internal that needs a wider refactor.
- New architectural surprise (e.g., distinct-V collection collides on overload keys despite the F4 analysis; or the predicate shape doesn't compose with an existing CSM gate).

In those cases the spillover lands as a 6d follow-up (with the trip-criterion that triggered it documented).

## Outstanding after code ship

Tracked here so a follow-up session can close them without re-deriving from git history. None of these are code-blocking; they are doc/verification housekeeping that the 6c code commit (`62ec673e`) did not cover.

- **`MusicLibrarySectionedRequest<T>` not verified.** The 06.md exit criteria call out parity with `MusicLibraryRequest<T>`. Route C's predicate is structural, so it should emit the same shape — but no one has hand-inspected the regenerated output or counted overloads.
- **Device gate not run for Route C.** Only `nuke binding-tests --skip-regen` (sim, Mono JIT) was run after the Route C fixes. NativeAOT device run (`--device`) is required by CLAUDE.md for changes that touch calling conventions / @_cdecl wrapper shape.
- **Status of `00-overview.md` not refreshed.** The overview's status section still pre-dates 6b/6c shipping.
- **`06.md` exit-criteria checklist not ticked off** (see that doc's own "Outstanding after code ship" mirror).
- **Active release doc A-1 deferral** (likely `src/docs/sdk-0.11.0-remaining.md` or successor) not closed.
- **Public wiki update** not posted (`Known Limitations` for `MusicLibraryRequest`-style PAT generics should now retract the "no sort" caveat).
- **Two Future docs left untracked** in `src/docs/Future/` from prior work (`foundation-nsobject-typed-upgrade.md`, `property-getter-constrained-generic.md`) — unrelated to 6c but visible in `git status`; either commit standalone or rebase out.

## References

- `keypath-subsystem/00-overview.md` — design decision (typed singletons for IN path, SafeHandle for OUT).
- `keypath-subsystem/04-typed-singleton-emission.md:17` — bound-generic-existential allowlist follow-up.
- `keypath-subsystem/06-musiclibraryrequest-re-enablement.md` — parent re-enablement spec (sort tombstoned via 6a/6b).
- `keypath-subsystem/06b-csm-method-own-generic-machinery.md:88-94,240-241,246-255` — explicit 6b→6c boundary (`sort(by:)` + bound-generic-existential admission).
- `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs:247-284` — `IsContainerWithSupportedDirectExistential` (Blocker D site).
- `src/Swift.Bindings/src/Marshaler/Projection/TypeProjectionFactory.cs:295,552-575` — `KeyPathFamilyArities` + `IsKeyPathFamily` + `GetKeyPathArity` + `Project` (projectability check for Blocker D).
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodClosureBridge.cs:2095,2122-2123,1745-1756` — `ParamAbiCategory.KeyPathFamily` + `ClassifyParam` + `IsAbiCategoryPassable` (6b's foundation).
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/KeyPathSingletonEmitter.cs:78,115,324,395,467,508,524` — Session 4 bag walker (extraction source for the shared `KeyPathBagWalker`).
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs:417,422`, `FrozenStructHandler.cs:444,448`, `NonFrozenStructHandler.cs:362,366` — handler post-body sites that invoke the singleton emitter and will also invoke the new Route C emitter.
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.Sync.cs:34-92` — sync parent-generic predicate (Route C predicate consultation site).
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs:1192,745,2490,2717-2818` — KeyPathFamily ABI arm + emission loop + `BuildKeyPathPublicCSharpType` (6b precedent for typed-KeyPath rendering).
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/DefaultParameterOverloadEmitter.cs:657`, `src/Swift.Bindings/src/Marshaler/IHandler.cs:521` — overload key construction (collision analysis F4).
- `src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmissionContext.cs:195,209` — dedup container + `TryAddKeyPathSingletonContainer` (precedent for Route C container dedup).
- `src/Swift.Bindings/src/Marshaler/ConcreteSpecializationEngine.cs:548,723,752,1139` — `FindSpecializableMethods` + method-own gate + `ParentTupleSatisfiesMethodConstraints` (Route C suppression-awareness sites).
- `BindingTests/Sources/SwiftBindingsTestLib/KeyPath/KeyPathProtocolBag.swift:140-211` — current `AnyKeyPath` workaround that Blocker D lifts.
- Apple swiftinterface: `/Applications/Xcode-26.3.0.app/.../iPhoneSimulator.sdk/.../MusicKit.framework/Modules/MusicKit.swiftmodule/arm64-apple-ios-simulator.swiftinterface` — verified sort signature.

## Reviewer-finding origin trace

Tracked so future sessions can see which design decisions trace to which reviewer findings:

- **Route C pivot (vs. Route A)** — paired Codex F1 + Grok F1 (both Critical), empirical Swift 6.2.4 evidence.
- **Sibling emitter (vs. CSM extension)** — Codex F3 + Grok F3 (both High).
- **Shared bag walker (no duplication)** — Codex F2 + Grok F2 (both High).
- **Per-V tombstoning (not per-conformer)** — Codex F5 + Grok F5 (both Medium).
- **Blocker D `Project()` projectability gate (not just `!IsExistential`)** — Codex F4/F6 + Grok F6 (Medium/High).
- **Overload-key collision safety** — Codex F4 + Grok F4 (both Medium, confirmatory — no new logic needed).
- **`precondition` removal at Swift wrapper** — falls out of Route C: the exact concrete C# signature makes runtime root-check unnecessary. (Resolves prior-review F5 from `/tmp/6c-codex-review.md` re: `precondition` testability.)
- **Predicate single source of truth (helper helper helper)** — D's lesson restated from 6b; reinforced by Grok F1 (prior review) and Codex F3 (Route C review).
