# Async Members with Non-Frozen Types

Status: **as-built**. Both the parameter copy-buffer path and the return-value carrier path for non-frozen (and complex-enum) value types across an async boundary are implemented. This document is the design reference for that machinery.

## Problem

Non-frozen Swift structs (and complex enums) have opaque, resilient layouts. On the C# side they project as managed classes with a `SafeHandle`/`SwiftSafeHandle` payload, not as blittable register values.

Async members cannot pass that projection straight through a Swift-convention P/Invoke:

1. **Calling convention** — `[UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]` rejects non-blittable managed types (`SafeHandle` → `InvalidProgramException`).
2. **Indirect layout** — the value must cross the boundary as a pointer to properly initialized Swift memory, not as a bitwise memcpy of opaque bits.
3. **Lifetime** — the Swift async wrapper runs the real `async` work inside a `Task { }`. The C# foreground frame has already returned `tcs.Task` when the continuation reads parameters or deposits a return into a carrier. Stackalloc buffers, short-lived `using` containers, and “borrow the original payload until the call returns” are all wrong.

The binding therefore always:

- lowers non-frozen / complex-enum async parameters to `IntPtr` (marker `MarshalledType.NonFrozenIntPtr`);
- owns independent VWT copies for values that must outlive the foreground frame;
- frees those copies only from the async completion path (typed `SwiftAsyncCallHolder.Cleanup`).

## Architecture overview

| Concern | Where it lives |
|---|---|
| Detect params that need async copy buffers; emit C# `InitializeWithCopy` + Swift `.pointee` / `.load` | `WrapperEmitter.EmitAsync` in `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Async.cs` |
| Async success/error callbacks, holder construction helpers, return marshalling | `AsyncHarnessEmitter` in `…/Handler/AsyncHarnessEmitter.cs` |
| Method-level generic async bridge (same return ownership algebra) | `AsyncMethodGenericBridgeEmitter` in `…/Handler/AsyncMethodGenericBridgeEmitter.cs` |
| Return carrier ownership decision (`CallbackTakesOwnership` / `CarrierNeedsDestroy`) | `AsyncResultPlanner` / `AsyncResultPlan` in `…/Handler/AsyncResultPlan.cs` |
| P/Invoke param markers (`NonFrozenIntPtr` vs `NonFrozenSafeHandle`) | `PInvokeEmitter` + `MarshalledType` / `MethodSignature` |
| Typed holder, copy-buffer cleanup, deferred container dispose | `SwiftAsyncCallHolder`, `CopyBufferWithType`, `AsyncDeferredDisposeList` in `src/Swift.Runtime/src/Swift/Runtime/AsyncHelpers.cs` |
| VWT entry points | `ValueWitnessTable.InitializeWithCopy` / `Destroy` in `src/Swift.Runtime/src/Swift/Runtime/ValueWitnessTable.cs` |

## Parameter path (non-frozen / complex enum)

### C# foreground (`WrapperEmitter.EmitAsync`)

For each parameter whose type record is non-frozen or a non-simple enum (ObjC-bridged/rooted/bridgeable types and simple enums are excluded):

1. Resolve metadata via `SwiftObjectHelper<T>.GetTypeMetadata()`.
2. `NativeMemory.Alloc(metadata.Size)`.
3. `metadata.ValueWitnessTable->InitializeWithCopy(dest, src, metadata)` from the original payload:
   - **Class payload** — source is `&selfPtr` where `selfPtr` is the object pointer from the handle.
   - **Struct / complex-enum payload** — source is `(void*)param.Payload.DangerousGetHandle()`.
4. Pass the copy buffer as `IntPtr {name}Handle` into the P/Invoke (`MethodSignature.GetCallArgumentString` maps `NonFrozenIntPtr` → `{name}Handle`).
5. Wrap the buffer as `CopyBufferWithType` and store it on `SwiftAsyncCallHolder.CopyBuffers`.
6. Keep the **original** managed parameter object (and receiver `this` for instance methods) in `SwiftAsyncCallHolder.KeepAlives` so GC cannot run `Destroy` on the source while the copy still shares internal storage under COW.

Frozen blittable struct params on a cdecl async wrapper take a related path: heap `NativeMemory.Alloc` + `SwiftMarshal.MarshalToSwift` instead of stackalloc, also cleaned via `CopyBufferWithType`.

### Swift wrapper (same `EmitAsync` render)

Before entering `Task { }`, each non-frozen param is reified:

```swift
// Typical non-frozen / complex-enum param
let fooValue = foo.assumingMemoryBound(to: Module.Foo.self).pointee
// Existential param uses load(as:) instead
let barValue = bar.load(as: any SomeProtocol.self)
```

**Design intent:** C# already performed a correct VWT copy into the buffer. `.pointee` / `.load(as:)` is a bitwise load that does **not** bump reference counts. The copy buffer remains the owner of the `+1` from `InitializeWithCopy`. The Swift method is called with `fooValue` (and siblings). C# destroys and frees the buffer only after the async callback runs.

### Cleanup

`SwiftAsyncCallHolder.Cleanup` (success, fault, cancel, and launch-catch paths via `AsyncHarnessEmitter.BuildHolderCleanupCode`):

1. For each `CopyBufferWithType`: `ValueWitnessTable->Destroy` then `NativeMemory.Free`.
2. Clear keep-alives (no native release — pure GC roots).
3. Release retained class self / deferred struct self / existential heaps / deferred dispose list / cancellation registration as applicable.

Idempotent and exception-safe: each field is cleared after processing so a second pass cannot double-free.

### Collections of non-frozen elements

`Array` / `Set` / `Dictionary` parameters are not copy-buffered as opaque non-frozen scalars. They are serialized into managed containers (`SwiftArray<T>`, etc.) whose lifetime must extend past the foreground `using`. Those containers are appended to `AsyncDeferredDisposeList` on the holder and disposed only in callback cleanup — see the comments on deferred dispose in `WrapperEmitter.Async` and `AsyncHelpers.AsyncDeferredDisposeList`. Covered end-to-end by:

- `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncNonFrozenStructArrayParams.swift`
- `…/AsyncNonFrozenStructDictionaryParams.swift`
- matching `RuntimeTestsApp/Async/AsyncNonFrozenStruct*ParamTests.cs`

## Return path (non-frozen / complex enum)

### Swift carrier

For complex value returns, the generated Swift wrapper allocates a carrier and writes the result with a real copy witness (not `storeBytes` / raw `copyMemory`):

```swift
let _rawPtr = UnsafeMutableRawPointer.allocate(
    byteCount: MemoryLayout<T>.size,
    alignment: MemoryLayout<T>.alignment)
_rawPtr.initializeMemory(as: T.self, repeating: result, count: 1)
// hand OpaquePointer(_rawPtr) to the C# callback; free via SBW_Free after C# is done
```

That `initializeMemory` leaves the carrier holding its own `+1` on internal references. Class / ObjC-bridgeable returns use a different pointer-bit path (`Unmanaged.passRetained` / bridge) and do not use this algebra.

### Ownership algebra (`AsyncResultPlanner`)

Both `AsyncHarnessEmitter` and `AsyncMethodGenericBridgeEmitter` route the carrier decision through `AsyncResultPlanner.ClassifyCarrierOwnership(TypeRecord)` so the two renderers cannot drift:

| Return shape | `CallbackTakesOwnership` | `CarrierNeedsDestroy` | C# callback action |
|---|---|---|---|
| Non-frozen struct | true | true | `NativeMemory.Alloc` + `InitializeWithCopy` into managed buffer → `MarshalFromSwift` (SafeHandle owns buffer); `Destroy` carrier; `SBW_Free` |
| Complex (non-simple) enum | true | true | same as non-frozen |
| Frozen-as-class (`RequiresMemoryManagement`) | false | true | `MarshalFromSwift` from carrier (NewFromPayload does its own copy); `Destroy` carrier; `SBW_Free` |
| Frozen blittable / simple enum | false | false | value-copy `MarshalFromSwift`; raw `SBW_Free` only |
| Class / ObjC-bridged (separate paths) | n/a | n/a | pointer / `GetNSObject` paths, not this planner |

`WidenDestroyForOptionalPayload` extends `CarrierNeedsDestroy` for `Optional<T>` when `T` is non-frozen, complex enum, or frozen-as-class (carrier holds `+1` on the embedded payload; `SwiftOptional<T>`’s own `NewFromPayload` takes an independent copy).

**Two live copies on the callback-owned path:** briefly during the success callback the Swift carrier and the C#-owned VWT copy both exist. The managed wrapper adopts the C# buffer; the carrier’s `+1` is released with `Destroy` before `SBW_Free`. On marshal throw, a `catch` destroys and frees the not-yet-adopted C# buffer so it cannot leak alongside the carrier release in `finally`.

### `AsyncMethodGenericBridgeEmitter`

Method-own generic async methods (class-bound protocol constraints without Self / associated types) use a separate `@_cdecl` bridge but the **same** complex-value ownership algebra for non-frozen / complex-enum returns. V1 of that bridge still excludes some return shapes (tuple, string, array-of-string, generic collection, ObjC-bridgeable, optional-class) that the main harness already supports — those exclusions are bridge-surface limits, not a reopening of the non-frozen copy problem.

## P/Invoke surface

In `PInvokeEmitter`:

- **Async** non-frozen struct / class and complex enum params → `MarshalledType.NonFrozenIntPtr` (`IntPtr` in the extern signature).
- **Sync** same shapes → `NonFrozenSafeHandle` / `EnumSafeHandle` (SafeHandle-backed).

`WrapperValidation` treats async non-frozen params as blittable at the P/Invoke boundary precisely because they lower to `IntPtr` after the copy-buffer setup (`NonFrozenIntPtr` path).

## Lifetime of related resources (receiver / containers)

Orthogonal to the non-frozen value copy, but part of the same holder:

- **Swift class self** — `Arc.UnknownObjectRetain` before launch; `RetainedSelfPtr` + `UnknownObjectRelease` in cleanup (isa-dispatched so `@objc` self is not over-released).
- **Struct self** — `DeferredSafeHandleRelease` (`DangerousAddRef` balanced by `DangerousRelease` in cleanup).
- **Existential param heaps** — `ExistentialContainerHeap` entries freed (and optionally destroyed when owned) in cleanup, not in a foreground `finally` that would race the continuation.
- **Cancellation** — process-wide keys from `SwiftAsyncCancellation.NextCancelKey()` (not recyclable GCHandle cookies).

## End-to-end coverage

| Shape | BindingTests |
|---|---|
| Non-frozen struct **return** (property read, dispose, concurrent, nested) | `AsyncComplexTypeTests` + `AsyncComplexTypes.swift` (`AsyncReport`, `AsyncUsageMetadata`) |
| Complex enum async return | same class (`AsyncStatus`) |
| Frozen-with-memory async return (carrier destroy leak guard) | `TestAsyncGetResult_RepeatedCalls_NoCarrierLeak` |
| `Array<NonFrozenStruct>` async param / return | `AsyncNonFrozenStructArrayParamTests` |
| `Dictionary<String, NonFrozenStruct>` async param | `AsyncNonFrozenStructDictionaryParamTests` |

Unit pins: `AsyncResultPlannerTests`, async harness / AMGBE emitter tests under `src/Swift.Bindings/tests/UnitTests/EmitterTests/`, and P/Invoke marker tests (`Parameter_NonFrozenStructAsync_UsesIntPtrFromNonFrozen`).

## Remaining gaps (genuinely open or out of scope)

Nothing in the generator still treats “async + non-frozen scalar parameter/return” as an unfixed crash. What remains limited is adjacent surface area:

1. **Module-internal async** — async members on `@usableFromInline internal` parents (or internal free functions) are skipped (`SkipReason.ParentModuleInternalNoFallback` / `ModuleInternal` in `MemberValidationPipeline`): there is no direct CallConvSwift fallback for async.
2. **ABI-unsafe direct async P/Invoke** — method-own generic, top-level existential, or non-cdecl closure params without a proper bridge are fail-closed rather than emitted as direct CallConvSwift trampolines (`AsyncSkipPolicyShapes.swift` / `WrapperValidation.IsSkippedWrapperDirectPInvoke`).
3. **AMGBE V1 return exclusions** — listed above; main harness handles those shapes for non-generic async.
4. **CSM / supplement-owned non-frozen returns** — separate product gap (e.g. CryptoKit NIST ECDSA) tracked outside this design; not a failure of the generic async non-frozen copy model.

## Related docs

- `binding-value-witness-table.md` — VWT operations used here (`initializeWithCopy`, `destroy`).
- `binding-structs.md` — frozen vs non-frozen projection.
- `memory-management.md` — general retain/release expectations for projected value types.
