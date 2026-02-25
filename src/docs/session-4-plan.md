# Session 4: Generic Throwing Closures (GRDB-Targeted)

**Status**: Plan v3 — revised per Codex review + ownership/alignment/constraint audit
**Created**: February 2026
**Revised**: February 2026 (Codex P0/P1 findings + 4 follow-up checks addressed)
**Estimated effort**: 1 session (full)
**Primary target**: GRDB `DatabasePool.read/write` pattern
**Scope**: Pattern A only — sync, method-generic, noescape `(T) throws -> U` where the generic return `U` maps to the method's own generic parameter. Escaping, async, and type-generic patterns are explicitly out of scope.
**Specialization constraint**: The `T = UnsafeMutableRawPointer` trick is valid ONLY for identity-forwarding methods where the outer method returns exactly what the closure returns (`-> T`). Methods that inspect, transform, or constrain `T` beyond what `UnsafeMutableRawPointer` satisfies are NOT eligible. The classifier must verify this (see Sub-task 4a).

---

## Codex Review Response

### Finding 1 (P0): `Void` not representable as `T` in `Func<Database, T> where T : ISwiftObject`

**Agreed.** The original API `T Read<T>(Func<Database, T> block) where T : ISwiftObject` cannot represent side-effect-only closures (`Void` return). This is incomplete.

**Resolution — dual API surface:**

```csharp
// Returning variant — for closures that produce a value
public T Read<T>(Func<Database, T> block) where T : ISwiftObject

// Void variant — for side-effect-only closures
public void Read(Action<Database> block)
```

The void variant specializes `T = Void` in the Swift wrapper and has no result buffer. The generator detects the pattern `<T>(... throws -> T) rethrows -> T` and emits **both** overloads. This avoids `void` as a generic type argument entirely.

**Implementation detail**: The Swift wrapper for the void variant:
```swift
// Void specialization — no result buffer needed
@_silgen_name("..._XC_void")
func GRDB_DatabasePool_read_void(
    _ blockFuncPtr: UnsafeMutableRawPointer?,
    _ blockContext: UnsafeMutableRawPointer?,
    _ _self: UnsafeMutableRawPointer
) throws {
    let self_ = unsafeBitCast(OpaquePointer(_self), to: GRDB.DatabasePool.self)
    let cdecl = unsafeBitCast(blockFuncPtr!, to:
        (@convention(c) (UnsafeMutableRawPointer, UnsafeMutablePointer<UnsafeMutableRawPointer?>?, UnsafeMutableRawPointer?) -> Void).self)
    try self_.read { (db: GRDB.Database) throws -> Void in
        var innerError: UnsafeMutableRawPointer? = nil
        cdecl(Unmanaged.passUnretained(db).toOpaque(), &innerError, blockContext)
        if let err = innerError {
            throw unsafeBitCast(err, to: Swift.Error.self)
        }
    }
}
```

### Finding 2 (P0): Result buffer missing alignment and value-witness destroy

**Agreed.** `stackalloc byte[metadata.Size]` is size-correct but alignment-unaware, and the plan omitted VWT `Destroy` for nontrivial types. The codebase already has both patterns — `NativeMemory.AlignedAlloc` in `ExistentialContainer.cs` and `ValueWitnessTable->Destroy` in many places (SwiftSet, SwiftDictionary, SwiftHandle, generated code) — but the plan failed to apply them.

**Resolution — aligned allocation with correct ownership contract:**

```csharp
public unsafe T Read<T>(Func<Database, T> block) where T : ISwiftObject
{
    var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
    var alignment = (nuint)metadata.Alignment; // Always power-of-two (VWT stores alignment-1 in flags)
    var size = metadata.Size;
    // Guard zero-size (Void split out, but defensive):
    if (size == 0) size = 1; // AlignedAlloc requires size > 0
    void* resultBuf = NativeMemory.AlignedAlloc(size, alignment);
    bool resultConsumed = false;
    try
    {
        var handle = GCHandle.Alloc(block);
        try
        {
            NativeMethods.PInvoke_read_XC(
                (void*)s_read_block_callback_ptr,
                (void*)GCHandle.ToIntPtr(handle),
                resultBuf,
                out SwiftError error,
                new SwiftSelf(Payload.DangerousGetHandle()));

            if (error.Value != IntPtr.Zero)
            {
                // Buffer is UNINITIALIZED on error path — do NOT destroy.
                // The Swift wrapper only writes to resultBuf on success.
                throw SwiftRuntimeException.FromSwiftError(error);
            }

            // MarshalFromSwift copies bytes (primitives/structs) or wraps the
            // pointer and transfers ARC ownership (ISwiftObject via NewFromPayload).
            // It does NOT consume or destroy the source buffer.
            var result = SwiftMarshal.MarshalFromSwift<T>(new IntPtr(resultBuf));
            resultConsumed = true;
            return result;
        }
        finally
        {
            if (handle.IsAllocated) handle.Free();
        }
    }
    finally
    {
        // Destroy only if value was written (P/Invoke succeeded) but NOT consumed
        // (exception between P/Invoke return and MarshalFromSwift completion).
        // On the success path, MarshalFromSwift already transferred ownership —
        // calling Destroy here would double-free. This matches the established
        // pattern in SwiftDictionary (line 394) and SwiftSet (line 398).
        if (!resultConsumed)
        {
            // Only safe to destroy if P/Invoke succeeded (buffer is initialized).
            // We can't easily distinguish "P/Invoke succeeded but MarshalFromSwift
            // threw" from "P/Invoke threw" here, so we use the conservative approach:
            // skip Destroy entirely. The worst case is a leak of one Swift value on
            // an exception path, which is acceptable for this initial implementation.
            // A future refinement can add a pInvokeSucceeded flag if needed.
        }
        NativeMemory.AlignedFree(resultBuf);
    }
}
```

**Ownership model (verified against codebase):**

`MarshalFromSwift<T>()` is a **read-only** operation — it copies bytes or wraps the pointer via `NewFromPayload`, transferring ARC ownership to the returned C# object. It does NOT consume, invalidate, or destroy the source buffer. The caller retains full responsibility for buffer cleanup.

The established pattern in `SwiftDictionary.cs:394` and `SwiftSet.cs:398` is:
- **Success path**: `MarshalFromSwift` → `NativeMemory.Free` (NO `VWT->Destroy` — ownership transferred)
- **Exception path** (P/Invoke succeeded but MarshalFromSwift didn't run): `VWT->Destroy` → `NativeMemory.Free` (value still owned by buffer)

The v2 plan incorrectly called `VWT->Destroy` after `MarshalFromSwift` on the success path. For `ISwiftObject` types, `MarshalFromSwift` calls `NewFromPayload` which takes ownership of the ARC-managed pointer — a subsequent `Destroy` would release it a second time, causing a use-after-free.

**Alignment details:**
- `ValueWitnessTable.Alignment` computes `(Flags & AlignmentMask) + 1` — always a power of two (the VWT stores alignment minus one in the low bits of Flags). This satisfies `NativeMemory.AlignedAlloc`'s power-of-two requirement.
- `NativeMemory.AlignedAlloc` is already used in the codebase at `ExistentialContainer.cs:733` with this exact pattern.
- Zero-size guard: `Void` is split into its own variant (no buffer), but a defensive `size == 0 → 1` guard prevents undefined behavior from `AlignedAlloc(0, alignment)`.
- Size rounding: `NativeMemory.AlignedAlloc` handles internal rounding — no manual rounding needed by the caller.

### Finding 3 (P1): Phase sequencing may break validation

**Agreed.** Opening gates in Phase 1 (relaxing `IsSupportedClosureParameterType` and `ContainsPlaceholder`) without the emission path ready could cause methods to pass validation but produce broken C# code, failing the compile gate.

**Resolution — guarded gate relaxation:**

Phase 1 does NOT unconditionally relax gates. Instead:
1. Add the classification infrastructure (`IsMethodGenericClosureEligible`, etc.)
2. The gate relaxation in `IsSupportedClosureParameterType` is gated behind a **method-level flag** (`MethodDecl.HasGenericClosureBridge`) that is only set to `true` when the full emission pipeline (Phase 2 + 3) is wired up.
3. Until Phase 3 is complete, these methods remain skipped — the classification just tags them for future emission.

Alternatively (and likely simpler): **implement all three phases as a single atomic change.** Since this is one session, there's no practical need for incremental gate relaxation that works independently. The "phases" become implementation order within the session, not separately-deployable increments.

**Revised sequencing:**
1. **Step 1**: Implement classification + wrapper generation + C# emission as one logical unit
2. **Step 2**: Only then relax the skip gates to route eligible methods into the new path
3. **Step 3**: Validate — `run-tests.sh` + `validate-libraries.sh`

This eliminates the window where gates are open but emission is broken.

### Finding 4 (P1): Scope overstated vs. session framing

**Agreed.** The header claimed "all libraries with `(T) throws -> U` closure patterns" as secondary targets, but the actual implementation is Pattern A only.

**Resolution**: Header updated (see top of this document). The scope is:
- **Primary target**: GRDB `DatabasePool.read/write` — the ~59 skipped methods
- **Pattern scope**: Pattern A only — sync, method-generic, noescape
- **Not in scope**: Pattern B (escaping generic), Pattern C (type-generic, already handled), async variants

The file change list is large because Pattern A touches the full emission pipeline (classifier → wrapper → emitter → validator), not because scope is too broad. Each file change is narrowly targeted at the generic-closure path.

### Finding 5 (P1): Internal inconsistency on runtime changes

**Agreed.** The executive summary said "no runtime changes" but later introduced `SBW_CreateError` (a new wrapper-library helper) and `GCHandle` (which is .NET, not Swift.Runtime, but still a material design element).

**Resolution — clarified layers:**
- **`Swift.Runtime` NuGet package**: No changes. The existing `TypeMetadata`, `ValueWitnessTable`, `NativeMemory`, `SwiftMarshal`, `SwiftError` APIs are sufficient.
- **Wrapper library (generated Swift)**: **One new helper** — `SBW_CreateError` — to create a Swift `Error` from a C# exception message. This is generated into the per-module Swift wrapper file, alongside existing helpers like `SBW_GetErrorDescription`. It is NOT a runtime package change.
- **GCHandle**: Used in the generated C# code (not the runtime package). The `@noescape` guarantee means the GCHandle lifetime is bounded by the method call, managed via `try/finally`. This is the standard pattern for passing managed delegates through P/Invoke.

The "stack-allocated callback context" claim in the original summary was misleading. The delegate reference itself requires a `GCHandle` (heap-pinned managed reference). The *result buffer* uses `NativeMemory.AlignedAlloc` (heap). Only trivial intermediaries (like local `IntPtr` variables) are stack-allocated. Updated executive summary to reflect this.

### Finding 6 (P2): "Pattern D already working" overstated

**Agreed.** Pattern D (non-generic throwing closures) is supported by the Q3 infrastructure in principle, but specific GRDB instances like `writeInTransaction` are still skipped due to `Database.TransactionCompletion` being a nested enum that TypeDatabase doesn't resolve.

**Resolution**: Clarified in the pattern table. Pattern D's infrastructure works, but individual instances may be blocked by orthogonal issues (nested type resolution, TypeDatabase coverage). These are NOT Session 4 targets — they'll be fixed when the underlying type resolution improves.

---

## Revised Executive Summary

GRDB's core API revolves around generic throwing closures: `func read<T>(_ block: (Database) throws -> T) rethrows -> T`. These are currently skipped because (1) the closure's return type `T` is a generic type parameter (`tau_0_0`), which `IsSupportedClosureParameterType` rejects, and (2) the method's return type is also generic, causing `ContainsPlaceholder` to trigger a skip.

The solution is a **monomorphized Swift wrapper bridge**: for each method with a generic noescape throwing closure, generate a Swift `@_silgen_name` wrapper that specializes the generic parameter to `UnsafeMutableRawPointer` (for the returning variant) or `Void` (for the side-effect variant). The C# side passes a `@convention(c)` callback for the closure body and receives the result via a pre-allocated, properly-aligned buffer with VWT lifecycle management. Error marshalling reuses the existing `SwiftError*` out-parameter infrastructure, plus a new `SBW_CreateError` helper in the generated wrapper library.

**What changes:**
- **Generator** (`Swift.Bindings`): New classification, emission, and validation paths for generic closures
- **Generated Swift wrapper**: Per-method monomorphized wrappers + `SBW_CreateError` helper
- **Generated C# bindings**: Generic public methods with aligned buffer allocation and VWT destroy

**What does NOT change:**
- **`Swift.Runtime` NuGet package**: No modifications
- **Existing closure infrastructure**: Q3 Cdecl wrappers, SwiftResult, throwing closure thunks — all untouched
- **Other validation libraries**: No behavioral changes for non-GRDB libraries

---

## Current State: Why These Methods Are Skipped

### GRDB ABI JSON for `DatabasePool.read(_:)`

```json
{
  "name": "read",
  "printedName": "read(_:)",
  "genericSig": "<tau_0_0>",
  "sugared_genericSig": "<T>",
  "throwing": true,
  "children": [
    { "kind": "TypeNominal", "name": "GenericTypeParam", "printedName": "tau_0_0" },
    {
      "kind": "TypeFunc", "name": "Function",
      "printedName": "(GRDB.Database) throws -> tau_0_0",
      "typeAttributes": ["noescape"]
    }
  ],
  "mangledName": "$s4GRDB12DatabasePoolC4readyxxAA0B0CKXEKlF"
}
```

### Skip chain (current behavior)

1. **`MemberEmissionValidator.ShouldSkipMethodEmission` (line 796-806)**:
   - Iterates method parameters looking for closures
   - Finds `(GRDB.Database) throws -> tau_0_0` — this IS a closure (`TypeFunc`)
   - Calls `closureHandler.IsSupportedClosure(closureTypeSpec)`

2. **`ClosureHandler.IsSupportedClosure` (line 172-222)**:
   - Calls `IsSupportedClosureParameterType(closureTypeSpec.ReturnType)` for the closure's return type `tau_0_0`

3. **`ClosureHandler.IsSupportedClosureParameterType` (line 450-453)**:
   - `IsGenericTypeParameter("tau_0_0")` returns `true`
   - Returns `false` immediately — **this is the primary blocker**

4. Even if the closure passed, the method-level check at `MemberEmissionValidator.CanEmitMethod` (line 562) would catch it:
   - `signatureHandler.GetWrapperSignature().ContainsPlaceholder` — the method returns `tau_0_0`, which resolves to `AnyType` (placeholder)

### Impact across GRDB

From the binding report: **59 methods** skipped with `UnsupportedClosure` reason on `DatabasePool`, `DatabaseSnapshot`, `DatabaseSnapshotPool`, `DatabaseWriter`, `AnyDatabaseWriter`, and `AnyDatabaseReader`. These include `read`, `write`, `writeWithoutTransaction`, `barrierWriteWithoutTransaction`, `unsafeRead`, `unsafeReentrantRead`, `unsafeReentrantWrite`, and `writeInTransaction`.

---

## Pattern Classification

| Pattern | Example | Method Generic? | Noescape? | Approach | Status |
|---------|---------|:---------------:|:---------:|----------|--------|
| A. Method-generic, noescape | `read<T>(_ block: (Database) throws -> T) -> T` | Yes | Yes | Monomorphized wrapper | **Session 4 target** |
| B. Method-generic, escaping | `map<T>(_ transform: @escaping (Element) -> T) -> [T]` | Yes | No | Deferred (needs type-erased bridge) | Out of scope |
| C. Type-generic, noescape | `init(from: (Decoder<T>) throws -> Void)` | No | Yes | Already handled by BoundGenericsHandler | N/A |
| D. Non-generic throwing | `writeInTransaction((Database) throws -> TransactionCompletion)` | No | N/A | Q3 infrastructure handles this pattern; individual instances may be blocked by orthogonal issues (nested type resolution) | Not Session 4 |

---

## Sub-task 4a: Detect and Classify Generic Closure Methods

### Goal

Identify methods whose closure parameters have generic return types, and distinguish "method-level generic" from "type-level generic" patterns.

### Files to modify

1. **`src/Swift.Bindings/src/Marshaler/ClosureHandler.cs`** (~line 450)
   - New method: `IsMethodGenericClosureEligible(ClosureTypeSpec, MethodDecl)` — returns true when:
     - (a) Closure has generic return type (or generic params)
     - (b) All generic params map to the method's own generic signature (not type-level)
     - (c) Closure is noescape (`typeAttributes` contains `"noescape"`)
     - (d) All non-generic params pass existing `IsSupportedClosureParameterType`
     - (e) Closure may throw (throws is handled) or not (simpler case)
     - **(f) Identity-forwarding return**: The method's return type is the SAME generic parameter as the closure's return type (`method returns tau_0_0` AND `closure returns tau_0_0`). This ensures the `T = UnsafeMutableRawPointer` specialization is safe — the method just passes through whatever the closure returns without inspecting or constraining `T`. Methods that transform the return (e.g., `func mapRead<T, U>(_ block: (Database) throws -> T) -> [T]`) are NOT eligible.
     - **(g) No additional constraints on `T`**: The method's generic signature has no `where` clauses constraining `T` (e.g., no `where T : Comparable`). `UnsafeMutableRawPointer` is a simple pointer type — it cannot satisfy protocol conformance constraints. GRDB's sync `read<T>` has no constraints (the `Sendable`-constrained variant is the async one).
   - New method: `HasGenericTypeParameters(ClosureTypeSpec)` — returns true if the closure's return type or any argument is a generic type parameter
   - New method: `GetGenericTypeParameters(ClosureTypeSpec)` — returns list of generic param names used by the closure

2. **`src/Swift.Bindings/src/Marshaler/ClosureHandler.cs`** (line ~172, `IsSupportedClosure`)
   - When `IsMethodGenericClosureEligible` returns true AND the full emission pipeline is present (see Finding 3 resolution), return true instead of false

3. **`src/Swift.Bindings/src/Model/Declarations/MethodDecl.cs`** (read-only check)
   - Verify `IsGeneric`, `GenericParameters`, `GenericSignature` properties contain the relevant tau parameters

### Complexity: Low
### Risk: Low (new classification code, no existing behavior changes)

---

## Sub-task 4b: Monomorphized Swift Wrapper Bridge

### Goal

Generate a Swift `@_silgen_name` wrapper that erases the generic return type via `T = UnsafeMutableRawPointer` specialization, and a `Void` variant for side-effect closures.

### Design

#### Why `T = UnsafeMutableRawPointer` specialization works

The method `read<T>(_ block: (Database) throws -> T) rethrows -> T` has no constraints on `T` (the `Sendable`-constrained version is the async variant). We specialize `T = UnsafeMutableRawPointer`:

1. The wrapper calls `self.read { ... } -> UnsafeMutableRawPointer`
2. Inside the closure, the C# callback writes the actual typed result into a pre-allocated buffer
3. The closure returns the buffer pointer (which `read` passes through as its return value)
4. The C# side ignores the return value and reads from the buffer directly

This works because `UnsafeMutableRawPointer` is a trivial type with no constraints, so generic specialization is straightforward.

#### Swift wrapper — returning variant

```swift
@_silgen_name("$s4GRDB12DatabasePoolC4readyxxAA0B0CKXEKlF_XC")
public func GRDB_DatabasePool_read_XC(
    _ blockFuncPtr: UnsafeMutableRawPointer?,   // @convention(c) callback
    _ blockContext: UnsafeMutableRawPointer?,    // GCHandle to C# delegate
    _ _resultBuf: UnsafeMutableRawPointer,       // pre-allocated, aligned by caller
    _ _self: UnsafeMutableRawPointer
) throws {
    let self_ = unsafeBitCast(OpaquePointer(_self), to: GRDB.DatabasePool.self)

    let cdecl = unsafeBitCast(blockFuncPtr!, to:
        (@convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer, UnsafeMutablePointer<UnsafeMutableRawPointer?>?, UnsafeMutableRawPointer?) -> Void).self)

    let _: UnsafeMutableRawPointer = try self_.read { (db: GRDB.Database) throws -> UnsafeMutableRawPointer in
        var innerError: UnsafeMutableRawPointer? = nil
        cdecl(Unmanaged.passUnretained(db).toOpaque(), _resultBuf, &innerError, blockContext)
        if let err = innerError {
            throw unsafeBitCast(err, to: Swift.Error.self)
        }
        return _resultBuf
    }
    // Result already written to _resultBuf by the cdecl callback
}
```

#### Swift wrapper — void variant

```swift
@_silgen_name("$s4GRDB12DatabasePoolC4readyxxAA0B0CKXEKlF_XC_void")
public func GRDB_DatabasePool_read_void_XC(
    _ blockFuncPtr: UnsafeMutableRawPointer?,
    _ blockContext: UnsafeMutableRawPointer?,
    _ _self: UnsafeMutableRawPointer
) throws {
    let self_ = unsafeBitCast(OpaquePointer(_self), to: GRDB.DatabasePool.self)

    let cdecl = unsafeBitCast(blockFuncPtr!, to:
        (@convention(c) (UnsafeMutableRawPointer, UnsafeMutablePointer<UnsafeMutableRawPointer?>?, UnsafeMutableRawPointer?) -> Void).self)

    try self_.read { (db: GRDB.Database) throws -> Void in
        var innerError: UnsafeMutableRawPointer? = nil
        cdecl(Unmanaged.passUnretained(db).toOpaque(), &innerError, blockContext)
        if let err = innerError {
            throw unsafeBitCast(err, to: Swift.Error.self)
        }
    }
}
```

#### Error creation helper

```swift
// In the wrapper library — alongside existing SBW_GetErrorDescription, SBW_ReleaseError
@_silgen_name("SBW_CreateError")
public func SBW_CreateError(_ message: UnsafePointer<CChar>) -> UnsafeMutableRawPointer {
    let msg = String(cString: message)
    let error = NSError(domain: "SwiftBindings", code: -1, userInfo: [NSLocalizedDescriptionKey: msg])
    return unsafeBitCast(error as Swift.Error, to: UnsafeMutableRawPointer.self)
}
```

This is generated into the per-module wrapper file, NOT the `Swift.Runtime` package.

### C# public API — returning variant

```csharp
public unsafe T Read<T>(Func<Database, T> block) where T : ISwiftObject
{
    var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
    var size = metadata.Size;
    if (size == 0) size = 1; // AlignedAlloc requires size > 0
    void* resultBuf = NativeMemory.AlignedAlloc(size, (nuint)metadata.Alignment);
    bool resultConsumed = false;
    try
    {
        var handle = GCHandle.Alloc(block);
        try
        {
            NativeMethods.PInvoke_read_XC(
                (void*)s_read_block_callback_ptr,
                (void*)GCHandle.ToIntPtr(handle),
                resultBuf,
                out SwiftError error,
                new SwiftSelf(Payload.DangerousGetHandle()));

            if (error.Value != IntPtr.Zero)
                throw SwiftRuntimeException.FromSwiftError(error);

            // MarshalFromSwift copies/wraps — does NOT destroy source.
            // Ownership of ARC-managed values transfers to the returned object.
            var result = SwiftMarshal.MarshalFromSwift<T>(new IntPtr(resultBuf));
            resultConsumed = true;
            return result;
        }
        finally
        {
            if (handle.IsAllocated) handle.Free();
        }
    }
    finally
    {
        // Do NOT call VWT->Destroy on success — MarshalFromSwift transferred ownership.
        // On exception before MarshalFromSwift: buffer may contain unconsumed value.
        // Conservative: skip Destroy to avoid double-free risk. Acceptable leak on
        // rare exception path. See ownership model discussion in Finding 2 response.
        NativeMemory.AlignedFree(resultBuf);
    }
}
```

### C# public API — void variant

```csharp
public unsafe void Read(Action<Database> block)
{
    var handle = GCHandle.Alloc(block);
    try
    {
        NativeMethods.PInvoke_read_void_XC(
            (void*)s_read_void_block_callback_ptr,
            (void*)GCHandle.ToIntPtr(handle),
            out SwiftError error,
            new SwiftSelf(Payload.DangerousGetHandle()));

        if (error.Value != IntPtr.Zero)
            throw SwiftRuntimeException.FromSwiftError(error);
    }
    finally
    {
        if (handle.IsAllocated) handle.Free();
    }
}
```

### C# callback — returning variant (`[UnmanagedCallersOnly]`)

```csharp
[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
private static unsafe void ReadCallback(
    void* databasePtr,
    void* resultBuf,
    void** errorOut,
    IntPtr contextPtr)
{
    var handle = GCHandle.FromIntPtr(contextPtr);
    var block = (Func<Database, T>)handle.Target!;  // T resolved at emission time
    try
    {
        var db = new Database(new SwiftSafeHandle<Database>((IntPtr)databasePtr));
        var result = block(db);
        SwiftMarshal.MarshalToSwift(result, new Span<byte>(resultBuf, (int)metadata.Size));
    }
    catch (Exception ex)
    {
        *errorOut = (void*)SBW_CreateError(ex.Message);
    }
}
```

### Files to modify

1. **`src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.SwiftWrapper.cs`**
   - New method: `EmitGenericClosureCdeclSwiftWrapper(SwiftWriter, MethodEnvironment, TypeDecl?)` — generates both returning and void variants

2. **`src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.cs`** or new **`ClosureEmitter.GenericBridge.cs`**
   - New method: `EmitGenericClosureCallback(CSharpWriter, ...)` — emits `[UnmanagedCallersOnly]` callbacks for both variants

3. **`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs`** (~line 221-260)
   - `HandleParameter`: detect generic-closure methods and emit monomorphized P/Invoke parameters (callbackFuncPtr, callbackContext, resultBufPtr)
   - `HandleReturnType`: for generic-closure methods, return type is `void` (result via buffer)

4. **`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Marshalling.cs`** (~line 188-230)
   - `EmitClosureMarshallingSetup`: allocate aligned result buffer, create GCHandle
   - `EmitClosureCallArguments`: pass callback ptr + context + resultBuf to P/Invoke
   - `EmitClosureCleanup`: VWT Destroy on success path, GCHandle.Free in finally, AlignedFree in outer finally

5. **`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Return.cs`**
   - `EmitReturnValue`: read result from buffer via `MarshalFromSwift<T>` (not from P/Invoke return value)

6. **`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodSignature.cs`** (~line 70-100)
   - `HandleReturnType`: map generic return type `tau_0_0` to C# generic parameter `T` instead of `AnyType`

7. **`src/Swift.Bindings/src/Emitter/StringEmitter/MemberEmissionValidator.cs`** (~line 558-566, 796-806)
   - `ShouldSkipMethodEmission`: don't skip methods with generic closures that are eligible for the monomorphized bridge
   - `CanEmitMethod`: relax `ContainsPlaceholder` for these methods — but ONLY when the full emission pipeline is wired up

8. **Wrapper library** (generated Swift)
   - Add `SBW_CreateError` helper alongside existing `SBW_GetErrorDescription`

### Complexity: High
### Risk: Medium-High (new emission path for generic methods, touches many emitter files)

---

## Sub-task 4c: Error Marshalling for Generic Closures

### Goal

Extend error handling so that C# exceptions from the user's closure delegate propagate as Swift errors through `rethrows`.

### Design

The user's delegate throws normal C# exceptions. The `[UnmanagedCallersOnly]` callback catches them and converts to Swift errors:

1. **C# callback** catches `Exception`, calls `SBW_CreateError(ex.Message)` to get a Swift error pointer, writes to `errorOut`
2. **Swift wrapper** checks `innerError` after the Cdecl callback returns; if non-nil, `throw`s it
3. **`rethrows`** on the original `read<T>` propagates the error to the outer `try`
4. **Swift wrapper's `catch`** block writes the error to the outer `errorOut` parameter
5. **C# public method** checks `SwiftError`, throws `SwiftRuntimeException`

This matches the existing pattern in `ClosureEmitter.SwiftWrapper.cs` (lines 244-269) for non-generic throwing closures.

### `SBW_CreateError` implementation

This helper is generated into the per-module Swift wrapper file. It creates an `NSError` (which conforms to `Swift.Error`) from a C string message. The C# P/Invoke:

```csharp
[DllImport("SwiftBindings")]
private static extern IntPtr SBW_CreateError([MarshalAs(UnmanagedType.LPUTF8Str)] string message);
```

This follows the established pattern of `SBW_GetErrorDescription` and `SBW_ReleaseError`.

### Complexity: Medium
### Risk: Medium (error creation across boundary is a known pattern)

---

## Detailed File Change List

### Files requiring modification

| File | Method/Area | Change |
|------|-------------|--------|
| `ClosureHandler.cs` | `IsSupportedClosureParameterType` | Add generic-closure-eligible bypass before `IsGenericTypeParameter` rejection |
| `ClosureHandler.cs` | New methods | `HasGenericTypeParameters`, `IsMethodGenericClosureEligible`, `GetGenericParamNames` |
| `ClosureHandler.cs` | `IsSupportedClosure` | Accept closures with generic params when method context indicates eligibility |
| `ClosureEmitter.SwiftWrapper.cs` | New method | `EmitGenericClosureCdeclSwiftWrapper` — both returning and void variants |
| `ClosureEmitter.cs` or new `.GenericBridge.cs` | New method | `EmitGenericClosureCallback` — Cdecl callbacks for both variants |
| `PInvokeEmitter.cs` | `HandleParameter` | Detect generic closure param, emit callbackPtr + contextPtr + resultBufPtr |
| `PInvokeEmitter.cs` | `HandleReturnType` | For generic-closure methods, return type is `void` (result via buffer) |
| `WrapperEmitter.Marshalling.cs` | `EmitClosureMarshallingSetup` | Aligned buffer alloc (size > 0 guard), GCHandle creation |
| `WrapperEmitter.Marshalling.cs` | `EmitClosureCallArguments` | Pass callback ptr + context + resultBuf to P/Invoke |
| `WrapperEmitter.Marshalling.cs` | `EmitClosureCleanup` | GCHandle.Free (inner finally), AlignedFree (outer finally). NO VWT Destroy on success path. |
| `WrapperEmitter.Return.cs` | `EmitReturnValue` | Read result from buffer via MarshalFromSwift<T> |
| `MethodSignature.cs` | `HandleReturnType` | Map generic return to C# generic `T` instead of AnyType |
| `MemberEmissionValidator.cs` | `ShouldSkipMethodEmission` | Don't skip generic-closure methods (gated on full pipeline) |
| `MemberEmissionValidator.cs` | `CanEmitMethod` | Relax `ContainsPlaceholder` for generic-closure methods (gated) |
| `WrapperEmitter.cs` | Constructor / `_syncPlan` | Detect generic-closure methods, set flags |
| Shared wrapper support | `SBW_CreateError` | New error creation helper (generated Swift + C# P/Invoke) |

### Files requiring read-only review (verify compatibility)

| File | Reason |
|------|--------|
| `MethodDecl.cs` | Verify `IsGeneric`, `GenericParameters`, `GenericSignature` properties |
| `ClosureTypeSpec.cs` | Verify `ReturnType`, `Throws`, `IsAsync` properties |
| `SwiftABIParser.cs` | Verify how `TypeFunc` with `typeAttributes: ["noescape"]` is parsed |
| `NameProvider.cs` | Verify mangled name generation for wrapper |
| `ValueWitnessTable.cs` | Verify `Destroy` function pointer signature |
| `TypeMetadata.cs` | Verify `Size`, `Alignment` properties |

---

## Implementation Order (Atomic — no partial gate relaxation)

### Step 1: Classification infrastructure

1. Add `HasGenericTypeParameters`, `IsMethodGenericClosureEligible`, `GetGenericParamNames` to `ClosureHandler.cs`
2. Do NOT relax any skip gates yet
3. **Checkpoint**: `run-tests.sh` — all existing tests pass, no behavior change

### Step 2: Swift wrapper generation

1. Implement `EmitGenericClosureCdeclSwiftWrapper` — both returning and void variants
2. Add `SBW_CreateError` to wrapper library template
3. Wire into `MethodHandler` / `ModuleHandler` detection path
4. **Checkpoint**: Generate GRDB bindings, inspect Swift wrapper output manually

### Step 3: C# emission + P/Invoke

1. Modify `MethodSignature.HandleReturnType` — map generic return to `T`
2. Modify `PInvokeEmitter` — emit monomorphized P/Invoke signature
3. Implement `EmitGenericClosureCallback` — both returning and void callbacks
4. Modify `WrapperEmitter.Marshalling` — aligned buffer alloc, VWT Destroy, GCHandle lifecycle
5. Modify `WrapperEmitter.Return` — read from result buffer
6. **Checkpoint**: Generate GRDB bindings, attempt `dotnet build`

### Step 4: Gate relaxation (only after Steps 1-3 are complete)

1. Modify `IsSupportedClosureParameterType` to accept eligible generic closures
2. Modify `IsSupportedClosure` to pass eligible closures
3. Modify `MemberEmissionValidator` — relax `ContainsPlaceholder` for eligible methods
4. **Test**: `run-tests.sh` — must maintain 4161+ passing, no regressions
5. **Test**: `validate-libraries.sh` — must maintain 32/32

### Step 5: Void variant verification

1. Verify the `Action<Database>` overload compiles
2. Verify the void Swift wrapper (`T = Void` specialization) compiles
3. If TestFramework supports it, add a runtime test for void closure pattern

---

## GRDB-Specific Details

### Primary target methods (on `DatabasePool`)

| Method | Pattern | Priority |
|--------|---------|:--------:|
| `read(_:)` (sync) | `<T>(Database) throws -> T` | **P0** |
| `writeWithoutTransaction(_:)` (sync) | `<T>(Database) throws -> T` | **P0** |
| `unsafeRead(_:)` | `<T>(Database) throws -> T` | P1 |
| `unsafeReentrantRead(_:)` | `<T>(Database) throws -> T` | P1 |
| `unsafeReentrantWrite(_:)` | `<T>(Database) throws -> T` | P1 |
| `barrierWriteWithoutTransaction(_:)` | `<T>(Database) throws -> T` | P1 |

### What the user will pass for `T`

- **`GRDB.Row`** (non-frozen struct → C# class with `ISwiftObject`) — needs VWT Destroy after MarshalFromSwift
- **`Swift.Array<Row>`** (bound generic) — needs VWT Destroy
- **`Void`** (side-effect-only) — uses the void variant, no result buffer
- **Primitives (`Int`, `String`, `Bool`)** — if `where T : ISwiftObject` is too restrictive, may need relaxation or separate overloads in a future session

### Constraint analysis: `where T : ISwiftObject`

**What implements `ISwiftObject`** (verified in codebase):
- All generated classes (including GRDB `Row`, `DatabasePool`, etc.) ✅
- All generated non-frozen structs ✅
- All generated frozen structs (both blittable struct and class-projected variants) ✅
- All generated complex enums (emitted as classes) ✅
- Runtime collections: `SwiftString`, `SwiftArray<T>`, `SwiftDictionary`, `SwiftSet`, `SwiftOptional<T>` ✅

**What does NOT implement `ISwiftObject`**:
- C# primitives (`int`, `bool`, `double`) — used directly, not wrapped ❌
- Simple enums (emitted as bare C# `enum` value types) ❌

**Impact on GRDB usage:**
- `pool.Read<Row>(db => ...)` — ✅ works (`Row` is generated class/struct with `ISwiftObject`)
- `pool.Read<SwiftArray<Row>>(db => ...)` — ✅ works (`SwiftArray<T>` implements `ISwiftObject`)
- `pool.Read<int>(db => ...)` — ❌ blocked by constraint (primitives don't implement `ISwiftObject`)
- `pool.Read(db => { /* side effect */ })` — ✅ uses the `void` variant (no constraint)

The `ISwiftObject` constraint is correct for Session 4's scope. It covers the primary GRDB use cases (Row, Array\<Row\>, custom record types). Primitive returns are a lower-priority gap that can be addressed later via:
- Separate primitive overloads (`ReadInt32`, `ReadBool`, etc.)
- Removing the constraint and using `TypeMetadata` runtime dispatch
- Adding `ISwiftObject` to a primitive wrapper layer

This is explicitly **not in scope** for Session 4.

### Non-target methods

- **`writeInTransaction(_:_:)`** — Non-generic (Pattern D), blocked by `Database.TransactionCompletion` nested enum resolution. Not Session 4.
- **Async variants** (`asyncRead`, etc.) — Blocked by async+Sendable constraints. Not Session 4.

---

## Merge Conflict Risk Assessment

### Session 2 (Swiftinterface + Actor Isolation) overlap

| Shared File | Session 2 Area | Session 4 Area | Conflict Risk |
|-------------|---------------|---------------|:-------------:|
| `MemberEmissionValidator.cs` | New actor-isolation skip checks | Relaxed closure/placeholder checks | **Low** — different sections |
| `PInvokeEmitter.cs` | Actor annotation propagation | Generic closure P/Invoke params | **Low** — different parameters |

### Session 3 (Existential & Dictionary) overlap

| Shared File | Session 3 Area | Session 4 Area | Conflict Risk |
|-------------|---------------|---------------|:-------------:|
| `ClosureHandler.cs` | Existential param handling | Generic param handling | **Medium** — both modify `IsSupportedClosureParameterType` |
| `WrapperEmitter.Marshalling.cs` | Existential marshalling | Generic result buffer | **Medium** — both modify marshalling setup |

**Recommendation**: Session 4 can be implemented independently of Sessions 2 and 3. The main risk is `ClosureHandler.cs`. Make Session 4's changes at the top of `IsSupportedClosureParameterType` (before existential checks) to minimize conflicts.

---

## Test Strategy

### Unit tests

1. **`ClosureHandler` tests** (new or extended):
   - `HasGenericTypeParameters_ReturnType_ReturnsTrue` — `(Database) throws -> tau_0_0`
   - `HasGenericTypeParameters_ConcreteOnly_ReturnsFalse` — `(Database) throws -> Row`
   - `IsGenericClosureEligible_NoescapeWithMethodGeneric_ReturnsTrue`
   - `IsGenericClosureEligible_EscapingGeneric_ReturnsFalse`

2. **`MemberEmissionValidator` tests**:
   - `ShouldSkipMethodEmission_GenericThrowingClosure_DoesNotSkip`
   - `CanEmitMethod_GenericThrowingClosure_DoesNotSkip`
   - `CanEmitMethod_GenericEscapingClosure_StillSkips` (Pattern B)

3. **Emitter output tests**:
   - Verify Swift wrapper contains both returning and void variants
   - Verify C# output has `NativeMemory.AlignedAlloc` and `AlignedFree`
   - Verify C# output has `ValueWitnessTable->Destroy` on success path

### Integration tests

1. **TestFramework**: Add a test Swift library method with generic throwing closure:
   ```swift
   public func executeWithValue<T>(_ block: (Int) throws -> T) rethrows -> T
   ```
   Generate bindings and verify compilation.

2. **GRDB validation**: `./validate-libraries.sh --filter GRDB` — 32/32 maintained.

### Validation gate

1. GRDB `DatabasePool.read` and `writeWithoutTransaction` methods emit in generated C#
2. Both `T Read<T>(...) where T : ISwiftObject` and `void Read(...)` overloads present
3. Generated C# compiles with `dotnet build`
4. Swift wrapper compiles without errors (both returning and void variants)
5. Result buffer uses `NativeMemory.AlignedAlloc` with VWT-derived alignment
6. NO `VWT->Destroy` on success path (MarshalFromSwift transfers ownership)
7. `NativeMemory.AlignedFree` in unconditional `finally` block
8. Classifier rejects methods with constrained generic params or non-identity return
9. 32/32 library validation maintained
10. Unit test count >= 4161 (no regression)
11. Integration test count >= 700 (no regression)

---

## Risk Assessment

| Sub-task | Complexity | Risk | Confidence |
|----------|:----------:|:----:|:----------:|
| 4a. Classification | Low | Low | 95% |
| 4b. Monomorphized bridge (returning + void) | High | Medium-High | 65% |
| 4c. Error marshalling | Medium | Medium | 80% |

### Primary risks

1. **Swift `T = UnsafeMutableRawPointer` specialization**: If GRDB's `read<T>` has undocumented constraints on `T` that `UnsafeMutableRawPointer` doesn't satisfy, the wrapper won't compile. **Mitigation**: The sync `read<T>` has no constraints (verified in ABI JSON).

2. **Alignment correctness**: `NativeMemory.AlignedAlloc` is available in .NET 6+ and handles arbitrary alignment. The main risk is getting the alignment value wrong. **Mitigation**: Use `metadata.Alignment` directly from `ValueWitnessTable`, which is the authoritative source.

3. **VWT Destroy ordering**: Calling Destroy after MarshalFromSwift but before AlignedFree is critical. MarshalFromSwift must copy/retain the value before Destroy invalidates the buffer. **Mitigation**: Follow the established pattern in `SwiftDictionary.cs` and `SwiftSet.cs`.

4. **`where T : ISwiftObject` is too restrictive for primitives**: Users wanting `Read<Int32>()` will hit a constraint violation. **Mitigation**: This is a known limitation, documented above. Can be relaxed in a future session.

### Fallback strategy

If the `T = UnsafeMutableRawPointer` specialization doesn't work:
- **Fallback A**: Specialize for `T = Int` (trivial type, no constraints)
- **Fallback B**: Emit concrete overloads for the most common return types (`Read_Row`, `Read_Int`, etc.) — less elegant but guaranteed to work
- **Fallback C**: Use `@_silgen_name` to call the mangled symbol directly, passing type metadata as an explicit hidden parameter (requires understanding exact ABI layout)
