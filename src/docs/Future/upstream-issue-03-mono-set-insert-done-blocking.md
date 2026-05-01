# [Mono] `Cannot transition thread from STARTING with DONE_BLOCKING` on `(Bool direct, @out via x0)` tuple-return `CallConvSwift` P/Invoke

> Standalone bug report for filing against [dotnet/runtime](https://github.com/dotnet/runtime/issues). Project: [swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings). Repro: [swift-interop-repro](https://github.com/justinwojo/swift-interop-repro). Contact: Justin Wojciechowski.

## Title

`[Mono] "Cannot transition thread from STARTING with DONE_BLOCKING" when calling Swift method with (Bool, @out Element) tuple return via CallConvSwift`

## Labels

`area-Interop-Swift`, `os-ios`, `os-maccatalyst`, `bug`, `runtime-mono`

## Description

**Environment:**
- .NET 10.0 (10.0.103), Mono runtime (iOS Simulator, arm64)
- Microsoft.iOS.Sdk 26.2.10197
- Xcode 26.2, iOS Simulator runtime 26.3
- Reproduced in: [swift-interop-repro](https://github.com/justinwojo/swift-interop-repro), `Issue3_MonoSetInsertDoneBlocking` class

**Symptom:**

Calling `Swift.Set<T>.insert(_:)` via `[UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]` P/Invoke on Mono causes an immediate `SIGABRT` with:

```
error: Cannot transition thread 0x0 from STARTING with DONE_BLOCKING
```

This is Mono's thread-state machine asserting that the thread is in `STARTING` state when `mono_threads_transition_done_blocking` is called to end the managed-to-native GC-safe region after the P/Invoke returns. The expected state before `DONE_BLOCKING` is `BLOCKING`; the actual state is `STARTING`, indicating the thread state was corrupted during the `CallConvSwift` callout.

**ABI shape that triggers the crash:**

`Set<T>.insert(_:)` returns a `(Bool inserted, Element memberAfterInsert)` tuple where:
- `Bool` (`inserted`) is returned directly in `x0` (a single-register scalar)
- `@out Element` (`memberAfterInsert`) is written via a pointer **also passed in `x0`** — not via `x8` (`SwiftIndirectResult`)

This is a mixed tuple-return ABI: when one element is direct (`Bool`) and one is `@out`, the `@out` buffer pointer occupies `x0` on call entry, and the direct `Bool` is returned in `w0`/`x0` after return — `x0` is reused for both the inbound out-pointer argument and the outbound scalar result. This differs from the pure `@out` path that uses `x8`/`SwiftIndirectResult`.

**P/Invoke signature (matches swift-bindings `SwiftSetPInvokes.Insert` exactly):**

```csharp
[UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
[DllImport("libswiftCore.dylib", EntryPoint = "$sSh6insertySb8inserted_x17memberAfterInserttxnF")]
public static extern byte Insert(
    IntPtr outMemberBuffer,   // x0 — @out Element buffer
    IntPtr element,           // x1 — @in Element value
    IntPtr setMetadata,       // x2 — full Set<T> metadata (generic context)
    SwiftSelf self);          // x20 — @inout Set<T> (storage pointer buffer)
// return: byte (Bool in x0)
```

**Control group — same call pattern, no @out — passes:**

```csharp
[UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
[DllImport("libswiftCore.dylib", EntryPoint = "$sSh8containsySbxF")]
public static extern byte SetContains(
    IntPtr element,            // x0 — element
    IntPtr setStoragePtr,      // x1 — Set value (storage pointer, passed by value)
    IntPtr elementMetadata,    // x2 — T metadata
    IntPtr hashableWT);        // x3 — T:Hashable witness table
// return: byte (Bool in x0)
```

`SetContains` **passes** on Mono. `SetInsert` **crashes** with `DONE_BLOCKING` error. The delta is the `(Bool, @out via x0)` return shape.

**Memory addresses from repro run:**
```
Int metadata:        0x1E8A72AC0
Int:Hashable WT:     0x1E8A6A340
Set<Int> metadata:   0x1E8A762C8
Set<Int> size:       8, Int size: 8

@_cdecl pre-populate insert(99): 1  (set properly initialized)
Set storage ptr (after @_cdecl insert): 0x60000211EBC0  (valid heap address)
Storage ptr looks like heap address: True

3a. Set<Int>.contains(99) [CONTROL]: 1 (expected 1) — PASS

[SetInsert called here — process crashes]
error: Cannot transition thread 0x0 from STARTING with DONE_BLOCKING
SIGABRT
```

**Native stacktrace key frames:**
```
mono_threads_transition_done_blocking
mono_threads_exit_gc_safe_region_unbalanced
wrapper_managed_to_native_..._SetInsert_intptr_intptr_intptr_SwiftSelf
Issue3_MonoSetInsertDoneBlocking_Run
```

**Symbol verified:**
```
nm -g libswiftCore.dylib | grep Sh6insert
000000000004a190 T _$sSh6insertySb8inserted_x17memberAfterInserttxnF
// swift-demangle: Swift.Set.insert(__owned A) -> (inserted: Swift.Bool, memberAfterInsert: A)
```

**Real-world impact:**

`swift-dotnet-bindings` wraps Swift's `Set<T>` as `SwiftSet<T>` with an `Add(Element)` method that calls `insert(_:)` via this P/Invoke. The crash prevents any `SwiftSet<T>.Add()` call from completing on Mono (iOS Simulator), causing the `BulkCollectionStressTests` and `SwiftSetTests` to fail with SIGABRT rather than assertion failures.

**SIL signatures (unspecialized, from verified dump):**

```
// Set<T>.insert(_:)
$sSh6insertySb8inserted_x17memberAfterInserttxnF:
  @convention(method) (@in T, @inout Set<T>) -> (Bool, @out T)
```

The return `(Bool, @out T)` is NOT handled via `x8`/`SwiftIndirectResult`. Instead, the `@out T` buffer pointer goes in `x0` and the direct `Bool` result is returned in `x0` after the call returns — the same register is reused for the inbound out-pointer argument and the outbound scalar result. The `(Bool direct, @out via x0)` shape correlates uniquely with the failure: `Set.contains` (no `@out`, single-direct return) and `Dictionary.updateValue` (uses `x8`/`SwiftIndirectResult`) both pass on Mono with `CallConvSwift`. The corruption mechanism inside Mono's `CallConvSwift` managed-to-native trampoline is hypothesized but not pinned down — see "Verification scope" below.

**Workaround:**

Use an `@_cdecl` Swift wrapper that calls `insert` and returns just the `Bool` inserted flag, avoiding the mixed tuple-return ABI entirely:

```swift
@_cdecl("swiftset_insert")
public func swiftset_insert(_ setPtr: UnsafeMutableRawPointer, _ value: Int) -> Int32 {
    let result = setPtr.assumingMemoryBound(to: Set<Int>.self).pointee.insert(value)
    return result.inserted ? 1 : 0
}
```

**Filing notes:**
- Verified on 2026-04-30 (.NET 10.0.103, Mono iOS Simulator arm64, Xcode 26.2)
- Related to the companion Mono issue `[Mono] !ji->async during signal-handler unwind through a CallConvSwift frame` and the general pattern of Mono not handling non-standard `CallConvSwift` return ABIs
- Not reproduced on NativeAOT (device) — needs separate verification
- Priority: high for `SwiftSet<T>` correctness in swift-dotnet-bindings

**Verification scope (2026-04-30):**

- **ABI shape — verified.** Direct disassembly of `libswiftCore.dylib` (arm64 simulator slice) and the SIL signature confirm `Set.insert` takes `(x0=@out T*, x1=@in T*, x2=Set<T> metadata, x20=@inout Set<T> self via Swift context register)` and returns `Bool` in `w0`/`x0`, reusing `x0` for the inbound `@out` pointer and the outbound scalar.
- **P/Invoke shape match — verified.** Our `Insert(IntPtr outMemberBuffer, IntPtr element, IntPtr setMetadata, SwiftSelf self) -> byte` lowers to `(x0, x1, x2, x20) → x0` per Mono's `SwiftSelf → ARMREG_R20` mapping at `mini-arm64.c:~1927`. It matches the Swift ABI.
- **Failure correlates with shape — verified.** `Set.contains` (no `@out`, single-direct return) and `Dictionary.updateValue` (uses `x8`/`SwiftIndirectResult` for the indirect result) both pass on Mono with `CallConvSwift`. The unique failing shape is `(Bool direct, @out via x0)`.
- **Root cause inside Mono's trampoline — hypothesized, not pinned down.** Reviewed `marshal.c`, `marshal-lightweight.c`, `mini-arm64.c`, `mono-threads-state-machine.c`, `mono-threads.c`. The IL stub uses `mono_threads_enter_gc_safe_region_unbalanced` / `mono_threads_exit_gc_safe_region_unbalanced` brackets; `mono_threads_transition_done_blocking` (`state-machine.c:772`) only accepts `STATE_BLOCKING` / `STATE_BLOCKING_SUSPEND_REQUESTED`; `STATE_STARTING == 0` (`mono-threads.h:146`). The "thread `0x0` from STARTING" wording is consistent with a zeroed `MonoThreadInfo*` or a state field reading zero, but the exact path from the `(Bool direct, @out via x0)` shape to that zeroed state was not isolated. A reviewer with a local Mono build can instrument the trampoline directly.
