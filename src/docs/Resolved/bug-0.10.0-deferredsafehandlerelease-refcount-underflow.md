# Bug: `DeferredSafeHandleRelease` constructor skips `DangerousAddRef`, callback always `Release`s — refcount underflow

> SDK 0.10.0 runtime correctness bug (Swift.Runtime). Discovered 2026-05-05
> during the WeatherKit + MusicKit cross-package consumer-experience audit
> (Round 5). See
> [`audit-weatherkit-musickit-2026-05-05.md`](../../swift-dotnet-packages/audit-weatherkit-musickit-2026-05-05.md)
> finding **O-4**.

## Summary

`DeferredSafeHandleRelease` is the helper used by every async wrapper
that wants to keep a SafeHandle alive across the async PInvoke + Swift
continuation. The async-callback path always calls
`handle.DangerousRelease()` once the continuation lands. The
constructor that stores the handle does **not** call
`DangerousAddRef` to balance that release. On normal-completion
paths this is sometimes okay (the `Payload` reference is held by the
foreground caller), but on cancellation paths the holder may early-
return and run `DangerousRelease` without a balancing `AddRef` — a
direct refcount underflow.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.1
- .NET SDK 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64

## Repro — runtime helper

```bash
sed -n '20,50p' swift-bindings/src/Swift.Runtime/src/Swift/Runtime/AsyncHelpers.cs
```

```csharp
// AsyncHelpers.cs:27
public sealed class DeferredSafeHandleRelease
{
    public SafeHandle Handle { get; }

    public DeferredSafeHandleRelease(SafeHandle handle)
    {
        Handle = handle;     // [1] stores handle, no DangerousAddRef
    }

    public void Release()
    {
        Handle.DangerousRelease();  // [2] always called from async callback
    }
}
```

The intent is: caller holds a SafeHandle, the async PInvoke is fired,
the holder is captured by the callback closure, the foreground method
returns to the caller (whose reference still keeps the SafeHandle
alive), the Swift continuation eventually lands and the callback calls
`Release()` to drop the holder's logical reference.

The bug is that the holder's logical reference was never created.
`DangerousRelease` decrements the refcount without a matching
`DangerousAddRef`.

## Repro — call site

```bash
sed -n '2553,2587p' apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/MusicKit.cs
```

```csharp
// MusicKit.cs:2562  (MusicDataRequest.ResponseAsync)
var holder = new DeferredSafeHandleRelease(this.Payload);
var taskIndex = TaskIndexer.Add(tcs);

if (cancellationToken.IsCancellationRequested)
{
    holder.Release();   // [3] underflow: no AddRef ever called
    tcs.TrySetCanceled(cancellationToken);
    return tcs.Task;
}

PInvoke_response_<HASH>(s_responseCallback_<HASH>, holder, taskIndex);
return tcs.Task;
```

When `cancellationToken.IsCancellationRequested` is true on entry:

- `holder.Release()` calls `this.Payload.DangerousRelease()` — refcount
  underflow if the SafeHandle's refcount is at the foreground-caller's
  baseline value.
- On the *non-cancelled* path, the Swift callback is the only caller of
  `Release`, but the same imbalance exists — it just shifts when the
  underflow surfaces (post-callback rather than pre-PInvoke).

The cancelled path is the easier reproducer; the always-on path can
also surface depending on GC/finalization timing of the foreground
SafeHandle.

## Affected sites

The same shape recurs in 6+ async wrappers across MusicKit:

- `MusicKit.cs:2553-2587` — `MusicDataRequest.ResponseAsync`
- `MusicKit.cs:4783` — adjacent async wrapper
- `MusicKit.cs:29491` — `MusicCatalogResourceRequest`-family
- `MusicKit.cs:32584` — adjacent
- `MusicKit.cs:33698` — adjacent
- `MusicKit.cs:44422` — `MusicSubscription.Updates.NextAsync`

Likely affects every async wrapper across every SDK-emitted binding;
WeatherKit, StoreKit2, Stripe products use the same helper.

## Hypothesis

Implementation oversight in the runtime helper. The helper was named
`DeferredSafeHandleRelease` to convey "SafeHandle release happens
later (deferred)," but the corresponding `AddRef` was never written —
the helper holds a reference but never declares it.

The fix is one line in the constructor:

```csharp
public DeferredSafeHandleRelease(SafeHandle handle)
{
    bool addedRef = false;
    handle.DangerousAddRef(ref addedRef);
    if (!addedRef) throw new InvalidOperationException("…");
    Handle = handle;
}
```

This pairs cleanly with the existing `Release()` body. No call sites
need to change.

## Impact

Cancellation patterns are first-class .NET — every consumer that
threads a `CancellationToken` from a UI button, a timeout, or a
`CancellationTokenSource.Token` into an async MusicKit/WeatherKit/etc.
call hits this. Underflow on `SafeHandle.DangerousRelease` either
- destabilizes the SafeHandle's lifetime (premature
  `ReleaseHandle()` call → use-after-free on subsequent operations),
  or
- triggers an `ObjectDisposedException` on next access, which the
  consumer didn't dispose.

Hard to reproduce in unit tests without explicit cancellation — most
tests don't pass a `CancellationToken`. The bug is silent on
"happy path" execution but reproducible under cancellation.

## Severity

**High.** Cross-cutting runtime helper used by every async wrapper.
Pairs with cancellation patterns standard in .NET. Single-point fix.

## Fix gate

The `DeferredSafeHandleRelease` constructor at
`swift-bindings/src/Swift.Runtime/src/Swift/Runtime/AsyncHelpers.cs:27`
should add a `DangerousAddRef` call. A test that constructs a
SafeHandle, wraps it in `DeferredSafeHandleRelease`, calls `Release()`,
and asserts the SafeHandle's refcount returned to its starting value
would catch the regression.

Once fixed, `MusicDataRequest.ResponseAsync` with an already-cancelled
token should not crash the SafeHandle's lifetime.
