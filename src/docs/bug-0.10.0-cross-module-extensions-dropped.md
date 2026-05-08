# Bug: Extensions on a type owned by a different module are dropped

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Stripe](https://github.com/justinwojo/swift-dotnet-packages)
> (StripePayments 26.2.1).
>
> **Status: OPEN.** In scope for 0.10.0. Fixing this requires new
> emission infrastructure (a fourth routing path beyond the three listed
> below) — see "Routing gaps" and "Hypothesis" sections for the shape of
> the fix.

## Routing gaps (as of Bundle 04 closure)

Three call paths exist for cross-module extensions in the parser/emitter
pipeline; only one of them currently produces output for the bug shape
above.

1. **Apple/system-module extensions (e.g. `extension Swift.KeyPath` in
   RichTextKit)** — handled today. SwiftABIParser sets
   `moduleNameOverride` for nodes whose `node.ModuleName` is in
   `IsKnownAppleOrSystemModule`, building a `ClassDecl` with a foreign
   `SwiftTypeName.Module`. ClassHandler dispatches to
   `CrossModuleExtensionEmitter`, which emits a
   `static partial class {Type}{Module}Extensions` with `this`-prefixed
   instance methods. Wired and verified by static analysis of
   `ClassHandler.cs` lines 101–108.
2. **ObjC foreign-type extensions (e.g. `extension UIKit.UIView` in a
   Swift module)** — handled today by `ForeignTypeExtensionEmitter`,
   gated through `IsForeignObjCClassType`.
3. **Third-party Swift-module extensions (the Stripe shape:
   `extension StripeCore.STPAPIClient` declared in StripePayments;
   also reproduced by `extension SwiftBindingsTestLibDependency.DependencyPoint`
   in BindingTests)** — DROPPED. `SwiftABIParser` lines 826–833 skip the
   re-export entirely; the swiftinterface fallback puts the members on
   `ExtensionMemberCandidates`, `ResolveForeignExtensions` correctly
   classifies them as foreign, but `ForeignTypeExtensionEmitter` rejects
   non-ObjC foreign types at `IsForeignObjCClassType` and the candidates
   end up nowhere.

The fix tracked in Bundle 12 needs to thread a fourth path: synthesize
a cross-module emission shape for foreign-Swift-module receivers using
the same emit shape as path 1 (`{Type}{Module}Extensions` with
`this`-prefixed methods), so consumer C# code in module B can call
extension members on a type owned by module A through the natural
extension-method idiom.

## Test coverage today

`BindingTests/Sources/SwiftBindingsTestLib/CrossModule/CrossModuleUsage.swift`
lines 144–154 declare the extension shape:

```swift
extension DependencyPoint {
    public func scaled(by factor: Double) -> DependencyPoint
    public var manhattanDistance: Double { … }
}
```

Neither member appears in `BindingTests/output/SwiftBindingsTestLib.cs` or
`BindingTests/output/SwiftBindingsTestLibDependency.cs`. The fixture is
intentionally left in source as a regression sentinel — once Bundle 12
lands, both members should surface as `DependencyPointSwiftBindingsTestLibExtensions`
static methods on the consuming module's binding.

## Summary

When module B declares an extension on a type owned by module A — e.g.
`extension StripeCore.STPAPIClient` declared inside `StripePayments` —
the generator processing module B does not emit the extension's members
into either module's C# binding. The members are lost from the public
API surface.

In Stripe specifically, ~50% of the user-facing PaymentsClient API
surface lives in this shape:

```swift
// StripePayments declares this extension on a StripeCore-owned type:
extension StripeCore.STPAPIClient {
    public func createToken(...)        // payment tokens
    public func createSource(...)       // legacy sources
    public func retrievePaymentIntent(...)
    public func confirmPaymentIntent(...)
    public func createPaymentMethod(...)
    public func updatePaymentMethod(...)
    public func verifyMicrodeposits(...)
    // …a lot more
}
```

None of this surface appears in `StripePayments.cs` or `StripeCore.cs`.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source library: stripe-ios 26.2.1 (StripeCore + StripePayments)

## Repro

```bash
sed -n '3290,3360p' libraries/Stripe/StripePayments/StripePayments.xcframework/ios-arm64_x86_64-simulator/StripePayments.framework/Modules/StripePayments.swiftmodule/arm64-apple-ios-simulator.swiftinterface

# What's in the generated C#:
grep -E "createToken|createSource|retrievePaymentIntent|confirmPaymentIntent" \
     libraries/Stripe/StripePayments/obj/Debug/net10.0-ios/swift-binding/StripePayments.cs
grep -E "createToken|createSource|retrievePaymentIntent|confirmPaymentIntent" \
     libraries/Stripe/StripeCore/obj/Debug/net10.0-ios/swift-binding/StripeCore.cs
```

The swiftinterface shows the extension and its dozens of public methods.
Both grep commands return nothing.

## Native ground truth — extension layout

```text
swiftinterface (StripePayments.swiftinterface line 3295):
  extension StripeCore.STPAPIClient {
    public func createToken(withParameters: STPTokenParams,
                            completion: @escaping STPTokenCompletionBlock)
    public func createSource(with sourceParams: STPSourceParams,
                             completion: @escaping STPSourceCompletionBlock)
    public func retrievePaymentIntent(withClientSecret secret: String,
                                      completion: @escaping STPPaymentIntentCompletionBlock)
    // …~30 more
  }
```

Swift's extension semantics: the extension's members become first-class
methods on `STPAPIClient` *for any code that imports `StripePayments`*.
A Swift consumer who `import StripePayments` and then calls
`apiClient.createToken(...)` is calling into PaymentsClient code via
StripeCore's API surface.

## Hypothesis

The generator's pass over module B (`StripePayments`) likely processes
members and groups them by their declared owning type. When it sees a
member belonging to a type owned by a different module
(`StripeCore.STPAPIClient`), it has three possible behaviors:

1. **Skip and report** — emit nothing, log nothing. (What appears to
   happen today; nothing in `binding-report.json` flags these as
   skipped.)
2. **Emit on module A's type** — generate the member into
   `StripeCore.cs` as if `StripeCore` had declared it. Wrong: it would
   pollute `StripeCore` consumers with a member they didn't ask for.
3. **Emit as a static helper class on module B** — generate something
   like `static class STPAPIClient_StripePaymentsExtensions { public
   static Token CreateToken(this STPAPIClient client, …) … }`. This is
   the C# extension-method pattern and matches Swift's semantics:
   importing the static helpers (via `using`) makes the methods
   available on the type.

#3 is the right answer. C# extension methods exist for exactly this
shape; they live in the consuming module and show up on the type only
when the consumer references the static class.

The fix needs:

- Detection: classify each Swift extension by the module of its receiver
  type vs. the module of the extension declaration.
- For cross-module extensions, route to a new emitter that produces
  `static class <Receiver>_<ModuleB>Extensions` containing
  `this`-prefixed static methods.
- Preserve generic / async / throwing modifiers as the standard C#
  extension-method emission does.

## Native ground truth — Stripe's design intent

The whole `STPAPIClient` design hinges on this extension pattern.
StripeCore defines the basic transport and `STPAPIClient`. StripePayments
extends it with payments APIs. StripeIdentity extends it with identity
APIs. StripePaymentSheet extends it with PaymentSheet APIs. Each
add-on product modularly adds API surface to the same shared client
without needing inheritance, decoration, or per-product client classes.

The C# bindings need the same layering or the modular design collapses:
either every product has to expose its own client (breaking the shared-
configuration property of `STPAPIClient`), or every product's APIs have
to be emitted on `StripeCore`'s side (breaking `StripeCore`'s
modularity). Extension methods on the C# side preserve both properties.

## Impact

- **~50% of StripePayments' public client API unavailable.** Every API
  declared via extension on `STPAPIClient` is missing — token creation,
  source creation, intent retrieval/confirmation, payment-method
  CRUD, microdeposit verification.
- **Library scope.** Affects every multi-module Swift SDK that uses
  cross-module extensions. Stripe is the worst offender today (15
  products, all extending `STPAPIClient`). Apple SDKs heavily use this
  pattern too — e.g. UIKit extensions on Foundation types, SwiftUI
  extensions on UIKit types.
- **Silent.** Nothing in `binding-report.json` flags these as skipped,
  so the bug doesn't surface during a normal binding-validation pass —
  only when a consumer goes looking for the API.

## Workaround

Consumer side: there is no workaround. The methods don't exist in C#.
Consumers must implement equivalent functionality by manually
constructing requests and calling lower-level Stripe API shapes — most
of which are also missing.

The proper fix is in
[swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings):
emit cross-module Swift extensions as C# extension-method classes.

## Severity

**Correctness — High.** This is the largest single API-surface gap in
the Stripe binding line. PaymentsClient is the primary surface most
non-PaymentSheet integrations use; without it, PaymentsClient
integrations (custom card flows, server-confirmation patterns,
charge-only flows) are blocked from .NET entirely.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 3 / I-6.
