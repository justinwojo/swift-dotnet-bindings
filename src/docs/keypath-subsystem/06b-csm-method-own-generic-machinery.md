# Session 6b — CSM method-own generic machinery for KeyPath param substitution

**Status:** design, awaiting team-lead approval.
**Parent session:** 6 (closed at `345dd701`).
**Branch:** `keypath-subsystem`.
**Driving deferral:** 8 tombstoned `MusicLibraryRequest<T>` surfaces — 7× `filter(matching:…)` (KeyPath-shaped) + 1× `sort(by:)` (Value-erasure).

This doc designs the CSM widening needed to emit those surfaces. It also defines the **6b ↔ 6c boundary**: `sort(by:)` is split out a priori (it trips the wrapper/runtime guardrail), and an internal trip-criterion is set for further splitting if the filter machinery itself overshoots LOC tripwires.

## Scope decision (a priori split)

| Shape | Session | Rationale |
|---|---|---|
| 7× `filter(matching:…)` — closed-conformer KeyPath at PAT-parent-generic methods | **6b** (this) | Predicate/emitter/typedb plumbing only. No new Swift wrapper or runtime helper. Reuses Session 4's emitted typed singletons on the IN path. |
| RelatedMusicItemType cross-mult containment | **6b** (this) | Tiered Phase 3 sequence: Step 1 regen with no containment → measure cartesian per surface; Step 2 if cap-OK ship 0-LOC; Step 3 if explosion ship threshold + same-conformer fast-path (~30 LOC, predicate spec'd in Phase 3 section for r1 pre-validation); Step 4 if threshold drops Apple-exposed surfaces, defer structural enumeration-allowlist to follow-up (DO NOT cram into 6b). |
| `sort<V>(by: KeyPath<Item, V>)` — method-own unconstrained Value generic at `@_cdecl` boundary | **6c** | Requires `KeyPath<any P, V>` admission into `BoundGenericsHandler.IsContainerWithSupportedDirectExistential` (`04-typed-singleton-emission.md` line 17 follow-up) AND a Swift wrapper that calls the Value-typed property setter through an existential bridge. Trips guardrail #4 (no Swift wrapper / runtime work without explicit design pass). |
| `KeyPath<any P, V>` bound-generic-existential admission generally | **6c** | Couples with sort(by:). Out of 6b. |

**Internal split criterion 6b → 6c during impl:** if any single phase of 6b proper crosses **150 LOC OR 5 files in CSM emitter machinery**, pause and SendMessage team-lead before continuing. Don't merge phases into a single megacommit to avoid the trip.

## Architecture: anchors verified against `keypath-subsystem` @ `345dd701`

(File paths and line numbers verified end-of-Session-6; several handoff anchors were stale and are corrected here. Source-of-truth is on-disk, not the handoff.)

### Blocker A — Filter KeyPath substitution: 3 sub-blockers

#### A1 — `HasNonGenericParamReferencingGeneric` blanket-rejects KeyPath<τ_0_0, V>

`ConcreteProtocolSpecializationEmitter.cs:2303-2342` walks each non-generic param's `SwiftTypeSpec` and rejects any pairing that references the parent generic. For `KeyPath<MusicItemType.LibraryFilter, MusicItemID>`, the `NamedTypeSpec` branch at line 2358 (`named.Name.StartsWith(genericParamName + ".")`) sees `MusicItemType.LibraryFilter` and matches `MusicItemType + "."` → returns true → the entire pairing is rejected. The guard is a correctness shield against unsubstituted generic refs in render output — but it pre-dates KeyPath's typed-singleton machinery, which DOES legitimately resolve such refs via Session 4's per-conformer trampolines.

**Fix:** relax the gate so that a non-generic param whose `SwiftTypeSpec` is a `KeyPath<Root, Value>` family type WHOSE `Root` is a substitutable associated-type reference (`τ_0_0.Bag`) and whose substituted form maps to a Session-4-emitted typed singleton container is **admitted** (not rejected). The relaxation is gated on:
1. `KeyPathFamilyArities.ContainsKey(named.Name)` (top-level KeyPath family).
2. The `Root` generic param of the KeyPath resolves via `SubstitutePairingGenericsInTypeSpec` to a fully-closed concrete type spec (no remaining open generics).
3. The `Value` slot is itself classifiable (primitive / ObjCHandle / Utf8Slice / Sessions 4's per-conformer typed singleton).

Approximate cost: a `TrySubstituteAndValidate` helper (~20 LOC) called from the `KeyPath`-family branch of the `ContainsGenericParam` walk.

#### A2 — No `KeyPathFamily` ABI category in `ParamAbiCategory`; PayloadHandle arm is wrong for SafeHandle

`MethodClosureBridge.cs:2052-2070` defines `ParamAbiCategory`: `Primitive | ObjCHandle | PayloadHandle | NativeRemapped | FrozenStruct | PointerType | Utf8Slice | Unsupported`. `ClassifyParam` (line 2075-2126) routes `Swift.KeyPath`/`Swift.AnyKeyPath`/`Swift.WritableKeyPath`/`Swift.ReferenceWritableKeyPath`/`Swift.PartialKeyPath` through `TryGetTypeRecord` — but those family types have **no TypeRecord** in any XML database (they're handled structurally via `TypeProjectionFactory.KeyPathFamilyArities` at `TypeProjectionFactory.cs:552-558`). The method falls through to `return Unsupported`. `IsAbiCategoryPassable` (line 1735) doesn't pass `Unsupported`, so `AreNonGenericParamsCompatible` fails → no pairing emits.

If `KeyPath` did classify as `PayloadHandle` (the closest existing arm), `ConcreteProtocolSpecializationEmitter.cs:1158` would emit:
```cs
callArgs.Add($"((global::Swift.Runtime.ISwiftObject){csName}).SwiftHandle");
```
But `SwiftKeyPathHandle` (the SafeHandle base in `src/Swift.Runtime/src/Swift/SwiftKeyPath.cs:50`) extends `SafeHandleZeroOrMinusOneIsInvalid` and does **not** implement `ISwiftObject`. The cast is a compile-time CS0030 (or runtime `InvalidCastException`). The `Payload => this` shim at line 83 of `SwiftKeyPath.cs` exists for emitter paths that emit `param.Payload.DangerousGetHandle()`, but the CSM PayloadHandle arm at CPSE.cs:1158 emits `((ISwiftObject)x).SwiftHandle` — wrong arm.

**Fix:** add a new `KeyPathFamily` member to `ParamAbiCategory`. In `ClassifyParam`, before the `TryGetTypeRecord` block, branch on `TypeProjectionFactory.KeyPathFamilyArities.ContainsKey(named.Name)` and return `KeyPathFamily`. Add `KeyPathFamily` to `IsAbiCategoryPassable`. In the CSM C# bridge builder (CPSE.cs around line 1151, the non-generic-param arm), add a `KeyPathFamily` case that emits:
```cs
callArgs.Add($"{csName}.DangerousGetHandle()");
```
(mirroring `KeyPathProjection.GetParameterPlan` at `src/Swift.Bindings/src/Marshaler/Projection/KeyPathProjection.cs`). C# public param type uses `ResolvePublicCSharpType(arg.SwiftTypeSpec, typeDatabase)` after the substituted `Root`/`Value` have been resolved, which lands on `KeyPath<{ClosedRoot}, {ClosedValue}>`.

Approximate cost: enum value + 4 emitter arm cases (sync + async + return-type render touches if any) ≈ 30 LOC across `MethodClosureBridge.cs` + `ConcreteProtocolSpecializationEmitter.cs`.

#### A3 — Substituted-nested-generic render in C# signature + Swift wrapper sites

Today only **direct pairing-generic matches** get substituted in the non-generic-param render. A param typed `KeyPath<τ_0_0.LibraryFilter, String>` needs the `τ_0_0.LibraryFilter` reference rewritten to e.g. `MusicKit.LibraryAlbumFilter` (Session 4 protocol bag) or `MusicKit.Album.LibraryFilter` (nested concrete struct) per conformer **before** `ResolvePublicCSharpType` runs.

`SubstitutePairingGenericsInTypeSpec` (CPSE.cs:556 — definition; called at lines 904 and 1242) does the substitution. Currently called only at the Swift-side wrapper return-render path; not threaded through non-generic param render sites. Need to call it on every non-generic param's `SwiftTypeSpec` before classifying or rendering, when the param is `KeyPathFamily`.

Verification gate: after substitution, the rendered concrete bag name (`MusicKit.LibraryAlbumFilter`) MUST resolve to a Session-4-emitted typed singleton container OR to a real `TypeDecl` reachable through the type database. If neither, the pairing falls out as not-emittable.

Approximate cost: `SubstitutePairingGenericsInTypeSpec` call threading (~10 LOC) + a `KeyPathRootResolves` validity check (~15 LOC).

### Blocker B — RelatedMusicItemType cross-mult exclusion

Per Explore Anchor 10 quick summary: `CartesianPairings` (CPSE.cs:187-211) is an odometer over `(parent × method-own)` generics. `ConformerPairingSatisfiesCoupling` (CPSE.cs:220) and `ParentTupleSatisfiesMethodConstraints` (CSE.cs:1139-1189) already prune invalid cross-pairs via `SpecializableParam.CouplingConstraints` and per-method where-clauses.

**Empirical expectation:** once Blocker A is fixed, RelatedMusicItemType-typed filter surfaces (e.g., `filter(matching: KeyPath<Album.LibraryFilter, Artist>, equalTo: Artist)`) emit correctly via the existing coupling machinery, because `Artist` is itself a closed conformer of `MusicLibraryRequestable` and the `Value` slot resolves through `KeyPathFamilyArities`-driven projection.

**Disposition for Phase 3 (per team-lead — tiered escalation, replaces the prior "tripwire then add predicate" shortcut):**

1. Step 1: regen MusicKit at the end of Phase 2 with no new containment predicate. Measure actual cartesian per `filter(matching:)` surface and per-conformer extension count.
2. Step 2: if cartesian ≤ `MaxCsmCartesianProductSize` AND emission is sane → ship the 0-LOC outcome. Document measured cartesian in the commit message.
3. Step 3: if explosion or wrong emission → ship a threshold + same-conformer fast-path predicate. Spec: same-conformer pair (`parent_conformer == related_conformer`) ALWAYS admitted; cross-conformer pair gated on threshold. Predicate spans `ConcreteProtocolSpecializationEmitter.cs` (eligibility) + `ConcreteSpecializationEngine.cs` (engine honoring), ~30 LOC, single-source-of-truth helper called from both predicate and emitter (R4).
4. Step 4: if threshold drops Apple-exposed surfaces — DO NOT cram a structural fix into 6b. Defer to follow-up with explicit design pass (enumerate each conformer's bag KeyPaths and allowlist only the pairs Apple actually exposes). Dropped surfaces stay rejection-tombstoned per Session 6 C visibility.

We're NOT pre-emptively writing an "explicit allowlist" because we don't yet have the Apple surface enumerated — that's premature optimization against unknown data. Threshold + same-conformer fast-path is the right *fallback* shape only IF Step 1 data demands it.

Approximate cost: **0 LOC** (Step 2) or **~30 LOC** (Step 3). Beyond that → defer per Step 4.

### Blocker C (split to 6c) — `sort(by:)` Value-erasure

`sort<Value>(by: KeyPath<Item, Value>) where Value: Comparable` has a method-own unconstrained `Value` generic. Enumerating it at the `@_cdecl` boundary requires either:
1. **Existential admission** — admit `KeyPath<Item, any Comparable>` (or `KeyPath<any P, V>` more broadly) into `BoundGenericsHandler.IsContainerWithSupportedDirectExistential` (currently allows only `Optional<any P>`, `Array<any P>`, `Dictionary<K, any P>` per Explore Anchor 5).
2. **Swift wrapper** — a `@_cdecl` shim that dispatches `keyPath.appending(...)` through a runtime existential bridge before invoking the Swift comparator.

Both routes hit guardrail #4 (no Swift wrapper / runtime work without explicit design pass). **6c** owns this end-to-end.

## Predicate ↔ emitter contract (D's lesson, re-stated for 6b)

`IsCsmAsyncEligibleForGenericParent` (`ConcreteProtocolSpecializationEmitter.AsyncGenericParent.cs:53-112`) is the predicate that fronts `MemberValidationPipeline.cs:386-408`'s `RoutedElsewhere` for async parent-generic methods. Its per-param loop at lines 105-112 currently allowlists ONLY `Utf8Slice`:
```cs
for (int i = 1; i < method.CSSignature.Count; i++)
{
    var arg = method.CSSignature[i];
    if (arg.SwiftTypeSpec.IsEmptyTuple) return false;
    if (MethodClosureBridge.ClassifyParam(arg, typeDatabase)
            != MethodClosureBridge.ParamAbiCategory.Utf8Slice)
        return false;
}
```

When 6b adds `KeyPathFamily`, the allowlist MUST broaden to `Utf8Slice | KeyPathFamily`. **Plus** mirror every emitter-side gate the actual emission applies:
- `ConformerPairingSatisfiesCoupling`
- `engine.ParentTupleSatisfiesMethodConstraints`
- The new A1/A3 substituted-validity check (`KeyPathRootResolves`)
- The new A2 passability (`IsAbiCategoryPassable` already covered if `KeyPathFamily` is added to the allowlist).

The sync side (`IsCsmSyncEligibleForGenericParent`, `Sync.cs:34-92`) delegates to `CanEmitConcreteOverloadForPairing` → `AreNonGenericParamsCompatible` → `IsAbiCategoryPassable(ClassifyParam(...))`, so adding `KeyPathFamily` to passable categories propagates automatically — but the **A1 guard** (`HasNonGenericParamReferencingGeneric`) is also called from `CanEmitConcreteOverloadForPairing` (CPSE.cs:1853), so the predicate path inherits the A1 relaxation too. No second-source predicate update required for sync **provided A1's relaxation is correct in `CanEmitConcreteOverloadForPairing` for both paths**.

Pattern, per D's lesson:
> Walk `CartesianPairings`, apply `ConformerPairingSatisfiesCoupling` + `engine.ParentTupleSatisfiesMethodConstraints` + `IsEmittableXxxPairing` in order, return true only on first fully-validating pairing.

For 6b: the new predicate guard in `IsCsmAsyncEligibleForGenericParent` MUST replicate the cartesian dry-run (matching the sync side's pattern), not just per-param category check, so that a method whose first pairing fails A1's substitution but whose second pairing succeeds is admitted via the second pairing.

## Phased implementation plan

Three phases. Per-phase = one commit, gated by per-phase validation (`nuke test` + `nuke binding-tests --skip-regen` sim; device + `nuke validate` at phase 3 only).

### Phase 1 (commit 1) — `KeyPathFamily` ABI category foundation

**Goal:** add `KeyPathFamily` to `ParamAbiCategory`, route `Swift.KeyPath` family types through it, emit `DangerousGetHandle()` from a new CSM bridge arm. Wire a BindingTests fixture demonstrating sync-only closed-conformer KeyPath param emission (no MusicKit yet).

**Files (estimated):**
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodClosureBridge.cs` — add `KeyPathFamily` enum value (~5 LOC); add `KeyPathFamilyArities.ContainsKey` branch in `ClassifyParam` returning `KeyPathFamily` (~5 LOC); add `KeyPathFamily` to `IsAbiCategoryPassable` (~2 LOC). **~12 LOC.**
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs` — new `KeyPathFamily` arm in TWO switches: (1) the C# bridge builder around line 1124 emits `{csName}.DangerousGetHandle()` as the P/Invoke call argument; (2) the Swift `@_cdecl` wrapper builder around line 705 emits `_label: UnsafeRawPointer` param + `Unmanaged<…>.fromOpaque(_label).takeUnretainedValue()` reconstruction. Plus a small `BuildKeyPathPublicCSharpType(NamedTypeSpec, ITypeDatabase)` helper rendering `Swift.{ShortName}<…>` (mirroring `KeyPathProjection._publicType`). (~35 LOC total.)
- `src/Swift.Bindings/src/Marshaler/Projection/TypeProjectionFactory.cs` — expose an `internal static bool IsKeyPathFamily(string name)` helper over `KeyPathFamilyArities` so `MethodClosureBridge.ClassifyParam` can route without duplicating the family list (~5 LOC).

**Async predicate deliberately NOT broadened in Phase 1.** `IsCsmAsyncEligibleForGenericParent` at `AsyncGenericParent.cs:109` keeps its `Utf8Slice`-only allowlist. Rationale: predicate↔emitter contract (D's lesson) — admitting `KeyPathFamily` in the predicate without extending `EmitParentOnlyAsyncSwiftWrapper`'s inlined Utf8 marshalling (lines 537-561, which hardcodes Utf8 rather than switching on category) would over-accept and silent-drop async-KeyPath methods. Since no in-scope 6b/6c surface uses an async method that takes a KeyPath parameter (MusicLibraryRequest's `filter*` and `sort(by:)` are sync; `response()` is async but doesn't take a KeyPath), the async-KeyPath path stays out of 6b. If a future session needs it, the work is: refactor `EmitParentOnlyAsyncSwiftWrapper` to a per-category switch + add the predicate case at the same time.
- `BindingTests/Sources/SwiftBindingsTestLib/CSM/CsmKeyPathParam.swift` (new) — `Bag<T: PatProto>` with method `count(matching keyPath: KeyPath<ConcreteFilter, String>) -> Int` where `ConcreteFilter` is a **top-level (non-conformer-nested) struct**, plus two closed conformers `ConformerA`/`ConformerB` of `PatProto`. The Root being concrete (not `T.Filter`) is the deliberate choice: it isolates **A2** (`KeyPathFamily` category + `DangerousGetHandle()` emission) from **A1**'s substitution relaxation. Ships with a small `@_cdecl` test-helper trampoline (`SBW_TEST_KP_ConcreteFilter_title`) so C# can originate a `KeyPath<ConcreteFilter, String>` without depending on Session 4's typed-singleton machinery (Session 4 only emits singletons for conformer-nested bags, and `ConcreteFilter` is top-level). (~35 LOC Swift.)
- `BindingTests/RuntimeTestsApp/KeyPath/CsmKeyPathParamTests.cs` (new) — assert per-conformer extension classes (`CsmKeyPathBagConformerACsmExtensions`, `CsmKeyPathBagConformerBCsmExtensions`) emit with `KeyPath<ConcreteFilter, Swift.String>` param type; round-trip a KeyPath via the test-helper trampoline through `bag.Count(matching: kp)`; assert P/Invoke call site uses `DangerousGetHandle()` (verified indirectly by green test — emitted-source-diff check is in Phase 1's unit test layer). (~45 LOC C#.)

**Estimated LOC delta in CSM machinery:** ~30 LOC across 3 files (the BindingTests fixture is separate per CLAUDE.md). **Well within tripwire.**

**Phase-1 predicate-↔-emitter contract check:**
- `IsCsmAsyncEligibleForGenericParent`'s allowlist stays at `Utf8Slice`-only (deliberately not broadened — emitter for async-KeyPath not in this phase; predicate would over-accept).
- Verify `IsCsmSyncEligibleForGenericParent` routes the new method through `CanEmitConcreteOverloadForPairing`'s dry-run, which now sees `IsAbiCategoryPassable(KeyPathFamily) = true` and admits the pairing.
- Verify A1 (`HasNonGenericParamReferencingGeneric`) **does not fire** on the fixture because `KeyPath<ConcreteFilter, String>` has no `τ_0_0` reference in its type tree — pure A2 isolation. This is the explicit rationale for option (a) on the Phase 1 fixture: A1 relaxation is left untested in Phase 1 by design; Phase 2 owns the A1+A3 path.

**Phase-1 gates:**
- `nuke test` green (new unit tests for `ClassifyParam` returning `KeyPathFamily`).
- `nuke binding-tests --skip-regen` sim green; the new fixture's tests pass.
- **Defer device + validate to phase 3** (no MusicKit re-emission in phase 1, so cross-cutting validation isn't useful yet).

**Phase-1 r1 review:** paired Codex + Grok mandatory before commit.

### Phase 2 (commit 2) — A1 relaxation + A3 substituted-nested-generic render

**Goal:** relax `HasNonGenericParamReferencingGeneric` so `KeyPath<τ_0_0.Bag, V>` is admitted when the substituted concrete Root is a real `TypeDecl` reachable via Session 4's emission. Thread `SubstitutePairingGenericsInTypeSpec` through the non-generic-param render path for KeyPath-family params.

**Files (estimated):**
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs` — in `HasNonGenericParamReferencingGeneric` (line 2303), add a `KeyPathFamilyArities.ContainsKey` branch that calls a new `TrySubstituteAndValidate(arg.SwiftTypeSpec, pairing)` helper (~20 LOC); thread `SubstitutePairingGenericsInTypeSpec` call into the param render before `ResolvePublicCSharpType` (~10 LOC). Add `KeyPathRootResolves` helper checking the substituted Root resolves to a Session-4-emitted singleton container or a typed `TypeDecl` (~25 LOC). **~55 LOC.**
- `BindingTests/Sources/SwiftBindingsTestLib/CSM/CsmKeyPathSubstitution.swift` (new) — fixture adding a `PatProto` variant `FilterablePatProto` with associated-type `Filter` constraint, with conformers `ConformerWithFilterA`/`B` each declaring a nested `Filter` struct (e.g. `ConformerWithFilterA.Filter { var title: String }`). New `Bag<T: FilterablePatProto>` method `count(matching keyPath: KeyPath<T.Filter, String>) -> Int` exercises **A1**'s relaxation (`τ_0_0.Filter` reference in the param tree) AND **A3**'s substituted render (`T.Filter` → `ConformerWithFilterA.Filter` per pairing). The conformer-nested `Filter` bags are eligible for Session-4 typed-singleton emission, providing the C# `static readonly` field path the test consumes. (~30 LOC Swift.)
- `BindingTests/RuntimeTestsApp/KeyPath/CsmKeyPathSubstitutionTests.cs` (new) — assert per-conformer extension class signature renders with substituted Root (`KeyPath<ConformerWithFilterA.Filter, Swift.String>`, not `KeyPath<T.Filter, Swift.String>`); round-trip a Session-4-emitted typed singleton (`ConformerWithFilterAFilterKeyPaths.Title`) through `bag.Count(matching: ...)`; assert separate per-conformer extension classes exist for both conformers. (~40 LOC C#.)

**Estimated LOC delta in CSM machinery:** ~55 LOC in 1 file. **At ~37% of single-file LOC tripwire; safe.**

**Phase-2 predicate-↔-emitter contract check:**
- `IsCsmAsyncEligibleForGenericParent`'s pairing dry-run now must call the same `TrySubstituteAndValidate` walk that the emitter applies; not just per-param category check (per D's lesson — predicate must mirror every emitter gate).
- Add a focused unit test on `HasNonGenericParamReferencingGeneric` proving the KeyPath substitution path is reached and validated.

**Phase-2 gates:**
- `nuke test` green.
- `nuke binding-tests --skip-regen` sim green.
- **Device** if marshalling paths changed (Phase 2 doesn't touch marshalling — paths are pure render-substitution — so device deferral is acceptable). Per the validation table in CLAUDE.md (Generator/emitter, calling conventions not changed → device "Optional"), defer to phase 3.

**Phase-2 r1 review:** paired Codex + Grok mandatory before commit.

### Phase 3 (commit 3) — MusicKit wiring + RelatedMusicItemType verification + `nuke validate`

**Goal:** validate that all 7× `filter(matching:…)` MusicLibraryRequest surfaces now emit per-conformer, with closed `KeyPath<Album.LibraryFilter, String>` and RelatedMusicItemType variants flowing through. Run cross-cutting gates.

**Containment sequencing — tiered regen→measure→decide** (per team-lead, replaces the "if explosion, add predicate" shortcut). The default is 0 LOC; a containment predicate is the *fallback* only if data demands it.

1. **Step 1 — Regen MusicKit with NO new containment predicate.** Phase 2's A1+A3 substitution + the existing `CouplingConstraints` / `ParentTupleSatisfiesMethodConstraints` machinery does whatever it does. Measure: actual cartesian product size per `filter(matching:)` surface; the count of per-conformer extension classes; whether RelatedMusicItemType variants emit at all.
2. **Step 2 — If cartesian ≤ `MaxCsmCartesianProductSize` AND surface emission is sane:** ship the 0-LOC outcome. Document the measured cartesian per surface in the commit message. Done — no new predicate code.
3. **Step 3 — If cartesian explodes OR emission is obviously wrong:** ship a threshold + same-conformer fast-path predicate. Spec: `parent_conformer == related_conformer` (same-conformer pair, e.g., `MusicLibraryRequest<Album>.filter(matching: KeyPath<Album.LibraryFilter, Album>)`) is ALWAYS admitted regardless of threshold; every cross-conformer pair is gated on `MaxCsmCartesianProductSize`. Predicate lives in `ConcreteProtocolSpecializationEmitter.cs` (eligibility predicate) + `ConcreteSpecializationEngine.cs` (engine-side honoring), ~30 LOC; document the cap value in code comments.
4. **Step 4 — If the threshold drops surfaces that Apple's MusicKit actually exposes** (rare — empirically Apple's LibraryFilter KeyPaths are predominantly same-conformer): DO NOT cram a structural fix into 6b. The proper fix is enumerating each conformer's bag KeyPaths and allowlisting only the pairs that appear; defer to a follow-up session with its own design pass. Phase 3 ships the threshold + same-conformer fast-path and the dropped surfaces stay tombstoned with the existing rejection-tombstone visibility from Session 6 C.

**Why this shape** (not premature explicit allowlist):
- We don't yet have the Apple surface enumerated; writing a positive list with no data to defend the entries is itself a design-pass-required item (guardrail #4).
- "Do nothing yet" is the right *default* until data demands action.
- Threshold + same-conformer fast-path is the correct *fallback* shape — narrow enough to avoid mis-admitting cross-conformer pairs while preserving the always-correct same-conformer case.
- The reviewer-spec for the fallback is documented above so r1 reviewers (Codex + Grok) can pre-validate the predicate↔emitter contract for Step 3 *even if Step 2 ships it as 0 LOC*.

**Files (estimated):**
- Step 2 outcome: 0 generator-side code changes.
- Step 3 outcome: ~30 LOC in `ConcreteProtocolSpecializationEmitter.cs` + `ConcreteSpecializationEngine.cs` (threshold + same-conformer fast-path predicate; same single-source-of-truth helper called from both predicate and emitter per R4).
- Update MusicKit baseline if surface count rises (expected: from 6/14 to 13/14 — sort(by:) still tombstoned, deferred to 6c).

**Phase-3 gates (full sweep):**
- `nuke test` green.
- `nuke binding-tests --skip-regen` sim green; new fixtures from phases 1+2 still green; **no green→red on existing tests**.
- `nuke binding-tests --device --skip-regen` green; **0 crashes per device flake-vs-regression memory; if crash count > 0, rerun once fresh before drawing conclusions**.
- `nuke validate --filter MusicKit` — **MusicKit must stay 4/4 pass**; `MusicKit.cs` line count grows (7 new surfaces × 7 conformers ≈ +6500 lines expected); per-conformer extension class confirmed for all 7 conformers.
- `nuke validate` (full sweep) — **opt-in for cross-cutting generator change**. Baseline holds; if `cs_compile` or `swift_compile` drops below baseline on any other consumer, regression — escalate before committing.

**Phase-3 r1 review:** paired Codex + Grok mandatory before commit. **r2 on Critical/High findings only.**

## Risks

| # | Risk | Phase | Mitigation |
|---|---|---|---|
| R1 | Phase-1 `KeyPathFamily` enum addition cascades to other switch statements outside CSM (e.g., diagnostic printing, telemetry) | 1 | `grep -rn "ParamAbiCategory\\." src/Swift.Bindings/` before commit; all switch sites pattern-matched. |
| R2 | A1 relaxation over-accepts: a non-KeyPath param `Optional<T.Filter>` references the parent generic but isn't a KeyPath — must NOT be admitted | 2 | Relaxation gated strictly on `KeyPathFamilyArities.ContainsKey(named.Name)` AND on the param being the *top-level* KeyPath (not buried in Optional / Array). Add a negative unit test. |
| R3 | A3 substitution renders a Root that **doesn't** resolve to a Session-4-emitted singleton (e.g., a closed conformer whose nested bag wasn't bag-walked) → broken C# referring to nonexistent type | 2 | `KeyPathRootResolves` helper validates resolution; on miss, fall out as unemittable (with a tombstone via the existing rejection-tombstone path Session 6 C added). |
| R4 | Predicate-↔-emitter drift: the new pairing dry-run in `IsCsmAsyncEligibleForGenericParent` falls behind a new emitter gate added later | all | Same `TrySubstituteAndValidate` helper called from both predicate and emitter — single source of truth. Document the predicate↔emitter contract at function-doc level. |
| R5 | RelatedMusicItemType cartesian explodes past `MaxCsmCartesianProductSize` | 3 | Tiered escalation per Phase-3 sequencing: **Step 1** regen with no new containment, measure cartesian per surface; **Step 2** if ≤ cap and emission sane → 0-LOC outcome, document data, ship; **Step 3** if explosion or wrong → ship threshold + same-conformer fast-path predicate (same-conformer pair always admitted, cross-conformer gated on threshold; ~30 LOC); **Step 4** if threshold drops Apple-exposed surfaces → DO NOT cram structural fix into 6b, defer to follow-up with explicit design pass (Apple surface enumeration + positive allowlist). 6b ships whichever of {0, ~30} LOC the data demands; nothing beyond. |
| R6 | Device-gate flake on cold device first run mis-attributed to phase 3 regression | 3 | Per memory `feedback_device_gate_flake_vs_regression.md`: rerun device gate fresh if `CrashCount > 0` before drawing conclusion. |
| R7 | LOC tripwire trips on phase 2 (`HasNonGenericParamReferencingGeneric` relaxation balloons past 150 LOC) | 2 | If LOC estimate drifts up by >2x between successive readings, PAUSE + SendMessage team-lead; consider splitting the substitution helper into its own commit before A1 relaxation. |
| R8 | `KeyPathProjection.cs` path matters: the runtime ships `Swift.Bindings/src/Marshaler/Projection/KeyPathProjection.cs` (not `Swift.Runtime`) — the existing projection is bindings-emitter-side, not runtime. Emitted code calls `DangerousGetHandle()` directly via SafeHandle, NOT through `KeyPathProjection` | 1 | Phase 1's emitter arm hardcodes `{csName}.DangerousGetHandle()`; no dependency on `KeyPathProjection`. |
| R9 | Schema-version handshake (per memory): if `MethodClosureBridge.ClassifyParam` signature changes, ensure no parser/emitter version mismatch | all | None of the planned changes touch parser-emitted JSON; signature unchanged; safe. |
| R10 | `nuke validate` full sweep at phase 3 reveals green→red on a non-MusicKit consumer (e.g. AppIntents, which has heavy KeyPath surface — if accidentally re-classified) | 3 | KeyPath param classification only fires when a param IS a KeyPath family type; AppIntents currently has no CSM-eligible KeyPath param paths (sessions 8+ wire them). The category addition is additive on the passable side. Verify by `git diff` of `MusicKit.cs` and one AppIntents-touching consumer pre/post. |

## r1 / r2 plan

- **r1 (mandatory at every phase, before commit):** paired Codex + Grok in parallel. Both reviewers receive the same prompt: design rationale + commit diff + the predicate↔emitter contract requirement.
  - Grok: `/Users/wojo/.grok/bin/grok --always-approve --disallowed-tools "search_replace,write" --no-subagents -p "<prompt>"` (prompt as `-p` argument — piping silently no-ops).
  - Codex: `/opt/homebrew/bin/codex exec --sandbox workspace-write --output-last-message <path>` (prompt via stdin pipe from file).
- **r2:** only on Critical or High findings from r1. The predicate↔emitter contract bug pattern from Session 6 D's r1 IS the canonical High finding worth r2 — both reviewers caught it in 6 and the cost was acceptable. Skip r2 for purely cosmetic or stylistic findings.
- **r3:** essentially never (per memory `feedback_orchestration_token_cost.md`).

## Validation gates per phase (recap)

| Phase | `nuke test` | `nuke binding-tests --skip-regen` sim | `nuke binding-tests --device` | `nuke validate` |
|---|---|---|---|---|
| 1 — `KeyPathFamily` foundation | required | required | deferred to 3 | deferred to 3 |
| 2 — A1+A3 substitution + render | required | required | deferred to 3 | deferred to 3 |
| 3 — MusicKit wiring + cross-mult | required | required | **required** (0-crash freshly) | **required** (full sweep) |

## Out-of-scope (explicit)

- `sort(by:)` Value-erasure (deferred to 6c).
- `KeyPath<any P, V>` bound-generic-existential admission (deferred to 6c).
- Sessions 7-10 consumer productionization (depends on this but is independent work).
- PackGate failure (pre-existing on main; per handoff, not Session 6b's problem).
- Any `Swift.Runtime` code changes beyond what's already in `keypath-subsystem` (no SafeHandle base changes; `Payload => this` shim stays).

## Internal split criterion (6b → 6c) during impl

PAUSE + SendMessage team-lead BEFORE proceeding if any of:
- Single phase crosses **150 LOC or 5 files** in CSM emitter machinery.
- New Swift wrapper or runtime work surfaces beyond what's design-doc'd here.
- LOC estimate drifts by >2× between successive readings (3 successive misjudgments = halt).
- New architectural surprise (new file shape, new ABI category beyond `KeyPathFamily`).
- RelatedMusicItemType cross-mult containment grows beyond ~30 LOC of new predicate code.

In those cases the spillover lands as 6c (alongside `sort(by:)` and the bound-generic-existential admission).

## References

- `keypath-subsystem/00-overview.md` — design decision (typed singletons for IN path, SafeHandle for OUT).
- `keypath-subsystem/04-typed-singleton-emission.md` (line 17) — bound-generic-existential allowlist follow-up.
- `keypath-subsystem/06-musiclibraryrequest-re-enablement.md` — parent re-enablement spec.
- `.agent/session-6-handoff.md` — 4 commit history + 8 deferred surfaces + 3 architectural blockers.
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs:2303,2358,1158,2582,556,904,1242` — gates and substitution sites.
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodClosureBridge.cs:2052-2126,1735` — `ParamAbiCategory` + `ClassifyParam` + `IsAbiCategoryPassable`.
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.AsyncGenericParent.cs:53-112` — async parent-generic predicate (line 109 = the per-param allowlist).
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.Sync.cs:34-92` — sync parent-generic predicate.
- `src/Swift.Bindings/src/Marshaler/ConcreteSpecializationEngine.cs:548,1139` — `FindSpecializableMethods` + `ParentTupleSatisfiesMethodConstraints`.
- `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs:247-311` — `IsContainerWithSupportedDirectExistential` (6c relaxation site).
- `src/Swift.Bindings/src/Marshaler/Projection/KeyPathProjection.cs` — `DangerousGetHandle()` marshalling reference.
- `src/Swift.Runtime/src/Swift/SwiftKeyPath.cs:50,83` — `SafeHandleZeroOrMinusOneIsInvalid` base + `Payload => this` shim.
- `src/Swift.Bindings/src/Emitter/StringEmitter/ValidationContext.cs:60-96` — `RoutedElsewhere` definition.
- `src/Swift.Bindings/src/Emitter/StringEmitter/MemberValidationPipeline.cs:210-270,386-408` — Phase-4a CSM intercepts.
- `src/Swift.Bindings/src/Marshaler/TypeProjectionFactory.cs:543-558` — `KeyPathFamilyArities` + `IsStdlibContainer`.
