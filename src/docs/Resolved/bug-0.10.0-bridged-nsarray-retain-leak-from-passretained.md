# Bug: Bridged NSArray result leaks one retain per call — Swift `passRetained` paired with `NSArray.ArrayFromHandle(owns:false)`

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Apple.StoreKit2](https://github.com/justinwojo/swift-dotnet-packages)
> (Apple StoreKit framework, 26.2.2).

## Summary

The Swift wrapper for `ExternalPurchaseLink.eligibleURLs` (an
`async throws → [URL]?` API bridged through Apple's NSArray ↔ Swift Array
machinery) calls `Unmanaged.passRetained(_unwrapped as AnyObject)` to
hand the bridged NSArray pointer to .NET, but the C# callback uses
`Foundation.NSArray.ArrayFromHandle<Foundation.NSUrl>(IntPtr)` — the
single-arg overload that does NOT transfer ownership. The +1 retain
emitted by the Swift wrapper is never balanced. Each successful call
leaks one NSArray (plus its contained NSURLs).

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source framework: system StoreKit (iOS 26.2.2)

## Repro

```bash
sed -n '8000,8025p' apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/StoreKit2.Wrapper.swift
sed -n '29230,29270p' apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/StoreKit2.cs
```

Swift wrapper (StoreKit2.Wrapper.swift:8000-8023):

```swift
let resultgetEligibleURLs = await StoreKit.ExternalPurchaseLink.eligibleURLs
let _rawPtr = UnsafeMutableRawPointer.allocate(byteCount: ..., alignment: ...)
if let _unwrapped = resultgetEligibleURLs {
    _rawPtr.storeBytes(of:
        Unmanaged.passRetained(_unwrapped as AnyObject).toOpaque(),  // [1] +1 retain
        as: UnsafeMutableRawPointer.self)
} else {
    _rawPtr.storeBytes(of: 0, as: Int.self)
}
_resultPtr = OpaquePointer(_rawPtr)
callback(_resultPtr, _sbwTask)
```

C# callback (StoreKit2.cs:29230-29270):

```csharp
[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
private static unsafe void getEligibleURLs_callback_…(IntPtr resultPtr, IntPtr taskHandle)
{
    ...
    IntPtr _ptr = *(IntPtr*)resultPtr;
    var result = _ptr == IntPtr.Zero
        ? null
        : Foundation.NSArray.ArrayFromHandle<Foundation.NSUrl>(_ptr);  // [2] owns:false default
    ...
    finally
    {
        SBW_Free(resultPtr);  // frees the OUTER 8-byte allocation, NOT the NSArray
    }
}
```

`SBW_Free` deallocates the small raw pointer holding the NSArray
*pointer*, but never touches the NSArray itself. The `passRetained`
+1 retain at [1] is never balanced.

## Native ground truth

`Foundation.NSArray.ArrayFromHandle<T>` has two overloads in macios:

```csharp
public static T[]? ArrayFromHandle<T>(IntPtr handle);                   // owns: false (default)
public static T[]? ArrayFromHandle<T>(IntPtr handle, bool owns);
```

The single-arg form (`owns: false`) reads the array without taking
ownership — appropriate when the source is a non-+1 reference. With
`owns: true`, `ArrayFromHandle` releases the +1 retain after copying.

## Hypothesis

The wrapper-emission pipeline has a generic "bridge to Cocoa array" path
that uses `passRetained` on the Swift side (correct: ensures the array
survives the call) but the C# callback emitter uses the non-owning
`ArrayFromHandle` overload by default. Likely fix: use the `(IntPtr
handle, bool owns: true)` overload in the C# callback.

Alternatively, the Swift side could emit `passUnretained` (rely on the
async continuation holding the array alive); but `passRetained` +
`owns: true` is the safer pattern when the Swift wrapper deallocates
the raw pointer holder before the callback returns.

## Impact

- **One NSArray (and its contained NSURLs) leaked per successful call to
  `ExternalPurchaseLink.GetEligibleURLsAsync`.** Per-launch / per-checkout
  call rate is small in practice; not catastrophic but still an ARC bug.
- **Likely affects other `passRetained` + `NSArray.ArrayFromHandle`
  pair sites.** A generator-wide audit of the `passRetained.*as
  AnyObject` ↔ `ArrayFromHandle<...>(IntPtr)` pattern would find them.

## Workaround

Consumer side: none. The leak is small and unavoidable from C#.

## Severity

**Correctness — Low.** Per-call leak; bounded call rate.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / M-10.
