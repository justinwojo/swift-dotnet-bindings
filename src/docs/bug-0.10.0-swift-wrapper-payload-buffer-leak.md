# Bug: Swift wrapper file allocates callback enum payload buffers without freeing them

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Stripe](https://github.com/justinwojo/swift-dotnet-packages)
> (StripeCardScan 26.2.1).

## Summary

Distinct from the C#-side `GCHandle` leaks documented in
[bug-0.10.0-callback-trampoline-gchandle-leak.md](./bug-0.10.0-callback-trampoline-gchandle-leak.md):
the **Swift wrapper file** itself (the `.swift` file the SDK generates as
the `@_cdecl` shim layer between Swift APIs and C# PInvoke entry points)
allocates per-call buffers to hold callback enum payloads, hands them to
the C-callable trampoline, and never deinitializes / frees them.

Both leaks fire on the same call. Per `present(...)` invocation in
StripeCardScan, the consumer leaks:

1. A C# `GCHandle` rooting the managed `Action<…>` (Family A bug).
2. A native enum payload buffer allocated by the Swift wrapper (this bug).

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source library: stripe-ios 26.2.1 (StripeCardScan)

## Repro

```bash
sed -n '288,310p' libraries/Stripe/StripeCardScan/obj/Debug/net10.0-ios/swift-binding/StripeCardScan.Wrapper.swift
sed -n '380,400p' libraries/Stripe/StripeCardScan/obj/Debug/net10.0-ios/swift-binding/StripeCardScan.Wrapper.swift
```

```swift
// StripeCardScan.Wrapper.swift:294 — CardScanSheet.present
@_cdecl("$s7CardScanS… …")
public func _swiftWrapper_CardScanSheet_present(...) {
    // …
    let resultBuffer = UnsafeMutableRawPointer.allocate(
        byteCount: MemoryLayout<CardScanSheetResult>.size,
        alignment: MemoryLayout<CardScanSheetResult>.alignment)         // [1] alloc
    sheet.present(from: presentingViewController) { result in
        resultBuffer.initializeMemory(as: CardScanSheetResult.self,
                                       to: result)                       // [2] init
        cdeclCallback(resultBuffer, completionContext)                   // [3] hand off
        // ← no resultBuffer.deinitialize()
        // ← no resultBuffer.deallocate()
    }
}

// StripeCardScan.Wrapper.swift:387 — CardImageVerificationSheet.present
// (same shape)
```

The buffer is allocated up-front, written into in the completion
callback, handed to the C-callable trampoline that re-enters managed
code, and then leaks: no `defer` block frees it; no continuation in the
completion freeing path runs.

## Native ground truth — Swift's allocation contract

`UnsafeMutableRawPointer.allocate(...)` returns memory that the caller
owns. The contract is:

1. Initialize with `initializeMemory(as:to:)` (transfers ownership of
   the value into the buffer).
2. (Use the value via the buffer pointer.)
3. Deinitialize with `deinitialize(count:)` (releases anything Swift's
   ARC owns inside the value).
4. Deallocate with `deallocate()` (returns the memory to the allocator).

The wrapper does steps 1–2; never steps 3–4.

For a payload with no ARC-managed inner state (a pure enum tag, an Int,
etc.), step 3 is a no-op. But step 4 is always required to return the
memory. `CardScanSheetResult` is a Swift enum with associated values
that include nominal class types (`ScannedCard`) — so step 3 is
*also* required to release the inner `ScannedCard` reference.

The leak is therefore both a memory leak (the buffer itself) and a
reference leak (the contained `ScannedCard` instance, which holds onto
its underlying Swift class allocation).

## Hypothesis

The Swift wrapper-emitter's "callback that returns an enum payload"
emission has the buffer-allocation step, the buffer-write step, and the
trampoline-handoff step. It's missing the cleanup step entirely. Likely
fix: emit a `defer { resultBuffer.deinitialize(count: 1);
resultBuffer.deallocate() }` at the top of the completion closure body,
*after* the trampoline call, OR pass the buffer ownership across the
trampoline boundary and have the C# trampoline free it when done with
the value.

The latter is cleaner because the trampoline already projects the
buffer's contents into managed types via `SwiftMarshal.MarshalFromSwift<T>`,
and the projection itself can be the freeing point.

## Impact

- **Per-call native memory leak.** Each `present` of a CardScanSheet
  or CardImageVerificationSheet allocates a buffer (~32 bytes for the
  `CardScanSheetResult` enum) plus retains a `ScannedCard` reference
  (~50–100 bytes) that never get freed. Linear growth on repeat scans.
- **Library scope.** Worth auditing the Swift wrapper output for
  every API that uses the `allocate + initializeMemory + cdeclCallback`
  pattern. The pattern is the generator's standard idiom for callback
  values larger than a single word — likely present in many products.
- **Composition with Family A.** This bug compounds with the C#-side
  `GCHandle` leak (same call site, different layer). A consumer who
  wonders why their scan flow grows memory will find leaks at both
  layers.

## Round 4 — StoreKit2 site (2026-05-05)

The Lottie + StoreKit2 audit confirmed one more site, in StoreKit2's
storefront-change observer wrapper. Same emitter, same shape — the
buffer is allocated, initialized in the callback, handed to the C
trampoline, and never freed.

```bash
sed -n '6080,6110p' apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/StoreKit2.Wrapper.swift
```

```swift
// StoreKit2.Wrapper.swift:6095 — Product.PurchaseOption.onStorefrontChange
@_cdecl("$s9StoreKit2…")
public func _swiftWrapper_PurchaseOption_onStorefrontChange(...) {
    let _payloadBuffer = UnsafeMutableRawPointer.allocate(
        byteCount: MemoryLayout<Storefront>.size,
        alignment: MemoryLayout<Storefront>.alignment)            // alloc
    let result = StoreKit.Product.PurchaseOption.onStorefrontChange { storefront in
        _payloadBuffer.initializeMemory(as: Storefront.self, to: storefront)
        cdeclCallback(_payloadBuffer, _context)                    // handoff
        // ← no deinitialize, no deallocate
    }
    ...
}
```

`Storefront` is a Swift struct that wraps an Objective-C `SKStorefront`
reference internally — step 3 (deinitialize) is required to release the
inner reference, step 4 (deallocate) is always required.

This site compounds with the GCHandle leak family (the same call's
managed delegate is rooted indefinitely), so each `onStorefrontChange`
fires triple: GCHandle + native buffer + inner Storefront reference.

The same emitter is used across Apple framework bindings — additional
StoreKit2 sites likely exist for any callback that hands an enum/struct
payload to a C trampoline. Worth a generator-wide audit of `allocate +
initializeMemory + cdeclCallback` in the Wrapper.swift outputs.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / I-7 recurrence.

## Workaround

None purely consumer-side. Both leaks are inside generated wrapper /
binding code.

The proper fix is in
[swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings):
emit the deinitialize+deallocate cleanup in the Swift wrapper.

## Severity

**Correctness — Medium.** Memory + reference leak, not a crash or
correctness defect on the value itself. The per-call cost is small, but
the leak is unbounded over a process's lifetime, and CardScan's scan
loop is exactly the shape that exercises it.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 3 / I-7.
