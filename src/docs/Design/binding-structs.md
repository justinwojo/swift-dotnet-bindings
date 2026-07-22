# Binding Structs

**Status:** as-built — three-way struct projection.

Swift structs are value types whose layout and ownership rules vary by **frozenness** (ABI-stable layout under library evolution) and by whether the value **requires memory management** (reference-holding fields such as classes or `String`). C# has no automatic Swift value-witness teardown when a stack value is overwritten or goes out of scope, so the generator cannot always project a Swift struct as a C# `struct`.

The generator implements a **three-way model**. Informal labels used throughout the codebase and BindingTests:

| Label | When | C# surface |
|---|---|---|
| **Struct** (blittable frozen) | `@frozen` + no managed payload | real C# `struct` |
| **ClassWithBufferStruct** | `@frozen` + reference-ish fields | C# `class` with nested `.Buffer` |
| **ClassWithOpaquePayload** | not effectively frozen | C# `class` with SafeHandle opaque payload |

Ownership details (when to `InitializeWithCopy` / VWT `Destroy`, dispose vs finalizer) live in [memory-management.md](memory-management.md). Value-witness table layout lives in [binding-value-witness-table.md](binding-value-witness-table.md). This document is the **classification + C# surface + P/Invoke boundary** design only.

---

## Classification

### Effective frozenness and flags

Parser initially sets `StructDecl.IsFrozen` from the `@frozen` attribute (`SwiftABIParser`). After property processing, `ModuleProcessor` recomputes flags in `CacluateFlags` and **rewrites** `structDecl.IsFrozen` to the **effective** frozen bit:

1. **Non-copyable** (`~Copyable`): if the type lists `Swift.Escapable` without `Swift.Copyable`, set `TypeRecordFlags.NonCopyable`.
2. **Not `@frozen`**: return early — no `Frozen` flag (non-frozen path).
3. **`@frozen`**: set `TypeRecordFlags.Frozen`, then walk **instance** stored properties:
   - Nested **non-frozen struct** field → clear `Frozen` (struct is not effectively frozen).
   - Field with `RequiresMemoryManagement`, or `TypeRecordKind.Class` → set `RequiresMemoryManagement`.
   - Unregistered field type (e.g. some generics) → clear `Frozen` and set `RequiresMemoryManagement` (fail safe).
   - Float / Bool field classification also sets `HasFloatFields` / `HasBoolFields` (CallConvSwift register safety), using the same field classifier as ABI layout.

Registered as a `TypeRecord` via `RegisterStructType` (`ModuleProcessor`). Predicate helpers in `MarshallingHelpers`:

- `IsTypeFrozen` — `TypeRecordFlags.Frozen`
- `RequiresMemoryManagement` — `TypeRecordFlags.RequiresMemoryManagement`
- `IsFrozenStructProjectedAsClass` — `Kind == Struct && Frozen && RequiresMemoryManagement`

### Handler dispatch

| Factory | Predicate | Emitter |
|---|---|---|
| `FrozenStructHandlerFactory` | `StructDecl.IsFrozen` (effective) | `FrozenStructHandler` |
| `NonFrozenStructHandlerFactory` | `!StructDecl.IsFrozen` | `NonFrozenStructHandler` |

Inside `FrozenStructHandler`, `isProjectedAsClass = MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord)` splits shapes 1 and 2.

### Projection dispatch

`TypeProjectionFactory.CreateProjectionForTypeRecord` (after ObjC / enum / class special cases):

```
!IsTypeFrozen          → NonFrozenStructProjection
!RequiresMemoryManagement → BlittableProjection
else                   → FrozenWithMemoryProjection
```

Complex (non-simple) enums reuse `NonFrozenStructProjection` with `useMarshalFromSwift: true` — same opaque-handle shape, not covered further here.

---

## Shape 1 — Frozen blittable → C# `struct`

**Decision:** effectively frozen and **not** `RequiresMemoryManagement` (all fields are frozen value types with no heap payload).

**Emitter:** `FrozenStructHandler` with `isProjectedAsClass == false`.

**C# surface:**

- `public unsafe partial struct T : ISwiftObject, …`
- Backing fields mirror Swift instance storage (typed fields or `IntPtr` words for sizes that need word packing). Static stored properties are **not** laid out in the instance (they would oversize the type).
- Optional `[StructLayout(LayoutKind.Sequential, Size = …)]` when live Swift metadata is available.
- `ISwiftObject.NewFromPayload` is a bitwise read: `return *(T*)handle`.
- `PayloadConstructionSemantics.Inline` — no SafeHandle, no buffer ownership.
- `Dispose()` is a no-op (no managed resources).

**Projection:** `BlittableProjection` — `PublicType` and `PInvokeType` are the same type; parameter/return plans are pass-through.

**P/Invoke / self:**

- Arguments and returns cross the boundary as the C# struct value (or stack buffer for large / indirect-result constructors).
- Instance methods use `SwiftSelfKind.FrozenStructValue`: `new SwiftSelf<T>(this)`.
- Mutating / fixed-block paths use a pointer to `this` when required (`SwiftSelfKind.FixedBlock`).

**Ownership:** no VWT destroy on C# dispose. `MarshalToSwift` may still `InitializeWithCopy` into a destination span when the runtime needs a Swift-owned copy of the value bytes.

---

## Shape 2 — Frozen with memory management → ClassWithBufferStruct

**Decision:** effectively frozen **and** `RequiresMemoryManagement` (e.g. a class field, `String`, or nested type that itself requires memory management). Layout is ABI-stable and known, but Swift still runs VWT destroy for refcounted fields — a bare C# `struct` assignment would leak or double-free.

**Emitter:** `FrozenStructHandler` with `isProjectedAsClass == true`.

**C# surface:**

- `public partial class T : ISwiftObject, ISwiftStruct, IDisposable, …`
- Private `SwiftSafeHandle<T> _payload` owns a `NativeMemory.Alloc`'d buffer holding the Swift value bytes.
- Nested **`public struct Buffer`** with the same field layout as shape 1 (typed fields / `IntPtr` words). Used only as the **blittable P/Invoke carrier**.
- `public unsafe PayloadBuffer<T.Buffer> PayloadBuffer` — pins the SafeHandle and exposes `Buffer` / `BufferRef` for by-value / `inout` lowering (`Swift.Runtime.PayloadBuffer<T>`).
- `Dispose()` disposes `_payload` (no separate wrapper finalizer — the SafeHandle is the finalizable owner).

**`NewFromPayload` (Copy semantics):**

```text
Alloc(metadata.Size)
VWT.InitializeWithCopy(dest, wireHandle, metadata)   // +1 for the wrapper
_payload = new SwiftSafeHandle<T>(dest)
```

`ISwiftObject.PayloadConstructionSemantics` → `PayloadConstructionSemantics.Copy`. The wire temporary's retains are **not** adopted; call sites that own the wire buffer must destroy it after construction (see below).

**Projection:** `FrozenWithMemoryProjection`

| Direction | Behavior |
|---|---|
| Public type | `T` (the class) |
| P/Invoke type | `T.Buffer` |
| Parameter | `using PayloadBuffer<T.Buffer>` → pass `.Buffer` by value |
| Return (direct / by-value register) | `SwiftMarshal.MarshalFromSwiftObjectConsuming<T>(&result)` — `NewFromPayload` then VWT destroy of the stack temporary |
| Return (indirect / out buffer) | `SwiftMarshal.MarshalFromSwiftObject<T>(…)` — copy construction; wire cleanup is the marshal seam's responsibility per `Copy` semantics |

**P/Invoke / self (`MethodMarshalPlanBuilder`):**

- Non-setter: `SwiftSelfKind.FrozenStructBuffer` — `new SwiftSelf<T.Buffer>(*(T.Buffer*)_payload.DangerousGetHandle())`.
- Setter: `SwiftSelfKind.FrozenStructSetter` — `new SwiftSelf((void*)_payload.DangerousGetHandle())` (pointer to payload, not a by-value buffer copy).
- Constructors that need an indirect result allocate `NativeMemory.Alloc(sizeof(T.Buffer))` and assign the handle into `_payload` after the call.

**Ownership destroy path:** `SwiftSafeHandle<T>.ReleaseHandle` runs VWT `Destroy` on the payload buffer (direct on explicit `Dispose`; `SBW_VWTDestroy` cdecl trampoline from the GC finalizer), then `NativeMemory.Free`. See [memory-management.md](memory-management.md).

**Note (historical):** projecting every non-blittable struct as an opaque class simplifies emission but forces a heap allocation even when layout is frozen. The Buffer split keeps a **real by-value ABI** at the P/Invoke boundary while still owning ARC via a SafeHandle.

---

## Shape 3 — Non-frozen → ClassWithOpaquePayload

**Decision:** not effectively frozen (no `@frozen`, or nested non-frozen content, or fail-safe demotion). Layout size/stride may change across library versions; Swift passes the value **by reference** (buffer pointer), not as a lowered multi-register value type.

**Emitter:** `NonFrozenStructHandler`.

**C# surface:**

- `public partial class T : ISwiftObject, ISwiftStruct, IDisposable, …`
- `static nuint _payloadSize` from type metadata (eager register path for generics / NativeAOT factory registration; lazy when OS-availability gates require it).
- `SwiftSafeHandle<T> _payload` — the handle **is** the opaque value buffer (size from metadata, not a compile-time C# layout).
- No nested `.Buffer` type — layout is not mirrored in managed fields.
- Public properties go through Swift accessors / P/Invoke, not field offsets.
- `Dispose()` → `_payload.Dispose()`.

**`NewFromPayload` (Adopt semantics):**

```text
_payload = new SwiftSafeHandle<T>(handle)   // wraps the wire buffer; no InitializeWithCopy
```

`PayloadConstructionSemantics.Adopt` — the wrapper owns the temporary's `+1`; cleanup must **not** destroy the same buffer again.

**Projection:** `NonFrozenStructProjection`

| Direction | Behavior |
|---|---|
| Public type | `T` |
| P/Invoke type | `IntPtr` |
| Parameter | `param.Payload.DangerousGetHandle()` |
| Return | `SwiftMarshal.MarshalFromSwiftObject<T>(…)` |
| Swift containers (`SwiftArray` / etc.) | `SwiftContainerGenericType` is `T` itself (inline value slots use `ISwiftObject.MarshalToSwift` / VWT copy, not raw `IntPtr` slots) |

**P/Invoke / self:**

- `SwiftSelfKind.NonFrozenStruct`: `new SwiftSelf((void*)_payload.DangerousGetHandle())` — the buffer pointer **is** the struct data.
- Async / CallConvSwift paths that cannot take `SafeHandle` lower parameters to `IntPtr` with `DangerousAddRef` / `DangerousRelease` lifetime pinning (see [async-non-frozen-types.md](async-non-frozen-types.md)).

**Ownership:** same SafeHandle VWT destroy + free as shape 2, but **Adopt** means `NewFromPayload` does not allocate a second buffer. `MarshalToSwift` uses `InitializeWithCopy` from the payload into the destination span.

---

## Summary table

| | Blittable frozen | Frozen + memory mgmt | Non-frozen |
|---|---|---|---|
| **Handler** | `FrozenStructHandler` | `FrozenStructHandler` | `NonFrozenStructHandler` |
| **Projection** | `BlittableProjection` | `FrozenWithMemoryProjection` | `NonFrozenStructProjection` |
| **C# kind** | `struct` | `class` + nested `Buffer` | `class` |
| **Marker** | `ISwiftObject` | `ISwiftObject`, `ISwiftStruct` | `ISwiftObject`, `ISwiftStruct` |
| **Payload** | value bits in the struct | `SwiftSafeHandle` → layout buffer | `SwiftSafeHandle` → opaque buffer |
| **P/Invoke type** | `T` | `T.Buffer` | `IntPtr` |
| **NewFromPayload** | `*(T*)handle` | Alloc + `InitializeWithCopy` | adopt handle |
| **Semantics** | `Inline` | `Copy` | `Adopt` |
| **Destroy** | none (no-op Dispose) | VWT Destroy on SafeHandle release | VWT Destroy on SafeHandle release |
| **Self (typical)** | `SwiftSelf<T>(this)` | `SwiftSelf<T.Buffer>(*(…))` | `SwiftSelf((void*)handle)` |

---

## Fail-closed type skips

Type-level refusal is centralized in `TypeSkipConditions.FirstMatch` (shared by handlers, `TypeSkipPrePass`, and silent-tombstone registration). Struct-relevant arms:

| Condition | Applies to | Why fail closed |
|---|---|---|
| `IndeterminateBufferLayout` | ClassWithBufferStruct (`HasIndeterminateBufferLayout`) | A stored field's inline size is not derivable cross-compile (e.g. generic value-type field without size). Guessing Buffer size corrupts the heap. |
| `SubWordOptionalLayoutMismatch` | By-value frozen (`HasSubWordOptionalLayoutMismatch`) | Emitted `IntPtr`-word optional fields shift a later field off Swift's packed offset → wrong bytes at the cdecl boundary. |
| `UnsupportedGenericConstraint` | any | Unsupported framework protocol constraint (e.g. SwiftUI/Combine). |
| `VariadicGenericParameterPack` | any | `each T` has no C# equivalent. |
| `IndeterminatePwtShape` | generic types | Conformance witness tables cannot be lowered into metadata-accessor PWT args. |
| `EmitterFault` | any | Prior emission fault denylisted the type for regenerate-from-plan recovery. |

Inside a frozen Buffer emission loop, an indeterminate field that somehow reaches emission throws rather than emit a wrong-sized field (`ClassifyFrozenStructField` → `FrozenFieldLayoutKind.Indeterminate`).

### Cross-module extensions

Cross-module `extension ForeignModule.ForeignType { … }` receivers are re-routed through `CrossModuleExtensionEmitter` (both handlers). The foreign trampoline path supports **frozen value structs without managed payload** only — non-frozen or `RequiresMemoryManagement` foreign receivers are skipped (`SwiftABIParser` + emitter gates). Nested types declared *inside* a cross-module extension still emit on the normal path (owned by the current module).

---

## Open edges

These are real limits in the current code, not proposals:

1. **Codable JSON helpers** — emitted for ClassWithOpaquePayload (`NonFrozenStructHandler`) via `_payloadSize` / `NewFromPayloadCore`. **Not** emitted for ClassWithBufferStruct (comment in `FrozenStructHandler`: Buffer path lacks those primitives).
2. **Frozen-with-memory as container element (parameter conversion)** — `FrozenWithMemoryProjection.GetParameterElementConversion` returns `null` (no safe LINQ-style `PayloadBuffer` extraction). Accidental composition fails at C# compile time rather than leaking handles. No validated library currently depends on this composition.
3. **Non-copyable (`~Copyable`)** — flagged as `TypeRecordFlags.NonCopyable` and special-cased in wrapper self-reconstruction / metadata paths (`WrapperValidation.IsNonCopyableStructParent`). Full binding of non-copyable value types remains constrained (Swift forbids ordinary copy witnesses).
4. **Async + non-blittable** — CallConvSwift cannot pass `SafeHandle`; non-frozen (and similar) parameters lower to `IntPtr` with pin/release. Documented separately in [async-non-frozen-types.md](async-non-frozen-types.md).
5. **`inout` residual (non-cdecl)** — cdecl blittable-frozen `inout` writeback is implemented; some non-cdecl / projection paths may still lack post-call readback. Tracked as residual work outside this design surface.

---

## Related code map

| Concern | Location |
|---|---|
| Flag computation | `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` (`CacluateFlags`, `RegisterStructType`) |
| Predicates | `src/Swift.Bindings/src/Marshaler/MarshallingHelpers.cs` |
| Frozen emission | `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs` |
| Non-frozen emission | `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs` |
| `NewFromPayload` / `MarshalToSwift` | `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandlerHelpers.cs` (`ISwiftObjectMethodWriter`) |
| Projection selection | `src/Swift.Bindings/src/Marshaler/Projection/TypeProjectionFactory.cs` |
| Projections | `BlittableProjection.cs`, `FrozenWithMemoryProjection.cs`, `NonFrozenStructProjection.cs` |
| Self / param plans | `src/Swift.Bindings/src/Marshaler/Projection/MethodMarshalPlanBuilder.cs` |
| Type skips | `src/Swift.Bindings/src/Emitter/StringEmitter/TypeSkipConditions.cs` |
| Ownership enum | `src/Swift.Runtime/src/Swift/Runtime/PayloadConstructionSemantics.cs` |
| SafeHandle + VWT destroy | `src/Swift.Runtime/src/Swift/Runtime/SwiftHandle.cs` (`SwiftSafeHandle<T>`, `VwtDestroyTrampoline`) |
| Consuming direct return | `src/Swift.Runtime/src/Swift/Runtime/InteropServices/SwiftMarshal.cs` (`MarshalFromSwiftObjectConsuming`, `DestroyWireBufferRetains`) |
| Runtime tests | `BindingTests/RuntimeTestsApp/MemoryManagement/` (`DisposeTests`, `StructVwtDestroyLeakTests`, `LeakDetectionTests`) |
