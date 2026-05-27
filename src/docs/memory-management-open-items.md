# Memory Management — Open Items

**Goal:** a consumer who generates C# bindings should never have to reason about Swift↔C# ownership. Returned values, collection elements, existentials, and stream elements should release exactly once, automatically, with no leaks and no use-after-free.

This doc tracks the remaining gaps toward that goal. It is ordered by **severity** (crash > leak > design decision), not by effort. Detailed root-cause notes live in the linked memory files; this is the index and plan.

What's already handled (do **not** re-chase):

- Owned `any P` / `(any P)?` returns (scalar + optional, method + property/accessor) adopt the existential's `+1` via the EC1 `ownsContainer` flag and release on `Dispose`/finalize. Borrowed receiver-callback parameters correctly stay non-owning.
- The wire-carrier copy-out return family (Array/Dictionary/Set/Optional/Result/FrozenWithMemory) copies-then-Destroys.
- Set/Dictionary **of class keys**: class `Hashable` conformances now emit a witness-table registration, so the NativeAOT path no longer falls to reflection `MakeGenericMethod`. The broader "conformance registration" hole was narrower than once thought — the conformance dictionary already registers non-Self-requirement protocols, and Hashable was the one Self-requirement protocol the Set/Dictionary runtime path needs. There is no general registration gap left here.

---

## 1. AsyncStream class/struct element — use-after-free (HIGH) — RESOLVED

`SwiftAsyncStream<T>.OnElement` (`src/Swift.Runtime/src/Swift/SwiftAsyncStream.cs`) now takes a synchronous independent copy of the element via `SwiftMarshal.ExtractCopiedValue<TElement>` **before** the borrowed slot pointer goes out of scope (class → deref + `Arc.Retain`; reference-backed non-POD → VWT `InitializeWithCopy`). The Swift wrapper deliberately keeps the borrowed `withUnsafePointer(to: element)` slot — the fix is entirely on the C# extraction side, so the marshalled value that escapes through the channel is self-owning and no longer aliases freed storage.

Coverage: `BindingTests/RuntimeTestsApp/Async/AsyncStreamOwnershipTests.cs` — class element, non-frozen struct element, and large heap `String` element, each asserting ARC balance to zero after channel drain + dispose.

---

## 2. Composite-payload per-element ARC (MEDIUM) — RESOLVED

The durable fix is a **unified per-element ownership extraction** core in `SwiftMarshal` (`ExtractCopiedValue` / `ExtractCopiedElement` / `MarshalElementFromSwiftUnsafe`), which classifies each element address-only vs. direct and applies move-vs-copy-with-retain per element rather than VWT-`Destroy`-ing a whole temp after mixed extraction.

**P1 — Dict/Set non-class ref-containing values — RESOLVED.** `SwiftMarshal.MarshalMovedValueFromSlot<T>` gained a second branch for reference-backed non-class, non-move-on-construction values (`metadata.ValueWitnessTable->IsNonPOD`): `ExtractCopiedValue` (VWT `InitializeWithCopy`) then VWT `Destroy` of the source slot. `SwiftDictionary`/`SwiftSet` enumeration (`CollectEntries`/`CollectElements`) and single-slot (`TryGetValue`/`RemoveValue`) paths all route through it. Coverage: `WireCarrierLeakProbeTests.cs` — frozen-with-ref struct + non-frozen struct dict values, frozen-with-ref set members, single-slot `TryGetValue`.

**P3 — tuple elements — RESOLVED.** Root cause turned out to be **carrier metadata erasure**, not just an extraction under-retain: `TupleProjection` had no `MarshalFromSwiftType` override, so a tuple carrier (`SwiftOptional`/`SwiftResult`/`SwiftArray`/…) lowered a class element to `IntPtr`. The carrier's Swift VWT (built from the C# element types via `swift_getTupleTypeMetadata`) then saw that slot as POD → it neither retained the class on copy nor released it on destroy, orphaning the wire buffer's `+1`. Fix: `TupleProjection.MarshalFromSwiftType` now composes each element's `MarshalFromSwiftType` (class → wrapper type, String → `SwiftString`), so the carrier metadata is ARC-correct and extraction hands each element through self-owning (class → pass-through `+1`; String → `.ToString()` + dispose). The earlier "low realized risk / generator does not emit these shapes" premise was the reason this stayed latent — the maximum-case fixtures (`Optional`/`Result` of `(class, String)`) **forced** emission and exposed the metadata bug. Coverage: `ExtractionRetainProbeTests.cs` — `TestOptionalSomeTupleExtractionDoesNotOverReleaseSharedRef` + `TestResultSuccessTupleExtractionDoesNotOverReleaseSharedRef` (surviving-owner probes asserting the global-owned ref stays live==1 then 0).

Detail: memory `project_wire_carrier_adjacent_ownership_gaps` (P1, P3).

---

## 3. EC2+ composition existentials (`any A & B`) — RESOLVED

The EC1 `ownsContainer` mechanism now extends to composition existentials. The single-conforming-value invariant is the reason it generalizes with no new ownership model: an `ExistentialContainerN` (N ≥ 1) holds exactly one payload value (3-word inline buffer or heap box) plus `ObjectMetadata`; the extra protocols add only witness-table words. So a value-witness `Destroy` driven by the existential's own metadata (`GetExistentialTypeMetadata(Count)`) releases exactly that one value regardless of protocol count — the same release path EC1 already proved sound.

Fix: the composition proxy (`ModuleHandler.EmitCompositionProxy`) now stores `_ownsContainer`/`_disposed`, takes an ownership-aware 2-arg ctor (`ownsContainer` defaulting to non-owning), and on `Dispose`/finalize runs `SwiftMarshal.DestroyWireBufferRetains` through the EC2+ container's metadata — mirroring the EC1 proxy exactly. The four owned-return gate sites (`WrapperEmitter.Return.cs`, `ExistentialBypassEmitter.cs`, `ProtocolProxyEmitter.InterfaceImpl.cs`, `ExistentialProjection.cs`) now route through a single shared predicate `ExistentialHandler.IsOwnedExistentialContainerType` (EC1–EC8; EC0 bare-any excluded), so they stamp `ownsContainer: true` for compositions too. Borrowed callback parameters and `NewFromPayload` reads stay non-owning. The opaque-layout assumption is unchanged — class-bound/ObjC compositions, which the proxy already reads as opaque, would need a separate class-existential release shape if that path ever materializes.

Coverage: `ExistentialReturnLeakProbeTests.cs` — `TestCompositionExistentialReturnReleasesInlinePayload` (non-optional `any Nameable & Ageable`, inline class payload), `TestOptionalCompositionExistentialReturnReleasesInlinePayload` (`(any Nameable & Ageable)?`), and `TestCompositionExistentialReturnReleasesBoxedPayload` (a value-type conformer with five embedded `TrackedRef`s, heap-boxed past the 3-word inline buffer — asserts the EC2 release path drives the container's VWT for the boxed case, not a bare first-word release). All three are surviving-owner ARC-balance probes backed by `TrackedNameableAgeable` / `BoxedTrackedNameableAgeable` in `MemoryManagement/ExistentialReturnLeak.swift`. Validated RED→GREEN on Simulator (Mono) and Device (NativeAOT).

---

## 4. `(any Error)?` / `AnyError` — RESOLVED (reference-type wrapper)

The product call landed on the **reference-type wrapper**: `Swift.Foundation.AnyError` is now a `sealed class : ISwiftObject, IExistentialContainer, ISwiftExistentialConvertible<ExistentialContainer1>` instead of a blittable value struct. The earlier "blittable carrier ABI vs. deterministic release" tension dissolved once the wire reality was pinned down: `any Error` is not a 5-word opaque existential but a **single 8-byte `swift_errorRelease`-managed boxed reference** living in `ExistentialContainer1.Payload0`. There is no generic `SwiftResult<TSuccess, AnyError>` carrier in generated output to preserve — the actual Result-shaped failure surface is the named-enum `TryGetFailed(out AnyError)` accessor (enum-payload extraction), and Optional error returns go through `OptionalProjection`. Both are single-word reads, so a reference wrapper carries the box pointer without any ABI breakage.

The wrapper mirrors the EC1/EC2 proxy ownership model exactly: an `_ownsContainer` flag, a `_disposed` guard, an ownership-aware ctor (`SwiftDisposeScope.TryRegister(this)` when owning, `GC.SuppressFinalize(this)` when not), and a `Dispose`/finalizer that — gated on `_ownsContainer && Payload0 != IntPtr.Zero` — calls the new `@_cdecl("SBW_AnyError_Destroy")` runtime export (a `deinitialize(count:)` of the `(any Error)` at the container pointer, i.e. one `swift_errorRelease`). `LocalizedDescription` stays a live accessor (throws `ObjectDisposedException` after dispose).

On a Swift→C# **owned** transfer (method/property returns, Optional returns, enum-payload extraction, closure returns) the construction site stamps `ownsContainer: true` via the `ExistentialHandler.WellKnownOwnedTransferArg` helper, so the wrapper adopts the box's `+1` and releases it deterministically. **Borrowed** sites (closure PARAMETERS — `MethodClosureBridge`, the MCB callback lambdas) keep the non-owning ctor: the existential lives on Swift's stack only for the callback duration, so the C# side must not release it.

**Source-breaking public-API change** (flagged for the user — owner of versioning + wiki): `(any Error)?` is no longer `Nullable<AnyError>`. Consumers use `!= null` / `is null` instead of `.HasValue`, direct member access instead of `.Value`, and there is no parameterless `new AnyError()`. `AnyError` is now `IDisposable` and the analyzer (SB1001) flags an undisposed owned local.

Coverage: `TestOptionalErrorReturnReleasesPayload` (the formerly `[Skip]`-retained probe, now green), `TestNonOptionalErrorReturnReleasesPayload` (direct `any Error` return), and `TestErrorEnumPayloadExtractionReleasesPayload` (Result-shaped `TrackedErrorBox.failed` → `TryGetFailed(out AnyError)` extraction, surviving-owner ARC-balance), all in `ExistentialReturnLeakProbeTests.cs` backed by `TrackedError` / `TrackedErrorBox` in `MemoryManagement/ExistentialReturnLeak.swift`; plus `UndisposedAnyErrorLocal_ReportsInfo` in `SwiftObjectDisposeAnalyzerTests.cs`. Validated on Simulator (Mono) and Device (NativeAOT).

**Parallel proxy-sibling gap — RESOLVED.** The same owned-transfer audit was extended to the *opaque-existential proxy* extraction sites. An empirical enumeration confirmed real latent under-release: the `EnumHandler.Marshalling.cs` proxy enum-payload extraction (3 sites) and the closure-return proxy sites (`ClosureEmitter.cs`, `ClosureEmitter.StructParams.cs`, `ClosureEmitter.Throwing.cs`) constructed the proxy non-owning on owned-transfer paths — the proxy analogue of the `ExistentialProjection` well-known miss. All now stamp `ownsContainer: true` via `ExistentialHandler.IsOwnedExistentialContainerType(containerType)` (EC1–EC8; bare-`any` falls to the separate non-proxy branch). The enum-extraction case is reachable and covered by `TestProxyEnumPayloadExtractionReleasesPayload` (`TrackedRenderableBox.shown` → `TryGetShown(out IRenderable)`, surviving-owner ARC-balance). The closure-return proxy sites are not currently emitted by any binding (no fixture, and the closure support gate does not surface closure-returning-existential shapes), so those edits are forward-safe consistency fixes matching the pre-existing well-known (`AnyError`) closure-return treatment, correct-by-construction and gated to the proxy-only branch.

Detail: memory `project_wire_carrier_adjacent_ownership_gaps`.

---

## Suggested sequencing

1. ~~**AsyncStream UAF**~~ — RESOLVED (item 1).
2. ~~**Unified per-element extraction**~~ — RESOLVED (item 2, closes P1 + P3).
3. ~~**EC2+ composition existentials**~~ — RESOLVED (item 3).
4. ~~**AnyError**~~ — RESOLVED (item 4): reference-type wrapper, deterministic release on Dispose/finalize. Source-breaking API change flagged for the user.

All tracked items are now resolved.
