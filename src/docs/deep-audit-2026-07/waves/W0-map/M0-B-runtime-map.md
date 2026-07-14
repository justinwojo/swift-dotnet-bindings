# M0-B — Runtime map

**Wave**: 0 (map)  
**Scope**: `src/Swift.Runtime/src/`, `src/Swift.Runtime/swift/`, `src/Swift.Runtime/tests/`  
**Mode**: Read-only inventory for deep-audit 2026-07  
**Date**: 2026-07-15  

---

## 0. Layout overview

| Area | Path | Role |
|------|------|------|
| Managed runtime | `src/Swift.Runtime/src/Swift/` + `…/Runtime/` | ISwiftObject surface, collections, marshal seam, ARC/SafeHandle, existentials, async/closures |
| Type DB XML | `src/Swift.Runtime/src/Swift/*Database.xml` | Generator type knowledge for Apple/stdlib modules (shipped with Runtime) |
| Native framework | `src/Swift.Runtime/swift/` → `native/SwiftBindingsRuntime.xcframework` | Cdecl trampolines, concurrency hook, string/VWT helpers, collection sret bypass |
| Package targets | `src/Swift.Runtime/src/build/SwiftBindings.Runtime.targets` | `NativeReference` embed + runtime-flavor AppContext switch |
| Unit tests | `src/Swift.Runtime/tests/{LibraryTests,MetadataTests}/` | Library projection + metadata/ARC/dispatch unit coverage |

Namespace split:

- **`Swift`** — consumer-facing value types (`SwiftString`, `SwiftArray<>`, `SwiftOptional<>`, …)
- **`Swift.Runtime`** — interop machinery (Arc, handles, TypeMetadata, VWT, closures, EveryProtocol)
- **`Swift.Runtime.InteropServices`** — `SwiftMarshal` + dispatcher registries

---

## 1. Ownership / ARC model

### 1.1 Core contracts

| Concept | Type / API | Contract |
|---------|------------|----------|
| Class ARC | `Arc` (`Runtime/Arc.cs`) | `swift_retain` / `swift_release`; retain leaf `[SuppressGCTransition]`; **release is not** (deinit may re-enter managed) |
| Class-bound existential ARC | `Arc.UnknownObjectRetain/Release` | `swift_unknownObjectRetain/Release` — isa-dispatches Swift vs ObjC |
| Finalizer-safe class release | `Arc.UnknownObjectReleaseFinalizerSafe` → `SBW_SwiftUnknownObjectRelease` | Avoids Mono `!ji->async` on GC finalizer after CallConvSwift contamination |
| Class handle | `SwiftClassHandle<T>` | Handle **is** retained Swift object ptr; `ReleaseHandle` → Arc or trampoline |
| Value / buffer handle | `SwiftSafeHandle<T>` | Owns `NativeMemory` buffer; `ReleaseHandle` → VWT Destroy (+ free); caches metadata for finalizer path |
| Consuming move | `SwiftSafeHandle.MarkConsumed` | Skips VWT Destroy after Swift `consuming` param took ownership |
| Process exit | `SwiftExitGuard` | Finalizer releases skipped during exit; explicit `Dispose` still runs (deinit side effects) |
| Payload ownership enum | `PayloadConstructionSemantics` | `Adopt` / `Copy` / `Move` / `Inline` — single declared truth for wire-buffer cleanup |
| Borrowed marshal | `ISwiftObject.SuppressPayloadFinalizer` | Prevents finalizer over-release of +0 borrowed payloads |

### 1.2 Handle duality (load-bearing)

```
Swift class instance
  └─ SwiftClassHandle<T>  ── Arc.Release / SwiftReleaseTrampoline
                             (no intermediate buffer)

Swift value / COW storage / non-frozen struct buffer
  └─ SwiftSafeHandle<T>   ── VWT Destroy (direct or SBW_VWTDestroy) + NativeMemory.Free
```

**Finalizer vs Dispose** (both handle families):

- Explicit `Dispose` → full cleanup even during process exit (`_explicitDispose`).
- Finalizer during exit → skip native release (runtime may be torn down).
- Finalizer otherwise → trampoline path (Cdecl) so Mono does not JIT from the finalizer thread.

Evidence: `SwiftClassHandle.cs:96–137`, `SwiftHandle.cs:67–196`, `Arc.cs:10–168`.

### 1.3 PayloadConstructionSemantics (marshal cleanup axis)

Declared on every `ISwiftObject` as `static abstract PayloadConstructionSemantics`; registered at module-init into `PayloadSemanticsDispatcher` (never static-virtual from shared generics — Mono assertion).

| Semantics | Meaning | Typical types |
|-----------|---------|---------------|
| **Adopt** | Wrapper’s SafeHandle takes wire buffer + its +1 | Non-frozen structs, complex enums, EveryProtocol, KeyPath family, SwiftUI value wrappers |
| **Copy** | Wrapper `InitializeWithCopy`s into own buffer; cleanup Destroy+free temp | `SwiftArray`/`Dictionary`/`Set`/`Optional`/`Result`/`ClosedRange`, frozen-with-ref structs |
| **Move** | Bitwise transfer of +1 into wrapper buffer; cleanup free only | **`SwiftString` only** |
| **Inline** | `*(T*)handle` by value; free temp only | Frozen blittable structs, `AnyHashable`/`AnyType` as value types |

Registration site (Runtime open generics + known types): `SwiftFrameworkResolver.InitializeRuntime` (`SwiftFrameworkResolver.cs:57–90`). Generated bindings register concrete types from their own `[ModuleInitializer]`.

### 1.4 Related helpers

- `SwiftDisposeScope` / extensions — batch dispose of temporary Swift wrappers.
- `ProxyLifetimeTracker` / `SwiftLeakCensus` — diagnostic lifetime tracking.
- `WeakSwiftReference<T>` — unowned/weak class refs.
- `BulkArc` + `SBW_BulkRetain/BulkRelease` — N retains/releases in one native transition.
- `PayloadBuffer<T>` / `SafeHandlePin` — pin-and-read buffer without transfer.

### 1.5 Later L1 audit focus

- Any new `ISwiftObject` without literal `RegisterPayloadSemantics` → reflection backstop may miss on NativeAOT.
- Finalizer trampoline absence on older native frameworks (swallowed exceptions in release paths).
- Class vs value handle misuse in generator emit (wrong SafeHandle family).

---

## 2. Existential containers, VWT, SwiftMarshal

### 2.1 Existential containers

`ExistentialContainer.cs` defines fixed-layout structs:

| Type | Witness tables | Role |
|------|----------------|------|
| `ExistentialContainer0` | 0 | `Any` |
| `ExistentialContainer1`…`8` | 1–8 | `any P` compositions |
| `ClassExistentialContainer1` | 1 | Class-bound existential (pointer + WT) |
| `ExistentialContainerFactory` | — | Box via `swift_allocBox` when payload > 24 bytes (3 words) |

Interfaces:

- `IExistentialContainer` — common layout accessors  
- `IExistentialBoxable` / `ISwiftExistentialConvertible<T>` — type-driven box/unbox  
- `ExistentialUnion` — multi-protocol union helper  

Native support: `SwiftBindings_GetExistentialTypeMetadata(n)` builds `(any _EP0 & …)` metadata for N∈0..8; `SBW_AnyError_*` for `any Error`.

### 2.2 Value witness table

`ValueWitnessTable` (`Runtime/ValueWitnessTable.cs`) — sequential layout of Swift VWT function pointers:

- `InitializeBufferWithCopyOfBuffer`, `Destroy`, `InitializeWithCopy`, `AssignWithCopy`, `InitializeWithTake`, `AssignWithTake`
- Single-payload enum: `GetEnumTagSinglePayload` / `StoreEnumTagSinglePayload`
- Multi-payload enum: `GetEnumTag`, destructive inject, …
- Flags: `IsNonPOD`, `IsNonInline`, `IsNonBitwiseTakable`, …

Accessed as `metadata.ValueWitnessTable->…` from marshal paths. Finalizer-safe destroy: `SwiftMarshal.DestroyWireBufferRetainsFinalizerSafe` → `SBW_VWTDestroy` (reads VWT from `metadata[-1]` in native code).

### 2.3 TypeMetadata

`TypeMetadata.cs` (~1.1k+ LOC) — kind/flags, size/stride, VWT access, cache (`TypeMetadataCache` / `ITypeMetadataCache`), tuple metadata, resolution:

- Symbol load from dylibs  
- Cache-first via `TypeMetadataDispatcher` (module-init factories)  
- Reflection last resort (`GetTypeMetadataOrThrow`) for Mono-safe shared-generic contexts  

Related: `ProtocolDescriptor`, `ProtocolConformanceDescriptor`, `ProtocolWitnessTable`, `SwiftConformance`, registries for Hashable/Comparable.

### 2.4 SwiftMarshal entry points (central seam)

`InteropServices/SwiftMarshal.cs` is the **largest** managed runtime surface (~1.8k+ LOC). Roles:

| Family | APIs | Purpose |
|--------|------|---------|
| Registration | `RegisterSwiftObjectFactory`, `RegisterPayloadSemantics`, conformance/WT registrars | Module-init population of dispatchers |
| Primary marshal-in | `MarshalFromSwift<T>`, `MarshalFromSwiftObject<T>`, `MarshalFromSwiftObjectConsuming` | Wire → managed |
| Slot extract | `MarshalMovedValueFromSlot`, `MarshalBorrowedValueFromSlot`, `ExtractCopiedValue`, `MarshalExtractedPayloadValue` | Optional/Result/Dict/Set/stream element ownership |
| Callback borrow | `MarshalCallbackArg<T>` | +0 borrowed args; suppress finalizer for Adopt/Move |
| Wire cleanup | `DestroyWireBufferRetains`, `CopyWireBufferRetains`, finalizer-safe variant | Balance after copy-out |
| Size | `GetSwiftTypeSize<T>` | Indirect-result buffer allocation |

**Dispatchers** (all `ConcurrentDictionary`, register-once):

1. `NewFromPayloadDispatcher`  
2. `TypeMetadataDispatcher`  
3. `ConformanceDispatcher`  
4. `WitnessTableDispatcher`  
5. `PayloadSemanticsDispatcher`  

### 2.5 EveryProtocol / reverse dispatch support

`EveryProtocol` is the concrete Swift class behind C# protocol proxies (`SwiftClassHandle`). Metadata is **per-binding-module** (not process-global); `GetTypeMetadata()` throws by design. Registry: `SwiftObjectRegistry` maps Swift ptr ↔ C# proxy for reverse vtable calls.

---

## 3. Collections (Array / Dict / Set / Optional / String)

### 3.1 Type map

| Managed type | Payload semantics | Interfaces | Notes |
|--------------|-------------------|------------|-------|
| `SwiftString` | **Move** | `ISwiftObject`, `ISwiftStruct` | 16-byte buffer; ToString/Length via `SBW_SwiftString_*` (Cdecl) to avoid CallConvSwift Mono crash |
| `SwiftArray<Element>` | **Copy** | `IReadOnlyList`/`IList` | Lazy element metadata; NativeAOT eager init gated |
| `SwiftDictionary<K,V>` | **Copy** | `IReadOnlyDictionary` | Needs Hashable WT for K |
| `SwiftSet<T>` | **Copy** | set ops | Hashable element |
| `SwiftOptional<T>` | **Copy** | Some/None | Tag-byte vs extra-inhabitant paths |
| `SwiftResult<S,F>` | **Copy** | success/failure extract | Uses marshal extract helpers |
| `SwiftClosedRange<T>` | **Copy** | range | |
| Projections | `SwiftArrayProjection`, `SwiftDictionaryProjection` | managed ↔ Swift collection bridges | |

### 3.2 Collection ABI paths

1. **Direct CallConvSwift** — most reads (`subscript`, `count`, `contains`, append shapes that don’t hit the Catalyst bug).  
2. **Cdecl collection wrappers** — `SwiftCollectionCdeclWrappers` ↔ `SwiftBindingsRuntimeCollections.c` for **six** mutating ops whose shape is `sret + intermediate ints + SwiftSelf` (Catalyst-x64 Mono trampoline corrupts `self`). Ops: Dict update/remove/iterator next, Set insert/remove/iterator next.  
3. **Specialized Set insert** — `SBW_SetInt64_Insert`, `SBW_SetInt_Insert`, `SBW_SetString_Insert` in Swift runtime (additional CallConvSwift bypasses).

### 3.3 Optional layout

`SwiftOptional.GetTagByteOffset()`:

- Blittable primitive fast path  
- Else `Optional.Size > T.Size` → tag at `T.Size`  
- Else extra-inhabitant (class/string) → VWT tag APIs  

Buffer alloc always ≥ machine word to avoid over-read of sub-word optionals via `PayloadBuffer<IntPtr>`.

---

## 4. Async / closure helpers

### 4.1 Closures (sync)

| Type | Role |
|------|------|
| `ClosureHandle` + `ClosureHandlePolicy` | Escaping (Swift owns GCHandle via `_SBClosureCtx`) vs NonEscaping (finally free) |
| `SwiftClosure` / `SwiftClosureData` / `SwiftClosureMarshaller` / `SwiftClosureFactory` / `SwiftEscapingClosure<TDelegate>` | Marshal managed delegates to Swift closure ABI |
| `SwiftClosureContext` | Registers free trampoline; Swift deinit of context box upcalls C# |

Native: `SwiftBindings_NewClosureContext`, `SwiftBindings_SetClosureContextDestroyCallback`, `SBW_UnboxClosureContext`.

### 4.2 Async (Swift → C# Task)

| Type | Role |
|------|------|
| `AsyncHelpers` | `RetainedSelfPtr`, `DeferredSafeHandleRelease`, `CopyBufferWithType`, `AsyncDeferredDisposeList`, `ExistentialContainerHeap`, `SwiftAsyncCallHolder` lifetime tokens |
| `AsyncClosureHelper` + arity state classes | Run `Task` work; marshal result to Swift continuation; **does not** free GCHandle per-invocation (box owns it) |
| `AsyncThrowingClosureState*` / `AsyncClosureState*` | Arity 0–4 state bags |
| `StringAsyncClosureHelper` | String-specialized async path |
| `AsyncResumeGuard` | Resume-once guard |
| `SwiftAsyncStream` | Stream element borrow → `ExtractCopiedValue` |
| `SwiftConcurrency` + native hook | `SwiftBindings_InitializeConcurrency` installs `swift_task_enqueueGlobal_hook` → GCD concurrent executor |

**Known concurrency limits** (documented in native Swift):

- `@MainActor` not hooked (`swift_task_enqueueMainExecutor_hook` buggy historically)  
- Cancellation does not propagate through GCD  
- Custom actor executors not intercepted  

Managed: `MainActorGuard`, `SwiftMainActorAttribute`, `UnownedSerialExecutor`.

---

## 5. Native SwiftBindingsRuntime responsibilities

**Sources**: `swift/SwiftBindingsRuntime.swift` + `SwiftBindingsRuntimeCollections.c`  
**Build**: `swift/build-runtime.sh` → `native/SwiftBindingsRuntime.xcframework`  
**Ship shape**: Flat framework slices (TN2435); `install_name` `@rpath/SwiftBindingsRuntime.framework/SwiftBindingsRuntime`

### 5.1 Symbol groups (`@_cdecl`)

| Group | Examples | Why native |
|-------|----------|------------|
| Concurrency | `SwiftBindings_InitializeConcurrency` | Hook global enqueue to GCD |
| Existential meta | `SwiftBindings_GetExistentialTypeMetadata`, `SBW_AnyError_*` | Correct existential metadata without raw ProtocolDescriptorRef |
| String | `SBW_SwiftString_{ToUtf8,GetCount,Create,Destroy,FreeUtf8,GetMetadata}` | Cdecl avoids CallConvSwift Mono crash |
| VWT / ARC trampolines | `SBW_VWTDestroy`, `SBW_SwiftRelease`, `SBW_SwiftReleaseRaw`, `SBW_SwiftUnknownObjectRelease`, `SBW_BulkRetain/Release` | Finalizer-safe / bulk |
| Closures | context create/unbox/destroy callback | ARC box ownership of GCHandle |
| Metadata shims | CGPoint/CGRect/CGSize, UUID/Date/Decimal, simd_*, Measurement, Token, ManagedSettings marker | Unexported or CallConvSwift-unsafe metadata accessors |
| Collections (C) | `SBW_Dict_*`, `SBW_Set_*` | Catalyst-x64 sret+self trampoline bug |
| Set specialize | `SBW_Set{Int64,Int,String}_Insert` | Mono DoneBlocking / CallConvSwift issues |
| KeyPath | `SBW_AnyKeyPath_Equals/HashValue` | |
| SwiftUI minimal | `SBW_SwiftUI_Text_Create/Destroy` | Thin helpers |

### 5.2 Consumer delivery

`SwiftBindings.Runtime.targets`:

1. Conditionally injects `NativeReference` to the xcframework (Apple TFMs only; opt-out `IncludeSwiftBindingsRuntimeNative=false` for harness apps that inject themselves).  
2. Injects `AppContext` switch `Swift.Runtime.IsNativeAot` from `SwiftBindingsInteropMode` / `PublishAot` so Mono device full-AOT is not misclassified as NativeAOT.

Resolver: `SwiftFrameworkResolver` maps `DllImport("SwiftBindingsRuntime")` → `@rpath/SwiftBindingsRuntime.framework/…` with ALC fallback.

---

## 6. Module init / RuntimeContract

### 6.1 Load sequence (typical app)

```
Assembly load
  ├─ Swift.Runtime [ModuleInitializer] InitializeRuntime
  │    ├─ SwiftExitGuard
  │    ├─ DllImport resolver + ALC fallback
  │    ├─ SwiftClosureContext.EnsureRegistered
  │    ├─ RegisterSwiftObjectFactory for Runtime types
  │    └─ RegisterPayloadSemantics for Runtime open generics + known types
  │
  └─ Generated binding [ModuleInitializer]
       ├─ RuntimeContract.AssertCompatible(emittedEpoch)   // HARD fail if outside window
       ├─ RegisterSwiftObjectFactory / PayloadSemantics / Witness / Conformance (best-effort try/catch)
       └─ SwiftFrameworkResolver.RegisterForAssembly(this)
```

### 6.2 RuntimeContract (`Runtime/RuntimeContract.cs`)

| Field | Meaning |
|-------|---------|
| `Version` | Runtime epoch = `major*1000 + minor` from package version; **0** for `0.0.0-dev` |
| `MinimumSupportedGeneratedVersion` | Floor = **16** (0.16 — payload-construction-semantics contract) |
| `AssertCompatible(n)` | Accept if either side is 0, else require `floor ≤ n ≤ Version` |

**Fail modes**:

- Binding newer than runtime → missing registration APIs / wrong dispatch  
- Binding older than floor → cannot supply `PayloadConstructionSemantics`  

**Bump discipline**: floor is the only hand-set value; raise only on real dispatch-contract breaks at minor boundaries. Unit tests lock floor↔minor and epoch parser lockstep with generator.

Exception type: `SwiftRuntimeContractMismatchException` (uncatchable from module init → app-wide abort — intentional for integrity).

---

## 7. Complexity heatmap

### 7.1 Largest / densest managed files (approx LOC from inventory)

| File | ~LOC | Coupling / why large |
|------|------|----------------------|
| `Runtime/InteropServices/SwiftMarshal.cs` | **~1.8k+** | Central marshal seam; all ownership shapes; tuple/element extract; callback borrow |
| `Runtime/TypeMetadata.cs` | **~1.1k+** | ABI metadata model + resolution + cache + tuples |
| `Runtime/ExistentialContainer.cs` | **~0.9k+** | EC0–EC8 + factory (repetitive layout structs) |
| `Swift/SwiftArray.cs` | large | Full IList + P/Invoke surface + NativeAOT init |
| `Swift/SwiftDictionary.cs` | large | Hashable WT + iterator + cdecl wrappers |
| `Swift/SwiftSet.cs` | large | Same pattern as Dict |
| `Swift/SwiftOptional.cs` | medium-large | Tag/extra-inhabitant + extract |
| `Swift/SwiftString.cs` | medium | Dual metadata path + Move semantics |
| `Runtime/AsyncClosureHelper.cs` | **~0.5k+** | Arity explosion (0–4 × void/result × throwing) |
| `Runtime/AsyncThrowingClosureState.cs` + `AsyncClosureState.cs` | repetitive | Parallel arity bags |
| `Runtime/Arc.cs` | **~0.4k+** | Retain/release + trampolines + bulk |
| `Runtime/SwiftHandle.cs` | medium | SafeHandle + VWT trampoline + pin helpers |
| `Runtime/SwiftFrameworkResolver.cs` | medium | Resolver + module-init registration hub |
| `swift/SwiftBindingsRuntime.swift` | large | Many independent @_cdecl islands |
| `swift/SwiftBindingsRuntimeCollections.c` | medium | Six collection wrappers |

### 7.2 Coupling graph (conceptual)

```
Generated binding ──P/Invoke──► source framework + wrapper framework
       │
       ├─ ModuleInit ──► RuntimeContract + SwiftMarshal registrars
       │
       └─ call sites ──► SwiftMarshal / Arc / SafeHandles
                              │
                              ├─ TypeMetadata ──► VWT / ProtocolWitnessTable
                              ├─ ExistentialContainerFactory ──► swift_allocBox / EveryProtocol
                              ├─ Collections ──► CallConvSwift and/or SwiftBindingsRuntime
                              └─ Async/Closure ──► GCHandle + native context box + concurrency hook
```

**Highest fan-in**: `SwiftMarshal`, `TypeMetadata`, `Arc`, `SwiftFrameworkResolver`.  
**Highest fan-out from native**: `SwiftBindingsRuntime` (string, VWT, ARC, collections, metadata, concurrency).

### 7.3 Test coverage map (unit)

| Folder | Focus |
|--------|--------|
| `tests/MetadataTests/` | Arc, SafeHandle, Existential, TypeMetadata, ProtocolWitness, RuntimeContract, PayloadSemantics, FrameworkResolver, DisposeScope, ObjCInterop, … |
| `tests/LibraryTests/` | Array/Dict/Set/Optional/String projections, ClosureHandle, Async holders, Dispose safety |

**Note**: End-to-end ABI / Mono-vs-NativeAOT behavior lives in BindingTests (outside this map’s path but the real runtime gate).

---

## 8. Simplification candidates (L4 — light notes for later)

Status: **`simplification` / candidate** — not verified as safe consolidations yet.

| ID | Area | Observation | Suggested shape | Risk class |
|----|------|-------------|-----------------|------------|
| R-S1 | ExistentialContainer0–8 | Near-identical structs differ only by WT count | Source-gen or shared unsafe layout with N | Needs fixture; ABI layout sensitive |
| R-S2 | AsyncClosureState / AsyncThrowing* arity bags | 0–4 × void/result duplicates | Single generic arity model or source-gen | Behavior-preserving if API surface frozen via InternalsVisibleTo only |
| R-S3 | Collection types | Array/Dict/Set share COW + SafeHandle + lazy meta patterns | Shared base / helper for metadata + dispose | Medium — P/Invoke surfaces differ |
| R-S4 | String dual path | CallConvSwift vs SBW_SwiftString_* | Prefer single Cdecl path everywhere | Behavior-preserving; already partially done |
| R-S5 | Marshal extract APIs | Many siblings (`Moved`/`Borrowed`/`Extracted`/`CallbackArg`) | Document matrix is intentional; maybe façade table in docs only | Prefer **docs** over merge — divergence is ownership-correctness |
| R-S6 | Dispatcher quintet | Five ConcurrentDictionaries with same Register/TryGet shape | Shared `TypeKeyedRegistry<T>` | Byte-identical if careful |
| R-S7 | Native metadata shims | Long list of `SBW_*_GetMetadata` | Macro / code-gen for trivial `unsafeBitCast(T.self)` | Low risk |
| R-S8 | Dual Mono/NativeAOT branches | `SwiftObjectHelper`, collection .cctor, reflection vs static-virt | Already justified by Mono issues; do **not** unify blindly | High risk if “simplified” |

**Do not simplify without fixtures**:

- Finalizer trampoline split (`Arc.Release` vs `SwiftReleaseTrampoline`)  
- PayloadConstructionSemantics four-way cleanup  
- Escaping vs non-escaping `ClosureHandle` ownership transfer  

---

## 9. File inventory (Runtime managed core)

### 9.1 `src/Swift.Runtime/src/Swift/Runtime/`

| File | One-line role |
|------|----------------|
| `Arc.cs` | Swift ARC P/Invokes + finalizer trampolines |
| `AsyncClosureHelper.cs` | Async reverse-closure execution |
| `AsyncClosureState.cs` / `AsyncThrowingClosureState.cs` | Arity state bags |
| `AsyncHelpers.cs` | Async call lifetime tokens |
| `ClosureHandle.cs` | GCHandle lifetime policy wrapper |
| `ComparableConformanceRegistry.cs` / `HashableConformanceRegistry.cs` | Conformance caches |
| `EveryProtocol.cs` | Reverse-dispatch concrete class handle |
| `ExistentialContainer.cs` / `ExistentialUnion.cs` | Existential layouts + factory |
| `IExistentialBoxable.cs` / `IExistentialContainer.cs` / `ISwiftExistentialConvertible.cs` | Existential interfaces |
| `IProtocolProxyImpl.cs` | Proxy impl marker |
| `ISwiftObject.cs` / `ISwiftStruct.cs` | Core type contract + helpers |
| `ITypeMetadataCache.cs` / `TypeMetadataCache.cs` / `TypeMetadata.cs` | Metadata model + cache |
| `KnownLibraries.cs` | DllImport library name constants |
| `MainActorGuard.cs` / `SwiftMainActorAttribute.cs` | MainActor checks |
| `ObjCInterop.cs` | ObjC interop helpers |
| `PayloadConstructionSemantics.cs` | Ownership enum |
| `ProtocolConformanceDescriptor.cs` / `ProtocolDescriptor.cs` / `ProtocolWitnessTable.cs` | Protocol ABI |
| `ProxyLifetimeTracker.cs` | Proxy lifetime diagnostics |
| `RuntimeContract.cs` | Epoch handshake |
| `StringAsyncClosureHelper.cs` | String async closure path |
| `SwiftClassHandle.cs` / `SwiftHandle.cs` | SafeHandles |
| `SwiftClosure*.cs` / `SwiftClosureContext.cs` | Closure marshalling |
| `SwiftCollectionCdeclWrappers.cs` | Collection cdecl P/Invokes |
| `SwiftConcurrency.cs` | Managed concurrency init |
| `SwiftConformance.cs` | Conformance lookup |
| `SwiftDispose*.cs` / `SwiftExitGuard.cs` | Dispose scopes + exit |
| `SwiftException.cs` / `SwiftRuntimeException.cs` | Exception types |
| `SwiftFrameworkResolver.cs` | DllImport resolver + module init hub |
| `SwiftLeakCensus.cs` | Leak reporting |
| `SwiftMetadata.cs` | Metadata helpers |
| `SwiftObjectRegistry.cs` | Ptr ↔ proxy map |
| `SwiftRuntimeInfo.cs` | Mono vs NativeAOT classification |
| `SymbolicReferenceGrammar.cs` | Symbolic ref parsing |
| `UnownedSerialExecutor.cs` | Executor ABI |
| `ValueWitnessTable.cs` | VWT layout |
| `WeakSwiftReference.cs` | Weak refs |
| `InteropServices/SwiftMarshal.cs` | Central marshal seam |
| `Marshalling/*` | Utf8Slice, SwiftString/Optional marshallers |

### 9.2 Consumer types (`Swift/*.cs`)

Collections/string/result/optional as above; plus `AnyHashable`, `AnyType`, `Hasher`, `DispatchQueue`, CG geometry, KeyPath family, SwiftUI stubs (`SwiftUI/*`), XML type databases.

### 9.3 Tests

- `LibraryTests/*` — collection/string/async/closure dispose  
- `MetadataTests/*` — ARC, contract, metadata, existential, SafeHandle, resolver  

---

## 10. Pointers for later waves

| Wave / track | Runtime entry points |
|--------------|----------------------|
| L1 ABI / lifetime | `SwiftMarshal` extract matrix, SafeHandle ReleaseHandle, PayloadConstructionSemantics |
| Mono finalizer / CallConvSwift | `Arc` trampolines, `SBW_VWTDestroy`, collection cdecl wrappers |
| Async | `AsyncHelpers` ownership list, concurrency hook limits |
| Integrity | `RuntimeContract` floor discipline, dispatcher registration completeness |
| L4 simplification | §8 candidates; Existential N-structs + async arity bags first |

---

*End of M0-B runtime map.*
