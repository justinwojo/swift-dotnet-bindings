# Async-Closure Bridge — End-to-End Implementation Plan

Single consolidated plan for bridging `@escaping (Args...) async [throws] -> T`
closure parameters between C# and Swift. Revised after Codex review (round 1).

**Session layout:** 0 (spike, single-invoke ABI proof) → A (baseline +
`handle.Free()` removal + multi-invoke test) → B (args) → C (non-throwing)
→ D (hardening — see §3.2).

---

## 1. Background & Current State

The generator skips methods taking async closures via **five independent
gates** (corrected after Codex review — the prior draft only named one):

| # | Location | Behavior |
|---|---|---|
| G1 | `ClosureHandler.cs:203-216` (B13 + CX-12) | Type-level rejection: async+throws+args, and async-only+non-void |
| G2 | `ClosureEmitter.SwiftWrapper.cs:1102-1112` | Standalone Cdecl wrapper path refuses methods with any async-throwing closure |
| G3 | `MethodWrapperEmitter.cs:81` | Inline `@_cdecl` wrapper path: `HasAnyAsyncClosure(env)` → return false |
| G4 | `MethodWrapperEmitter.cs:309` | Adapter-code emitter filters async-throwing closures |
| G5 | `MemberValidationPipeline.cs:411` | Validation pipeline returns skip reason `"async_closure_params"` via `WrapperValidation.HasAnyAsyncClosure` |

`WrapperValidation.HasAnyAsyncClosure` (`WrapperValidation.cs:419`) is the
shared helper driving G3 and G5 — relaxing that helper is the primary
lever. G4's inverse predicate (`!IsAsyncThrowingClosure(…)`) in the
sync-path emitter must **stay** rejecting async closures (that path can't
emit an async adapter); the new async-closure emission is a separate
branch.

### What exists today (C# side, mostly complete)

| Component | File | Status |
|---|---|---|
| `AsyncClosureHelper.RunAsync<T>` / `RunVoidAsync` | `src/Swift.Runtime/src/Swift/Runtime/AsyncClosureHelper.cs` | Live — **single-shot** (frees GCHandle at line 81 after one invocation) |
| `AsyncThrowingClosureState<T>` / `Void` | `src/Swift.Runtime/src/Swift/Runtime/AsyncThrowingClosureState.cs` | Live — `Func<Task<T>>` only; dormant `CancellationTokenSource` field |
| `DataAsyncClosureHelper.RunDataAsync` | `src/Swift.Bindings.Apple/Sources/Foundation/DataAsyncClosureHelper.cs` | Live — uses a **different success-callback ABI** `(boxPtr, bytesPtr, length)` |
| `ClosureEmitter.Async.cs` | `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.Async.cs` | Live — emits the C# `Start` thunk + state-marshalling setup |
| P/Invoke arg emission | `PInvokeEmitter.cs:393-400` | Live — **emits context first, then start func pointer** |

### What is missing

1. **Swift-side emitter** — no `@_cdecl` wrapper emits
   `withCheckedThrowingContinuation`, `ContinuationBox`, `Task { … }`
   for async closure parameters. Grep confirms zero hits.
2. **Async-method wrapper integration** — `WrapperEmitter.Async.cs:782-797`
   currently renders closure params as `@escaping @Sendable` Swift
   closure types and `WrapperEmitter.Async.cs:2086-2104` builds the
   `Task { try await … ; callback(…) }` harness. Both need new
   branches: one to render closure params as `(ContextPtr, StartFunc)`,
   one to inject the adapted-closure construction before the awaited
   call.
3. **Viable lifetime model** — current C# side is single-shot
   (`handle.Free()` at `AsyncClosureHelper.cs:81`). Fix lands in
   Session A (§2.1), not Session D.
4. **All five gates** must be coordinated. Touching one without the
   others leaves the emission path unreachable.

### Direction scope

- **Closure parameter** (C# → Swift): target of this plan.
- **Closure return** (Swift → C#): deferred (§8).

---

## 2. Closure Lifetime Model — Design Decision Required

**The current `AsyncClosureHelper` is single-shot: `handle.Free()` runs
inside the `Task.Run` `finally` block** (`AsyncClosureHelper.cs:81`).
If Swift calls the adapted closure twice, the second call hits a freed
`GCHandle` and crashes.

This diverges from sync escaping closures, which deliberately leak their
`GCHandle` (documented at `WrapperEmitter.Marshalling.cs:1074-1079`):
> *"Escaping closures are intentionally leaked: Swift may store the
> function pointer + context beyond the P/Invoke return. Freeing here
> would leave Swift with a stale GCHandle context. The callback thunk
> also does NOT free — escaping closures may fire multiple times."*

Session A fixes this before any emitter changes land — see §2.1.

### 2.1 Lifetime fix lands in Session A (revised per Codex round 2)

The ABI cannot prove that a Swift method invokes a closure exactly
once. Heuristics ("method is async, closure is trailing") do not hold
— an async method is free to call its closure twice or retain it.
Shipping A–C with `handle.Free()` still in place would leak a real
crash into the corpus the instant any library matches the shape gate.

**Fix: remove `handle.Free()` from `AsyncClosureHelper.RunAsync` and
`RunVoidAsync` as part of Session A setup (before any emitter changes
land).** Match sync escaping-closure semantics, which deliberately
leak the `GCHandle` (documented at
`WrapperEmitter.Marshalling.cs:1074-1079`).

Cost analysis:
- Memory growth is proportional to async closure invocation count,
  not time. Modest for UI/completion-handler use cases.
- Matches sync escaping-closure behavior 1:1 — any consumer already
  comfortable with the sync leak model is comfortable with this.
- Leak-free alternative (explicit release callback, was "Option D-ii")
  remains a viable post-ship improvement; listed in §8.

### 2.2 Deferred post-1.0: leak-free lifetime

Future session — not in this plan's four: explicit release callback.
Swift continuation box `deinit` calls a `@_cdecl` `release` entry;
C# frees the `GCHandle`. Adds one callback per closure site. Only
worth pursuing if profiling shows the leak is material in a real
consumer.

### 2.3 Generator-time eligibility gate

Baseline heuristic for Session A: accept async closures only on
`async` outer methods. Sync Swift APIs that take async closures
(e.g. fire-and-forget registration) are rare and deferred — they
need storage-lifetime reasoning this plan doesn't cover.

---

## 3. ABI Contract

Frozen by existing C# emission. Swift must match.

### 3.1 Calling convention — CallConvCdecl (corrected)

Methods routing through `@_cdecl` wrappers use `CallConvCdecl`
(`WrapperValidation.cs:110-115`: any of `UsesCdeclMethodWrapper`,
`UsesCdeclConstructorWrapper`, `UsesCdeclPropertyWrapper`, or
`UsesNativeThunk` → `Cdecl`). Session A MUST set
`UsesCdeclMethodWrapper = true` on methods it emits for.

### 3.2 P/Invoke parameter order + types

For each async-throwing closure param, `PInvokeEmitter.cs:393-400` emits
**context first, then typed start function pointer**. Current typed
emission lives at `MethodSignature.cs:100-101`:

```csharp
[LibraryImport("SwiftBindings", EntryPoint = "<mangled>")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
private static unsafe partial U _method(
    IntPtr <name>ContextPtr,                                           // arg N
    delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>   // arg N+1
        <name>StartFunc,
    SwiftError* errorOut,
    /* other args */);
```

The call site passes `<name>ContextPtr` and `s_<callback>_Start`
(typed function pointer field — `MethodSignature.cs:224-228`), not an
`IntPtr`. The Swift wrapper receives the start function typed as
`@convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer, UnsafeMutableRawPointer, UnsafeMutableRawPointer) -> Void`
— the typed mapping is 1:1, no `unsafeBitCast` needed.

### 3.3 C# `Start` callback signature

```
void Closure_XYZ_Start(
    IntPtr contextPtr,            // GCHandle.ToIntPtr(state)
    IntPtr continuationBoxPtr,    // opaque — passes through unchanged
    IntPtr successFP,             // see table below
    IntPtr errorFP)               // Action<IntPtr, IntPtr> — (boxPtr, utf8ErrorMsgPtr)
```

Three success-callback shapes — emission must branch on return type:

| Return shape | Success callback signature | Helper |
|---|---|---|
| Void | `void(*)(IntPtr boxPtr)` | `AsyncClosureHelper.RunVoidAsync` |
| `Swift.Foundation.Data` | `void(*)(IntPtr boxPtr, IntPtr bytesPtr, nint length)` | `DataAsyncClosureHelper.RunDataAsync` (`DataAsyncClosureHelper.cs:24`) |
| Any other T | `void(*)(IntPtr boxPtr, IntPtr resultPtr)` | `AsyncClosureHelper.RunAsync<T>` |

Length is `nint` (Swift `Int`), not `int` — matches the existing helper.

### 3.4 Result ownership rules (sharpened)

The Swift success callback runs synchronously on the .NET threadpool
thread, BEFORE `AsyncClosureHelper`'s `finally` frees the native result
buffer. Swift must read the value **inside the callback** and either:

| T category | Swift read strategy | Notes |
|---|---|---|
| `BitwiseCopyable` primitives (`Int32`, `Double`, `Bool`, C struct with trivial fields) | `resultPtr.load(as: T.self)` | Session A's only supported branch. |
| `Swift.String` | C# has already materialised a native Swift string value into the result buffer via `SwiftMarshal.MarshalToSwift`. The Swift callback reads it using the existing indirect-return struct-read pattern (reinterpret as `Swift.String` at `resultPtr`, take ownership / copy as that pattern prescribes) — **NOT** `String(cString:)`, since the buffer does not contain a null-terminated C string. Reuse helpers from `ClosureEmitter.IndirectReturn.cs`. | Session C target. |
| `Foundation.Data` | Use the `(bytesPtr, length)` callback shape; Swift copies bytes into a `Data` before returning. | Session D explicit coverage. |
| Swift class (reference) | `Unmanaged<T>.fromOpaque(resultPtr).takeRetainedValue()` — C# helper must `Arc.Retain` before writing the pointer. | Session C target. |
| `Optional<T>` | Discriminator byte + payload at offset. Reuse optional-marshalling helpers. | Session C. |
| Struct with ref fields (`ClassWithBufferStruct`) | Use VWT via `TypeMetadata.GetTypeMetadata<T>().ValueWitnessTable->InitializeWithCopy(...)`. **Never `storeBytes(of:as:)` or `load(as:)` directly** — violates BitwiseCopyable requirement in Swift 6+. | Session C stretch; defer to session D if tricky. |
| Arrays / collections | Reuse existing container-marshalling helpers. | Defer to post-D. |

**Never** use `resultPtr.load(as: T.self)` on non-`BitwiseCopyable` T.
That was loose language in the prior plan and is an easy trap.

### 3.5 Error path

C# pins a UTF-8, null-terminated byte array and calls `errorFP(boxPtr,
msgPtr)`. Swift callback reads via `String(cString: msgPtr)` and
resumes with `SwiftBindingsBridgeError(description)`.

Error type:

```swift
public struct SwiftBindingsBridgeError: LocalizedError, CustomStringConvertible {
    public let description: String
    public var errorDescription: String? { description }
    public init(_ description: String) { self.description = description }
}
```

`LocalizedError` so `error.localizedDescription` surfaces the C#
exception message cleanly (Codex Q4).

### 3.6 Symbol naming (@_cdecl collision avoidance)

Per-T helper symbols MUST be module-qualified, not just T-qualified:

```
_SBW_<SanitizedModule>_asyncBox_<TypeHash>_success
_SBW_<SanitizedModule>_asyncBox_<TypeHash>_error
```

`<TypeHash>` is `DeterministicHash8` over the mangled Swift type name.
`<SanitizedModule>` is the wrapper library's module name with
non-identifier characters replaced by `_`. Relying on `<T>` alone
would collide across two generated wrapper libraries each using
`Int32` (Codex Q5).

### 3.7 Swift-side emission for outer async methods

Target methods are `async throws` on the Swift side. The current
generator emits the outer-async wrapper in the `Task { try await … ;
callback(…) }` harness at `WrapperEmitter.Async.cs:2086-2104`:

```swift
@_cdecl("<mangled>")
public func <pInvokeName>(...) {
    let _entry = _SBWTaskEntry()
    _sbwRegisterTask(_sbwTask, _entry)
    _entry.task = Task {
        defer { _sbwUnregisterTask(_sbwTask) }
        do {
            let _result = try await <callExpression>
            <stringMarshalCode>
            callback(<callbackResultArgs>_sbwTask)
        } catch {
            <catchBody>
        }
    }
}
```

Session A's async-closure emission plugs into this harness. Two
mutations per async-closure param:

1. In the Swift parameter list (rendered at `WrapperEmitter.Async.cs:782-797`
   — currently emits `@escaping @Sendable` closure-typed param): replace
   the closure param with two cdecl-typed params:
   ```swift
   _ <name>ContextPtr: UnsafeMutableRawPointer,
   _ <name>StartFunc: @convention(c) (UnsafeMutableRawPointer,
                                      UnsafeMutableRawPointer,
                                      UnsafeMutableRawPointer,
                                      UnsafeMutableRawPointer) -> Void
   ```
2. Inside `Task { }`, before the `try await <callExpression>` line
   (i.e. before `WrapperEmitter.Async.cs:2096`), inject the
   `adapted` closure construction and substitute `<callExpression>`
   to pass `adapted` in place of the original closure arg.

Full example — target `func callAsyncOp(_ op: @escaping () async throws -> Int32) async throws -> Int32`:

```swift
private final class _SBW_ModuleFoo_AsyncBox_Int32 {
    let cont: CheckedContinuation<Int32, Error>
    init(_ cont: CheckedContinuation<Int32, Error>) { self.cont = cont }
}

@_cdecl("_SBW_ModuleFoo_asyncBox_9F2E4C1B_success")
private func _SBW_ModuleFoo_asyncBox_9F2E4C1B_success(
    _ boxPtr: UnsafeMutableRawPointer,
    _ resultPtr: UnsafeMutableRawPointer
) {
    let box = Unmanaged<_SBW_ModuleFoo_AsyncBox_Int32>
        .fromOpaque(boxPtr).takeRetainedValue()
    let value = resultPtr.load(as: Int32.self)   // primitive only — see §3.4
    box.cont.resume(returning: value)
}

@_cdecl("_SBW_ModuleFoo_asyncBox_9F2E4C1B_error")
private func _SBW_ModuleFoo_asyncBox_9F2E4C1B_error(
    _ boxPtr: UnsafeMutableRawPointer,
    _ msgPtr: UnsafePointer<CChar>
) {
    let box = Unmanaged<_SBW_ModuleFoo_AsyncBox_Int32>
        .fromOpaque(boxPtr).takeRetainedValue()
    box.cont.resume(throwing: SwiftBindingsBridgeError(String(cString: msgPtr)))
}

// Outer async wrapper — same Task harness as existing async methods.
// Task-handle ABI matches WrapperEmitter.Async.cs:753 — _sbwTask is Int64,
// callback/errorCallback take Int64 as their trailing task-handle arg.
@_cdecl("SBW_callAsyncOp")
public func SBW_callAsyncOp(
    _ opContextPtr: UnsafeMutableRawPointer,                   // context FIRST (§3.2)
    _ opStartFunc: @convention(c) (UnsafeMutableRawPointer,    // typed, NOT UnsafeRawPointer
                                   UnsafeMutableRawPointer,
                                   UnsafeMutableRawPointer,
                                   UnsafeMutableRawPointer) -> Void,
    _ callback: @convention(c) (Int32, Int64) -> Void,
    _ errorCallback: @convention(c) (UnsafePointer<CChar>, Int32, Int64) -> Void,
    _ _sbwTask: Int64
) {
    let _entry = _SBWTaskEntry()
    _sbwRegisterTask(_sbwTask, _entry)
    _entry.task = Task {
        defer { _sbwUnregisterTask(_sbwTask) }
        do {
            // Build the adapted Swift closure that bridges back into C#.
            // No unsafeBitCast on opStartFunc — it arrives typed (§3.2).
            // Pass success/error resume callbacks as opaque pointers to fit the
            // generic C# Start signature; Swift-side they are typed @_cdecl symbols.
            let adapted: () async throws -> Int32 = {
                return try await withCheckedThrowingContinuation { cont in
                    let box = _SBW_ModuleFoo_AsyncBox_Int32(cont)
                    let boxPtr = Unmanaged.passRetained(box).toOpaque()
                    let successFP = unsafeBitCast(
                        _SBW_ModuleFoo_asyncBox_9F2E4C1B_success as
                            (@convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer) -> Void),
                        to: UnsafeMutableRawPointer.self)
                    let errorFP = unsafeBitCast(
                        _SBW_ModuleFoo_asyncBox_9F2E4C1B_error as
                            (@convention(c) (UnsafeMutableRawPointer, UnsafePointer<CChar>) -> Void),
                        to: UnsafeMutableRawPointer.self)
                    opStartFunc(opContextPtr, boxPtr, successFP, errorFP)
                }
            }

            let _result = try await callAsyncOp(adapted)   // original target call
            callback(_result, _sbwTask)
        } catch {
            // existing errorCallback path — existing generator emits this block
        }
    }
}
```

The typed-error-callback variant replaces the plain errorCallback signature
with `(UnsafeRawPointer, Int, UnsafePointer<CChar>, Int32, Int64) -> Void` —
the generator selects between the two per-method exactly as it does today.

### 3.8 Invariants

- **Resume-exactly-once** — `passRetained` paired with exactly one
  `takeRetainedValue` (on success OR error, never both). The C#
  helper's try/catch ensures structural guarantee.
- **No `storeBytes(of:as:)` on non-BitwiseCopyable** (§3.3).
- **`@convention(c)` function pointers are `Sendable`** — Swift 6
  strict-concurrency safe.

---

## 4. Session 0 — Handwritten Spike (full async harness, per Codex round 2)

**Goal:** de-risk the ABI by writing Swift + C# by hand, mirroring the
actual generator target — not just the adapter in isolation. No
generator changes.

### 4.1 Deliverables

- `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncClosureSpike.swift`
  — hand-written Swift wrapper matching §3.7 end-to-end:
  - Target Swift function: `func spike_callAsyncOp(_ op: @escaping () async throws -> Int32) async throws -> Int32`.
  - Hand-written `@_cdecl` wrapper that uses the real `Task {
    try await spike_callAsyncOp(adapted); callback(_result, _sbwTask) }`
    harness shape (not a synchronous `@_cdecl` returning `Int32`).
  - Hand-written `_SBW_AsyncBox_Int32` class + resume callbacks.
  - Reuses the existing `_SBWTaskEntry` / `_sbwRegisterTask` /
    `_sbwUnregisterTask` machinery — emission must be compatible
    with that outer async-harness contract.
- `BindingTests/RuntimeTestsApp/Async/AsyncClosureSpikeTests.cs` —
  hand-written C# with:
  - `AsyncThrowingClosureState<int>` setup (via `GCHandle.Alloc`).
  - `[UnmanagedCallersOnly(CallConvCdecl)]` `Start` thunk.
  - P/Invoke declaration with **`CallConvCdecl`** + typed
    `delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>`
    start-function parameter (NOT `IntPtr`).
  - Full `Task<int>` return via `TaskCompletionSource<int>` + outer
    `callback` + `errorCallback`, exactly as the generator emits for
    existing async methods.
- Two scenarios: happy-path returning `42`; error path throwing
  `InvalidOperationException("boom")` from the C# user lambda.
- **Single-invoke only.** The multi-invoke scenario that proves the
  leak-based lifetime model (§2.1) is deliberately deferred to
  Session A, because it depends on the `handle.Free()` removal in
  `AsyncClosureHelper`. Session 0 stays a pure ABI proof with no
  runtime-helper changes — mixing a runtime fix into "no generator
  changes" spike scope would muddle what's being validated.

### 4.2 Exit criteria

- `nuke binding-tests` green with both scenarios on simulator.
- `nuke runtime-tests-device` green (NativeAOT) — de-risks AOT issues
  before generator work. Particularly:
  - `[UnmanagedCallersOnly]` + generic `RunAsync<T>` resolves cleanly.
  - No `[DynamicDependency]` needed (or add if required).
- ABI confirmed: `CallConvCdecl` throughout, context-first param
  order, typed function-pointer handoff, three success-callback
  shapes, module-qualified symbol naming, `LocalizedError` bridging.
  (Leak-based lifetime safety for multi-invoke is validated in
  Session A after the helper fix lands.)

### 4.3 Why first

Every subsequent session leverages the emitter. Any ABI mistake in
generated code would be replicated across every generated wrapper
library. One-shot hand-built verification is cheap insurance.

**If Session 0 surfaces a design flaw,** revise §3 before Session A
starts. Sessions A–D are blocked on green Session 0.

---

## 5. Session A — Emitter for Baseline Case

**Goal:** end-to-end generator support for the narrowest case the spike
validated.
**Target signature:** `func callAsyncOp(_ op: @escaping () async throws -> T) async throws -> U`
where `T` is a `BitwiseCopyable` primitive.
**Explicit limitations** (documented in generated XML doc comments):
primitive return only; no args. Multi-invocation is safe by
construction — the `handle.Free()` removal (§2.1) lands as part of
Session A setup.

### 5.1 Files to touch

| File | Change |
|---|---|
| **`AsyncClosureHelper.cs:81` + `RunVoidAsync` finally** | **Remove `handle.Free()` call** (§2.1 — lifetime fix moves to A). Update comment to match `WrapperEmitter.Marshalling.cs:1074-1079`. |
| **NEW** `ClosureEmitter.AsyncSwiftWrapper.cs` | New partial — Swift box class, resume callbacks, adapter code emission (dedup per module+T) |
| `WrapperEmitter.Async.cs:782-797` | **New branch for async-closure params.** Currently renders `@escaping @Sendable` closure-typed Swift param; add branch that emits `(ContextPtr, StartFunc)` pair for async-closure args instead. |
| `WrapperEmitter.Async.cs:~2096` (before the `try await <callExpression>` line) | Inject the adapter-closure construction block (the `let adapted: () async throws -> T = { withCheckedThrowingContinuation { … } }` chunk from §3.7). Substitute `adapted` for the original arg in `<callExpression>`. |
| `WrapperValidation.cs:419` (`HasAnyAsyncClosure`) | Narrow to `HasUnsupportedAsyncClosure` — return false for baseline-shape; true for everything else |
| `MethodWrapperEmitter.cs:81` | Route baseline-shape async-closure methods to the new emitter instead of rejecting |
| `MethodWrapperEmitter.cs:309` | Leave filter in place (sync-path must not emit async adapters). New branch for async adapters lives in the async path above. |
| `MemberValidationPipeline.cs:411` | Reuse `HasUnsupportedAsyncClosure` — same narrowing |
| `ClosureEmitter.SwiftWrapper.cs:1102-1112` | Narrow exclusion to `HasUnsupportedAsyncThrowingClosure` per the spike's shape |
| `ClosureHandler.cs:203-216` | Leave B13 + CX-12 in place for Session A — they block argument-bearing and non-throwing cases we haven't built yet |
| `BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncClosures.swift` | Ungate one function matching the baseline shape |
| **NEW** `BindingTests/RuntimeTestsApp/Async/AsyncClosureTests.cs` | 3 tests: success, error propagation, multi-invoke (validates leak-based lifetime) |
| **NEW** `SwiftBindingsBridgeError.swift` (in the wrapper runtime) | `LocalizedError`-conforming error struct (§3.5) |

### 5.2 Emitter structure

The new partial emits, per module, once:

1. `SwiftBindingsBridgeError` (if not already emitted by an earlier
   async artifact).
2. Per unique `(module, T)` pair:
   - `_SBW_<Module>_AsyncBox_<TypeHash>` final class.
   - `_SBW_<Module>_asyncBox_<TypeHash>_success` and `_error` `@_cdecl`
     helpers.

Per method wrapper, emit:
- Two `@_cdecl` params per closure: `opContextPtr: UnsafeMutableRawPointer`
  and the typed `opStartFunc: @convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer, UnsafeMutableRawPointer, UnsafeMutableRawPointer) -> Void`.
  No `unsafeBitCast` on entry — the function arrives typed (§3.2).
- Adapter block: `let adapted: () async throws -> T = { try await withCheckedThrowingContinuation { cont in … opStartFunc(opContextPtr, boxPtr, successFP, errorFP) } }`.
  The only `unsafeBitCast` inside the adapter is on the Swift-side resume
  callbacks (`successFP`, `errorFP`), to fit them into the generic
  `UnsafeMutableRawPointer` slots of the C# `Start` signature.
- Target method call substitutes `adapted` for the original closure arg.

Dedup key: `(wrapperModuleName, mangledClosureReturnType)`. Emission
tracker lives alongside the existing per-module collectors (see
`ModuleEmitter.cs` reset pattern).

### 5.3 Eligibility predicate (new, lives in `ClosureHandler`)

```csharp
public bool IsBaselineAsyncThrowingClosure(ClosureTypeSpec spec)
    => spec.IsAsync
       && spec.Throws
       && !spec.EachArgument().Any()
       && IsBitwiseCopyablePrimitive(spec.ReturnType);  // Int/Int32/Int64/Bool/Double/Float, etc.
```

All five gates in §1 check this predicate: if true, emit; if false,
existing rejection behavior.

### 5.4 Exit criteria

- `nuke compile` + `nuke test` green.
- `nuke binding-tests` + `nuke runtime-tests-simulator` green with
  2 new tests.
- `nuke runtime-tests-device` green (NativeAOT).
- `.validation-baseline.json` `UnsupportedClosure` count ≤ baseline;
  any change (up or down) explained in commit message.
- XML doc comment on the generated C# method states the
  baseline-scope limitations (no args, primitive return).
- Commit: "Async-closure bridge: baseline (no args, primitive return)".

### 5.5 Non-goals in A

- Arg-bearing closures (B).
- Non-throwing async closures (C).
- String, class, Data, Optional, struct returns (C + D).
- Cancellation (D+).

---

## 6. Session B — Closure Arguments

**Goal:** support `@escaping (Arg0, Arg1, …) async throws -> T` up to the
same arity cap the sync side uses (check `ClosureEmitter` — likely 4
or 5; confirm pre-session).

### 6.1 State-type decision (per Codex Q1)

Per-arity typed state shapes. Do NOT use `Func<object?[], Task<T>>` —
hides projection/lifetime bugs and is not NativeAOT-friendly.

New types in `Swift.Runtime`:

```csharp
public sealed class AsyncThrowingClosureState<A0, TResult> {
    public required Func<A0, Task<TResult>> AsyncFunc { get; init; }
    public CancellationTokenSource? CancellationSource { get; set; }
}
public sealed class AsyncThrowingClosureState<A0, A1, TResult> { … }
// up to arity cap
```

Corresponding `AsyncClosureHelper.RunAsync<A0, TResult>`,
`RunAsync<A0, A1, TResult>`, etc.

### 6.2 Argument lifetime rule (Codex P2)

**C# `Start` must copy/marshal every argument synchronously before
`Task.Run` returns.** Swift passes stack or borrowed pointers — they are
invalid once `Start` returns. Specifically:

| Arg category | Copy strategy |
|---|---|
| Primitive | Pass by value through `Start` signature; no pointer involved |
| `SwiftString` | Read bytes, materialise as managed `string` before `Task.Run` |
| Swift class | `Arc.Retain` before `Task.Run`; state stores an owning `SafeHandle`; release on state dispose |
| Struct (frozen) | Copy by value through `Start` signature |
| Struct (non-frozen) | Alloc owned `TypeMetadata.Size` buffer, VWT `InitializeWithCopy`, free on state dispose |
| `Optional<T>` | Copy discriminator + payload synchronously |

The helper must NOT capture the Swift-owned pointer into the closure
given to `Task.Run`.

### 6.3 Start signature shape

Per-arity Start signatures (not a single buffer):

```
void Closure_XYZ_Start_A0(
    IntPtr contextPtr, IntPtr continuationBoxPtr,
    <A0 raw>, IntPtr successFP, IntPtr errorFP);
void Closure_XYZ_Start_A0_A1(
    IntPtr contextPtr, IntPtr continuationBoxPtr,
    <A0 raw>, <A1 raw>, IntPtr successFP, IntPtr errorFP);
```

Matches per-arity patterns already in sync closure code — Codex Q1's
recommendation.

### 6.4 Swift side

`adapted: (A0, A1) async throws -> T = { a0, a1 in … }`. Inside the
continuation block, pass `a0`/`a1` through to `startFn` using their
raw-ABI representation (pointers for strings/classes, values for
primitives).

### 6.5 Exit criteria

- B13 gate removed from `ClosureHandler.cs:205-210`.
- Runtime tests for 1-, 2-, 3-arg cases with mix of primitives,
  strings, classes.
- `UnsupportedClosure` drops by B13-triggered skip count in corpus
  (measured via `nuke validate` pre/post).
- No regression in other async-closure tests.

### 6.6 Risks

- Non-frozen struct args — VWT InitializeWithCopy path is untested in
  the closure context. Defer to session D if tricky; document as skip.
- Generic args — out of scope (`GenericTypeCallback` territory).

---

## 7. Session C — Non-Throwing Async Closures

**Goal:** support `@escaping (Args) async -> T` where `T != Void`.

### 7.1 Design — keep separate throwing/non-throwing ABI (Codex Q2)

Do NOT fully unify. The Swift adapter differs structurally
(`async throws` vs `async`), and the two have **different exception
semantics**:

**C# exception policy for non-throwing Swift closures (Codex P2):**
- Swift `() async -> T` cannot surface an error.
- C# `Func<Task<T>>` can still throw (bugs, contract violations).
- Policy: **explicit catch + fail-fast.** The helper lambda:
  ```csharp
  _ = Task.Run(async () =>
  {
      try
      {
          var result = await state.AsyncFunc();
          // marshal + call success callback
      }
      catch (Exception ex)
      {
          Environment.FailFast(
              $"Unhandled exception in non-throwing async closure: {ex}", ex);
      }
  });
  ```
  Unobserved `Task` exceptions DO NOT reliably fail-fast — relying on
  that would silently swallow bugs. Explicit catch is required.
- Document in generated XML doc comment: "Non-throwing Swift async
  closure — if your C# delegate throws, the process terminates."

### 7.2 New types

```csharp
public sealed class AsyncClosureState<TResult> {
    public required Func<Task<TResult>> AsyncFunc { get; init; }
}
public static void AsyncClosureHelper.RunAsyncNonThrowing<TResult>(
    GCHandle handle, AsyncClosureState<TResult> state,
    IntPtr continuationBoxPtr, Action<IntPtr, IntPtr> successAction);
```

Error action is present but drives `FailFast`, not Swift error resume
(see §7.1 — unobserved Task exceptions do not reliably fail-fast).

### 7.3 Swift side

Uses `withCheckedContinuation` (non-throwing variant). Separate per-T
box class `_SBW_<Module>_AsyncBoxNT_<TypeHash>` with
`CheckedContinuation<T, Never>`.

### 7.4 Complex return types (T beyond primitive)

Session C is where the emitter graduates beyond primitive returns per
§3.3. Priority order:
1. `Swift.String` — most common, reuses `IndirectReturn` helpers.
2. Swift class — requires `Arc.Retain` on the C# side before handing
   back the pointer.
3. `Optional<primitive>` — discriminator + payload.

Defer to D: `Foundation.Data` (separate helper already exists, wire
up), struct with ref fields, arrays.

### 7.5 Exit criteria

- CX-12 gate removed from `ClosureHandler.cs:212-215`.
- Runtime tests for non-throwing closures with String + primitive +
  class returns.
- `UnsupportedClosure` drops by CX-12 skip count.
- Fail-fast behavior verified by a **subprocess-based test**:
  parent test spawns a child process, child runs the non-throwing
  closure with a throwing C# lambda, parent asserts the child exited
  with non-zero status and a "FailFast" message in stderr. Do NOT
  use `AssemblyLoadContext` — `Environment.FailFast` terminates the
  whole process regardless of ALC scope, which would take the test
  runner down. Gate the subprocess test behind a simulator-only
  guard so it doesn't interfere with CI parallelism.

---

## 8. Session D — `Data`, Device, Validation, Docs

**Goal:** production-grade readiness. (Lifetime fix already landed in
Session A per §2.1.)

### 8.1 Lifetime stress test

- Stress test: 10,000 async closure invocations in a tight loop,
  assert process memory growth < 100MB.
- Run on both Mono simulator and NativeAOT device.
- Validates that the Session A leak-based model doesn't grow
  pathologically under realistic use.

### 8.2 `Foundation.Data` return

- Wire `DataAsyncClosureHelper.RunDataAsync` into the return-type
  branch for `Swift.Foundation.Data`.
- Swift box: `_SBW_<Module>_AsyncBoxData_<hash>` + success callback
  `(boxPtr, bytesPtr, length)` → `Data(bytes: bytesPtr, count: length)`.
- Runtime test: Swift library returns a 1MB `Data`, C# awaits,
  verifies byte equality.

### 8.3 NativeAOT device validation

- Full async-closure test suite on `nuke runtime-tests-device`.
- Known risk: `[UnmanagedCallersOnly]` + generic method (`RunAsync<T>`).
  Confirm `DynamicDependency` not required; if it is, add.
- Stress test from §8.1 repeated on device.

### 8.4 Library validation pass

- `rm -rf /tmp/binding-validation-<branch>/` (cache invalidation —
  per `constraints.md`).
- `nuke validate`; capture `UnsupportedClosure` delta.
- Expected big movers: Kingfisher, Nuke, Alamofire, GRDB,
  StripeApplePay, RxSwift.
- Update `.validation-baseline.json`.

### 8.5 Docs

- Wiki: "Async Closures" page moves from Known Limitations → Supported.
- `src/docs/roadmap.md`: strike the async-closure item.
- Keep explicit caveats: cancellation not bridged, `@MainActor` not
  yet supported, closure *returns* deferred.

### 8.6 Exit criteria

- All four gates (unit, validate, binding-tests-simulator, device)
  green.
- Zero regressions elsewhere.
- Stress test passes on both Mono and NativeAOT.

---

## 9. Non-Goals (explicit defers)

| Item | Rationale |
|---|---|
| Closure **returns** (Swift → C# async closure) | Rare; symmetric infrastructure required. Separate plan. |
| `AsyncStream<T>` / `AsyncThrowingStream<T>` | Streaming, different primitive. Currently `UnsupportedAsyncStream`. |
| Method-level generic type args in async closure signature | `GenericTypeCallback` — 18 skips today. Sync generic callback infrastructure also absent. |
| Cancellation propagation | See §2.2 — `CancellationTokenSource` scaffolding stays dormant. Revisit when a consumer blocks. |
| `@MainActor` / actor-isolated async closures | Rejected in v1 with explicit skip reason. Requires hop-to-main in the Swift adapter; additive. |
| Struct-with-ref-fields return type in closures | Defer to post-D. VWT path works in principle; untested. |
| Arrays / complex containers as closure return | Defer to post-D. |
| Removing `#if swift(>=99.0)` from `AsyncClosures.swift` wholesale | Session-by-session as gates lift. Anything still gated after D is tracked follow-up. |

---

## 10. Decisions Locked In (from Codex answers)

1. **Arg strategy (B):** per-arity typed state shapes, per-arity Start
   signatures. No `object?[]` runtime state.
2. **Throwing/non-throwing (C):** separate ABI. Fail-fast on C#
   exceptions in non-throwing Swift closures.
3. **Continuation box:** named class, not retained closure. Debuggable,
   extensible to cancellation.
4. **Error type:** `SwiftBindingsBridgeError: LocalizedError` with
   `errorDescription` (§3.4).
5. **Symbol naming:** module-qualified (`_SBW_<Module>_asyncBox_<hash>_…`).
6. **Lifetime:** `handle.Free()` removed in Session A setup, matching
   sync escaping-closure semantics. No one-shot caveat at any point.

---

## 11. Measurement

Baseline: `UnsupportedClosure` = 129 (`.validation-baseline.json`).

| Session | Expected new floor | Notes |
|---|---|---|
| 0 (spike) | 129 | No generator changes; hand-code only |
| A | 129 − <baseline-shape skips> (measure first) | Narrow scope; expect 0–5 drop |
| B | A − <B13 skips with primitive/string args> | 15–30 drop estimated |
| C | B − <CX-12 skips> | 10–20 drop estimated |
| D | ≤ 70 target | + data/class/optional from C reach more cases |

Measurement protocol: before A starts, run `nuke validate` and
categorise each `UnsupportedClosure` skipped item by its closure
shape (grep `binding-report.json` per library). Record the
categorisation in an appendix to this plan.

---

## 12. Sequencing & Rollback

0 → A → B → C → D. Each session independently mergeable/revertible:

- New fixtures + tests per session (additive).
- Own `.validation-baseline.json` snapshot per session.
- Session 0 is reversible via deleting its fixture files.
- Session A's lifetime fix (§2.1) is a two-line delete (`handle.Free()`
  in both helpers) and can be reverted in isolation if needed — though
  reverting it would reintroduce the crash-on-multi-invoke hazard, so
  it should stay landed.

Estimated commits: 5 (one per session 0–D) + 1 docs/wiki cleanup in D.
