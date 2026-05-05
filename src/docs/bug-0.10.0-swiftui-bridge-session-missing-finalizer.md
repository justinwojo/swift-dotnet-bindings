# Bug: SwiftUI bridge session classes own native handles + `GCHandle`s but lack a finalizer

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Lottie](https://github.com/justinwojo/swift-dotnet-packages)
> (Lottie 4.x, SwiftUI bridge — `LottieView`, `LottieButton`, `LottieSwitch`).

## Summary

Generated SwiftUI bridge session classes (`LottieViewSession`,
`LottieButtonSession`, `LottieSwitchSession`) own:

1. A retained native session pointer obtained via `Unmanaged.passRetained`
   in the Swift wrapper.
2. Pinned `GCHandle`s for lifecycle / action callbacks (`_lifecycleHandles`,
   `_closureHandles`).

The C# class implements `Dispose()` that calls `Free(_handle)` and frees
the GCHandles, but has **no finalizer**. If a consumer constructs a
session via `LottieViewSession.Create(animation, onAppear: …)` and forgets
to dispose, the native session, the SwiftUI hosting controller, and every
captured GCHandle leak permanently with no fallback path.

The contrast with the rest of the binding (where `SwiftClassHandle<T>` /
`SwiftSafeHandle<T>` have inherited critical finalizers via SafeHandle)
makes this omission unexpected. The SwiftUI bridge codegen produces a
plain `class : IDisposable` instead of a `SafeHandle`-backed type.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source library: lottie-spm 4.x

## Repro

```bash
sed -n '100,290p' libraries/Lottie/obj/Debug/net10.0-ios/swift-binding/Lottie.SwiftUIBridge.cs
sed -n '85,115p'  libraries/Lottie/obj/Debug/net10.0-ios/swift-binding/Lottie.SwiftUIBridge.swift
```

Generated C# (Lottie.SwiftUIBridge.cs:100-284, abbreviated):

```csharp
public partial class LottieViewSession : IDisposable
{
    private IntPtr _handle;
    private List<GCHandle> _lifecycleHandles = new();
    private List<GCHandle> _closureHandles = new();

    public static LottieViewSession Create(Lottie.LottieAnimation animation,
        Action? onAppear = null, /*...*/)
    {
        var lifecycleHandle = onAppear is null ? default : GCHandle.Alloc(onAppear);
        // pin more
        var nativePtr = LottieViewBridgeNativeMethods.Create(/*...*/);
        return new LottieViewSession { _handle = nativePtr, _lifecycleHandles = ..., ... };
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            LottieViewBridgeNativeMethods.SBW_Lottie_LottieView_Free(_handle);
            _handle = IntPtr.Zero;
        }
        foreach (var h in _lifecycleHandles) if (h.IsAllocated) h.Free();
        foreach (var h in _closureHandles)   if (h.IsAllocated) h.Free();
    }

    // [BAD] no ~LottieViewSession() finalizer.
}
```

Generated Swift (Lottie.SwiftUIBridge.swift:85-112):

```swift
@_cdecl("SBW_Lottie_LottieView_Create")
public func SBW_Lottie_LottieView_Create(...) -> UnsafeMutableRawPointer {
    let session = LottieViewSession(...)
    return Unmanaged.passRetained(session).toOpaque()  // [1] +1 retain
}

@_cdecl("SBW_Lottie_LottieView_Free")
public func SBW_Lottie_LottieView_Free(_ ptr: UnsafeMutableRawPointer) {
    Unmanaged<LottieViewSession>.fromOpaque(ptr).release()  // [2] balances [1]
}
```

The retain/release pair is correct *if the consumer disposes*. Without a
finalizer, GC reclaiming the C# wrapper without `Dispose` strands the
native session permanently.

Same shape on `LottieButtonSession` (Lottie.SwiftUIBridge.cs:351-554) and
`LottieSwitchSession` (:625-794).

## Native ground truth

The SwiftUI types `LottieView`, `LottieButton`, `LottieSwitch` are
declared in the swiftinterface around line 692 with various modifier and
async/placeholder initializers. Because SwiftUI views are not directly
expressible from .NET, the SDK emits a session-object bridge: a Swift
class with explicit lifecycle methods that the C# side drives.

That bridge requires deterministic-or-best-effort cleanup. Today it has
deterministic cleanup only.

## Hypothesis

The SwiftUI-bridge codegen pipeline emits a plain
`class Session : IDisposable` template rather than wrapping the handle
in a `SafeHandle` subclass. Likely fix: emit the handle as a private
`SafeHandle`-derived field whose `ReleaseHandle()` calls `Free(_handle)`,
or add a `~Session()` finalizer that calls a `Dispose(disposing: false)`
overload that frees only unmanaged state.

The `GCHandle`s also need to be released on finalize — the safest pattern
is to store them via the same `SafeHandle` so the critical finalizer
walks them.

## Impact

- **Memory growth in any SwiftUI-bridge consumer who relies on GC-driven
  cleanup.** The "create a session, hand it to a SwiftUI host, forget it"
  pattern is idiomatic .NET but leaks under this binding.
- **Captured-state pinning.** A `Create(animation, onAppear: () =>
  myViewModel.Refresh())` pins `myViewModel` for the process lifetime
  if the session is GC'd without dispose.
- **Likely affects future SwiftUI bridges across all bindings.** Stripe's
  PaymentSheet SwiftUIBridge (verified "real" in Round 3 but not audited
  for finalizer presence) is a candidate to recheck.

## Workaround

Consumer side: always wrap session creation in `using var session =
LottieViewSession.Create(...);` or pair with explicit `Dispose()` in the
view's teardown. SwiftUI's natural `var session` field on a `View` struct
does NOT auto-dispose.

## Severity

**Correctness — Medium.** Latent leak; not user-visible until profiled or
under stress. Foundation for any consumer who builds a Lottie-driven
list/grid in SwiftUI from C#.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / M-4. Also covers the SwiftUI-bridge state-change
callback gap (M-13) — `LottieSwitchSession` has no `Action<bool>` for
isOn changes; the same emitter that gets the finalizer fix should also
emit binding-style state-change callbacks.
