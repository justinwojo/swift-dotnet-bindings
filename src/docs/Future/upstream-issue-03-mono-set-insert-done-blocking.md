# [Mono] `Cannot transition thread from STARTING with DONE_BLOCKING` — the managed-to-native wrapper parks its GC-safe-region cookie in `x20`, which `CallConvSwift` reserves for `SwiftSelf`

> Standalone bug report for filing against [dotnet/runtime](https://github.com/dotnet/runtime/issues). Project: [swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings). Repro: [swift-interop-repro](https://github.com/justinwojo/swift-interop-repro). Contact: Justin Wojciechowski.

## Title

`[Mono] arm64: managed-to-native wrapper allocates the GC-safe-region cookie into x20, which CallConvSwift reserves for SwiftSelf — "Cannot transition thread from STARTING with DONE_BLOCKING"`

> **Scope correction (2026-09-08).** This document originally scoped the defect to the `(Bool direct, @out via x0)` tuple return of `Set.insert`. That was a three-sample correlation, not the cause. The mechanism is now pinned down at register level (see **Root cause — pinned down** below): the trigger is Mono's register allocator placing the GC-safe-region cookie in a Swift-reserved argument register, and it can hit **any** `CallConvSwift` P/Invoke that passes an untyped `SwiftSelf` (self in `x20`), regardless of return or argument shape. The parallel `x21`/`SwiftError` arm is mechanically possible but was not observed in the surveyed corpus, and a typed `SwiftSelf<T>` is not implicated at all — both qualifications are set out under **Root cause — pinned down**. The `Set.insert` material below is retained as the original reproduction, not as the scope.

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

**ABI shape of the original reproduction** (this was first reported as the trigger; it is **not** — see *Root cause — pinned down*. The one part that matters is that `SetInsert` carries `SwiftSelf`):

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

`SetContains` **passes** on Mono. `SetInsert` **crashes** with the `DONE_BLOCKING` error. This was originally read as the return shape being the delta. It is not: note that `SetContains` takes no `SwiftSelf` at all, so it has no Swift-reserved argument register for the GC-safe-region cookie to collide with and cannot exhibit the defect regardless of its return shape. It is not a control for the tuple return; it is a call with a different register footprint.

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

The return `(Bool, @out T)` is NOT handled via `x8`/`SwiftIndirectResult`. Instead, the `@out T` buffer pointer goes in `x0` and the direct `Bool` result is returned in `x0` after the call returns — the same register is reused for the inbound out-pointer argument and the outbound scalar result.

This shape was originally reported as the trigger, on the basis that `Set.contains` (no `@out`) and `Dictionary.updateValue` (uses `x8`/`SwiftIndirectResult`) both pass. **That correlation is now known to be incidental** — the three samples happened to differ in Mono's register allocation, not in anything the Swift signature expresses. See **Root cause — pinned down** below.

**Workaround:**

Route the member through an `@_cdecl` Swift wrapper. A `CallConvCdecl` signature carries neither `SwiftSelf` nor `SwiftError`, so `x20`/`x21` are ordinary callee-saved registers the wrapper may legally park the cookie in — there is no reserved register for it to collide with. (The original rationale given here — "avoids the mixed tuple-return ABI" — was based on the superseded shape hypothesis; the workaround is effective, but for the register reason.)

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
- Not reproduced on NativeAOT. The device NativeAOT lane runs the same generated P/Invokes — including `consumeDirectAndThrow`, which aborts on both Mono lanes — and passes. That is an outcome, not a codegen claim — no NativeAOT stub was disassembled, and the `MonoThreadInfo*` GC-safe-region cookie is a Mono construct with no NativeAOT counterpart. (The 2026-04-30 revision of this line said "needs separate verification"; that verification is the NativeAOT device lane's standing green.)
- Priority: high for `SwiftSet<T>` correctness in swift-dotnet-bindings

**Verification scope (2026-04-30):**

- **ABI shape — verified.** Direct disassembly of `libswiftCore.dylib` (arm64 simulator slice) and the SIL signature confirm `Set.insert` takes `(x0=@out T*, x1=@in T*, x2=Set<T> metadata, x20=@inout Set<T> self via Swift context register)` and returns `Bool` in `w0`/`x0`, reusing `x0` for the inbound `@out` pointer and the outbound scalar.
- **P/Invoke shape match — verified.** Our `Insert(IntPtr outMemberBuffer, IntPtr element, IntPtr setMetadata, SwiftSelf self) -> byte` lowers to `(x0, x1, x2, x20) → x0` per Mono's `SwiftSelf → ARMREG_R20` mapping at `mini-arm64.c:~1927`. It matches the Swift ABI.
- **Failure correlates with shape — SUPERSEDED.** The three-sample correlation (`Set.insert` fails; `Set.contains` and `Dictionary.updateValue` pass) held, but the differentiator is not the return shape. See **Root cause — pinned down** below.
- **Root cause inside Mono's trampoline — PINNED DOWN 2026-09-08.** Superseded; see the section below. The 2026-04-30 source review remains accurate as background: the IL stub uses `mono_threads_enter_gc_safe_region_unbalanced` / `mono_threads_exit_gc_safe_region_unbalanced` brackets; `mono_threads_transition_done_blocking` (`state-machine.c:772`) only accepts `STATE_BLOCKING` / `STATE_BLOCKING_SUSPEND_REQUESTED`; `STATE_STARTING == 0` (`mono-threads.h:146`). The "thread `0x0` from STARTING" wording is consistent with a zeroed/garbage `MonoThreadInfo*`, which is exactly what the register clobber produces.

## Root cause — pinned down (2026-09-08)

Established by disassembling Mono's own statically-compiled managed-to-native wrappers out of a **Mono full-AOT** iOS device build of the project's `RuntimeTestsApp`, where every wrapper is emitted ahead of time and directly readable.

**Mechanism.** Mono's managed-to-native P/Invoke wrapper brackets the call with a GC-safe-region transition. `mono_threads_enter_gc_safe_region_unbalanced` returns a cookie (a `MonoThreadInfo*`) in `x0` which must survive the native call and be handed back to `mono_threads_exit_gc_safe_region_unbalanced`. The wrapper's register allocator parks that cookie in a callee-saved register — **without excluding `x20` and `x21`, which the Swift calling convention reserves for `SwiftSelf` and `SwiftError`.** When the allocator picks one of those, the wrapper's own argument setup for the very same call overwrites the cookie before branching to the callee. After the call the wrapper reloads the clobbered register and passes it to the exit helper, which dereferences a bogus thread record — state field reads `0` = `STATE_STARTING` — and aborts.

**Failing wrapper** (Swift member with `SwiftSelf` + `SwiftError`; cookie allocated to `x20`):

```
+220: bl   mono_threads_enter_gc_safe_region_unbalanced   ; cookie -> x0
+232: mov  x20, x0        ; cookie parked in x20
...
+272: mov  x20, x2        ; OVERWRITTEN — x2 holds SwiftSelf
+276: mov  x21, #0x0      ; SwiftError zeroed
+280: bl   <native Swift callee>
+288: str  x21, [x16]     ; swifterror writeback
+300: mov  x0, x20        ; cookie reloaded from the CLOBBERED register
+308: bl   mono_threads_exit_gc_safe_region_unbalanced    ; aborts
```

**Passing sibling** — same class, near-identical Swift signature minus `throws`; the allocator happened to pick `x22`:

```
+208: bl   mono_threads_enter_gc_safe_region_unbalanced
+212: mov  x22, x0        ; cookie in x22 (not Swift-reserved)
...
+248: mov  x20, x2        ; self
+252: bl   <native Swift callee>
+264: mov  x0, x22        ; cookie intact
+272: bl   mono_threads_exit_gc_safe_region_unbalanced    ; fine
```

These two also differ in their `SwiftError` setup, so the pair alone does not isolate the variable. The control that does is `consumeDirectWithLater` below: it is non-throwing, carries no `SwiftError` at all, and shows the identical `x20` clobber. Nothing in any of these Swift signatures, argument shapes or return shapes selects the allocator's choice.

**Corpus survey.** All 181 `wrapper_managed_to_native_*` symbols carrying `SwiftSelf` were disassembled out of that binary (13,630 wrappers total; 31 also carry `SwiftError`). Each was classified by whether the cookie's register is *written* between the transition entry and the native branch: **14 clobbering, 157 safe, 5 with no GC transition, 5 unparsed**. Safe cookie registers were distributed `x23:69, x24:42, x22:22, x25:20, x19:2, x20:1, x21:1` — i.e. the allocator picks freely from the callee-saved bank, and `x20`/`x21` are simply in it.

All 14 observed clobbers are `x20` parks destroyed by the `SwiftSelf` argument.

**The `x21` arm is mechanically live but has not been observed.** No `x21` clobber occurs in this corpus. Two things follow, and they must not be conflated:

- *The write exists.* Mono's wrapper unconditionally primes the swifterror register on every `SwiftError`-carrying call. `ThrowingBytesNamespace.countBytesOrThrow` — a `CallConvSwift` P/Invoke with `SwiftError` and no `SwiftSelf` — emits `<+244>: mov x21, #0x0` between the transition entry and the branch to the callee (`error-only-wrappers.asm`). A cookie allocated to `x21` would be destroyed by that instruction exactly as an `x20` cookie is destroyed by the self argument.
- *The allocation has not been seen.* Six `SwiftError`-carrying, `SwiftSelf`-free wrappers were disassembled separately; their cookies landed in `x19` (1), `x20` (2) and `x22` (1), with two unparsed — **none in `x21`, and none clobbered**. The two `x20` parks are positive controls in the other direction: with no `SwiftSelf` to write, an `x20` cookie survives and those members pass.

So the `x21` arm should be filed as *not excluded by Mono's allocator and destroyable if chosen*, **not** as a reproduced failure. An earlier revision of this section cited `NestedClosureHost`/`NCB_F17B0E10` as an `x21`-park specimen; that was wrong — its P/Invoke is declared `CallConvCdecl`, so it reserves no registers at all and is not evidence about `CallConvSwift` either way.

**Typed `SwiftSelf<T>` is outside the defect.** A frozen-struct self is passed in ordinary argument registers rather than `x20`, so an `x20` cookie is not disturbed. `LeaseProbe.consumeGated` (`SwiftSelf<LeaseProbe.Buffer>`) is the control: its wrapper parks the cookie in `x20`, is not clobbered, and passes. Nine of the corpus' `CallConvSwift` declarations are of this typed form and none is implicated.

**Predictive validation (4/4).** The model was checked against four members whose runtime outcome was independently known before the disassembly was read:

| Member | Cookie register | Model predicts | Known outcome |
|---|---|---|---|
| `ThrowingGetterBox.checkedValue` getter | `x23` | pass | passed (device Mono full-AOT) |
| `GenericMethodHost.describeWithTag` | `x20` | abort | in-tree `[Skip]` citing this exact abort |
| `OwnershipStructConsumer.consumeDirectAndThrow` | `x20` | abort | aborts (sim Mono JIT + device Mono full-AOT) |
| `OwnershipStructConsumer.consumeDirect` | `x22` | pass | passes |

`describeWithTag`'s two sibling wrappers landed on `x22` and are safe — matching that exactly one member of that class is skipped, not the class.

**Shape independence — direct disproof of the original scoping.** `OwnershipStructConsumer.consumeDirectWithLater` is **non-throwing** (no `SwiftError` at all) and its wrapper shows the identical `mov x20, x0` / `mov x20, x3` / `mov x0, x20` clobber. It is latent only because no test calls it. So neither `throws`, nor `SwiftError`, nor a tuple return, nor an `@out` is necessary — an untyped `SwiftSelf` alone is sufficient exposure. `SwiftError` would widen the exposure to `x21` if the allocator ever parked the cookie there, which it has not been observed to do (see the `x21` arm above).

**Why the caller cannot avoid it.** The affected P/Invoke declarations are ABI-correct — the same declarations run correctly under NativeAOT. (That is an observation about NativeAOT's outcome, not about its codegen: no NativeAOT stub was disassembled here, and the `MonoThreadInfo*` cookie is a Mono construct with no NativeAOT counterpart, so the correct claim is "NativeAOT does not exhibit it", not "NativeAOT allocates the cookie elsewhere".) The trigger is entirely inside Mono's wrapper codegen and is not a function of anything the managed declaration or the Swift signature can express, so no binding generator can predict or route around the shape. The only shape-level escape is dropping `SwiftSelf`/`SwiftError` from the signature altogether, i.e. the `@_cdecl` workaround.

**Suggested fix.** Exclude `x20` and `x21` from the callee-saved candidate set the managed-to-native wrapper's register allocator may use for the GC-safe-region cookie whenever the call site uses `CallConvSwift` (or unconditionally for these wrappers — the two registers are cheap to reserve). Alternatively spill the cookie to the wrapper's frame across the native call rather than holding it in a register.

**Environment for this determination:** .NET 10.0, Mono full-AOT `ios-arm64` (`dotnet build -c Debug -r ios-arm64`, no `PublishAot`) and Mono JIT `iossimulator-arm64`; Microsoft.iOS.Sdk 26.2.x; Xcode 26.3. The same abort text, frame chain and wrapper symbol appear on both the simulator (JIT) and the device (full AOT), so this is Mono's wrapper codegen rather than a JIT-only artifact.
