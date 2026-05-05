# Bug: Callback-style wrappers leak GCHandles for the managed delegate

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Nuke](https://github.com/justinwojo/swift-dotnet-packages)
> 13.0.5 generated bindings.

## Summary

Two distinct closure-marshalling code paths in the generator emit
`GCHandle.Alloc(...)` for a managed delegate that is bridged into a Swift
`@escaping` closure parameter, then never call `handle.Free()` — neither
in the wrapper's `finally`, nor in the unmanaged callback trampoline that
re-enters managed code. Every call leaks one or two managed `GCHandle`s
plus the captured delegate / its closure state, indefinitely.

The two affected paths in Nuke 13.0.5:

1. **"Indirect-context" trampoline pattern** (Cdecl, `MCB_*` trampoline +
   `Action<IntPtr> __inner`). Used for `LoadImage(request, completion)` and
   `LoadData(url, completion)`.
2. **`SwiftClosureData` direct-context pattern** (Swift CC, `s_*_Callback`
   + per-arg `SwiftClosureData`). Used for the multi-closure overloads
   `LoadImage(request, queue, progress, completion)` and
   `LoadData(request, didReceiveData, completion)`.

Both patterns alloc; neither frees.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source library: Nuke 13.0.5

## Repro

Once the build is unblocked, a consumer-side test that calls
`pipeline.LoadImage(request, completion)` in a loop and watches the
process's GC handle table size will see linear growth with no plateau.
Until then, the leak is observable by static read of the generated
wrapper / trampoline pair.

```bash
sed -n '15220,15315p' libraries/Nuke/obj/Debug/net10.0-ios/swift-binding/Nuke.cs
```

## Generated code — pattern 1 (`MCB_*` indirect-context)

```csharp
// Nuke.cs:15223
public unsafe Nuke.ImageTask LoadImage(
    Nuke.ImageRequest request,
    Action<Swift.SwiftResult<Nuke.ImageResponse, Nuke.ImagePipeline.Error>> completion)
{
    Action<IntPtr> __inner = (IntPtr __p0) =>
    {
        var __a0 = SwiftMarshal.MarshalBorrowedFromSwift<…>(__p0);
        completion(__a0);
    };
    var __gcHandle = GCHandle.Alloc(__inner);                      // [1] alloc
    var __result = PInvoke_MCB_82E7C4F9_0(
        request.Payload.DangerousGetHandle(),
        s_MCB_82E7C4F9_0,
        GCHandle.ToIntPtr(__gcHandle),                             // pinned context
        Payload.DangerousGetHandle());
    return (Nuke.ImageTask)SwiftMarshal.MarshalFromSwift<Nuke.ImageTask>(__result);
    // ← no Free, no try/finally
}

// Nuke.cs:15305
[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
private static unsafe void MCB_332154D3_0(IntPtr arg0, IntPtr contextPtr)
{
    var handle = GCHandle.FromIntPtr(contextPtr);
    var callback = (Action<IntPtr>)handle.Target!;
    callback(arg0);
    // ← no handle.Free()
}
```

The wrapper allocs the handle [1] and hands the pinned `IntPtr` to Swift as
opaque context. Swift retains the closure for as long as the outstanding
`ImageTask` lives, then invokes the trampoline once with `(arg0,
contextPtr)`. The trampoline reads `handle.Target`, dispatches to the
delegate, and returns. Neither side ever calls `handle.Free()`, so the
managed delegate (and the captured `completion` reference) stay rooted
forever.

## Generated code — pattern 2 (`SwiftClosureData` direct-context)

```csharp
// Nuke.cs:15259  (3-closure overload)
public Nuke.ImageTask LoadImage(
    Nuke.ImageRequest request,
    Swift.DispatchQueue? queue,
    Action<Nuke.ImageResponse?, long, long>? progress,
    Action<SwiftResult<…>> completion)
{
    GCHandle progressHandle = default;
    GCHandle completionHandle = default;
    try
    {
        SwiftClosureData progressClosure;
        if (progress != null)
        {
            progressHandle = GCHandle.Alloc(progress);             // [2] alloc
            progressClosure = new SwiftClosureData(
                (IntPtr)s_loadImage_progress_BF297B66_Callback,
                GCHandle.ToIntPtr(progressHandle));
        }
        else { progressClosure = default; }
        completionHandle = GCHandle.Alloc(completion);              // [3] alloc
        var completionClosure = new SwiftClosureData(
            (IntPtr)s_loadImage_completion_BF297B66_Callback,
            GCHandle.ToIntPtr(completionHandle));
        ...
        var result = PInvoke_loadImage_BF297B66(
            request.Payload, queueBuffer,
            progressClosure, completionClosure, self);
        return (Nuke.ImageTask)SwiftMarshal.MarshalFromSwiftObject<Nuke.ImageTask>(result);
    }
    finally
    {
        if (success)
           _handle.DangerousRelease();
        // ← no progressHandle.Free(), no completionHandle.Free()
    }
}

// Nuke.cs:15244 callback target
private static unsafe void loadImage_completion_BF297B66_Callback(
    void* arg0, SwiftSelf context)
{
    var del = SwiftClosureMarshaller
        .GetDelegateFromContext<Action<…>>(new IntPtr(context.Value));
    del(SwiftMarshal.MarshalBorrowedFromSwift<…>(new IntPtr(arg0)));
    // ← no Free
}
```

Same shape: alloc per closure arg, no Free anywhere.

## Affected sites (Nuke 13.0.5 alone)

| Site | C# line | Pattern | Closures alloc'd |
|---|---|---|---|
| `ImagePipeline.LoadImage(request, completion)` | 15223 | 1 (MCB) | 1 |
| `ImagePipeline.LoadImage(request, queue, progress, completion)` | 15259 | 2 (SwiftClosureData) | 1 or 2 |
| `ImagePipeline.LoadData(url, completion)` | ≈15326 | 1 (MCB) | 1 |
| `ImagePipeline.LoadData(request, didReceiveData, completion)` | ≈15378 | 2 (SwiftClosureData) | 2 |

There are similar trampolines elsewhere in the file (e.g.
`s_loadData_didReceiveData_29893761_Callback` at 10525,
`s_loadData_completion_29893761_Callback` at 10533) that follow the same
shape and presumably leak when invoked from a wrapper using pattern 2.

## Hypothesis

There is no central "lifecycle policy" for `@escaping` closures bridged
across the C#→Swift→C# boundary. Two competing emitters both grew the
"alloc the handle" half of the pattern in 0.10.0 (or earlier) without the
matching "free in the trampoline" half.

Right place to fix:

- **For pattern 1 (single-shot completion):** the trampoline `MCB_*` knows
  the closure has fired exactly once — append `handle.Free()` after the
  callback dispatches. (For closures that may fire many times, the policy
  needs to differ; see below.)
- **For pattern 2 (`SwiftClosureData`):** the runtime helper
  `SwiftClosureMarshaller` (which already brokers `GetDelegateFromContext`)
  is the natural lifetime owner. One option: the wrapper hands the
  `GCHandle` to a `SwiftClosureData` constructor that registers a Swift
  destroy-thunk, and Swift's reference release fires both. Another:
  Swift-side closure types lower to a Swift class that the SDK's runtime
  controls, and `deinit` of that class frees the `GCHandle` over an FFI
  upcall. Either way, the wrapper's `finally` cannot free unilaterally
  because Swift may still hold the closure alive after the wrapper returns
  (Swift `@escaping` semantics).

The lifetime model needs to distinguish:

- **Single-shot:** completion handlers, `await`-style wrappers. Trampoline
  knows to free after dispatch.
- **Multi-shot:** progress handlers, observer registrations. Trampoline
  cannot free. Swift's release of the closure context must drive the free.

The current emitter treats both the same — alloc, never free — which is
correct for neither.

## Impact

- **Slow leak.** Per call site: ~24 bytes for the `GCHandle` table entry +
  whatever the captured delegate's closure-class allocation is (typically
  64-256 bytes for a non-trivial lambda, more if it captures `this`).
- **Realistic budget.** A scrolling image-grid that loads 100 thumbnails
  per minute leaks ~6,000 closures/hour through `LoadImage(request,
  completion)` alone. The `GCHandle` table is unbounded; the rooted
  delegates also pin every captured object transitively, so the practical
  effect is "the entire image-loading callback graph is uncollectable."
- **Async wrappers (`*Async`) are unaffected** — those bridge through
  `TaskCompletionSource` and free in their own continuation; the leaking
  pattern is only in the Action-based callback overloads.
- **Library-wide.** Anywhere the SDK emits an `@escaping` closure
  marshaller in the Action-based form. Nuke is the discovery case but
  every binding that exposes callback-style APIs is affected. Stripe,
  Lottie, BlinkIDUX (cameras / event streams) are all candidates.

## Round 4 — Lottie + StoreKit2 sites (2026-05-05)

The cross-package audit of `SwiftBindings.Lottie` (4.x) and
`SwiftBindings.Apple.StoreKit2` (Apple framework, 26.2.2) confirms 7
new sites — including the first audit-confirmed Apple-framework
recurrences (StoreKit2's `onStorefrontChange` and AdvancedCommerce
variant). Same `GCHandle.Alloc → empty finally` pattern; same emitter.

**Lottie:**

- Lottie.cs:4754-4787 — `LottieAnimationLayer.Play(Action<bool>?
  completion = null)`. Allocates `completionHandle` at line 4766,
  passes via `GCHandle.ToIntPtr(completionHandle)` at 4777, `finally
  { }` is empty at 4782-4784. The trampoline at 4756-4760 calls
  `SwiftClosureMarshaller.GetDelegateFromContext<Action<bool>>` which
  reads `GCHandle.Target` but does not call `handle.Free()`.
- Lottie.cs:4794-4847 — `LottieAnimationLayer.Play(double? fromProgress,
  double toProgress, …)` — same shape, multi-arg variant.
- Lottie.cs:5198 — `LottieAnimationLayer.SetPlaybackMode` —
  same shape, property setter calling completion-style closure.
- Lottie.cs:23790 — compatible-view playback variant — same shape.
- Lottie.cs:15845-15859 — DotLottie callback-based load
  (`handleResultHandle`).

**StoreKit2:**

- StoreKit2.cs:21728-21735 — `Product.PurchaseOption.OnStorefrontChange
  (Action<Storefront>)`. `GCHandle.Alloc` at 21728, passed to PInvoke at
  21730, `finally { }` empty at 21735.
- StoreKit2.cs:27331 — `AdvancedCommerceProduct.PurchaseOption`
  `OnStorefrontChange` variant, same shape.

The Stripe Round 3 audit (catalogued in
[audit-stripe-2026-05-05.md](../../swift-dotnet-packages/audit-stripe-2026-05-05.md))
expanded the count to ~20 sites across 7 products. Cumulative cross-
audit total now ~31 confirmed leaking call sites. Single emitter fix
addresses all of them.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / Family A.

## Workaround

None on the consumer side. Until 0.10.1 ships, callers should prefer the
`*Async` overloads when both forms exist and treat the callback overloads
as known leaks.

## Severity

**Correctness — High.** Memory leak with no upper bound, in code paths
that consumers will hit on every screen of every image-loading UI. Pair
with C1 ([bug-0.10.0-dataloader-validate-uninitialized-buffer.md](./bug-0.10.0-dataloader-validate-uninitialized-buffer.md))
in the next SDK ship.
