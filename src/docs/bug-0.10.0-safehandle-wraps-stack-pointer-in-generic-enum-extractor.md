# Bug: Generic enum extractor wraps a `stackalloc` pointer in `SwiftSafeHandle` for `ISwiftObject` payload type — heap-corruption / crash on Dispose

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Apple.StoreKit2](https://github.com/justinwojo/swift-dotnet-packages)
> (Apple StoreKit framework, 26.2.2).

## Summary

The generator's enum-payload extractor (`TryGetVerified` / `TryGetUnverified`
on `VerificationResult<TSignedType>`) `stackalloc`s a buffer for the
projected payload, calls `InitializeWithCopy` to copy the enum's storage
into that stack buffer, then — for the `ISwiftObject` non-`ISwiftStruct`
class branch — passes the *stack pointer* into `SwiftMarshal.MarshalFromSwift
<TSignedType>(new IntPtr(enumCopy))`.

`MarshalFromSwift<T>` for an `ISwiftObject` `T` calls `T.NewFromPayload(handle)`,
which stores the `IntPtr` directly into a `SwiftSafeHandle<T>` with no copy.
Eventual `SwiftSafeHandle.ReleaseHandle` calls `NativeMemory.Free((void*)
stackPointer)` — undefined behavior; in practice a heap-corruption crash on
iOS.

The sibling `Product.PurchaseResult.TryGetSuccess` extractor at
`StoreKit2.cs:22107` uses the *correct* heap-alloc pattern, demonstrating
that the generator already knows how. The generic-`TSignedType` path is
the regression. The `TryGetUnverified` second tuple element (`value1:
VerificationError`) ALSO uses the correct heap-alloc path — only the
`value0: TSignedType` branch is broken.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source framework: system StoreKit (iOS 26.2.2)

## Repro

```bash
sed -n '3656,3700p' apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/StoreKit2.cs
sed -n '3576,3630p' apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/StoreKit2.cs
```

`TryGetVerified` (StoreKit2.cs:3656-3700):

```csharp
public bool TryGetVerified([MaybeNullWhen(false)] out TSignedType value)
{
    ...
    byte* enumCopy = stackalloc byte[(int)metadata.Size];
    metadata.ValueWitnessTable->InitializeWithCopy(
        enumCopy, (void*)_payload.DangerousGetHandle(), metadata);
    metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);
    if (typeof(global::Swift.Runtime.ISwiftObject).IsAssignableFrom(typeof(TSignedType))
        && !typeof(TSignedType).IsValueType
        && !typeof(global::Swift.Runtime.ISwiftStruct).IsAssignableFrom(typeof(TSignedType))
        && global::Swift.Runtime.TypeMetadata.GetTypeMetadataOrThrow<TSignedType>().Kind
            == global::Swift.Runtime.TypeMetadataKind.Class)
    {
        // Class T (e.g. Transaction, AppTransaction). The IntPtr stored in
        // the SafeHandle IS enumCopy — a stack pointer.
        value = SwiftMarshal.MarshalFromSwift<TSignedType>(*(IntPtr*)(enumCopy));
    }
    else
    {
        // Same shape — `new IntPtr(enumCopy)` straight into MarshalFromSwift.
        value = SwiftMarshal.MarshalFromSwift<TSignedType>(new IntPtr(enumCopy));
    }
    return true;
}
```

`TryGetUnverified` value0 branch (StoreKit2.cs:3620):

```csharp
// value0 is TSignedType — same broken path.
value0 = SwiftMarshal.MarshalFromSwift<TSignedType>(new IntPtr(enumCopy + (int)offset0));

// value1 is VerificationError — uses heap-alloc, correctly:
void* _value_heap = NativeMemory.Alloc(metadata1.Size);
metadata1.ValueWitnessTable->InitializeWithCopy(_value_heap,
    enumCopy + (int)offset1, metadata1);
value1 = SwiftMarshal.MarshalFromSwift<VerificationError>(new IntPtr(_value_heap));
```

`SwiftHandle.ReleaseHandle` (Swift.Runtime/SwiftHandle.cs:213-243):

```csharp
protected override bool ReleaseHandle()
{
    if (handle != IntPtr.Zero)
        NativeMemory.Free((void*)handle);   // [BAD] frees stack pointer
    return true;
}
```

## Native ground truth

```text
swiftinterface (StoreKit framework, line ~310):
  @frozen public enum VerificationResult<SignedType : Sendable> : Sendable {
    case verified(SignedType)
    case unverified(SignedType, VerificationError)
  }
```

The realized `TSignedType` instantiations in this module are
`Transaction`, `AppTransaction`, and `Product.SubscriptionInfo
.RenewalInfo` — all classes / `ISwiftObject` non-`ISwiftStruct` — so every
real consumption goes through the broken branch.

## Hypothesis

The extractor emitter has two code paths:

1. **Concrete known-payload-type:** uses `_value_heap = NativeMemory.Alloc
   (metadata.Size)` + `InitializeWithCopy(_value_heap, enumCopy+offset,
   metadata)` + `MarshalFromSwift<T>(new IntPtr(_value_heap))`. Correct.
   This is what `Product.PurchaseResult.TryGetSuccess` and the value1
   branch of `TryGetUnverified` use.
2. **Generic `TSignedType` payload:** falls back to `MarshalFromSwift<TSignedType>
   (new IntPtr(enumCopy))` — the *stack* pointer — because the generic
   path skips the heap-alloc step, presumably because metadata for
   `TSignedType` is fetched per-instantiation rather than statically.

Likely fix: emit the heap-alloc-and-copy step in the generic path too.
The `metadata.Size` (or `metadata1.Size`) is already known at runtime
inside the extractor; allocating a heap buffer is not blocked by genericity.

## Impact

- **Crash on Dispose** of any `Transaction` / `AppTransaction` extracted
  via `TryGetVerified` or `TryGetUnverified`. iOS heap allocator
  refuses to free a non-heap pointer; behavior is undefined but in
  practice the process aborts.
- **Crash on finalize** if the consumer doesn't `using` the returned
  value. The `SwiftSafeHandle<T>` has the inherited critical finalizer
  → release runs on a finalizer thread → process crash with no
  recovery path.
- **Affects every StoreKit consumer** — `Transaction.Updates`,
  `Transaction.All`, `Transaction.Latest(productID:)`,
  `Transaction.CurrentEntitlements`, `Transaction.CurrentEntitlement
  (productID:)`, `AppTransaction.GetSharedAsync`, all subscription-status
  payloads. The verification path is THE central StoreKit2 surface.

## Workaround

Consumer side: there is no safe workaround. Holding the
`VerificationResult<Transaction>` alive forever leaks; disposing it
crashes. The only mitigation is to extract the relevant projected
properties (`PurchaseDate`, `OriginalID`, etc.) into local variables
*before* disposing, accepting that the dispose itself will crash.

In practice consumers must not ship a build that exercises this surface
until the SDK fix lands.

## Severity

**Correctness — High.** Crash-on-use for the central StoreKit verification
API. THE worst single defect in the Round 4 audit. Distinct emitter from
`bug-0.10.0-async-task-wrapper-leaks-existential-heap.md` (Round 3 I-1)
which is a leak; this is undefined-behavior-on-cleanup.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / M-1.
