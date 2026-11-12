# [Mono] `Cannot transition thread from STARTING with DONE_BLOCKING` — the managed-to-native wrapper parks its GC-safe-region cookie in `x20`, which `CallConvSwift` reserves for `SwiftSelf`

> Standalone bug report for filing against [dotnet/runtime](https://github.com/dotnet/runtime/issues). Project: [swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings). Repro: [swift-interop-repro](https://github.com/justinwojo/swift-interop-repro). Contact: Justin Wojciechowski.

## Title

`[Mono] arm64: managed-to-native wrapper allocates the GC-safe-region cookie into x20, which CallConvSwift reserves for SwiftSelf — "Cannot transition thread from STARTING with DONE_BLOCKING"`

> **Scope correction (2026-09-08).** This document originally scoped the defect to the `(Bool direct, @out via x0)` tuple return of `Set.insert`. That was a three-sample correlation, not the cause. The mechanism is now pinned down at register level (see **Root cause — pinned down** below): the trigger is Mono's register allocator placing the GC-safe-region cookie in a Swift-reserved argument register, and it can hit **any** `CallConvSwift` P/Invoke that passes an untyped `SwiftSelf` (self in `x20`), regardless of return or argument shape. The parallel `x21`/`SwiftError` arm is mechanically possible but was not observed in the surveyed corpus, and a typed `SwiftSelf<T>` is not implicated at all — both qualifications are set out under **Root cause — pinned down**. The `Set.insert` material below is retained as the original reproduction, not as the scope.

> **Scope widening (2026-11-12).** The cookie is not the only wrapper value the allocator parks in `x20`. The wrapper's LMF push also lands there, and the LMF pop after the call then writes through `x20`, which by then holds `SwiftSelf`. That overwrites the first word of the Swift object (its isa) and leaves the thread's LMF head pointing at a dead frame. The failure is a later SIGSEGV when the object is released, far from the call, not the `DONE_BLOCKING` abort. See **Second victim — the LMF address** below. The defect is "any wrapper value live across the native call may sit in `x20`/`x21`", and the cookie is one instance of it.

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

**Status (2026-09-10):** applied in this repo — the generator's wrapper route was widened to cover the reroutable population, taking direct `CallConvSwift` declarations carrying an untyped `SwiftSelf` from 149 to 85 and typed `SwiftSelf<T>` to 0; what remains is held open by obstructions above the ABI rather than by the wrapper route, and is recorded in the [runtime/ABI reference notes](notes/runtime-abi.md).

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

## Second victim — the LMF address (2026-11-12)

**Mechanism.** Before the transition, the wrapper pushes its LMF (last managed frame) record onto the thread's LMF list. It calls `mono_get_lmf_addr`, parks the returned slot address in a callee-saved register, saves the previous head into its frame and stores its own record into the slot. After the native call it pops by storing the saved previous head back through the same register. When the allocator chooses `x20`, the `SwiftSelf` setup overwrites the slot address. Because `x20` is callee-saved, it still holds self after the call, so the pop stores a stack address into the first word of the Swift object. A wrapper with the cookie safe in another register is still exposed this way, so the cookie-only classification above cannot see it.

```
+176: blr  x0             ; mono_get_lmf_addr
+180: mov  x20, x0        ; LMF slot address parked in x20
+188: ldr  x1, [x20]      ; previous LMF head
+192: str  x1, [x29, #0xc0]
+196: str  x0, [x20]      ; push this frame's LMF
+244: bl   mono_threads_enter_gc_safe_region_unbalanced
+248: mov  x22, x0        ; cookie in x22 (safe)
+296: mov  x20, x3        ; SwiftSelf — the LMF slot address is lost
+300: bl   <native Swift callee>
+500: ldr  x0, [x29, #0xc0]
+504: str  x0, [x20]      ; LMF pop writes the previous head into *self
```

**Register-level proof.** The proof was taken on the iOS Simulator (Mono, net10 / Xcode 26.3), `SwiftBridgeProbe.combine`, under lldb, against the image the survey below classified:

| Point | Observation |
|---|---|
| After `mono_get_lmf_addr` | `x20` = `0x1209d6810` (the thread's LMF slot) |
| Before the branch | self = `0x137dedd40`; `*self` = `0x11d8f93a8` (the class metadata), refcount word `0x3` |
| After the call | `x20` = `0x137dedd40` (self, preserved by the callee) |
| At `+504` | `x0` = `0x16cf9a638`, a stack address (the previous LMF head) |
| After the store | `*self` = `0x16cf9a638`, so the isa is destroyed; the LMF slot is never restored and keeps pointing at the returning frame |
| Later release | `SwiftBridgeProbe.Dispose` → `swift_release` → `_swift_release_dealloc` loads the destroyed isa and branches into the stack at `0x16cf9a8c8`: SIGSEGV |

The call itself returns correct values. The consumer sees a crash on the object's next release or dealloc. The test that calls the member is not green while corrupting: `SwiftBridgeTests.TestCombineThroughBridge_SelfIndirectResultAndArguments` crashes in that `Dispose`.

**Survey (2026-11-12).** The survey ran on .NET 10 with Microsoft.iOS / Mac Catalyst / tvOS SDK 26.2.x and Xcode 26.3, against the project's `RuntimeTestsApp`. Every managed-to-native wrapper carrying `SwiftSelf` or `SwiftError` was classified by `build/scripts/classify-mono-aot-cookie.py` (`m2n-x20-v3`). For each wrapper, the classifier finds the `x20` write that loads self, the value parked in `x20` before it, and every read of `x20` reachable after the native call before `x20` is redefined. It does the same for `x21` on `SwiftError` wrappers.

| Lane | Wrappers carrying untyped `SwiftSelf` | LMF victims | Cookie victims |
|---|---|---|---|
| iOS Simulator, BindingTests configuration | 14 | 1 | 3 |
| iOS device, Mono full-AOT | 14 | 1 | 3 |
| iOS Simulator, regenerated without lowering evidence | 69 | 6 | 4 |
| Mac Catalyst | 69 | 6 | 4 |
| tvOS Simulator (interpreter) | 0 | — | — |

- **Which members are victims.**
  - The two iOS BindingTests lanes have the same four victims: one LMF victim (`PendingRequestTask.start`) and three cookie victims (`IndirectSource.wrap`, `IndirectTable.with`, `StaffRoster.firstEntry`).
  - The two 69-wrapper lanes add six more, in both registers' victim classes:
    - LMF victims: `SwiftBridgeProbe.combine`, `SwiftBridgeProbe.derive`, `InoutTally.measure`, `GenericOptionalAbiBox.tagIsPresent` and `WireNote.decodeMessage`.
    - A cookie victim: `SwiftBridgeProbe.accumulate`, which carries both `SwiftSelf` and `SwiftError`.
- **`x21`.** No `x21` victim was found in any lane.
- **tvOS.** tvOS runs these calls through `interp_to_native_trampoline`. That trampoline saves `x20`/`x21` to its own frame, loads self and the error slot, and restores both after the call, so the defect does not arise there.

**Typed indirect calls.** The `CallConvSwift` function-pointer wrapper (`_Module_wrapper_native_indirect_void_modoptCallConvSwift_void__SwiftSelf_intptr`) never writes `x20` at all. In the disassembly it loads the `SwiftSelf` argument into `x1` before `blr`. That is a separate defect in the same lowering, recorded here from the static listing only.

**Suggested fix.** In a `CallConvSwift` managed-to-native wrapper, exclude `x20` and `x21` from the registers that may hold any value live across the native call. That covers the GC-safe-region cookie and the LMF slot address, plus anything else the allocator might place there. The rule could also apply unconditionally to these wrappers, since reserving the two registers is cheap. The alternative is to spill such values to the wrapper's frame across the call. Fixing only the cookie's allocation would leave the LMF arm in place.

**Environment for this determination:** .NET 10.0, Mono full-AOT `ios-arm64` (`dotnet build -c Debug -r ios-arm64`, no `PublishAot`) and Mono JIT `iossimulator-arm64`; Microsoft.iOS.Sdk 26.2.x; Xcode 26.3. The same abort text, frame chain and wrapper symbol appear on both the simulator (JIT) and the device (full AOT), so this is Mono's wrapper codegen rather than a JIT-only artifact.
