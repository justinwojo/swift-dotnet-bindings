# EveryProtocol fail-closed — public-surface impact

**Scope:** RealityFoundation, RoomPlan, BlinkIDUX. **Generated:** 2026-07-10, locally at HEAD
`ce20d8ea`, iOS-simulator apple-framework / manual mode (`nuke validate --filter <lib>`). Local
analysis only — no packing, no publishing. **Baseline for "before":** the 2026-06-27 binding audits
(`src/docs/BindingAudit/{RealityFoundation,RoomPlan,BlinkIDUX}.md`), which reflect the shipped
0.17.0-era output.

## What "fail-closed" means here (and what it does *not*)

When a Swift protocol's `{Protocol}Proxy` reverse-dispatch conformance cannot be synthesized, the
generator **keeps** the surrounding member but degrades its behaviour:

- **produce-throw** — a getter / return that could only be produced by constructing the missing proxy
  emits a throwing stub (`NotSupportedException`).
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

This degradation is **not new** and this pass **removed nothing**: the generator change that this
report characterises (commit `ce20d8ea`) is **diagnostic-only and byte-identical** — the same throwing
getters and throw-on-C#-conformer setters that 0.17.0 shipped are still emitted, unchanged. What is new is that
each per-member decline is now promoted to a classified `SuppressedProxyMemberDegraded` skip row
(disposition `KnownLimitation`) in `binding-report.json`, instead of being invisible. So "which
members disappear under fail-closed" is really **"which public members the report now flags as
degraded"** — the tables below — so the owner can make per-library tier calls. Every one of these
members was already degraded in 0.17.0: a **produce-throw** getter is fully non-functional (always
throws), while a **consume-degraded** setter/param is functional *only* for Swift-vended conformers and
throws `InvalidCastException` for a C#-authored one. Either way there was never a working
C#-conformer path — a consumer that exercised it got an exception.

**Coverage caveat (important):** consume-degrade reporting is **not complete**. Only three scalar
consume sites route through `SuppressedProxyReporting.Record` today — the property setter
(`PropertyHandler`), the method/init parameter via the wrapper and P/Invoke paths
(`WrapperEmitter`/`PInvokeEmitter`). Every **other** place that drops the wrap fallback for a suppressed
proxy is currently unrecorded: the **collection-element** consume path (`[any P]` array/set/dict setter
or parameter, decided in the leaf `ExistentialProjection`), **enum-case construction**
(`EnumHandler.CaseConstruction`), **closure-return** consume (`ClosureEmitter`), and the reverse-dispatch
owned getters/returns. Of these, only the collection-element path is **realized** in the three libraries
here — RealityFoundation's 5 `[Material]` members (detailed under *RealityFoundation → Known coverage
gap*); the enum/closure/owned-getter shapes exist in the generator but no suppressed-proxy member in
these three libraries hits them, so they contribute 0 unrecorded rows *here* (but could in other
libraries). Net: the produce-throw and receiver-failfast paths are fully accounted, but the recorded
consume-degrade counts are a **lower bound**. Wiring the remaining consume sites into
`SuppressedProxyReporting` is tracked as remaining work (see the phase summary).

Members that are simply **absent for other reasons** (e.g. `UnsupportedExistential`, `AnyTypeFallback`,
`GenericProtocolConstraint`, `DuplicateSignature`, `SynthesizedCodable`) are **pre-existing** and are
**not** attributable to the EveryProtocol classifier; they are listed per library under
*"Pre-existing skips (not this classifier)"* so they are not double-counted here.

---

## RealityFoundation

- Members 2564 total / 2401 emitted / 317 skipped. **EveryProtocol-degraded public members the report
  flags: 5** (plus **5 unrecorded** consume-degrades — see *Known coverage gap* below, so the true count
  is 10). Proxy classes not emitted: 15. Report triage `ReviewCount: 0`.

| Member | Kind | Degradation | Root-cause proxy | Skip reason (disposition) | What the consumer loses / was getting before |
|---|---|---|---|---|---|
| `ModelComponent.materials` (getter) | property getter | produce-throw | `MaterialProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Cannot **read** a model's material list — the getter throws `NotSupportedException`. The setter emits and accepts **Swift-vended** materials, but throws `InvalidCastException` for a C#-authored `IMaterial` (**and is itself unrecorded** — see coverage gap). Before: identical throwing getter (audit "HIGH"). |
| `Scene.synchronizationService` (getter) | method | produce-throw | `SynchronizationServiceProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Cannot read the multipeer sync service. Collaboration-only surface (audit "Low"). Before: throwing getter. |
| `Scene.synchronizationService` (setter) | property | consume-degraded | `SynchronizationServiceProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Cannot install a **C#-authored** sync service — the set **throws `InvalidCastException`** at the marshalling boundary; a Swift-vended service would still round-trip. Practically nil — custom C# sync services are not a real use case. |
| `CustomMaterial.init` (×2 overloads) | initializer | consume-degraded | `MaterialProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Passing a **C#-authored** `Material` into `CustomMaterial`'s init **throws `InvalidCastException`**; Swift SDK materials (`SimpleMaterial`, `PhysicallyBasedMaterial`, …) are Swift-vended and pass normally. Practically nil. |

**Proxy classes not emitted (root cause, pre-existing):** `MaterialProxy`,
`SynchronizationServiceProxy`, `RealityCoordinateSpaceProxy`, `ActionHandlerProtocolProxy`,
`EntityActionProxy`, `TransientComponentProxy`, `MeshBufferSemanticProxy`, `MeshBufferContainerProxy`,
`EntityCollectionProxy`, `MaterialFunctionProxy`, `ForceEffectBaseProxy`, `AnimationStateProtocolProxy`,
`PostProcessEffectProxy`, `SystemProxy`, `CancellableProxy`. All classify structurally (associated-type/
Self-constrained, `UnsatisfiedHiddenRequirements`, `NoncopyableParamOrReturn`, `ConstructorRequirements`,
etc.) except `CancellableProxy` (module-internal → `ExpectedNonPublic`). Most have **no** degraded public
member — the proxy is unused on the public surface, or its would-be members were already dropped for a
different, pre-existing reason.

**Known coverage gap — 5 unrecorded consume-degrades on the `[Material]` collection surface.** These
public members consume `[Material]` (`IEnumerable<IMaterial>` / an `IReadOnlyList<IMaterial>` setter).
Because `MaterialProxy` is suppressed, the generator drops the per-element wrap fallback and emits the
no-fallback `CreateOwnedExistential1<IMaterial>(e)` (opaque arity-1; the class-bound analogue would be
`CreateOwnedClassCarrier<T>(e)`), which **throws `InvalidCastException`** for a C#-authored `IMaterial`
(Swift-vended `SimpleMaterial`/`PhysicallyBasedMaterial`/… still round-trip) — the identical
consume-degrade shape as the recorded rows above, but at the **collection-element** level:

- `ModelComponent.materials` (setter)
- `ModelComponent(mesh:materials:)` (init)
- `ModelEntity(mesh:materials:)` (init)
- `ModelEntity(mesh:materials:collisionShape:)` (init)
- `ModelEntity(mesh:materials:collisionShapes:)` (init)

**None of these five is recorded** in `binding-report.json`. Only the *scalar* consume path records the
decline at the property/method handler (`PropertyHandler.cs:1215`, `WrapperEmitter.Marshalling.cs:573`,
`PInvokeEmitter.cs:637`); the degradation for a *collection* element is decided in the leaf
`ExistentialProjection` element conversion, which has no handle on the owning decl and does not route
through `SuppressedProxyReporting`. Recording it requires threading a "degraded" signal from the leaf up
through every container projection (`ArrayProjection`/`SetProjection`/`DictionaryProjection`/
`OptionalProjection`/nested) to the handler — a cross-projection refactor, not a same-mechanism wire, so
it is **left unchanged and tracked as remaining work** (see the phase summary).

The collection-element path is the only unrecorded consume shape **realized** in these three libraries,
but it is **not the only** unrecorded consume shape in the generator: enum-case construction
(`EnumHandler.CaseConstruction.cs:490`/`1001`), closure-return consume (`ClosureEmitter.cs:537`), and the
reverse-dispatch owned getters/returns likewise drop the wrap fallback for a suppressed proxy without a
`SuppressedProxyReporting.Record` call. No suppressed-proxy member in RealityFoundation/RoomPlan/BlinkIDUX
exercises those, so they add 0 rows here — but the full remaining-work scope is "route **every** consume
site that drops the wrap fallback through the central decision," not the collection leaf alone.
Consequence for the owner: the recorded EveryProtocol-degraded counts in this report are a **lower
bound**; a suppressed-proxy `[any P]`-consuming surface (and, in other libraries, enum/closure consumers)
can carry additional throw-on-C#-conformer members the report does not yet flag.

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
| `RoomCaptureView.delegate` (getter) | method | produce-throw | `RoomCaptureViewDelegateProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Cannot read back the assigned view delegate — the getter throws `NotSupportedException` (so `if (view.Delegate != null)` throws rather than returning null). Before: identical throwing getter. |
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
| `BlinkIDAnalyzer.events` (getter) | method | produce-throw | `EventStreamProxy` | `SuppressedProxyMemberDegraded` (KnownLimitation) | Cannot read the raw `events` `EventStream` — the getter throws `NotSupportedException`. The supported path is the concrete `BlinkIDEventStream.Stream` (`IAsyncEnumerable`), which works. Before: identical throwing getter. |

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

Recorded rows (what `binding-report.json` flags today):

| Library | Recorded degraded members | produce-throw | consume-degraded | receiver-failfast | Unrecorded consume-degrades | Proxy classes skipped | `ReviewCount` |
|---|---|---|---|---|---|---|---|
| RealityFoundation | 5 | 2 | 3 | 0 | **5** (`[Material]` collection) | 15 | 0 |
| RoomPlan | 2 | 1 | 1 | 0 | 0 | 2 | 0 |
| BlinkIDUX | 1 | 1 | 0 | 0 | 0 | 3 | 0 |
| **Total** | **8** | **4** | **4** | **0** | **5** | **20** | **0** |

The **recorded** losses cluster tightly on the two produce/consume shapes the design anticipated (4 + 4),
with **zero** receiver-failfast. The produce-throw and receiver-failfast paths are fully accounted: every
throwing stub in the generated `.cs` has a matching report row (verified by grepping each `.cs` for the
throwing-stub message and `FailFastSuppressedProxyReceiver`; counts match exactly). **The consume path is
not fully accounted:** RealityFoundation carries **5 unrecorded collection-element consume-degrades** on
the `[Material]` surface (see *Known coverage gap* under RealityFoundation) — the earlier "zero silent
declines" reading was **wrong**, because the grep only covered the produce/receiver stubs and a
collection-element consume-degrade emits neither a throw stub nor a fail-fast. RoomPlan and BlinkIDUX have
**no** such gap (no suppressed-proxy `[any P]` collection is consumed). `ReviewCount` is **0** in all
three libraries: the classifier put nothing into the human-triage bucket. RealityFoundation's total skip
count (≈317) is within ±1 of the 2026-06-27 audit's 316, confirming no dramatic **surface** change — the
degraded members still emit; the gap is in *diagnostic coverage*, not in what compiles. No classifier
recalibration is warranted; wiring the collection-element consume path into `SuppressedProxyReporting` is
the tracked follow-up.

## Owner tier-call notes

- The only **functionally material** degraded member is **`RoomCaptureView.delegate` (setter)** —
  assigning a C#-authored delegate throws `InvalidCastException` at the marshalling boundary — and it has a
  documented, fully-functional workaround (`RoomCaptureSession.Delegate`).
- **`ModelComponent.materials` (getter)** is the highest-visibility throwing getter (every `ModelEntity`
  user can hit it). Its **setter and the `ModelComponent`/`ModelEntity` `[Material]` initializers** accept
  Swift-vended materials but throw `InvalidCastException` for a C#-authored `IMaterial` — and those five
  setter/init degrades are the **unrecorded** coverage gap, so the report undercounts the Material surface.
- The remaining recorded rows (`Scene.synchronizationService`, `CustomMaterial.init`, `BlinkIDAnalyzer.events`)
  are peripheral/Low per the audits, and the consume-degraded init/service rows lose only **C#-authored**
  conformers — Swift-vended SDK objects still round-trip.
- No member became non-functional *because of* this pass; the report simply makes most of the pre-existing
  degradations visible and classified (with the collection-element consume-degrade coverage gap noted
  above). Rescuing any of these members (emitting the missing proxy) is capability work, tracked separately
  and out of scope here.
