# Mono JIT Mitigation Strategy

Date: 2026-02-09
Updated: 2026-02-10 (Steps 1-3 complete: Strategy C, Strategy A full lifecycle, iOS Simulator validation)

## Scope

This document captures the investigation and strategy for mitigating Mono `CallConvSwift` JIT/runtime failures that block real-world Swift interop scenarios. The goal is to support implementation planning for expanding wrapper-based routing to avoid crashes.

> **Nuke LoadImage regression**: Resolved in Phase I1/I1a. Archived to `src/docs/CompletedPhases/nuke-loadimage-regression.md`.

---

## 1. Root Cause Analysis

### The Bug

Mono's JIT incorrectly marks `CallConvSwift` P/Invoke frames as "async", then hits assertion `!ji->async` at `jit-info.c:918` during stack unwinding. This is a Mono-specific defect — the called Swift functions are synchronous.

### Three Distinct Crash Categories

| Category | Trigger | Example | Severity | Status |
|----------|---------|---------|----------|--------|
| **Existential metadata** | `swift_getExistentialTypeMetadata` via `CallConvSwift` | `SwiftArray<ExistentialContainer0>` construction | High | **Resolved** (Strategy C) |
| **Closure callbacks** | `[UnmanagedCallersOnly(CallConvs = CallConvSwift)]` callback invoked by Swift | Escaping closures with `SwiftSelf` context parameter | High | Open |
| **SwiftString operations** | `PInvoke_GetLength`, `PInvoke_Create`, `PInvoke_GetUtf8ContiguousArray` via `CallConvSwift` | `SwiftString.Length`, `SwiftString.ToString()`, `new SwiftString(str)` | High | **Resolved** (Strategy A) |
| **VWT Destroy** | `ValueWitnessTable->Destroy()` indirect call via CallConvSwift function pointer | `MutableProps.Dispose()` (struct with String field) | Medium | Open (Step 3 finding) |

### Related Non-Crash Blockers

These are separate issues that compound with the JIT crash:

| Issue | Error | Impact |
|-------|-------|--------|
| **Non-blittable types** | `InvalidProgramException: Cannot use non-blittable types with Swift calling convention` | Blocks `SafeHandle`, `SwiftOptional<T>`, managed strings in `CallConvSwift` P/Invoke |
| **SafeHandle in async** | GC collects handle during Swift async suspension | Blocks async instance methods without manual ARC retain/release |

---

## 2. Current Mitigation Inventory

### What Already Works

The codebase has four proven mitigation layers, each addressing different scenarios:

#### Layer 1: Swift Wrapper Functions (`@_silgen_name` / `@_cdecl`)

**How it works**: Emit Swift-side wrapper functions that perform the risky operation (existential metadata lookup, string conversion, etc.) entirely on the Swift side, returning results via C-compatible types (`IntPtr`, `UnsafeMutableRawPointer`, C strings). C# calls the wrapper via `DllImport("SwiftBindings")`.

**Already used by**:
- `WrapperEmitter.Async.cs` — async method wrappers with `CallConvCdecl` callbacks
- `DefaultParameterOverloadEmitter.cs` — methods with trailing defaults
- `ArraySliceNormalizationEmitter.cs` — `ArraySlice<T>` → `Array<T>` conversion
- `WrapperEmitter.Marshalling.cs` (`EmitOpaqueReturnWrapper`) — opaque return type (`some Protocol`) boxing into existential containers
- `ExistentialBypassEmitter.cs` — constructor existential-arg bypass (omits existential params with defaults, lets Swift fill them in)
- Nuke hand-written wrappers (`ImageRequest_initWithURLString_simple`)

**Routing mechanism** (`PInvokeEmitter.cs:546`):
```
needsWrapperLib = IsAsync || hasOpaqueReturn || UsesWrapperLibrary
→ DllImport(AsyncLibraryName) instead of DllImport(moduleLib)
```

**Current truth about `CallConvSwift` in wrapper routing**: The wrapper routing only changes which **library** the P/Invoke links against. `PInvokeEmitter.cs:572` still emits `[UnmanagedCallConv(CallConvs = typeof(CallConvSwift))]` for all routed P/Invoke declarations. However, the codebase has **mixed patterns**: some wrapper surfaces already use explicit `CallingConvention.Cdecl` imports (e.g., `ProtocolProxyEmitter.SwiftObject.cs:124` for vtable setup, witness table access, and property accessors against `@_cdecl`/`@_silgen_name` wrapper exports). The `PInvokeEmitter` path and the `PInvokeHelperEmitter` path (used for generic-type declarations, `PInvokeHelperEmitter.cs:163`) are the two that unconditionally default to `CallConvSwift`.

The safety of wrapper routing comes from the Swift wrapper doing the risky work internally and exposing only blittable, C-compatible results. Current evidence suggests the `CallConvSwift` annotation on the C# P/Invoke to the wrapper is not itself the crash trigger — the crash appears to occur when the called function internally uses Swift-convention operations that confuse Mono's frame tracker. Since the wrapper is precompiled Swift code, Mono's JIT never processes its internal calls. (This inference is consistent with all observed behavior but has not been validated with a targeted A/B repro — e.g., calling the same wrapper function with `Cdecl` vs `CallConvSwift` on the C# side to confirm the annotation alone doesn't trigger the assertion.)

#### Layer 2: SBW_Utf8Slice String Bridge

**How it works**: `@frozen struct SBW_Utf8Slice { ptr: UnsafeMutablePointer<UInt8>, len: Int }` — a blittable struct that carries UTF-8 string data across the boundary. Swift wrapper allocates, C# decodes UTF-8 bytes and calls `SBW_Free()`.

**Already used by**:
- `WitnessDispatchEmitter.cs` — property getters returning strings
- `EnumHandler.RawRepresentable.cs` — enum `rawValue` accessors
- `WrapperEmitter.Async.cs` — async method callbacks returning strings
- `ProtocolProxyEmitter.InterfaceImpl.cs` — protocol proxy string handling

**Now also used by** (Strategy A, Step 2):
- `SwiftString.cs` runtime (`Length`, `ToString()`) — routes through `SBW_SwiftString_ToUtf8`/`SBW_SwiftString_GetCount` in `libSwiftBindingsRuntime.dylib` via `CallingConvention.Cdecl` (similar pattern to `SBW_Utf8Slice` but dedicated functions, not the generated struct)

#### Layer 3: Async Callback Pattern (`CallConvCdecl`)

**How it works**: `ClosureEmitter.Async.cs` emits `[UnmanagedCallersOnly(CallConvs = typeof(CallConvCdecl))]` callbacks. The Swift wrapper calls into C# via `@convention(c)` function pointers, completely avoiding `CallConvSwift` for the callback direction.

**Already used by**: All async+throwing closure callbacks, all async method completion handlers.

**Not used by**: All non-async closure callback variants still emit `CallConvSwift`:
- Regular escaping closures — `ClosureEmitter.cs:81`
- Throwing closure callbacks — `ClosureEmitter.Throwing.cs:68`
- Indirect-return closure callbacks — `ClosureEmitter.IndirectReturn.cs:55`

#### Layer 4: Defensive Runtime Handling (Observability/Diagnostics Only)

**Important distinction**: This layer provides **diagnostics and test isolation**, not crash prevention. The Mono JIT assertion (`!ji->async` at `jit-info.c:918`) is a **process-fatal abort** that bypasses all managed exception handlers. The try/catch in `TypeMetadata.cs:478` will never catch this assertion — it only catches managed exceptions from workaround attempts that fail for other reasons (e.g., invalid metadata pointer).

**What it actually does**:
- `TypeMetadata.cs` — Previously tried three P/Invoke workaround variants (lines 473-539, still present for reference). **Now superseded** by `TryGetExistentialTypeMetadataViaWrapper` which routes through the Swift wrapper (Strategy C). The old workaround function `TryGetExistentialTypeMetadataWithWorkarounds` is no longer called from any resolution path.
- `[CrashRisk("reason")]` attribute — Marks test classes known to trigger process-fatal crashes
- `--safe-only` flag — Test runner skips `[CrashRisk]` classes entirely
- Test ordering — Safe tests run first, crash-risk last (so earlier results survive a process abort)

### What's NOT Yet Covered

| Gap | Current Behavior | Impact |
|-----|-----------------|--------|
| ~~`SwiftString` full lifecycle~~ | ~~Direct `CallConvSwift` P/Invoke to `libswiftCore`~~ | **Resolved** — Strategy A wrappers for Create, ToString, Length (Steps 2+3). Constructor + accessor fully CallConvSwift-free on C# side. |
| Regular escaping closure callbacks | `[UnmanagedCallersOnly(CallConvs = CallConvSwift)]` in `ClosureEmitter.cs` | Crashes on Mono for any closure-taking method |
| Throwing closure callbacks | `[UnmanagedCallersOnly(CallConvs = CallConvSwift)]` in `ClosureEmitter.Throwing.cs` | Crashes on Mono for throwing closure params |
| Indirect-return closure callbacks | `[UnmanagedCallersOnly(CallConvs = CallConvSwift)]` in `ClosureEmitter.IndirectReturn.cs` | Crashes on Mono for closures returning complex types |
| ~~`swift_getExistentialTypeMetadata`~~ | ~~Three P/Invoke workaround variants all hit process-fatal Mono assertion abort~~ | **Resolved** — Strategy C wrapper implemented (zero-protocol case). See Step 1 status below. |
| **VWT Destroy (new)** | `metadata.ValueWitnessTable->Destroy()` indirect function pointer call via CallConvSwift | Crashes on Mono when `Dispose()` is called on types with non-trivial fields (e.g. MutableProps with String). Also corrupts frame tracker for later GC stack walks. |
| **VWT InitializeWithCopy** | `metadata.ValueWitnessTable->InitializeWithCopy()` in `MarshalToSwift` | Indirect CallConvSwift function pointer call. Risk of frame tracker corruption. |

---

## 3. Mitigation Strategies (Recommended Priority Order)

### Strategy A: SwiftString Runtime Wrapper (High Impact, Medium Effort)

**Problem**: `SwiftString.cs` has 5 P/Invoke declarations, all using `CallConvSwift` against `libswiftCore`:
- `PInvoke_Create` (line 177) — string construction
- `PInvoke_GetLength` (line 182) — `.Length` property
- `PInvoke_getMetadata` (line 174) — type metadata
- `PInvoke_GetUtf8ContiguousArray` (line 186) — UTF-8 conversion step 1
- `PInvoke_WithUnsafeBytes` (line 191) — UTF-8 conversion step 2 (also uses `delegate* unmanaged[Swift]` callback)

**Proposal**: Add a Swift wrapper library function that takes a `SwiftString.Buffer` (as raw bytes) and returns `SBW_Utf8Slice`:

```swift
@_silgen_name("SBW_SwiftString_ToUtf8")
public func sbw_swiftString_toUtf8(_ bufferPtr: UnsafeRawPointer, _ bufferSize: Int) -> SBW_Utf8Slice {
    let str: String = // reconstruct from buffer bytes
    let utf8 = Array(str.utf8)
    let ptr = UnsafeMutablePointer<UInt8>.allocate(capacity: utf8.count)
    ptr.initialize(from: utf8, count: utf8.count)
    return SBW_Utf8Slice(ptr: ptr, len: utf8.count)
}

@_silgen_name("SBW_SwiftString_GetLength")
public func sbw_swiftString_getLength(_ bufferPtr: UnsafeRawPointer, _ bufferSize: Int) -> Int {
    let str: String = // reconstruct from buffer bytes
    return str.count
}
```

**C# side**: Add alternative `ToString()` / `Length` implementations in `SwiftString.cs` that route through wrapper when available:
```csharp
// Check if wrapper library is loaded, use safe path; fallback to direct CallConvSwift
```

**Complexity**: Medium. The tricky part is reconstructing a `Swift.String` from its raw `Buffer` bytes on the Swift side without using `@convention(c)` — the buffer layout is ABI-stable but internal. An alternative is passing the buffer via `Unmanaged<NSString>` or using `withExtendedLifetime`. This needs prototyping.

**Alternative approach**: Instead of reconstructing from bytes, the wrapper could take an opaque pointer to the string (retained via `Unmanaged.passRetained`) and call `.count` / `.utf8CString` on the Swift side. This avoids ABI layout assumptions:

```swift
@_silgen_name("SBW_SwiftString_GetLength_Safe")
public func sbw_swiftString_getLength_safe(_ strPtr: UnsafeMutableRawPointer) -> Int {
    let str = Unmanaged<NSString>.fromOpaque(strPtr).takeUnretainedValue() as String
    return str.count
}
```

**Risk**: `Swift.String` is a value type, not a reference type. `Unmanaged` requires class types. We'd need to box the string first (e.g., wrap in `NSString` via bridging or a custom box class). This adds overhead.

**Simplest viable approach**: A single wrapper function that takes a `Swift.String` by value (using the Swift calling convention on the Swift side — fine since this runs entirely in Swift code) and returns the UTF-8 bytes:

```swift
@_cdecl("SBW_SwiftString_UTF8Bytes")
public func sbw_swiftStringUtf8Bytes(
    _ buf0: Int, _ buf1: Int,  // Swift.String is 2 words on arm64
    _ outPtr: UnsafeMutablePointer<UnsafeMutablePointer<UInt8>?>,
    _ outLen: UnsafeMutablePointer<Int>
) {
    // Reconstruct String from raw words
    let str = unsafeBitCast((buf0, buf1), to: String.self)
    // ...
}
```

**Assessment**: This approach requires understanding `String`'s ABI layout (2 words on arm64, but this is `@frozen` and stable). The `@_cdecl` export uses C calling convention, so C# calls it via `CallingConvention.Cdecl` — no `CallConvSwift` at all.

**Assumption to validate**: The raw-word `unsafeBitCast` approach is ABI-risky until a concrete prototype proves correctness on the target matrix (arm64 iOS Simulator + device). `String`'s internal representation may vary between small/large string forms. The `NSString` boxing approach is safer but adds overhead. **A prototype must be tested before committing to an approach.**

**Proof gate**: `SwiftString("hello").ToString()` returns `"hello"` on iOS Simulator via the wrapper path, without Mono JIT crash.
**Kill criteria**: If no safe mechanism exists to pass a `Swift.String` value to a `@_cdecl` function without `CallConvSwift` on the C# side (would require redesigning `SwiftString` to hold a boxed reference instead of a value-type buffer).

### Strategy B: Expand Closure Callback to Use Cdecl (High Impact, High Effort)

**Problem**: `ClosureEmitter.cs:81` emits all regular escaping closure callbacks with `CallConvSwift`:
```csharp
[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
```

Meanwhile, `ClosureEmitter.Async.cs:51` already uses the safe pattern:
```csharp
[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
```

**Proposal**: Route regular escaping closures through a Swift wrapper that adapts between `@convention(swift)` (what Swift expects for closures) and `@convention(c)` (what C# can safely provide). The Swift wrapper would:
1. Accept a `@convention(c)` function pointer + context from C#
2. Create a Swift closure that calls the C function pointer
3. Pass that closure to the original Swift method

**Key challenge**: This requires the generator to emit Swift wrapper functions for every method that takes an escaping closure parameter. The wrapper needs to know the closure's full type signature to create the adapter.

**Existing precedent**: The async+throwing closure pattern already does exactly this — `ClosureEmitter.Async.cs` + corresponding Swift helpers use `@convention(c)` exclusively. The regular closure path would mirror this pattern but without the async/throwing mechanics.

**Scope — all three non-async closure emitters need changes**:
- `ClosureEmitter.cs` — regular escaping closures (line 81)
- `ClosureEmitter.Throwing.cs` — throwing closure callbacks (line 68)
- `ClosureEmitter.IndirectReturn.cs` — indirect-return closure callbacks (line 55)

**Effort**: High — requires:
1. New `ClosureWrapperEmitter` that generates Swift adapter functions for all three callback variants
2. Modifications to all three `ClosureEmitter` files to emit `CallConvCdecl` callbacks
3. Updates to `SwiftClosureData` marshalling to pass C function pointers
4. Regression testing across all closure-taking APIs

**Proof gate**: Single closure-taking method (e.g., `Array.sort(by:)`) works end-to-end on iOS Simulator via Cdecl callback + Swift adapter, without Mono JIT crash.
**Kill criteria**: If the `@convention(swift)` → `@convention(c)` adapter in Swift introduces measurable overhead (>10% on hot path) or can't handle all closure type signatures generically.

### Strategy C: Existential Metadata via Swift Wrapper (Medium Impact, Low Effort) — IMPLEMENTED

**Status**: Implemented and validated (2026-02-10). Zero-protocol existential case (`Any` / `ExistentialContainer0`) works end-to-end. N-protocol cases deferred (require protocol descriptor pointers).

**Implementation summary**:
- `SwiftBindingsRuntime.swift`: `@_cdecl("SwiftBindings_GetExistentialTypeMetadata")` wrapper calls `swift_getExistentialTypeMetadata` via `@_silgen_name` import, returning metadata pointer. Uses `MetadataResponse` tuple to correctly capture the 2-word return on ARM64.
- `TypeMetadata.cs`: `RuntimeNativeMethods.GetExistentialTypeMetadata()` via `CallingConvention.Cdecl` → `TryGetExistentialTypeMetadataViaWrapper()`. All call paths now use wrapper-only — old workaround P/Invokes retained in file for documentation but removed from resolution paths.
- `run-runtime-tests.sh`: Injects `libSwiftBindingsRuntime.dylib` into app bundle post-build (Step 2.5) to sidestep InstallNameTool failure while making the dylib available at runtime.

**Design decision — wrapper-only, hard-fail without dylib**: `TryGetExistentialTypeMetadataViaWrapper` catches `DllNotFoundException` and `EntryPointNotFoundException`, returns false. Callers throw `SwiftRuntimeException` with a descriptive message — no fallback to direct `CallConvSwift` P/Invoke (process-fatal on Mono, unnecessary on non-Mono where the wrapper also works). This means `libSwiftBindingsRuntime.dylib` is a hard dependency for existential metadata. Downstream consumers must include the dylib in their application bundle.

**Proof gate passed**: `ExistentialMetadataTests` on iOS Simulator — 2/2 tests pass, metadata kind=Existential, no Mono JIT crash.

**Remaining scope**: N-protocol existentials (`ExistentialContainer1`+) require protocol descriptor pointers passed to `swift_getExistentialTypeMetadata`. The wrapper currently returns nil for `numProtocols > 0`, and the C# side throws `SwiftRuntimeException` explaining the limitation.

**Original problem**: `TypeMetadata.cs` tried three P/Invoke workarounds for `swift_getExistentialTypeMetadata`, all failed with the same JIT assertion.

**Kill criteria** (for N-protocol expansion): If N-protocol cases require per-protocol compile-time registration that makes the wrapper no simpler than existing per-type wrappers.

### Strategy D: Generator-Level Signature Risk Detection (Medium Impact, Medium Effort)

**Problem**: The generator currently has no mechanism to detect whether a given P/Invoke signature will trigger the Mono JIT crash. Wrapper routing is based on feature flags (`IsAsync`, `UsesWrapperLibrary`), not on signature analysis.

**Proposal**: Add a `IsMonoJitRisk()` analysis pass that flags methods with:
1. Closure parameters (escaping) — triggers `CallConvSwift` callback
2. `SwiftString` return through non-wrapper path — triggers `PInvoke_GetLength`
3. Existential container parameters — triggers metadata lookup
4. Non-blittable types in signature — triggers `InvalidProgramException`

Flagged methods would automatically set `UsesWrapperLibrary = true` and emit corresponding Swift wrappers.

**Effort**: Medium — the detection is straightforward (pattern match on TypeSpec), but generating appropriate wrappers for each case requires case-specific emitter logic.

**Proof gate**: At least one previously-emitted method that would crash on Mono is auto-detected and wrapper-routed without manual `UsesWrapperLibrary` override.
**Kill criteria**: If the number of distinct wrapper shapes needed exceeds what's practical to emit generically (each requires a different Swift wrapper template).

### Strategy E: NativeAOT Migration (Eliminates JIT Bug, High Effort)

**Assessment from `nativeaot-investigation.md`**:
- **Blocker 1 (JIT assertion)**: Definitively bypassed — NativeAOT has no JIT
- **Blocker 2 (Non-blittable types)**: Persists — ILCompiler enforces same restriction
- **Blocker 3 (SafeHandle in async)**: Uncertain — needs testing

NativeAOT eliminates the most severe blocker but requires the same workarounds for non-blittable types and async SafeHandle. It's the right long-term target but not a near-term fix for Mono-based deployments.

---

## 4. Execution Plan

### Step 1: Implement Strategy C (Existential Metadata Wrapper) — DONE

**Completed 2026-02-10.** Validated end-to-end wrapper plumbing pattern that later strategies depend on.

**Delivered**:
- `SwiftBindingsRuntime.swift`: `@_cdecl("SwiftBindings_GetExistentialTypeMetadata")` wrapper
- `TypeMetadata.cs`: `RuntimeNativeMethods` + `TryGetExistentialTypeMetadataViaWrapper`, wrapper-only resolution (no crashy fallback)
- `ExistentialMetadataWrapperTests.cs`: 4 unit tests (zero-protocol success, ExistentialContainer0, non-zero throws, error message content)
- `ExistentialMetadataTests.cs`: 2 Tier 2 runtime tests on iOS Simulator — proof gate passed
- Baselines maintained: 1760 unit, 699 integration, 94/94 TestFramework coverage

### Step 2: Prototype Strategy A (SwiftString Wrapper) — Spike DONE

**Completed 2026-02-10.** Spike validated the `UnsafeRawPointer` buffer pass-through approach (option 3, via `@_cdecl`).

**Approach chosen**: Pass `IntPtr` to the `SwiftString.Buffer` (16-byte raw representation), Swift side uses `bufferPtr.assumingMemoryBound(to: String.self).pointee` to get a retain-balanced copy. This avoids both the ABI risk of raw-word `unsafeBitCast` (option 1) and the overhead of `NSString` boxing (option 2). The `assumingMemoryBound` + `.pointee` pattern correctly increments the refcount when reading and decrements on scope exit, leaving the original buffer unaffected.

**Why this won over the alternatives**:
- **Option 1 (raw-word `unsafeBitCast`)**: Requires passing 2 `Int` parameters and reconstructing a `String` via `unsafeBitCast((word0, word1), to: String.self)`. The reconstructed value has no ARC balance — when Swift destroys it at scope exit, it decrements the refcount of the original string (use-after-free risk for heap-allocated large strings). Would require a manual `_fixLifetime` or `Unmanaged` workaround, but `String` is a value type so `Unmanaged` can't be used directly.
- **Option 2 (`NSString` boxing)**: Requires bridging `String` → `NSString` (Obj-C heap allocation) → `Unmanaged<NSString>` → pointer. Adds overhead and complexity. Not needed since option 3 works.
- **Option 3 (pointer pass-through)**: Single `UnsafeRawPointer` parameter, `assumingMemoryBound` reads the existing memory as a `String` with correct ARC semantics. Simplest, safest, no extra allocations. Compiles clean on Swift 6 (no `BitwiseCopyable` restrictions on typed pointer access, unlike `load(as:)`).

**Delivered (spike + full rollout combined)**:
- `SwiftBindingsRuntime.swift`: 3 `@_cdecl` functions — `SBW_SwiftString_ToUtf8`, `SBW_SwiftString_GetCount`, `SBW_SwiftString_FreeUtf8`
- `SwiftString.cs`: `RuntimeNativeMethods` inner class with `CallingConvention.Cdecl` P/Invokes, static `_useWrapperPath` flag, refactored `ToString()` and `Length` with wrapper-first + direct fallback
- `SwiftStringWrapperTests.cs`: 10 unit tests covering ASCII, empty, Unicode emoji, multi-byte UTF-8, long strings (heap-allocated), and entry point export verification
- Baselines maintained: 1770 unit + runtime, 699 integration

**Design decision — wrapper-first with fallback**: Unlike Strategy C (wrapper-only, hard-fail), Strategy A uses a try/catch fallback pattern. On first call, `_useWrapperPath` is true and the wrapper is attempted. If `DllNotFoundException` or `EntryPointNotFoundException` is caught, the flag is set to false and all subsequent calls go directly to the existing `CallConvSwift` P/Invokes. This allows the runtime to work on both Mono (wrapper path) and environments where the dylib isn't deployed (direct path). The flag is static so the exception penalty is paid at most once.

**Proof gate**: `SwiftString("hello").ToString()` returns `"hello"` via wrapper, `SwiftString("日本語テスト").Length` returns 6 (character count, not UTF-8 byte count). iOS Simulator runtime validation deferred to Step 3.

### Step 3: Runtime Validation on iOS Simulator — DONE

**Completed 2026-02-10.** SwiftString wrapper path validated end-to-end on iOS Simulator. Also extended Strategy A to wrap `PInvoke_Create` (string construction), making constructor + read paths CallConvSwift-free on the C# side. Dispose path (VWT Destroy) remains unwrapped — see crash category 4 below.

**Proof gate passed**: 8 StringMarshallingTests pass on iOS Simulator via wrapper path, exercising `SwiftString` construction + `.ToString()` + `.Length` without Mono JIT crash. Tests include ASCII, Unicode, emoji, and edge case strings.

**Extended Strategy A deliverables**:
- `SwiftBindingsRuntime.swift`: 2 new `@_cdecl` functions — `SBW_SwiftString_Create` (UTF-8 → String buffer), `SBW_SwiftString_Destroy` (buffer deinit)
- `SwiftString.cs`: Constructor now routes through `RuntimeNativeMethods.SwiftString_Create` via wrapper-first pattern (same as ToString/Length)
- `SwiftStringWrapperTests.cs`: 3 additional unit tests for Create wrapper (ASCII, Unicode, empty)
- Baselines maintained: 1760 unit, 699 integration, 133 runtime (up from 130)

**New finding — ValueWitnessTable Destroy via CallConvSwift (crash category 4)**:
- `SwiftSafeHandle<T>.ReleaseHandle()` calls `metadata.ValueWitnessTable->Destroy()` through an indirect function pointer with the Swift calling convention
- On Mono, this triggers the same `!ji->async` JIT assertion as direct CallConvSwift P/Invokes
- Deterministic trigger: `MutableProps.Dispose()` (struct with String field) → crash during `Destroy`
- Non-deterministic trigger: GC finalization timing can cause the assertion during unrelated stack walks after any CallConvSwift call has corrupted Mono's frame tracker
- Mitigation: `SBW_SwiftString_Destroy` wrapper added but not yet wired into `SwiftSafeHandle` generic path (requires type-specific dispatch or per-type generator-emitted destroy wrappers)
- `NegativePathTests.TestDisposedMutablePropsName*` demoted to Tier3 to prevent process-fatal crash during Tier2 runs

**MutableProps promotion blocked**: Cannot promote MutableProps tests from Tier3 to Tier2 due to the VWT Destroy crash. The SwiftString.ToString() path is safe, but any explicit `Dispose()` on types with String fields triggers the crash.

### Step 4: Implement Strategy D (Signature Risk Detection)

**After C and A are landed (both done).** D is a force-multiplier, but only useful when there are concrete wrapper targets to route to. With existential and SwiftString wrappers in place, the generator can auto-detect risky signatures and route them.

**Deliverables**:
- `IsMonoJitRisk()` analysis pass in the generator
- Auto-sets `UsesWrapperLibrary = true` for methods with closures, existential params, or SwiftString returns
- Unit tests verifying detection + routing

### Step 5: Implement Strategy B (Closure Cdecl Expansion)

**Do last.** Largest surface area (three closure emitter files), highest regression risk, most complex Swift adapter generation. Should build on all the proven patterns from Steps 1-4.

**Deliverables**:
- Swift adapter wrappers for `@convention(swift)` → `@convention(c)` closure bridging
- All three closure emitter files updated to emit `CallConvCdecl` callbacks
- Regression testing across all closure-taking APIs

### Strategy E (NativeAOT) — Ongoing / Opportunistic

Not a sequential step. Pursue when .NET 10 NativeAOT iOS tooling stabilizes. Eliminates the JIT bug entirely but doesn't replace the non-blittable and async SafeHandle workarounds.

### Summary Table

| Step | Strategy | Effort | Impact | Depends On | Status |
|------|----------|--------|--------|------------|--------|
| **1** | C: Existential metadata wrapper | Low | Medium | Nothing | **DONE** |
| **2** | A: SwiftString spike + rollout | Medium | High | Nothing | **DONE** |
| **3** | A: iOS Simulator validation + Create wrapper | Low | High | Step 2 | **DONE** |
| **4** | D: Signature risk detection | Medium | Medium | Steps 1 + 2 | Pending |
| **5** | B: Closure Cdecl expansion | High | High | Steps 1 + 2 + 4 | Pending |
| *ongoing* | E: NativeAOT migration | High | Complete | External (.NET 10 tooling) |

---

## 5. Code Location Reference

| File | Role | Key Lines |
|------|------|-----------|
| `SwiftString.cs` | 5 CallConvSwift P/Invokes + RuntimeNativeMethods wrapper path (Create, ToUtf8, GetCount, Destroy, FreeUtf8) | Full file |
| `SwiftBindingsRuntime.swift` | SwiftString wrapper functions (`SBW_SwiftString_*`) — 5 @_cdecl exports | 109-215 |
| `SwiftStringWrapperTests.cs` | 13 unit tests for SwiftString wrapper path (incl. Create) | Full file |
| `SwiftHandle.cs` | `SwiftSafeHandle<T>.ReleaseHandle()` — VWT Destroy call (crash vector) | 101-141 |
| `TypeMetadata.cs` | Wrapper path (`TryGetExistentialTypeMetadataViaWrapper`) + retained workaround P/Invokes (reference only) | 362-571 |
| `PInvokeEmitter.cs` | Wrapper lib routing decision | 544-549 |
| `PInvokeHelperEmitter.cs` | Generic-type P/Invoke declarations (also defaults to CallConvSwift) | 163 |
| `ClosureEmitter.cs` | Regular escaping closures use CallConvSwift | 81 |
| `ClosureEmitter.Throwing.cs` | Throwing closure callbacks use CallConvSwift | 68 |
| `ClosureEmitter.IndirectReturn.cs` | Indirect-return closure callbacks use CallConvSwift | 55 |
| `ClosureEmitter.Async.cs` | Async closures use CallConvCdecl (safe pattern) | 51 |
| `WrapperEmitter.Marshalling.cs` | Opaque return wrapper (`EmitOpaqueReturnWrapper`) | 16 |
| `ExistentialBypassEmitter.cs` | Constructor existential-arg bypass | 9 |
| `ProtocolProxyEmitter.SwiftObject.cs` | Uses CallingConvention.Cdecl for wrapper imports | 124 |
| `MethodDecl.cs` | `UsesWrapperLibrary` flag | 86 |
| `Utf8SliceEmitter.cs` | SBW_Utf8Slice struct + SBW_Free | Full file |
| `WrapperEmitter.Async.cs` | Proven async wrapper pattern | Full file |
| `DefaultParameterOverloadEmitter.cs` | Sets UsesWrapperLibrary=true | 163 |
| `ArraySliceNormalizationEmitter.cs` | Sets UsesWrapperLibrary=true | 328 |

---

## 6. Key Observations from Code Review

### Mixed calling convention patterns already exist in the codebase

`PInvokeEmitter.cs:572` emits `[UnmanagedCallConv(CallConvs = typeof(CallConvSwift))]` for all generated P/Invoke declarations, including wrapper-routed ones. However, other emitters already use `CallingConvention.Cdecl` for their wrapper imports:
- `ProtocolProxyEmitter.SwiftObject.cs:124` — vtable setup, witness table access, property accessors
- `ClosureEmitter.Async.cs:51` — async+throwing closure callbacks

This means the codebase has **two coexisting patterns** for calling wrapper functions: `CallConvSwift` (from PInvokeEmitter) and `Cdecl` (from ProtocolProxy/Async emitters). For `@_cdecl` or `@_silgen_name` wrappers that use C calling convention on the Swift side, `Cdecl` is more correct. Normalizing wrapper-routed imports to `Cdecl` would reduce Mono JIT exposure and align with the existing precedent.

### The async closure pattern is the template for all closure safety

`ClosureEmitter.Async.cs` demonstrates the complete safe pattern:
- C# callback: `[UnmanagedCallersOnly(CallConvs = typeof(CallConvCdecl))]`
- Swift side: `@convention(c)` function pointer types
- Data flow: C# allocates `GCHandle` → passes `IntPtr` context → Swift calls C function → C# retrieves delegate from context

The only thing preventing regular closures from using this pattern is that Swift expects `@convention(swift)` closures (with context in the self register), not `@convention(c)` function pointers. A Swift adapter wrapper would bridge this gap.

### SwiftString is the hidden cascade trigger

Many methods that appear safe (no closures, no existentials) still crash because their return type goes through `SwiftString.ToString()` which calls `PInvoke_GetLength` via `CallConvSwift`. This makes SwiftString the single highest-impact fix target — a safe `ToString()` path would unblock string-returning methods across the board.

### The three TypeMetadata workaround attempts are architecturally informative

All three attempts in `TypeMetadata.cs` (retained for reference, no longer called) failed because the bug is in Mono's JIT frame classification, not in the P/Invoke marshalling:
1. `[SuppressGCTransition]` — GC transition is not the trigger
2. `CallingConvention.Cdecl` — calling convention on C# side doesn't change Mono's frame tracking for the actual native call
3. `nint` return type — return type marshalling is not the trigger

This confirms that **no C#-side attribute manipulation can fix the bug**. The only viable mitigation is to not enter the Mono JIT's `CallConvSwift` code path at all — which means Swift-side wrappers with C calling convention.

---

## 7. Upstream Filing Status

Three upstream issues are drafted in `upstream-bug-reports-draft.md`:
1. **Bug**: JIT assertion `!ji->async` — clear defect, file as bug report
2. **Feature**: Non-blittable type support with `CallConvSwift` — likely intentional limitation, file as feature request
3. **Question**: SafeHandle/SwiftSelf in async P/Invoke — post as comment on Swift interop tracking issue

Filing is deferred until the repo goes public (provides concrete reproduction code as context).

---

## 8. Validation Checklist

For each mitigation strategy implemented:
- [ ] Unit tests covering the new wrapper path
- [ ] Integration tests verifying end-to-end (Swift → wrapper → C# → result)
- [ ] Regression check: `./run-tests.sh` baseline maintained
- [ ] TestFramework: `./build-and-test.sh` + coverage report shows no degradation
- [ ] Library validation: affected library builds remain clean
- [ ] Runtime test on iOS Simulator confirming the previously-crashing path now works

### Strategy C (Existential Metadata) — Completed 2026-02-10

- [x] Unit tests: `ExistentialMetadataWrapperTests.cs` — 4 tests (zero-protocol, ExistentialContainer0, non-zero throws, error message)
- [x] End-to-end: `ExistentialMetadataTests.cs` — Tier 2 runtime tests on iOS Simulator
- [x] Regression: 1760 unit / 699 integration / 94/94 TestFramework coverage
- [x] Runtime proof gate: zero-protocol existential metadata via wrapper, no Mono JIT crash
- **Note**: `libSwiftBindingsRuntime.dylib` is now a hard runtime dependency for existential metadata. Without it, `GetExistentialTypeMetadata` and `TryGetTypeMetadata<ExistentialContainerN>` throw `SwiftRuntimeException`. Downstream consumers must include the dylib in their app bundle.

### Strategy A (SwiftString Wrapper) — Constructor + Read Paths Completed 2026-02-10

- [x] Unit tests: `SwiftStringWrapperTests.cs` — 13 tests (ASCII, empty, Unicode emoji, multi-byte UTF-8, long strings, Create wrapper, entry points)
- [x] Regression: 1760 unit / 699 integration / 133 runtime
- [x] Runtime proof gate: 8 StringMarshallingTests pass on iOS Simulator via wrapper path — construction, ToString, Length all validated. No Mono JIT crash.
- **Approach**: `UnsafeRawPointer` + `assumingMemoryBound(to: String.self).pointee` via `@_cdecl`. Retain-balanced, no ABI risk, no `BitwiseCopyable` issues.
- **Design**: Wrapper-first with fallback — `_useWrapperPath` static flag, DllNotFoundException/EntryPointNotFoundException downgrade to direct `CallConvSwift` path. Hard dependency on Mono (throws `SwiftRuntimeException` if wrapper unavailable); fallback to direct path allowed on non-Mono runtimes.
- **Step 3 extension**: Added `SBW_SwiftString_Create` (constructor wrapper) and `SBW_SwiftString_Destroy` (deinit wrapper). Constructor + read paths (Create, ToUtf8, GetCount, FreeUtf8) are CallConvSwift-free. Destroy wrapper is exported but not yet wired into `SwiftSafeHandle<SwiftString>` generic path — dispose still uses VWT Destroy via CallConvSwift.
- **VWT Destroy blocker**: MutableProps.Dispose() still crashes because `SwiftSafeHandle<T>.ReleaseHandle` calls the generic VWT Destroy path. The SwiftString-specific `SBW_SwiftString_Destroy` exists but needs type-specific dispatch in `SwiftSafeHandle` to use it. `NegativePathTests` MutableProps dispose tests demoted to Tier3.
