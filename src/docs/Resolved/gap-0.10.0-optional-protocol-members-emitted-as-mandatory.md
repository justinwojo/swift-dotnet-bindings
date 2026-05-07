# Gap: Optional ObjC / Swift protocol members are emitted as mandatory C# interface members

> SDK 0.10.0 generator feature gap. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Stripe](https://github.com/justinwojo/swift-dotnet-packages)
> (StripePaymentsUI + StripePayments 26.2.1).

## Summary

Swift / Objective-C protocols can declare members as `@objc optional`
(or, in pure-Swift, optional via protocol-extension defaults). Conformers
implement only the subset they care about; the framework no-ops or
falls back to defaults for the rest.

The generator emits these protocols as C# interfaces with **every**
member declared as a required interface method. Consumers must implement
all of them — even the ones they never opted into — typically as no-op
stubs that return defaults.

A second wrinkle: at least one Stripe protocol gains a *fabricated*
member that does not exist in the Swift protocol at all
(`ISTPAuthenticationContext.AuthenticationContextDidDismiss` —
not in swiftinterface:1181). The generator appears to be either
inheriting from a parent protocol it shouldn't, or synthesizing helper
shapes that should live elsewhere.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source library: stripe-ios 26.2.1 (StripePaymentsUI, StripePayments)

## Repro — case 1: optional ObjC delegate methods become mandatory

```bash
sed -n '4170,4210p' libraries/Stripe/StripePaymentsUI/obj/Debug/net10.0-ios/swift-binding/StripePaymentsUI.cs
```

```csharp
// StripePaymentsUI.cs:4172
public partial interface ISTPPaymentCardTextFieldDelegate
{
    void PaymentCardTextFieldDidChange(STPPaymentCardTextField textField);
    void PaymentCardTextFieldDidBeginEditing(STPPaymentCardTextField textField);
    void PaymentCardTextFieldDidEndEditing(STPPaymentCardTextField textField);
    void PaymentCardTextFieldDidBeginEditingNumber(STPPaymentCardTextField textField);
    void PaymentCardTextFieldDidEndEditingNumber(STPPaymentCardTextField textField);
    // …12 methods, all mandatory
}
```

Native:

```text
swiftinterface (line 352-365):
  @objc public protocol STPPaymentCardTextFieldDelegate : NSObjectProtocol {
    @objc optional func paymentCardTextFieldDidChange(_: STPPaymentCardTextField)
    @objc optional func paymentCardTextFieldDidBeginEditing(_: STPPaymentCardTextField)
    @objc optional func paymentCardTextFieldDidEndEditing(_: STPPaymentCardTextField)
    @objc optional func paymentCardTextFieldDidBeginEditingNumber(_: STPPaymentCardTextField)
    @objc optional func paymentCardTextFieldDidEndEditingNumber(_: STPPaymentCardTextField)
    // …12 methods, all `@objc optional`
  }
```

Every method is `@objc optional` in Swift. Every method is mandatory in
C#.

A consumer who only cares about "did change" must still implement 11
no-op stubs to make the compiler happy.

## Repro — case 2: optional Swift members made mandatory + fabricated member added

```bash
sed -n '21600,21625p' libraries/Stripe/StripePayments/obj/Debug/net10.0-ios/swift-binding/StripePayments.cs
```

```csharp
// StripePayments.cs:21606
public partial interface ISTPAuthenticationContext
{
    UIViewController AuthenticationPresentingViewController();
    void PrepareForPresentation(Action completion);            // optional in Swift, mandatory here
    void AuthenticationContextDidDismiss();                    // [1] not in Swift at all
}
```

Native:

```text
swiftinterface (line 1181-1184):
  public protocol STPAuthenticationContext : NSObjectProtocol {
    func authenticationPresentingViewController() -> UIViewController
    @objc optional func prepare(forPresentation: @escaping () -> Swift.Void)
  }
```

Two issues stacked:

1. `prepare(forPresentation:)` is `@objc optional`, emitted as mandatory.
2. `AuthenticationContextDidDismiss` does not exist in the Swift
   protocol at all. (No corresponding `@objc func` or default extension
   declaration — confirmed by grep over the swiftinterface.)

The fabricated member appears to be a shape inherited from
`STPPaymentHandlerActionParams` or a sibling protocol that the
generator's "merge inherited members" pass copied into the wrong place.

## Hypothesis

C# does not have a direct analogue to `@objc optional`. The closest
analogues:

- **Default interface methods (DIM)** — the C# 8 feature would let the
  generator emit `void Foo() { }` as a default implementation on the
  interface itself, satisfying the optional-by-no-op contract.
  Caveat: DIMs require explicit interface implementation when the
  consumer wants to override, which changes consumer ergonomics.
- **Adapter base class** — emit a `Stp…DelegateAdapter : ISp…Delegate`
  class with all members no-op, and document "inherit from the adapter,
  override only what you need." This is the pattern Java/.NET Foundation
  classes use for similar shapes.
- **Optional-method attribute + runtime check** — emit the member with
  an `[OptionalProtocolMember]` attribute and have the proxy emitter
  skip dispatching when the member is unimplemented. Requires
  proxy-side cooperation.

The DIM approach is the cleanest fit for protocol-as-interface lowering.

For case 2 (fabricated member): audit the inherited-member-merge pass.
The generator likely walks the protocol's inheritance chain and copies
members in, but the boundary between "a protocol I inherit from" and
"a sibling type that has overlapping witness names" is loose.

## Impact

- **Boilerplate burden.** Every C# consumer of any ObjC delegate
  protocol with N optional methods must implement N no-op stubs.
  StripePaymentsUI alone has at least 12 such methods on
  `STPPaymentCardTextFieldDelegate`; the cumulative cost across all
  Stripe delegates and other ObjC-heavy SDKs is substantial.
- **Wrong-shape interface contract.** Fabricated members (case 2) tell
  the consumer to implement methods Swift will never call. At best
  consumers leave them as no-op stubs forever; at worst they wire them
  up to logic and wonder why the logic never runs.
- **Library scope.** Affects every Swift `@objc optional` member, which
  is the dominant pattern across UIKit/AppKit-style delegate protocols
  bridged from ObjC into Swift.

## Workaround

Consumer side: implement all interface members. For ones you don't care
about, leave them empty (`{ }`) or return defaults (`return null!;`).

For case 2 (fabricated): leave the fabricated member as a no-op stub. It
will never be called. Diff against the Swift swiftinterface to identify
which interface members are real vs. fabricated.

## Severity

**Feature gap — Medium.** Doesn't crash, doesn't corrupt state. Just
forces consumers to write boilerplate. But the boilerplate burden is
proportional to the breadth of the SDK's delegate surface, which is
large for Stripe and large for any UIKit-bridged Swift framework.
Also affects perceived API quality — delegate protocols feel
heavyweight in C# in a way they don't in Swift.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 3 / I-3.
