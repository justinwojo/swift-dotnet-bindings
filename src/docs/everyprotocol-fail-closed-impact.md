# EveryProtocol fail-closed — public-surface impact

> **RESCUE UPDATE (2026-07-12, session 02 — supersedes the counts below).** The proxy-rescue
> pass recovered **12 of the 13** recorded degraded members. The per-library tables and the
> cross-library summary further down are the **pre-rescue (session 01) baseline**, kept as the
> historical "before"; the current state is:
>
> | Library | Degraded before | Degraded after | What changed |
> |---|---|---|---|
> | RealityFoundation | 10 | **0** | `MaterialProxy`, `MaterialFunctionProxy`, `SynchronizationServiceProxy`, `RealityCoordinateSpaceProxy` (all blocked only by digester-stripped `__`-prefixed hidden requirements → *forward-safe*) now emit as **forward-only** proxies via the read-only admission (`HasForwardSafeReverseImpossibleReason` wired into the `suitableProtocols` filter). SB0006 sites 2 → **0**; `EveryProtocolConformanceSkipped` proxies 15 → **11**; emitted members 2397 → **2403**, skipped 322 → **308**. |
> | RoomPlan | 2 | **0** | `RoomCaptureViewDelegateProxy` (blocked by `: NSCoding` class identity) now emits with a **full reverse-dispatch** conformance: the ObjC-rooted carrier `EveryObjCProtocol` gains a no-op `NSCoding` stub (`encode(with:)`/`init?(coder:)`), so `extension EveryObjCProtocol: RoomCaptureViewDelegate` type-checks. SB0006 1 → **0**. A C#-authored `IRoomCaptureViewDelegate` now round-trips (runtime-proven: `NSCodingDelegateDispatchTests`, sim + device). |
> | BlinkIDUX | 1 | **1** | Unchanged — `BlinkIDAnalyzer.events` getter stays a produce-throw (SB0006). Root proxy `EventStreamProxy` is blocked by a **PAT** (`associatedtype Event`), which is *not* forward-safe, so it correctly stays fail-closed (exit ramp; concrete `BlinkIDEventStream.Stream` `IAsyncEnumerable` already works). |
> | **Total** | **13** | **1** | |
>
> **Forward-only vs full-reverse, precisely.** RoomCaptureViewDelegate is a **full** rescue
> (reverse dispatch — a C#-authored delegate receives callbacks). The four RealityFoundation
> proxies are **forward-only**: the produce/read side is fully recovered (a `[any P]` /
> `(any P)?` getter now reads Swift-vended conformers through their own witness table — the hard
> SB0006 compile-error is gone), and Swift-vended conformers round-trip through the
> setter/initializer consume path. Authoring a **brand-new C#** `IMaterial` /
> `ISynchronizationService` from scratch and packing it remains unsupported (the forward-only
> proxy has no reverse-dispatch impl ctor) — a niche that stays a documented limitation, now
> carried by a forward-only proxy rather than a throwing/degraded member. Rescue → full-property
> restoration is automatic (the session-01 set-only/absent surface keys on suppression, and an
> emitted or read-only-marked proxy is absent from `SuppressedProxyClassNames`).

**Scope:** RealityFoundation, RoomPlan, BlinkIDUX. **Generated:** 2026-07-12, locally at HEAD
`3df00117` **plus the consume-degrade reporting-completeness change and the produce-throw
compile-poison change** (this session), iOS-simulator apple-framework / manual mode
(`nuke validate --filter <lib>`). Local analysis only — no packing, no publishing. **Baseline for
"before":** the 2026-06-27 binding audits
(`src/docs/BindingAudit/{RealityFoundation,RoomPlan,BlinkIDUX}.md`), which reflect the shipped
0.17.0-era output.

> **Update (compile-poison, this session):** every **produce-throw** read/return is now
> **compile-time-visible**. In front of the throwing stub the generator emits
> `[Obsolete("…", error: true, DiagnosticId = "SB0006", UrlFormat = …)]`, so a consumer that reads a
> suppressed-proxy getter/return gets a **build error (SB0006)**, not a silent runtime
> `NotSupportedException`. The throwing body stays underneath as a defense-in-depth backstop. This is
> **report-parity-preserving and additive to the emitted C#**: RealityFoundation still records its 10
> rows (2 SB0006 poison sites for the 2 produce-throw getters) and RoomPlan its 2 rows (1 poison site);
> only the `[Obsolete]` attribute lines are added to the `.cs`, and both libraries recompile clean
> (the class's own internal reads route through the un-poisoned private `{Name}_Get()` accessor, so the
> marker never self-errors). **consume-degrade** rows are unchanged — a setter/param that still
> round-trips Swift-vended conformers is *not* poisoned (the assign-only surface stays usable).

> **Update (reporting completeness, prior commit):** the consume-degrade coverage gap the earlier revision of this report
> flagged is now **closed**. Every consume site that drops the per-element wrap fallback for a
> suppressed proxy — the collection-element path plus enum-case construction, closure-return/-arg, and
> the reverse-dispatch owned getters — now routes through `SuppressedProxyReporting`, so
> RealityFoundation's 5 previously-**unrecorded** `[Material]` collection consume-degrades are now
> first-class rows. RealityFoundation's recorded count moves **5 → 10** and the cross-library total
> **8 → 13**. This change is **diagnostic-only and byte-identical** — the RealityFoundation regen was
> A/B-diffed against the pristine HEAD generator and both the 221,751-line `RealityFoundation.cs` and
> the 54,091-line `RealityFoundation.Wrapper.swift` are SHA256-identical; only `binding-report.json`
> differs (the 5 new rows). The "lower bound" caveats are therefore removed below.

## What "fail-closed" means here (and what it does *not*)

When a Swift protocol's `{Protocol}Proxy` reverse-dispatch conformance cannot be synthesized, the
generator **keeps** the surrounding member but degrades its behaviour:

- **produce-throw** — a getter / return that could only be produced by constructing the missing proxy
  is **compile-poisoned**: the member (or single accessor) carries
  `[Obsolete("…", error: true, DiagnosticId = "SB0006")]`, so any consumer read is a **build error**,
  and a throwing stub (`NotSupportedException`) stays underneath as a runtime backstop. The read can
  therefore never be a *silent* runtime trap — it fails visibly at compile time or is absent.
- **consume-degraded** — a setter / parameter still round-trips **Swift-vended** conformers, but a
  **C#-authored** conformer cannot be marshalled in (there is no proxy to wrap it). This is **not** a
  silent no-op: dropping the wrap fallback routes the value through the no-fallback
  `ExistentialContainerFactory.GetOrCreate<T>(value, out …)` overload, which **throws
  `InvalidCastException`** at the marshalling boundary for anything that isn't already an
  `ExistentialContainer1` / `ISwiftExistentialConvertible` / `IExistentialBoxable` (a plain C#
  conformer is none of these). So the set/pass **fails loudly** at call time; only Swift-vended
  conformers pass.
- **receiver-failfast** — a reverse-dispatch `[UnmanagedCallersOnly]` entry fail-fasts. *(None of the
  three libraries in this report hit this shape.)*

> **Reading the `Kind` column.** This column is a *human-readable* member/accessor kind (e.g. `property
> getter`, `initializer`, `method`), **not** the raw `BindingItemKind` enum
> (`Type`/`Method`/`Property`/`Operator`/`Subscript`). It is intentionally not uniform across getters
> because the report models a degraded property getter as a `Method` named `{name}_Get` — so those rows
> read as `method` here — while `ModelComponent.materials` is modelled as a `Property`. The wording
> describes what the member *is* to a consumer; the underlying `BindingItemKind` for every row is
> `Method` or `Property`.

This degradation is **not new** and neither pass **removed anything**: the generator changes that this
report characterises — the scalar-site classifier (commit `ce20d8ea`) and this commit's completion of it
across the remaining consume sites — are both **diagnostic-only and byte-identical** (the same throwing
getters and throw-on-C#-conformer setters that 0.17.0 shipped are still emitted, unchanged; RealityFoundation's
regen was A/B-diffed SHA256-identical against pristine HEAD). What is new is that each per-member decline is
now promoted to a classified `SuppressedProxyMemberDegraded` skip row
(disposition `KnownLimitation`) in `binding-report.json`, instead of being invisible. So "which
members disappear under fail-closed" is really **"which public members the report now flags as
degraded"** — the tables below — so the owner can make per-library tier calls. Every one of these
members was already degraded in 0.17.0: a **produce-throw** getter is fully non-functional (now a
compile-time SB0006 build error on read, throwing stub underneath), while a **consume-degraded**
setter/param is functional *only* for Swift-vended conformers and
throws `InvalidCastException` for a C#-authored one. Either way there was never a working
C#-conformer path — a consumer that exercised it got an exception.

**Coverage (now complete):** consume-degrade reporting routes **every** site that drops the per-element
wrap fallback for a suppressed proxy through `SuppressedProxyReporting`. The three original scalar sites —
the property setter (`PropertyHandler`) and the method/init parameter via the wrapper and P/Invoke paths
(`WrapperEmitter`/`PInvokeEmitter`) — are joined by the **collection-element** consume path (`[any P]`
array/set/dict setter or parameter, decided in the leaf `ExistentialProjection` and now surfaced by a
stateless post-build walk keyed on the suppressed-proxy predicate at each decl-owning handler),
**enum-case construction** (`EnumHandler.CaseConstruction`), **closure-return / closure-arg** consume
(`ClosureEmitter`), and the reverse-dispatch owned getters/returns. Of these, only the collection-element
path is **realized** in the three libraries here — RealityFoundation's 5 `[Material]` members (detailed
under *RealityFoundation → `[Material]` collection surface*); the enum/closure/owned-getter shapes are
wired but no suppressed-proxy member in these three libraries hits them, so they contribute 0 rows *here*
(they would be recorded if hit in another library). Net: the produce-throw, consume-degrade, and
receiver-failfast paths are **all fully accounted** — the recorded counts are no longer a lower bound.

Members that are simply **absent for other reasons** (e.g. `UnsupportedExistential`, `AnyTypeFallback`,
`GenericProtocolConstraint`, `DuplicateSignature`, `SynthesizedCodable`) are **pre-existing** and are
**not** attributable to the EveryProtocol classifier; they are listed per library under
*"Pre-existing skips (not this classifier)"* so they are not double-counted here.

---

## RealityFoundation

- Members 2564 total / 2397 emitted / 322 skipped. **EveryProtocol-degraded public members the report
  flags: 10** (2 produce-throw + 8 consume-degraded — the 5 `[Material]` collection consume-degrades that
  the earlier revision listed as *unrecorded* are now first-class rows; see *`[Material]` collection
  surface* below). Proxy classes not emitted: 15. Report triage `ReviewCount: 0`. *(Emitted moved 2401 →
  2397 and skipped 317 → 322 purely because a member recorded as degraded is counted as skipped rather
  than emitted; the emitted C# is byte-identical — no member was dropped.)*

| Member | Kind | Degradation | Root-cause proxy | Skip reason (disposition) | What the consumer loses / was getting before |
|---|---|---|---|---|---|
| `ModelComponent.materials` (getter) | property getter | produce-throw | `MaterialProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Cannot **read** a model's material list — reading the getter is now a **compile error (SB0006)**, throwing stub underneath. The setter emits and accepts **Swift-vended** materials, but throws `InvalidCastException` for a C#-authored `IMaterial` (the setter is **now recorded** as its own collection-element consume-degrade row — see below). Before: identical throwing getter (audit "HIGH"). |
| `Scene.synchronizationService` (getter) | method | produce-throw | `SynchronizationServiceProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Cannot read the multipeer sync service — the getter is **compile-poisoned (SB0006)**. Collaboration-only surface (audit "Low"). Before: throwing getter. |
| `Scene.synchronizationService` (setter) | property | consume-degraded | `SynchronizationServiceProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Cannot install a **C#-authored** sync service — the set **throws `InvalidCastException`** at the marshalling boundary; a Swift-vended service would still round-trip. Practically nil — custom C# sync services are not a real use case. |
| `CustomMaterial.init` (×2 overloads) | initializer | consume-degraded | `MaterialProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Passing a **C#-authored** `Material` into `CustomMaterial`'s init **throws `InvalidCastException`**; Swift SDK materials (`SimpleMaterial`, `PhysicallyBasedMaterial`, …) are Swift-vended and pass normally. Practically nil. |
| `ModelComponent.materials` (setter) | property setter | consume-degraded *(collection element)* | `MaterialProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Assigning a `[Material]` whose elements are **C#-authored** `IMaterial`s **throws `InvalidCastException`** per element; a list of Swift-vended materials round-trips. **Newly recorded** — the per-element decline is decided in the leaf `ExistentialProjection` and is now surfaced at the property handler. Before: identical throwing setter, unflagged. |
| `ModelComponent(mesh:materials:)` (init) | initializer | consume-degraded *(collection element)* | `MaterialProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Same as the setter, at construction: a `[Material]` of C#-authored elements **throws `InvalidCastException`**; Swift-vended materials pass. **Newly recorded.** |
| `ModelEntity(mesh:materials:)` (init) | initializer | consume-degraded *(collection element)* | `MaterialProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Same shape — `[Material]` of C#-authored elements **throws `InvalidCastException`**. **Newly recorded.** |
| `ModelEntity(mesh:materials:collisionShape:)` (init) | initializer | consume-degraded *(collection element)* | `MaterialProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Same shape. **Newly recorded.** |
| `ModelEntity(mesh:materials:collisionShapes:)` (init) | initializer | consume-degraded *(collection element)* | `MaterialProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Same shape. **Newly recorded.** |

**Proxy classes not emitted (root cause, pre-existing):** `MaterialProxy`,
`SynchronizationServiceProxy`, `RealityCoordinateSpaceProxy`, `ActionHandlerProtocolProxy`,
`EntityActionProxy`, `TransientComponentProxy`, `MeshBufferSemanticProxy`, `MeshBufferContainerProxy`,
`EntityCollectionProxy`, `MaterialFunctionProxy`, `ForceEffectBaseProxy`, `AnimationStateProtocolProxy`,
`PostProcessEffectProxy`, `SystemProxy`, `CancellableProxy`. All classify structurally (associated-type/
Self-constrained, `UnsatisfiedHiddenRequirements`, `NoncopyableParamOrReturn`, `ConstructorRequirements`,
etc.) except `CancellableProxy` (module-internal → `ExpectedNonPublic`). Most have **no** degraded public
member — the proxy is unused on the public surface, or its would-be members were already dropped for a
different, pre-existing reason.

**`[Material]` collection surface — 5 consume-degrades, now recorded.** These
public members consume `[Material]` (`IEnumerable<IMaterial>` / an `IReadOnlyList<IMaterial>` setter).
Because `MaterialProxy` is suppressed, the generator drops the per-element wrap fallback and emits the
no-fallback `CreateOwnedExistential1<IMaterial>(e)` (opaque arity-1; the class-bound analogue would be
`CreateOwnedClassCarrier<T>(e)`), which **throws `InvalidCastException`** for a C#-authored `IMaterial`
(Swift-vended `SimpleMaterial`/`PhysicallyBasedMaterial`/… still round-trip) — the identical
consume-degrade shape as the scalar rows above, but at the **collection-element** level:

- `ModelComponent.materials` (setter)
- `ModelComponent(mesh:materials:)` (init)
- `ModelEntity(mesh:materials:)` (init)
- `ModelEntity(mesh:materials:collisionShape:)` (init)
- `ModelEntity(mesh:materials:collisionShapes:)` (init)

**All five are now recorded** in `binding-report.json` as `SuppressedProxyMemberDegraded`
(`KnownLimitation`) consume-degrade rows. The scalar consume path already recorded the decline at the
property/method handler (`PropertyHandler`, `WrapperEmitter.Marshalling`, `PInvokeEmitter`); the
degradation for a *collection* element is decided in the leaf `ExistentialProjection` element conversion,
which has no handle on the owning decl. Rather than thread a mutable "degraded" signal up through every
container projection, a **stateless static walk** re-reads the built container projection's public
sub-projection accessors (`ArrayProjection`/`SetProjection`/`DictionaryProjection`/`OptionalProjection`/
nested) at each decl-owning handler and records the distinct suppressed-proxy names found at existential
leaves — keyed on the suppressed-proxy predicate (a live proxy, plain `object`, or an existential-union
leaf contributes nothing). Emitted C# is unchanged; only the report gains the rows.

The collection-element path is the only consume shape **realized** in these three libraries, but the same
completeness pass also wired the remaining generator consume sites — enum-case construction
(`EnumHandler.CaseConstruction`), closure-return / closure-arg consume (`ClosureEmitter`), and the
reverse-dispatch owned getters/returns — so a suppressed-proxy member that hits any of those in another
library is now recorded too. No suppressed-proxy member in RealityFoundation/RoomPlan/BlinkIDUX exercises
those shapes, so they add 0 rows here. Consequence for the owner: the recorded EveryProtocol-degraded
counts in this report are **complete**, not a lower bound — every consume site that drops the wrap
fallback for a suppressed proxy now lands as a classified row.

**Pre-existing skips (not this classifier):** `UnsupportedSignature` 66, `SynthesizedCodable` 64,
`AnyTypeFallback` 52, `DuplicateSignature` 41, `UnsupportedType` 41, `UnsupportedClosure` 8,
`UnsupportedExistential` 7, `NonBlittableCallConvSwift` 6, `UnsatisfiedGenericConstraint` 5,
`NetUnavailableType` 3, `SwiftUIConstraint` 2, `IndeterminatePwtShape` 1, `GenericProtocolConstraint` 1,
`StaticProtocolMember` 1.

---

## RoomPlan

- Members 188 total / 145 emitted / 42 skipped. **EveryProtocol-degraded public members: 2.**
  Proxy classes not emitted: 2. Report triage `ReviewCount: 0`.

| Member | Kind | Degradation | Root-cause proxy | Skip reason (disposition) | What the consumer loses / was getting before |
|---|---|---|---|---|---|
| `RoomCaptureView.delegate` (getter) | method | produce-throw | `RoomCaptureViewDelegateProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Cannot read back the assigned view delegate — reading the getter is now a **compile error (SB0006)** (so `if (view.Delegate != null)` no longer compiles), throwing stub underneath. Before: identical throwing getter. |
| `RoomCaptureView.delegate` (setter) | property | consume-degraded | `RoomCaptureViewDelegateProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | **Real loss:** assigning a C#-authored `IRoomCaptureViewDelegate` compiles but **throws `InvalidCastException`** at the marshalling boundary (there is no proxy to wrap it), so scan-completion callbacks can never be delivered to a C# delegate. Workaround: the `RoomCaptureSession.Delegate` (session-delegate) path is fully functional. Before: identical throw. |

**Proxy classes not emitted (root cause, pre-existing):** `RoomCaptureViewDelegateProxy` (class-bound
protocol requiring NSObject/AnyObject identity), `CapturedRoomAttributeProxy` (associated-type/
Self-constrained). `CapturedRoomAttributeProxy` produces **no** degraded public member — its would-be
members surface under `UnsupportedExistential`/`AnyTypeFallback` instead (pre-existing).

**Pre-existing skips (not this classifier):** `SynthesizedCodable` 18, `AnyTypeFallback` 8,
`UnsupportedSignature` 6, `UnsupportedExistential` 5, `GenericProtocolConstraint` 1.

---

## BlinkIDUX

- Members 187 total / 140 emitted / 22 skipped. **EveryProtocol-degraded public members: 1.**
  Proxy classes not emitted: 3. Report triage `ReviewCount: 0`.

| Member | Kind | Degradation | Root-cause proxy | Skip reason (disposition) | What the consumer loses / was getting before |
|---|---|---|---|---|---|
| `BlinkIDAnalyzer.events` (getter) | method | produce-throw | `EventStreamProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Cannot read the raw `events` `EventStream` — the getter is **compile-poisoned (SB0006)**, throwing stub underneath. The supported path is the concrete `BlinkIDEventStream.Stream` (`IAsyncEnumerable`), which works. Before: identical throwing getter. |

**Proxy classes not emitted (root cause, pre-existing):** `EventStreamProxy`,
`ScanningResultProtocolProxy`, `CameraFrameAnalyzerProxy` — all associated-type/Self-constrained. Only
`EventStreamProxy` has a degraded public member (above); the other two are used only where the concrete
SDK types (`BlinkIDEventStream`, `BlinkIDAnalyzer`) already cover the standard case, and their protocol
members were dropped for pre-existing reasons (`UnsupportedSignature`, `AnyTypeFallback`).

**Pre-existing skips (not this classifier):** `ModuleInternal` 10 (Combine `@Published` storage — non-
public), `SwiftUIView` 4, `UnsupportedType` 3, `GenericTypeCallback` 2, `UnsupportedSignature` 2,
`AnyTypeFallback` 1.

---

## Cross-library summary & diff-size sanity check

Recorded rows (this is the **session-01 pre-rescue baseline**; see the RESCUE UPDATE banner at
the top for the current 13 → 1 state after the session-02 proxy rescue):

| Library | Recorded degraded members | produce-throw | consume-degraded | receiver-failfast | Proxy classes skipped | `ReviewCount` |
|---|---|---|---|---|---|---|
| RealityFoundation | 10 | 2 | 8 | 0 | 15 | 0 |
| RoomPlan | 2 | 1 | 1 | 0 | 2 | 0 |
| BlinkIDUX | 1 | 1 | 0 | 0 | 3 | 0 |
| **Total** | **13** | **4** | **9** | **0** | **20** | **0** |

The recorded losses cluster on the two produce/consume shapes the design anticipated (4 produce-throw + 9
consume-degrade), with **zero** receiver-failfast. **All three paths are now fully accounted.** Every
throwing stub in the generated `.cs` has a matching produce-throw / receiver-failfast row (verified by
grepping each `.cs` for the throwing-stub message and `FailFastSuppressedProxyReceiver`), and the consume
path — which emits neither a throw stub nor a fail-fast, so a grep can't see it — now routes through
`SuppressedProxyReporting` at every site that drops the wrap fallback, including the collection-element
leaf. RealityFoundation's 5 previously-unrecorded `[Material]` collection consume-degrades are the
difference between the earlier total of 8 and the current **13**; RoomPlan and BlinkIDUX are unchanged (no
suppressed-proxy `[any P]` collection is consumed). `ReviewCount` is **0** in all three libraries: the
classifier put nothing into the human-triage bucket. RealityFoundation's total skip count (322) is within a
handful of the 2026-06-27 audit's ~316, confirming no **surface** change — the degraded members still
emit (the recorded count grew because 5 more are now *flagged*, not because 5 were dropped; the emitted C#
is byte-identical). No classifier recalibration is warranted, and the consume-degrade coverage gap this
report previously tracked as a follow-up is now closed.

## Owner tier-call notes

- The only **functionally material** degraded member is **`RoomCaptureView.delegate` (setter)** —
  assigning a C#-authored delegate throws `InvalidCastException` at the marshalling boundary — and it has a
  documented, fully-functional workaround (`RoomCaptureSession.Delegate`).
- **`ModelComponent.materials` (getter)** is the highest-visibility produce-throw getter (every
  `ModelEntity` user can hit it) — now a **compile error (SB0006)** on read rather than a silent runtime
  throw. Its **setter and the `ModelComponent`/`ModelEntity` `[Material]` initializers** accept
  Swift-vended materials but throw `InvalidCastException` for a C#-authored `IMaterial` — and those five
  setter/init degrades are **now recorded**, so the report no longer undercounts the Material surface.
- The remaining recorded rows (`Scene.synchronizationService`, `CustomMaterial.init`, `BlinkIDAnalyzer.events`)
  are peripheral/Low per the audits, and the consume-degraded init/service rows lose only **C#-authored**
  conformers — Swift-vended SDK objects still round-trip.
- No member became non-functional *because of* this pass; the report simply makes the pre-existing
  degradations visible and classified — now including the collection-element consume-degrades that were
  previously silent. Rescuing any of these members (emitting the missing proxy) is capability work, tracked
  separately and out of scope here.
