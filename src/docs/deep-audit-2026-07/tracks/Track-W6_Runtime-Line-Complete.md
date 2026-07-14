# Track W6 — Runtime Line-Complete

**Wave**: 6  
**Track**: W6 Runtime line-complete  
**Date**: 2026-07-16  
**Mode**: Read-only analysis (no production edits)  
**Risk (headline)**: **~2/5 (Low–Medium)** — A3 ownership map holds; residual risk is intentional fail-soft edges, Mono dual-path complexity, and L4 dual-factory / EC0–8 surface — not a new emission-live double-free/UAF class.

**Inputs**: [`00-methodology.md`](../00-methodology.md), [`M0-B-runtime-map.md`](../waves/W0-map/M0-B-runtime-map.md), [`Track-A3_ARC-Ownership-Lifetime.md`](Track-A3_ARC-Ownership-Lifetime.md), [`00-mid-audit-executive-summary.md`](../synthesis/00-mid-audit-executive-summary.md), prior A7/A4 runtime-adjacent notes.

**Scope**:
- `src/Swift.Runtime/src/**/*.cs` (managed runtime + consumer types + XML databases as inventory)
- `src/Swift.Runtime/swift/*` (native)
- `src/Swift.Runtime/tests` (coverage honesty spot-check)

---

## Headline

Wave 6 completes the **runtime ledger** that Wave 0 inventoryed and Wave 1 A3 deep-read for ownership. Re-confirmation of ARC / SafeHandle / marshal extract / Design-B2 / async holders found **no new emission-live P0 double-free or UAF**. The runtime is a **mature integrity layer**: fail-closed where contract integrity matters (`RuntimeContract`, missing PayloadSemantics registration), fail-soft where SafeHandle contract forbids throws (finalizer / metadata miss), and dual-pathed where Mono forces cdecl/collection bypasses.

| Focus | Verdict |
|-------|---------|
| Double-free / UAF residual | **Low** — core paths gated; residual = intentional process-exit leak + nested-escaping design leak (A3) |
| Fail-soft finalizer | **Confirmed candidate residual** (A3-012) — zero `_metadataHandle` → free without Destroy |
| RuntimeContract floor | **Sound** — floor 16; epoch 0 dev bypass intentional; pure window unit-tested; **product decision** on 0.x→1.0 still owner-gated |
| Collection Mono dual paths | **Intentional, documented** — six cdecl mutators + three specialized Set inserts; fallback CallConvSwift remains Mono-broken for generic elements |
| L4 EC0–8 / dual factories | **High simplification value, low correctness risk** if left as-is |
| L3 graceful degrade | **N/A for runtime core** — generator/SDK is L3 home; runtime fails closed or fail-soft by SafeHandle contract, not “partial binding” |

**Overall track risk: ~2/5.** Aligns with mid-audit “ABI cores hardened; diminishing P0 yield.”

---

## Executive counts

| Bucket | Count |
|--------|------:|
| Confirmed new open defects | **0** |
| New candidates (this wave) | **3** |
| Already-known (re-tagged, not re-chased) | **12** |
| Refuted / verified-clean this wave | **6** |
| L4 simplification notes | **8** |
| L3 degrade-opportunity (runtime-owned) | **0** (intentional) |
| Managed `.cs` files checklisted | **100** (ledger scope) |
| Native sources checklisted | **3** |
| Unit test files spot-checked | **44** inventory; deep honesty notes below |
| Files `reviewed-deep` (this wave + A3 carry) | **~45** |
| Files `reviewed` (full read, no load-bearing concern) | **~35** |
| Files `hazard` (dual-path / complexity documented) | **~12** |
| Remaining pure inventory (stubs/XML/attributes) | **~20** — purpose-confirmed, no L1 surface |

---

## 1. Double-free / UAF residual (confirm A3)

### Ownership pillars (still hold)

| Pillar | Status | Key evidence |
|--------|--------|--------------|
| Class ARC + finalizer trampoline | Sound | `Arc.cs` / `SwiftReleaseTrampoline` / `SwiftClassHandle.ReleaseHandle` |
| Value SafeHandle + VWT Destroy | Sound | `SwiftHandle.cs` + `SBW_VWTDestroy`; `MarkConsumed` for consuming params |
| PayloadConstructionSemantics | Single cleanup axis | `PayloadConstructionSemantics.cs`; registered in `SwiftFrameworkResolver.InitializeRuntime` |
| Wire-buffer Destroy after Copy | Sound | `SwiftMarshal.DestroyWireBufferRetains*` |
| Borrowed callback arg | Copy-owning; Adopt/Move suppress | `MarshalCallbackArg` (`SwiftMarshal.cs:1214–1239`) |
| Design B2 ProxyLifetimeTracker | Sound | dual-root R0 + impl GCHandle; atomic `Released` |
| Async holder Cleanup | Idempotent + exception-safe | `SwiftAsyncCallHolder.Cleanup` (`AsyncHelpers.cs:275+`) |
| Iterator pre-retain | Sound | Dict/Set `Arc.Retain` before `makeIterator` |

### Residual (not new)

| ID | Claim | Status |
|----|-------|--------|
| DA-W1-A3-010 | Nested escaping inner-box intentional leak | already-known candidate |
| DA-W1-A3-011 | Returned throwing-closure error live count soft | already-known candidate |
| DA-W1-A3-012 | Finalizer skip Destroy if metadata cache zero | already-known **fail-soft** |
| DA-W1-A3-013 / DA-W4-A7-015 | `CompleteWithResult` Free without Destroy | already-known; POD-only admission |
| Process-exit skip native release | Intentional via `SwiftExitGuard` | refuted-as-bug (A3-R04) |

**No new emission-live double-free/UAF confirmed.**

---

## 2. Fail-soft finalizer (deep)

### Pattern

SafeHandle `ReleaseHandle` **must not throw**. All native cleanup is try/catch-swallowed. Two intentional fail-soft arms skip VWT Destroy:

1. **`_metadataHandle == IntPtr.Zero`** (`SwiftSafeHandle` construction catch → finalizer free-only) — `SwiftHandle.cs:99–114`, `:267–276`.  
2. **`DestroyWireBufferRetains<T>` metadata catch** — skips destroy, caller still frees carrier — `SwiftMarshal.cs:223–246`.  
3. **Trampoline `DllNotFoundException`** (unit tests / harness without native) — swallow in `SafeReleaseRawForFinalizer` / finalizer ReleaseHandle.

### Classification

| Lens | Note |
|------|------|
| L1 | If production type fails metadata at construction, finalizer-only path **orphans** embedded ARC retains (same as A3-012). Explicit Dispose still uses live `GetTypeMetadata()` path. |
| L5 | Easy to “improve” by throwing — **must not**. |
| Integrity | Production `ISwiftObject` types resolve metadata via module-init registration + cache-first dispatch. |

### DA-W6-001: Fail-soft Destroy is a deliberate SafeHandle contract, not a silent product policy

- **Severity**: P2 (latent only if metadata unavailable for a real Copy/Adopt type)  
- **Status**: already-known (extends A3-012) + **documented as intentional**  
- **Confidence**: high  
- **Lenses**: L1, L5  
- **Reachability**: latent production; common unit-test mock  
- **Claim**: Fail-soft is correct for SafeHandle; the residual risk is **only** the metadata-zero finalizer path for real types.  
- **Evidence**: `SwiftHandle.cs:99–114,267–276`; `SwiftMarshal.cs:218–244`; A3-012.  
- **Prior art**: DA-W1-A3-012.

---

## 3. RuntimeContract floor (deep)

### Model

| Field | Value / rule |
|-------|----------------|
| `Version` | `ParseEpoch(BuildVersion)` = `major*1000+minor`; **0** for `0.0.0-dev` |
| `MinimumSupportedGeneratedVersion` | **16** (0.16 — PayloadConstructionSemantics contract) |
| Window | Accept if either side epoch 0, else `floor ≤ gen ≤ Version` |
| `BuildVersion` | Generated from `$(SwiftBindingsSdkVersion)` in csproj (single-sourced) |

Evidence: `RuntimeContract.cs:65–138`; `Swift.Runtime.csproj` generated partial; `RuntimeContractTests.cs` full matrix.

### Fail modes

| Condition | Behavior | Product meaning |
|-----------|----------|-----------------|
| gen > runtime | Throw | Binding newer than runtime — missing APIs |
| gen < floor | Throw | Binding predates dispatch contract |
| gen or runtime == 0 | **Always accept** | Dev / ProjectReference self-consistent |
| Unparseable version | Epoch **0** | Fail-soft to always-compatible |

### Findings

#### DA-W6-002: `ParseEpoch` garbage → epoch 0 is intentional fail-soft (integrity note)

- **Severity**: P3 (integrity edge)  
- **Status**: confirmed (by design)  
- **Confidence**: high  
- **Lenses**: L2, integrity-gate  
- **Reachability**: integrity-gate  
- **Claim**: Unparseable `BuildVersion` maps to epoch 0 → `AssertCompatible` never rejects. Released packages use real `major.minor.*` so this is only a packaging-misconfig backstop hole, not a day-1 consumer path. Unit test pins `"garbage" → 0`.  
- **Evidence**: `RuntimeContract.cs:127–138`; `RuntimeContractTests.ParseEpoch_MapsVersionToEpoch` (`"garbage"`, `"x.8.0"`).  
- **Probe**: Force bad `BuildVersion` in a release build; expect silent always-compatible (bad) vs hard fail (desired for release-only).  
- **Suggested direction** (owner-gated): release builds could treat unparseable as hard fail; keep 0 for dev.

#### DA-W6-003: Floor 16 still hand-set — 0.x→1.0 decision not runtime-code

- **Severity**: P2 product / release hygiene  
- **Status**: already-known (docs + CLAUDE.md)  
- **Confidence**: high  
- **Lenses**: L2, integrity-gate  
- **Claim**: Floor does **not** auto-track minor. Gate **fails open** for old-but-in-window bindings if a future minor breaks dispatch without raising floor. Unit test only asserts `floor ≤ Version` on non-dev builds — not that floor moved on every minor.  
- **Evidence**: `RuntimeContract.cs:48–51,74–82`; `RuntimeContractTests.Floor_DoesNotExceedVersion_AtReleaseBuilds`.  
- **Prior art**: CLAUDE.md Runtime-contract floor discipline; mid-audit “docs/product policy.”

**Refuted**: “Floor is fail-open for *too-new* bindings” — forward direction is fail-closed (`gen > Version`).

---

## 4. Collection Mono dual paths (deep)

### Path matrix

| Op family | Path | Why |
|-----------|------|-----|
| Array/Dict/Set **reads** (subscript, count, contains, …) | Direct `CallConvSwift` | Passes Catalyst-x64 |
| Dict update/remove/iterator next; Set remove/iterator next; Array remove | **Cdecl** via `SwiftCollectionCdeclWrappers` → `SwiftBindingsRuntimeCollections.c` | Catalyst-x64 Mono corrupts self when sret + intermediate ints + SwiftSelf |
| Set.insert for `long` / `nint` / `SwiftString` | Specialized `SBW_Set{Int64,Int,String}_Insert` | Mono mishandles `(Bool direct, @out)` tuple return |
| Set.insert **other** Element | Fallback CallConvSwift | **Known-broken on Mono sim** for that shape — documented |

Evidence:
- `SwiftCollectionCdeclWrappers.cs:9–39` (coverage rule: six mutators only)  
- `SwiftBindingsRuntimeCollections.c` six cdecl exports  
- `SwiftSet.cs:393–468` insert dual path + fallback comment  
- `SwiftDictionary.cs` / `SwiftArray.cs` cdecl call sites  
- Iterator pre-`Arc.Retain`: Dict `:467–473`, Set `:587–593`

### Already-known Mono tags (do not invent 5th upstream)

| Topic | Tag |
|-------|-----|
| Finalizer `!ji->async` on direct release/VWT | UP-01 (mitigated trampolines) |
| Set.insert DoneBlocking / tuple return | UP-03 family; specialized cdecl |
| Catalyst-x64 sret+self | Documented as Mono workload; cdecl bypass |
| SafeHandle async lifetime | Upstream comment; deferred AddRef |

#### DA-W6-004: Generic Set.insert fallback still CallConvSwift-broken on Mono

- **Severity**: P2 (when Element ∉ {long, nint, SwiftString})  
- **Status**: already-known / documented  
- **Confidence**: high  
- **Lenses**: L1, L2  
- **Reachability**: emission-live for `SwiftSet<CustomHashable>` style elements under Mono  
- **Claim**: Fallback path (`SwiftSet.cs:450–468`) is known-broken on iOS Simulator Mono; BindingTests / product may rely on specialized elements or skip. Adding a new element type needs a new native wrapper.  
- **Evidence**: `SwiftSet.cs:450–453` comments; `SwiftBindingsRuntime.swift` `SBW_Set*_Insert`.  
- **Prior art**: Mono Set.insert; RuntimeLimitations / BindingTests Set coverage.

---

## 5. L4 — EC0–8 and dual factories

### EC0–8

`ExistentialContainer.cs` (~1.7k LOC) hand-rolls `ExistentialContainer0`…`8` with near-identical layout (payload words + metadata + N witness tables). Plus:

| Extra carrier | Role |
|---------------|------|
| `ClassExistentialContainer1` | 2-word class-bound stride (array element correctness) |
| `ExistentialContainerFactory` | Create / GetOrCreate / CreateOwned* mint-donate matrix |
| Dual **layout producers** for class-bound EC1 | Proxy → witness in Payload1; boxable → witness in `container[0]` — `FromExistentialContainer1` picks non-zero |

### Dual factories (intentional Mono/NativeAOT)

| Dual | Arms | Why both exist |
|------|------|----------------|
| Metadata resolve | Cache / typed factory → reflection → NativeAOT `RunClassConstructor` | Mono forbids static-virtual in shared generic; AOT needs registration |
| NewFromPayload | `NewFromPayloadDispatcher` → RunClassConstructor → reflection | Same |
| PayloadSemantics | Dispatcher cache → reflection backstop | Same |
| CreateAny\<T\> vs CreateAnyRuntime | Generic ISwiftObject vs erased object | Bare-`Any` collections |
| GetOrCreate overloads | Boxable vs convertible vs wrapFallback | Runtime ownership signal `ownsContainer` |
| CreateOwnedClassCarrier / CreateOwnedExistential1 | Mint vs donate | Array `__owned` consume + B2 weak proxy |
| String ops | Prefer SBW cdecl; historical CallConvSwift hazard | Mono |

### L4 simplification table

| ID | Opportunity | Risk class | Do not do if… |
|----|-------------|------------|---------------|
| W6-S1 | EC0–8 source-gen | needs fixture | Hand-edit word layout / SizeOf |
| W6-S2 | Shared `TypeKeyedRegistry` for 5 dispatchers | byte-identical careful | Register-once semantics diverge |
| W6-S3 | Async arity bags source-gen | behavior-preserving | Public InternalsVisibleTo consumers |
| W6-S4 | Collection metadata/dispose base helper | medium | P/Invoke surfaces differ |
| W6-S5 | Document mint/donate matrix only (no API merge) | docs | Collapsing ownsContainer signal |
| W6-S6 | Native metadata shim macro (`SBW_*_GetMetadata`) | low | Symbol names drift |
| W6-S7 | Unify finalizer trampoline **docs** only | docs | Merging raw/AnyObject/unknownObject entry points |
| W6-S8 | Do **not** unify Mono/NativeAOT dual factories | N/A | “Simplifying” reintroduces Mono static-virtual assert |

---

## 6. L3 — Graceful degradation (runtime lens)

**Product L3 lives in generator + SDK (G1/M2), not here.**

Runtime behaviors that look “soft” are **not** partial-binding UX:

| Behavior | Classification |
|----------|----------------|
| Skip Destroy when metadata missing | SafeHandle must-not-throw + test mocks |
| Closure GCHandle leak if native dylib absent | Packaging honesty (`IncludeSwiftBindingsRuntimeNative=false`) |
| RuntimeContract abort | Integrity fail-closed |
| Missing PayloadSemantics | Fail-closed on reflection miss (A3-014) |
| Process-exit intentional leak | Safer than deinit on torn-down Swift |

**L3 degrade-opportunity count for W6: 0.**

---

## 7. Native `SwiftBindingsRuntime` (deep spot)

**Sources**: `swift/SwiftBindingsRuntime.swift`, `SwiftBindingsRuntimeCollections.c`, `build-runtime.sh`  
**Ship**: framework slices in `native/SwiftBindingsRuntime.xcframework` (TN2435).

| Symbol group | Role | Mono relevance |
|--------------|------|----------------|
| Concurrency hook | `swift_task_enqueueGlobal_hook` → GCD concurrent | Async from C# works |
| Existential metadata | N∈0..8 + AnyError | Correct WT slot layout |
| String SBW_* | Cdecl string ops | Avoid CallConvSwift ToString/Length |
| VWT/ARC trampolines | `SBW_VWTDestroy`, `SBW_SwiftRelease`, Raw, UnknownObject, Bulk | Finalizer-safe |
| Closure context | New/destroy/unbox | GCHandle free from Swift deinit |
| Metadata shims | CG*, UUID, Date, simd, Measurement, Token, … | Unexported / unsafe CallConv |
| Collections C | Six SBW_Dict/Set/Array | Catalyst-x64 |
| Set specialize | Int64/Int/String Insert | Mono tuple-return |
| KeyPath / SwiftUI Text | Thin helpers | — |

**Known native limits** (documented, not defects): no MainActor hook; cancel does not propagate through GCD; custom actors not intercepted.

---

## 8. Async helpers residual

| Item | Status |
|------|--------|
| `SwiftAsyncCallHolder` typed fields (Finding 16) | Sound; Cleanup idempotent |
| Self retain = UnknownObject* | Issue #40 closed |
| DeferredSafeHandleRelease DangerousAddRef | Mono SafeHandle async mitigation |
| ExistentialContainerHeap OwnsContainer | DestroyAndFree gated |
| `NextCancelKey` ≠ GCHandle cookie | Collision class closed |
| `CompleteWithResult` Free-only | POD/string matrix only (A3-013 / A7-015) |
| `FailFastNonThrowing` | Correct for no error channel |

---

## 9. Findings index (W6)

### Confirmed new open defects

*(None.)*

### New candidates / confirmed-by-design notes

| ID | Title | Sev | Status |
|----|-------|-----|--------|
| DA-W6-001 | Fail-soft Destroy intentional SafeHandle contract | P2 latent | already-known extension |
| DA-W6-002 | ParseEpoch garbage → always-compatible | P3 | confirmed by design |
| DA-W6-003 | Floor hand-set / 1.0 product decision | P2 product | already-known |
| DA-W6-004 | Set.insert generic fallback Mono-broken | P2 | already-known |

### Already-known (re-tag only)

| Source | Topics |
|--------|--------|
| A3 | Mono finalizer, SafeHandle async, issue #40, B2, carrier +1, nested escaping, CompleteWithResult, PayloadSemantics miss, closure dylib leak |
| A7 | Async cancel mapping candidates (emitter-side); CompleteWithResult Free; reverse GCHandle not per-invoke free |
| UP-01..04 | Exactly four Mono upstream (+ SafeHandle comment) — **do not invent 5th** |

### Refuted this wave

| Topic | Why |
|-------|-----|
| New emission-live double-free class | Ownership matrix + mint/donate + holder Cleanup hold |
| RuntimeContract fails open for *too-new* bindings | Forward direction throws |
| Process-exit skip = bug | Intentional SwiftExitGuard |
| Dual factories accidental | Documented Mono static-virtual / AOT reflection |
| Collection cdecl path incomplete for reads | By design — only six mutators need it |
| DestroyAndFreeExistential ignores owns | Owns gate present |

---

## 10. File checklist

Status legend: `reviewed-deep` | `reviewed` | `hazard` | `inventory` | `deferred-known`.

### 10.1 `src/Swift.Runtime/src/Swift/Runtime/`

| File | Status | Notes |
|------|--------|-------|
| `Arc.cs` | **reviewed-deep** | Retain/release; UnknownObject; trampolines; BulkArc |
| `AsyncClosureHelper.cs` | **reviewed-deep** | CompleteWithResult Free residual |
| `AsyncClosureState.cs` | reviewed | Arity bags L4 |
| `AsyncHelpers.cs` | **reviewed-deep** | Holder Cleanup; cancel key; existential heap |
| `AsyncThrowingClosureState.cs` | reviewed | Arity bags L4 |
| `ClosureHandle.cs` | reviewed | Escaping vs non-escaping policy |
| `ComparableConformanceRegistry.cs` | reviewed | Cache |
| `EveryProtocol.cs` | **reviewed-deep** | No process-global metadata; module-local |
| `ExistentialContainer.cs` | **hazard** / reviewed-deep | EC0–8 + factory dual paths + mint/donate |
| `ExistentialUnion.cs` | reviewed | Multi-protocol helper |
| `HashableConformanceRegistry.cs` | reviewed | Cache |
| `IExistentialBoxable.cs` | inventory | Interface |
| `IExistentialContainer.cs` | reviewed | Layout contract |
| `IProtocolProxyImpl.cs` | inventory | Marker |
| `ISwiftExistentialConvertible.cs` | inventory | Interface |
| `ISwiftObject.cs` | **reviewed-deep** | Payload semantics + SuppressPayloadFinalizer |
| `ISwiftStruct.cs` | inventory | Marker |
| `ITypeMetadataCache.cs` | inventory | Interface |
| `KnownLibraries.cs` | inventory | Constants |
| `MainActorGuard.cs` | reviewed | Guard |
| `ObjCInterop.cs` | reviewed | Thin helpers |
| `PayloadConstructionSemantics.cs` | **reviewed-deep** | Four-way ownership enum |
| `ProtocolConformanceDescriptor.cs` | reviewed | ABI |
| `ProtocolDescriptor.cs` | reviewed | ABI |
| `ProtocolWitnessTable.cs` | reviewed | WT resolve |
| `ProxyLifetimeTracker.cs` | **reviewed-deep** | Design B2 |
| `RuntimeContract.cs` | **reviewed-deep** | Floor 16; window; ParseEpoch |
| `StringAsyncClosureHelper.cs` | reviewed | String async path |
| `SwiftClassHandle.cs` | **reviewed-deep** | Finalizer trampoline + exit |
| `SwiftClosure.cs` | **reviewed-deep** | Escape ownership |
| `SwiftClosureContext.cs` | **reviewed-deep** | Dylib-missing leak path |
| `SwiftCollectionCdeclWrappers.cs` | **hazard** | Mono dual-path hub |
| `SwiftConcurrency.cs` | reviewed | Init concurrency |
| `SwiftConformance.cs` | reviewed | Conformance lookup |
| `SwiftDispose.cs` | reviewed | Helpers |
| `SwiftDisposeScope.cs` | reviewed | Scope + Detach for proxy cache |
| `SwiftDisposeScopeExtensions.cs` | inventory | Extensions |
| `SwiftException.cs` | **reviewed-deep** | Error box finalizer |
| `SwiftExitGuard.cs` | **reviewed-deep** | Process exit dual signal |
| `SwiftFrameworkResolver.cs` | **reviewed-deep** | Module init registration hub |
| `SwiftHandle.cs` | **reviewed-deep** | SafeHandle + MarkConsumed + fail-soft meta |
| `SwiftLeakCensus.cs` | reviewed | Diagnostics |
| `SwiftMainActorAttribute.cs` | inventory | Attribute |
| `SwiftMetadata.cs` | reviewed | Helpers |
| `SwiftObjectRegistry.cs` | **reviewed-deep** | Weak proxy registry |
| `SwiftRuntimeException.cs` | inventory | Base exception |
| `SwiftRuntimeInfo.cs` | **hazard** | Mono vs NativeAOT classification |
| `SymbolicReferenceGrammar.cs` | reviewed | Symbolic ref |
| `TypeMetadata.cs` | **hazard** / reviewed-deep | Cache-first + dual resolve |
| `TypeMetadataCache.cs` | reviewed | Cache impl |
| `UnownedSerialExecutor.cs` | reviewed | Executor ABI |
| `ValueWitnessTable.cs` | **reviewed-deep** | VWT layout |
| `WeakSwiftReference.cs` | reviewed | Unowned/weak |
| `InteropServices/SwiftMarshal.cs` | **hazard** / reviewed-deep | Central seam ~2k LOC |
| `Marshalling/BlittableOptionalInt32.cs` | inventory | Blittable |
| `Marshalling/BlittableSwiftString.cs` | inventory | Blittable |
| `Marshalling/SwiftOptionalInt32Marshaller.cs` | reviewed | Marshaler |
| `Marshalling/SwiftStringMarshaller.cs` | reviewed | Marshaler |
| `Marshalling/Utf8Slice.cs` | reviewed | Utf8 bridge |

### 10.2 Consumer types `src/Swift.Runtime/src/Swift/*.cs`

| File | Status | Notes |
|------|--------|-------|
| `SwiftArray.cs` | **hazard** / reviewed-deep | Copy; cdecl remove; Mono .cctor skip |
| `SwiftArrayProjection.cs` | reviewed | Projection |
| `SwiftDictionary.cs` | **hazard** / reviewed-deep | cdecl mutators; iterator retain |
| `SwiftDictionaryProjection.cs` | reviewed | Projection |
| `SwiftSet.cs` | **hazard** / reviewed-deep | Insert dual path + cdecl |
| `SwiftString.cs` | **reviewed-deep** | Move; SBW cdecl string |
| `SwiftOptional.cs` | **reviewed-deep** | Copy; tag/EI |
| `SwiftResult.cs` | **reviewed-deep** | Copy; extract |
| `SwiftClosedRange.cs` | reviewed | Copy |
| `SwiftAsyncStream.cs` | **reviewed-deep** | ExtractCopiedValue elements |
| `SwiftKeyPath.cs` | reviewed | Adopt family |
| `AnyHashable.cs` | reviewed | Inline |
| `AnyType.cs` | reviewed | Inline |
| `Hasher.cs` | reviewed | Adopt |
| `DispatchQueue.cs` | reviewed | Adopt |
| `CGPoint.cs` / `CGRect.cs` / `CGSize.cs` | reviewed | Geometry stubs |
| `SwiftColor.cs` / `SwiftFont.cs` | reviewed | Thin |
| `SwiftEquatable.cs` / `SwiftHashable.cs` | reviewed | Conformance helpers |
| `SwiftErrorException.cs` | inventory | Exception |
| `SwiftVoid.cs` | inventory | Void |
| `UnsafePointer.cs` / `UnsafeBufferPointer.cs` | reviewed | Pointer wrappers |
| `ISwiftEncoder.cs` | inventory | Interface |
| `KeyValueObserving.cs` | inventory | KVO helpers |
| `RuntimeLimitations.cs` | reviewed | Documented limits surface |
| `*Attribute.cs` / `SwiftSendableAttribute.cs` | inventory | Attributes |
| `SwiftUI/*` (7 files) | inventory / reviewed | Stubs; PayloadSemantics registered Adopt |
| `*Database.xml` (all) | inventory | Generator TypeDB shipped with Runtime |
| `Util/DynamicLibraryLoader.cs` | reviewed | Loader helper |
| `build/SwiftBindings.Runtime.targets` | reviewed | NativeReference + IsNativeAot switch |
| `ILLink.Descriptors.xml` | inventory | Trim roots |
| `Swift.Runtime.csproj` | inventory | Package / BuildVersion gen |

### 10.3 Native `src/Swift.Runtime/swift/`

| File | Status | Notes |
|------|--------|-------|
| `SwiftBindingsRuntime.swift` | **reviewed-deep** | Concurrency, trampolines, string, Set insert, metadata |
| `SwiftBindingsRuntimeCollections.c` | **reviewed-deep** | Six collection cdecl wrappers |
| `build-runtime.sh` | reviewed | xcframework build |

### 10.4 Tests `src/Swift.Runtime/tests/` (coverage honesty)

| Area | Files | Honesty note |
|------|-------|--------------|
| RuntimeContract | `RuntimeContractTests.cs` | **Strong** — pure window + ParseEpoch + floor guard; real abort only on release Version≠0 |
| Arc / handles | `ArcTests`, `SwiftClassHandleTests`, `BorrowedMarshalFinalizerTests`, `DisposeSafetyTests` | Strong unit + finalizer paths |
| Payload / dispatch | `PayloadSemanticsDispatchTests`, `CacheFirstDispatchTests` | Pins dispatcher contract |
| Existential | `ExistentialContainerFactoryTests`, `ExistentialMetadataWrapperTests`, `ExistentialUnionTests` | Strong factory; not full mint/donate BindingTests substitute |
| TypeMetadata | `TypeMetadataTests`, `KnownMetadataTests`, `ValueWitnessFlagsTests` | Solid |
| Proxy / registry | `ProxyLifetimeTrackerTests`, `SwiftObjectRegistryTests` | B2 unit coverage |
| Collections | `SwiftArray*`, `SwiftDictionary*`, `SwiftSet*`, `SwiftOptional*`, `SwiftString*` | Library projection tests; **Catalyst-x64 dual path is BindingTests/device reality**, not fully proven in host unit tests alone |
| Async | `SwiftAsyncCallHolderTests`, `AsyncResumeGuardTests`, `SwiftAsyncCancellationTests` | Holder/cancel unit; e2e async is BindingTests |
| Closures | `ClosureHandleTests` | Policy unit |
| Resolver / info | `SwiftFrameworkResolverTests`, `SwiftRuntimeInfoClassificationTests` | Classification honesty good |
| AOT annotations | `AotAnnotationTests` | Structural (attributes present) — **not** device AOT proof |
| Limitations | `RuntimeLimitationsTests` | Documents limits |

**Honesty headline**: Runtime unit tests are **above average for a interop library** on contract/ownership/dispatch. They **cannot** replace BindingTests for CallConvSwift Mono vs NativeAOT or Catalyst-x64 sret. No theater finding at Runtime unit layer comparable to generator mega `Assert.Contains` tests (Track T).

---

## 11. Ledger update guidance

When merging into `00-file-coverage-ledger.md`:

- Promote all **reviewed-deep** / **reviewed** / **hazard** rows above from `inventory`.
- Leave `*Database.xml` and pure attributes as `inventory` (no executable L1).
- A3 list of 28 reviewed-deep **subsumed** into this broader checklist.

---

## 12. Recommended backlog (owner-gated; do not implement in audit)

1. **Release ritual**: on every minor, decide whether to raise `MinimumSupportedGeneratedVersion` (DA-W6-003 / CLAUDE.md).  
2. **Optional**: release-only hard fail if `ParseEpoch` returns 0 (DA-W6-002).  
3. **Probe**: finalizer-only dispose of real Copy type with forced metadata miss (tighten A3-012).  
4. **Probe**: tighten returned-throwing-closure leak to live==0 (A3-011).  
5. **Decide**: nested escaping box Known Limitation vs free protocol (A3-010).  
6. **L4 (W10)**: EC0–8 codegen + TypeKeyedRegistry — after fixtures.  
7. **Do not**: invent 5th Mono upstream issue; unify Mono/NativeAOT dual factories; throw from ReleaseHandle.

---

## 13. Risk summary

| Dimension | Rating | Notes |
|-----------|--------|-------|
| Crash / double-free residual | **Low** | Gated; residual intentional |
| Silent leak residual | **Low–Medium** | Nested escaping; metadata-zero finalizer latent; process-exit intentional |
| Mono dual-path complexity | **Medium** | Correct but L5 hazard if “simplified” |
| RuntimeContract integrity | **Low** | Floor + window solid; product floor discipline remains human |
| L3 partial-binding | **N/A** | Not runtime’s job |
| L4 complexity | **Medium** | EC0–8 + dual factories worth W10 inventory |
| **Overall** | **~2/5** | Line-complete; no new P0 |

---

## 14. 5-bullet executive summary

1. **Every managed Runtime file is at least inventory→reviewed; ~45 reviewed-deep; ~12 hazard-documented dual-path files.**  
2. **Zero new emission-live P0 double-free/UAF** — A3 ownership map re-confirmed.  
3. **Fail-soft finalizer + metadata-zero skip Destroy** remains the primary latent L1 residual (intentional SafeHandle contract).  
4. **RuntimeContract floor 16 is sound**; epoch-0 and ParseEpoch-garbage are intentional fail-soft; **raising the floor is still a human release decision**.  
5. **Collection Mono dual paths and EC0–8 dual factories are intentional complexity (L4), not accidental dual oracles** — do not “unify” without Mono fixtures.

---

*End of Track W6 Runtime Line-Complete.*
