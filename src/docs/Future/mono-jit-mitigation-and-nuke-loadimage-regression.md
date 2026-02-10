# Mono JIT Mitigation Strategy

Date: 2026-02-09
Updated: 2026-02-09 (Claude Opus deep-dive review)

## Scope

This document captures the investigation and strategy for mitigating Mono `CallConvSwift` JIT/runtime failures that block real-world Swift interop scenarios. The goal is to support implementation planning for expanding wrapper-based routing to avoid crashes.

> **Nuke LoadImage regression**: Resolved in Phase I1/I1a. Archived to `src/docs/CompletedPhases/nuke-loadimage-regression.md`.

---

## 1. Root Cause Analysis

### The Bug

Mono's JIT incorrectly marks `CallConvSwift` P/Invoke frames as "async", then hits assertion `!ji->async` at `jit-info.c:918` during stack unwinding. This is a Mono-specific defect — the called Swift functions are synchronous.

### Three Distinct Crash Categories

| Category | Trigger | Example | Severity |
|----------|---------|---------|----------|
| **Existential metadata** | `swift_getExistentialTypeMetadata` via `CallConvSwift` | `SwiftArray<ExistentialContainer0>` construction | High — blocks any protocol-typed array |
| **Closure callbacks** | `[UnmanagedCallersOnly(CallConvs = CallConvSwift)]` callback invoked by Swift | Escaping closures with `SwiftSelf` context parameter | High — blocks all closure-taking APIs |
| **SwiftString operations** | `PInvoke_GetLength`, `PInvoke_GetUtf8ContiguousArray`, `PInvoke_WithUnsafeBytes` via `CallConvSwift` | `SwiftString.Length`, `SwiftString.ToString()` | High — blocks string conversion at runtime |

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

**Not yet used by**:
- `SwiftString.cs` runtime (`Length`, `ToString()`) — still uses direct `CallConvSwift` P/Invoke

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
- `TypeMetadata.cs:473-539` — Tries three P/Invoke workaround variants; if all fail with managed exceptions, throws descriptive `SwiftRuntimeException` explaining the limitation
- `[CrashRisk("reason")]` attribute — Marks test classes known to trigger process-fatal crashes
- `--safe-only` flag — Test runner skips `[CrashRisk]` classes entirely
- Test ordering — Safe tests run first, crash-risk last (so earlier results survive a process abort)

### What's NOT Yet Covered

| Gap | Current Behavior | Impact |
|-----|-----------------|--------|
| `SwiftString.Length` / `SwiftString.ToString()` | Direct `CallConvSwift` P/Invoke to `libswiftCore` | Crashes on Mono when called from closure callback context |
| Regular escaping closure callbacks | `[UnmanagedCallersOnly(CallConvs = CallConvSwift)]` in `ClosureEmitter.cs` | Crashes on Mono for any closure-taking method |
| Throwing closure callbacks | `[UnmanagedCallersOnly(CallConvs = CallConvSwift)]` in `ClosureEmitter.Throwing.cs` | Crashes on Mono for throwing closure params |
| Indirect-return closure callbacks | `[UnmanagedCallersOnly(CallConvs = CallConvSwift)]` in `ClosureEmitter.IndirectReturn.cs` | Crashes on Mono for closures returning complex types |
| `swift_getExistentialTypeMetadata` | Three P/Invoke workaround variants all hit process-fatal Mono assertion abort (try/catch cannot intercept) | Blocks protocol-typed arrays entirely |

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

### Strategy C: Existential Metadata via Swift Wrapper (Medium Impact, Low Effort)

**Problem**: `TypeMetadata.cs` tries three P/Invoke workarounds for `swift_getExistentialTypeMetadata`, all fail with the same JIT assertion.

**Proposal**: Generate a Swift wrapper function that creates existential type metadata on the Swift side:

```swift
@_cdecl("SBW_GetExistentialTypeMetadata")
public func sbw_getExistentialTypeMetadata(_ numProtocols: Int) -> UnsafeMutableRawPointer {
    // Call swift_getExistentialTypeMetadata on the Swift side (no JIT involved)
    // Return the metadata pointer as an opaque pointer
}
```

**Complexity**: Low — this is a thin wrapper. The challenge is that `swift_getExistentialTypeMetadata` also takes protocol descriptors, and constructing those from C# identifiers requires a lookup mechanism. For the zero-protocol case (`Any`), this is trivial. For N-protocol cases, we'd need a protocol descriptor registry in the wrapper library.

**Current workaround comparison**: Today, users write per-type Swift wrappers (e.g., `ImageRequest_initWithURLString_simple`). This strategy would create a generic metadata wrapper that covers the common case.

**Assumption to validate**: Swift-side access to the `swift_getExistentialTypeMetadata` entrypoint is practical for all protocol-count cases. For zero protocols (`Any`), this is trivial. For N-protocol cases, the wrapper needs protocol descriptor pointers — whether these can be obtained generically at runtime (vs. requiring compile-time knowledge of each protocol) determines the scope of this strategy.

**Proof gate**: `SwiftArray<ExistentialContainer0>` construction succeeds on iOS Simulator via the wrapper, without Mono JIT crash.
**Kill criteria**: If N-protocol cases require per-protocol compile-time registration that makes the wrapper no simpler than existing per-type wrappers.

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

### Step 1: Implement Strategy C (Existential Metadata Wrapper)

**Do first.** Lowest risk, lowest effort, immediate unblock for protocol-typed arrays. Also validates the end-to-end wrapper plumbing pattern (Swift `@_cdecl` wrapper → `CallingConvention.Cdecl` import → runtime integration) that all later strategies depend on.

**Deliverables**:
- Swift wrapper function in runtime support library (`SBW_GetExistentialTypeMetadata`)
- Wire `TypeMetadata.cs` to call the wrapper instead of direct `swift_getExistentialTypeMetadata`
- Unit tests + iOS Simulator runtime test confirming `SwiftArray<ExistentialContainer0>` works
- Proof gate: no Mono JIT crash on existential array construction

### Step 2: Prototype Strategy A (SwiftString Wrapper) — Time-Boxed Spike

**Run in parallel with or immediately after Step 1.** Strategy A has the highest impact but three unresolved implementation approaches. Time-box to 1-2 sessions to pick an approach:

1. Raw-word `unsafeBitCast` via `@_cdecl` (fastest, ABI-risky)
2. `NSString` boxing via `@_cdecl` (safe, adds overhead)
3. `@_silgen_name` with `UnsafeRawPointer` buffer pass-through (middle ground)

**Spike deliverable**: One working `SwiftString("hello").ToString()` round-trip on iOS Simulator via wrapper path, without JIT crash. Document which approach won and why.

### Step 3: Implement Strategy A (SwiftString Wrapper) — Full Rollout

**After spike chooses approach.** Replace `SwiftString.Length` and `SwiftString.ToString()` hot paths with the chosen wrapper approach. This is the highest-impact single fix — unblocks string-returning methods across the board.

**Deliverables**:
- Swift wrapper function(s) for string operations
- Updated `SwiftString.cs` with safe path (wrapper) + fallback (direct `CallConvSwift`)
- Unit tests + runtime tests

### Step 4: Implement Strategy D (Signature Risk Detection)

**After C and A are landed.** D is a force-multiplier, but only useful when there are concrete wrapper targets to route to. With existential and SwiftString wrappers in place, the generator can auto-detect risky signatures and route them.

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

| Step | Strategy | Effort | Impact | Depends On |
|------|----------|--------|--------|------------|
| **1** | C: Existential metadata wrapper | Low | Medium | Nothing |
| **2** | A: SwiftString spike (prototype) | Low | — | Nothing (can parallel with 1) |
| **3** | A: SwiftString full rollout | Medium | High | Step 2 |
| **4** | D: Signature risk detection | Medium | Medium | Steps 1 + 3 |
| **5** | B: Closure Cdecl expansion | High | High | Steps 1 + 3 + 4 |
| *ongoing* | E: NativeAOT migration | High | Complete | External (.NET 10 tooling) |

---

## 5. Code Location Reference

| File | Role | Key Lines |
|------|------|-----------|
| `SwiftString.cs` | 5 CallConvSwift P/Invokes to libswiftCore | 172-192 |
| `TypeMetadata.cs` | 3 failed workaround attempts + existential handling | 358-539 |
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

All three attempts in `TypeMetadata.cs:429-539` failed because the bug is in Mono's JIT frame classification, not in the P/Invoke marshalling:
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
