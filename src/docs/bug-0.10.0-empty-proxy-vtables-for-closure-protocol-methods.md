# Bug: Protocol proxies declare `Func_*_N` slots for closure-taking protocol methods but never assign them

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Stripe](https://github.com/justinwojo/swift-dotnet-packages)
> (StripePayments + StripeIssuing 26.2.1).

## Summary

When a Swift protocol has a method that takes a closure parameter (e.g.
`func authenticate(completion: @escaping (Result) -> Void)`), the
generator emits a C# proxy class to bridge a managed `IFoo` implementation
back into Swift. The proxy declares the function-pointer slot for the
closure-taking method (`Func_methodName_N`) at the field level — but
never assigns it in the local vtable construction at the bottom of the
proxy. The slot stays at its default (null function pointer / unset
delegate).

From the C# consumer's perspective the protocol member is implementable —
they fill in the C# method, register the proxy with Swift, no compile
error. From the Swift runtime's perspective the witness-table dispatch
points into a null function pointer (or an unset trampoline). The result
is silent: the Swift call either no-ops or crashes the process when the
witness is invoked.

This is distinct from
[gap-0.10.0-everyprotocol-and-existentials.md](./gap-0.10.0-everyprotocol-and-existentials.md)
(which throws `NotSupportedException` explicitly — at least the failure is
observable). The empty-vtable bug fails *silently*.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source library: stripe-ios 26.2.1 (StripePayments, StripeIssuing)

## Repro — case 1: `STPAuthenticationContextProxy.Func_prepare_1` declared, never assigned

```bash
sed -n '67000,67050p' libraries/Stripe/StripePayments/obj/Debug/net10.0-ios/swift-binding/StripePayments.cs
```

```csharp
// StripePayments.cs:67014
public unsafe partial class STPAuthenticationContextProxy : …
{
    private static delegate* unmanaged[Cdecl]<…> Func_authenticationPresentingViewController_0;
    private static delegate* unmanaged[Cdecl]<…> Func_prepare_1;          // [1] declared
    …
    static STPAuthenticationContextProxy()
    {
        Func_authenticationPresentingViewController_0 = &…;
        // ← Func_prepare_1 never assigned, stays default (zeroed)
    }
}
```

Swift native shape:

```text
swiftinterface (line 1181-1184):
  public protocol STPAuthenticationContext : NSObjectProtocol {
    func authenticationPresentingViewController() -> UIViewController
    @objc optional func prepare(forPresentation: @escaping () -> Swift.Void)
  }
```

A C# consumer who implements `ISTPAuthenticationContext.PrepareForPresentation`
and registers their object as an authentication context will have Swift
attempt to dispatch through the null `Func_prepare_1` slot at the moment
`STPPaymentHandler` calls `prepare(forPresentation:)`.

## Repro — case 2: ephemeral-key provider proxies are structurally empty

```bash
sed -n '1295,1365p' libraries/Stripe/StripeIssuing/obj/Debug/net10.0-ios/swift-binding/StripeIssuing.cs
```

```csharp
// StripeIssuing.cs:1301
public unsafe partial class STPIssuingCardEphemeralKeyProviderProxy : …
{
    // function pointer slots declared as fields...

    static STPIssuingCardEphemeralKeyProviderProxy()
    {
        // ← static ctor body is empty.
        // No Func_* slot is ever assigned.
    }
}

// StripeIssuing.cs:1357 — same shape on STPCustomerEphemeralKeyProvider
```

Swift native:

```text
swiftinterface:
  public protocol STPIssuingCardEphemeralKeyProvider {
    func createIssuingCardKey(withAPIVersion: String,
                              completion: @escaping STPJSONResponseCompletionBlock)
  }
```

The whole protocol has *only* closure-taking methods, so the proxy's
entire public dispatch surface is unreachable from Swift.

## Hypothesis

Proxy emission has two phases:

1. **Field declaration** — walks the protocol's members and emits one
   `delegate* unmanaged[Cdecl]<…> Func_methodName_N` field per member.
   This phase handles every member shape uniformly.
2. **Static-ctor assignment** — walks the protocol's members again and
   emits `Func_methodName_N = &TrampolineImplName;` to wire each slot to
   its trampoline.

Phase 2 has a guard that skips closure-taking members — likely because
the trampoline emitter for closure-taking members hasn't been written
(or fails internally and is wrapped in a swallowed exception). The slot
declaration in phase 1 fires unconditionally, so the field is present
but never wired up.

Adjacent inconsistency that supports this theory: phase 1 emits the
slot for `prepare_1` (it's *named* in the proxy, so the generator at
least observed the member). Phase 2 just doesn't reach it.

The proper fix is to emit the trampoline for closure-taking proxy
members, the same way callback-arg trampolines are emitted on the
opposite (Swift→C#) direction. The trampoline shape:

```csharp
[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
private static unsafe void TrampolineImpl_prepare_1(
    IntPtr swiftSelf, IntPtr completionContext, IntPtr completionFunc)
{
    var swiftClosureProxy = SwiftClosureMarshaller.Wrap(completionContext, completionFunc);
    var managedSelf = (ISTPAuthenticationContext)GCHandle.FromIntPtr(swiftSelf).Target!;
    managedSelf.PrepareForPresentation(() => swiftClosureProxy.Invoke());
}
```

(plus the corresponding cleanup — see also
[bug-0.10.0-callback-trampoline-gchandle-leak.md](./bug-0.10.0-callback-trampoline-gchandle-leak.md))

## Impact

- **Silent broken delegate dispatch.** Consumer's C# implementation of
  the closure-taking protocol method is never invoked by Swift. The
  protocol contract is broken without any compile-time or first-call
  diagnostic.
- **Crash potential.** Whether Swift's witness call into a null function
  pointer crashes the process depends on the runtime's null-handling
  contract for `unmanaged[Cdecl]` function pointers — likely an
  immediate `EXC_BAD_ACCESS`.
- **Library scope.** Every protocol that mixes ordinary and
  closure-taking methods. In Stripe alone:
  `STPAuthenticationContext.prepare(forPresentation:)`, all four
  `*EphemeralKeyProvider.create*Key` methods. Likely also
  `STPCustomerEphemeralKeyProvider`, `STPApplePayContextDelegate`'s
  closure-taking variants.

## Workaround

Consumer side: do not implement closure-taking protocol methods from C#.
For optional methods, leave them unimplemented (Swift's optional
mechanism falls through cleanly). For required closure-taking methods,
the protocol is currently unimplementable from C# and consumers must
either pass a Swift-side instance or skip the feature.

## Severity

**Correctness — High.** Silent failure mode is the worst class of bug —
the consumer thinks they've implemented the protocol correctly, the
compiler agrees, the binding API surface looks complete, and the runtime
simply skips the call (or crashes). At minimum the static ctor should
explicitly assign these slots to a "throws NotSupportedException"
trampoline so the failure is observable, matching the EveryProtocol
gap-0.10.0 doc.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 3 / I-2.
