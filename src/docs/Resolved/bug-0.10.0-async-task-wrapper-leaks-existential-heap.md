# Bug: Async-Task wrapper leaks existential heap that the sync overload frees

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Stripe](https://github.com/justinwojo/swift-dotnet-packages)
> (StripePayments 26.2.1 + StripeFinancialConnections 26.2.1).
>
> **Status: RESOLVED on 2026-05-06.** Case 1 was resolved earlier by
> `MethodHandler.TryEmitCompletionHandlerOverload` (the delegating async
> overload calls the sync method, which frees the heap in its `finally`).
> Case 2 (property-setter handler subscription) is now resolved by the
> same closure-context owner-token mechanism that fixes Bug 1 Cat 3:
> `PropertyWrapperEmitter.cs` emits the `_sbWrapClosureContext` adapter
> for `Optional<closure>` setters with `isEscaping: true`, so when the
> property is replaced or cleared, Swift releases the previously stored
> closure, the `_SBClosureCtx` deinit upcalls the C# free callback, and
> the GCHandle is freed exactly once. BindingTests coverage:
> `ClosureEdgeCaseTests.TestBug3Case1AsyncOverloadDelegatesThroughExistential`
> (Case 1) and `OwnershipGCStressTests.TestBundleB_ClosureLifetime_PropertySetterReplace`
> (Case 2). See
> [`bug-0.10.0-callback-trampoline-gchandle-leak.md`](./bug-0.10.0-callback-trampoline-gchandle-leak.md)
> for the shared mechanism.

## Summary

When the generator emits both a callback-style overload (`Foo(args, completion)`)
and the corresponding `FooAsync(args)` Task-returning overload from the same
Swift `@escaping` closure parameter, only the callback-style overload's
`finally` block frees the unmanaged buffer holding the existential payload.
The async overload allocates the same buffer, hands it to the underlying
PInvoke, and returns the `Task` without ever freeing it.

Same shape applies to *property setters* whose backing closure storage is
allocated on assignment but never released when the property is replaced or
cleared (e.g. `OnEvent` event-handler subscription).

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source libraries: stripe-ios 26.2.1 (StripePayments, StripeFinancialConnections)

## Repro — case 1: async confirm overload leaks `authenticationContextHeap`

```bash
sed -n '44760,44795p' libraries/Stripe/StripePayments/obj/Debug/net10.0-ios/swift-binding/StripePayments.cs
sed -n '44940,44965p' libraries/Stripe/StripePayments/obj/Debug/net10.0-ios/swift-binding/StripePayments.cs
```

Sync (callback) overload — frees correctly:

```csharp
// StripePayments.cs:44769  (sync ConfirmPaymentIntent — frees in finally)
public unsafe void ConfirmPaymentIntent(
    string clientSecret,
    object authenticationContext,
    Action<…> completion)
{
    var authenticationContextHeap = SwiftMarshal.AllocExistential(...);
    var __gcHandle = GCHandle.Alloc(...);
    try
    {
        PInvoke_…(authenticationContextHeap, GCHandle.ToIntPtr(__gcHandle), …);
    }
    finally
    {
        SwiftMarshal.FreeExistential(authenticationContextHeap);  // [1] freed
        // (note: __gcHandle is itself leaked — see Family A doc)
    }
}
```

Async overload — same allocation, no `finally`:

```csharp
// StripePayments.cs:44951  (ConfirmPaymentIntentAsync — allocation never freed)
public unsafe Task<…> ConfirmPaymentIntentAsync(
    string clientSecret,
    object authenticationContext)
{
    var authenticationContextHeap = SwiftMarshal.AllocExistential(...);
    var tcs = new TaskCompletionSource<…>();
    var __gcHandle = GCHandle.Alloc((Action<…>)(r => tcs.TrySetResult(r)));
    PInvoke_…(authenticationContextHeap, GCHandle.ToIntPtr(__gcHandle), …);
    return tcs.Task;
    // ← no try/finally, no continuation that frees authenticationContextHeap
}
```

Each `await pipeline.ConfirmPaymentIntentAsync(...)` leaks one
existential-heap allocation per call.

## Repro — case 2: property-setter handler subscription leaks on assignment

```bash
sed -n '550,590p' libraries/Stripe/StripeFinancialConnections/obj/Debug/net10.0-ios/swift-binding/StripeFinancialConnections.cs
```

```csharp
// StripeFinancialConnections.cs:560
public Action<FinancialConnectionsEvent>? OnEvent
{
    get { … }
    set
    {
        Action<IntPtr> __inner = (IntPtr __p0) => { … value(...) … };
        var __gcHandle = GCHandle.Alloc(__inner);                       // [1] alloc
        PInvoke_setOnEvent(
            this.Payload.DangerousGetHandle(),
            GCHandle.ToIntPtr(__gcHandle),
            …);
        // ← no Free, no comparison-with-prior-handle, no clear path
    }
}
```

Replacing the handler `N` times (or clearing it via `OnEvent = null`) leaks
`N` `GCHandle`s plus their captured closure state.

## Native ground truth

```text
swiftinterface (StripePayments line 1180):
  open func confirmPayment(_ clientSecret: String,
                           with authenticationContext: any STPAuthenticationContext,
                           completion: @escaping STPPaymentHandlerActionPaymentIntentCompletionBlock)

swiftinterface (StripeFinancialConnections line 240):
  public var onEvent: ((FinancialConnectionsEvent) -> Void)?
```

The Swift surface gives no special hint that one overload's lifetime is
different from the other's — the generator generates both from the same
underlying `@escaping` closure parameter, but only the callback-form path
emits the cleanup.

## Hypothesis

The generator's wrapper-emission pipeline has two distinct emitters:

1. **Callback wrapper** (`Foo(args, completion)`). Wraps the PInvoke in
   try/finally; the `finally` is the generator's natural cleanup
   insertion point.
2. **Task wrapper** (`FooAsync(args)`). Wraps the PInvoke in a `tcs +
   return tcs.Task` shape. There's no synchronous `finally` because the
   PInvoke is fire-and-forget at C# scope — the cleanup needs to land
   inside the *callback* the generator hands to Swift.

Today the Task-wrapper emitter inherits the allocation step from the
callback emitter but never inherits the cleanup. The fix is to either:

- Lift the cleanup into the trampoline (the closure that fires when Swift
  calls back) so both shapes share the same cleanup site, *or*
- Have the Task-wrapper emit `tcs.Task.ContinueWith(_ =>
  SwiftMarshal.FreeExistential(authenticationContextHeap))` as the
  cleanup hook before returning.

The trampoline-cleanup approach is preferred — it's also the natural fix
for [bug-0.10.0-callback-trampoline-gchandle-leak.md](./bug-0.10.0-callback-trampoline-gchandle-leak.md)
(the `GCHandle.Free` belongs in the same trampoline). Both leaks land in
the same emitter site if the trampoline is the cleanup home.

For property-setter case (case 2): the setter must compare the new closure
against the stored prior closure, free the prior `GCHandle`, then store the
new one. Or — simpler — have the underlying Swift wrapper take ownership of
freeing the prior `GCHandle` when it accepts a new one.

## Impact

- **Memory growth on async hot paths.** Every `await ConfirmPaymentIntentAsync(...)`
  leaks one existential allocation (~32–64 bytes of unmanaged memory per
  call). Long-lived processes that confirm intents repeatedly will grow.
- **`GCHandle` table pressure on event-subscription churn.** UI screens
  that swap event handlers on view appearance/disappearance leak per
  appearance.
- **Library scope.** Affects every async overload generated from a Swift
  `@escaping` closure parameter (Stripe Payments, FinancialConnections,
  Identity, PaymentSheet — everywhere `…Async` is generated). Same
  emitter; one fix.

## Workaround

Consumer side: prefer the callback-form overload (where the generator
emits the cleanup) over the `…Async` overload until the SDK is fixed.
For setter case: only assign the handler once for the lifetime of the
object; do not replace or clear it.

## Severity

**Correctness — High.** Memory leak on the modern Swift surface
(async/await) that the SDK encourages consumers to use. Combined with
[bug-0.10.0-callback-trampoline-gchandle-leak.md](./bug-0.10.0-callback-trampoline-gchandle-leak.md)
(`GCHandle` leak — separate emitter site, same call paths), every async
Stripe API leaks both managed and unmanaged state per call. Both should
ship in the same SDK fix.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 3 / I-1.
