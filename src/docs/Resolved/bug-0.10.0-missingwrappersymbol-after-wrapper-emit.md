# Bug: `MissingWrapperSymbol` reported even after the wrapper appears to be emitted

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Stripe](https://github.com/justinwojo/swift-dotnet-packages)
> (StripePaymentSheet 26.2.1).

## Summary

`binding-report.json` reports several public Swift APIs as
`MissingWrapperSymbol`, meaning the C# binding generator could not find
the corresponding `@_cdecl` wrapper symbol that the Swift wrapper-emitter
was supposed to produce. The Swift wrapper file appears to declare the
wrapper functions, but they don't reach the final dylib's symbol table
(or aren't resolvable at the names the C# generator looks up).

In Stripe PaymentSheet specifically, all three `PaymentSheet.FlowController.Create`
overloads + the `Update` method are reported missing, and they don't
appear in the generated C# `StripePaymentSheet.cs` despite Swift
declaring them at swiftinterface:47. FlowController is one of two entry
points to PaymentSheet; this is a shipping-blocker for any consumer
using the FlowController-based flow.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source library: stripe-ios 26.2.1 (StripePaymentSheet)

## Repro

```bash
jq '.. | select(.reason? == "MissingWrapperSymbol")' libraries/Stripe/StripePaymentSheet/obj/Debug/net10.0-ios/swift-binding/binding-report.json
sed -n '1280,1310p' libraries/Stripe/StripePaymentSheet/obj/Debug/net10.0-ios/swift-binding/binding-report.json
```

Report excerpt:

```json
{
  "name": "PaymentSheet.FlowController.create(intentConfiguration:configuration:completion:)",
  "reason": "MissingWrapperSymbol",
  "wrapper-symbol": "$s17StripePaymentSheet0aB0V14FlowControllerC6create…"
}
```

(plus 2 more `create` overloads and `update`)

Native:

```text
swiftinterface (StripePaymentSheet line 47-72):
  public class FlowController {
    public static func create(paymentIntentClientSecret: String,
                              configuration: PaymentSheet.Configuration,
                              completion: @escaping (Result<FlowController, Error>) -> Void)
    public static func create(setupIntentClientSecret: String,
                              configuration: PaymentSheet.Configuration,
                              completion: @escaping (Result<FlowController, Error>) -> Void)
    public static func create(intentConfiguration: PaymentSheet.IntentConfiguration,
                              configuration: PaymentSheet.Configuration,
                              completion: @escaping (Result<FlowController, Error>) -> Void)
    public func update(intentConfiguration: PaymentSheet.IntentConfiguration,
                       completion: @escaping (Error?) -> Void)
  }
```

The corresponding section of generated `StripePaymentSheet.cs` has no
`Create` static methods or `Update` instance method on
`PaymentSheet.FlowController`.

## Hypothesis

`MissingWrapperSymbol` is reported when the C#-generation step looks up
a wrapper symbol by name and gets nothing back from the symbol table.
Two ways this can happen:

1. **The Swift wrapper-emitter never emitted the wrapper for this API**
   — perhaps because the closure parameter shape (`(Result<…, Error>) ->
   Void`, where the inner `Error` is an existential) tripped the
   wrapper-emitter's "skip and report" path. But that case should
   surface as `UnsupportedClosure` rather than `MissingWrapperSymbol`.
2. **The Swift wrapper was emitted but the symbol it produced doesn't
   match the symbol the C# generator looked up.** The mangled symbol
   `$s17StripePaymentSheet…` in the report is the Swift-side mangled
   name; if the wrapper file uses a different mangling (or the C#
   generator's lookup uses a different mangling) they'd miss each other.

The fact that all three `Create` overloads + `Update` all miss together
(rather than one or two) suggests this isn't a per-API closure-shape
issue but a class-level emission issue. Possibly:

- `FlowController` is a nested type inside `PaymentSheet`; the wrapper
  emitter may not be handling nested-class static-method symbols
  correctly.
- `Result<T, E>` (where the success type is `Self`-equivalent
  `FlowController`) requires special handling in the wrapper output.

The fix is to bisect: check the Swift wrapper file (in the SDK's wrapper
output) for whether the `create`/`update` wrappers are emitted at all.
If not — fix the wrapper emitter for nested types with `Result`-returning
closures. If they are emitted — fix the C# generator's symbol lookup to
use the same mangling.

## Native ground truth — what FlowController is for

`FlowController` is the lower-level PaymentSheet entry point. The
two PaymentSheet APIs are:

- `PaymentSheet(paymentIntentClientSecret: configuration:)` — full-sheet
  presentation; returns `(PaymentSheetResult)` from `present(from:)`.
- `PaymentSheet.FlowController.create(...)` — create-then-present pattern
  for hosting the payment-method picker inline on a checkout screen and
  triggering the full-sheet only at confirm time.

Most "Stripe inside an existing checkout flow" integrations use
FlowController. Stripe's official docs prefer it for any non-trivial
customization.

## Impact

- **FlowController flow unusable from C#.** Consumers cannot create or
  update a FlowController; they can only use the simpler `PaymentSheet`
  full-sheet flow.
- **Library scope.** Per-binding-report search across the rest of Stripe
  for `MissingWrapperSymbol` should surface other affected APIs. May
  also affect non-Stripe libraries that use nested-type static methods
  with Result-returning closures.

## Workaround

Consumer side: use the higher-level `PaymentSheet(...)` constructor
flows instead of FlowController. Loses the inline-picker UX.

## Severity

**Correctness — High.** A primary public-API entry point of a flagship
Stripe product silently dropped from the C# binding. Same severity as
any other "API present in Swift, missing in C#" defect.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 3 / I-5.

## Status — RESOLVED in 0.10.0 (closure-context owner-token, Session B)

The MCB pipeline now emits stable `@_cdecl` wrappers for the
nested-class + `Result<Self, any Error>` / `((any Error)?) -> Void`
closure shape. The fix landed as part of the closure-context
owner-token mechanism: the C# `GCHandle` carrying the captured
delegate is wrapped in a Swift-ARC-owned `_SBClosureCtx` box exported
from `libSwiftBindingsRuntime.dylib`. When Swift releases the closure,
the box's `deinit` upcalls a registered C# free callback
(`SwiftClosureContext.EnsureRegistered`), freeing the handle exactly
once. With escaping-closure ownership stable, the wrapper-emit
pipeline no longer drops these methods on the closure-arg cdecl-compat
predicate, and the symbols cross-reference cleanly between Swift
emission and C# `[LibraryImport]`.

**Cross-reference:** the underlying ownership-mechanism work is
documented in
`Resolved/bug-0.10.0-callback-trampoline-gchandle-leak.md`. The
StripePaymentSheet validation baseline now reports
`compile=ok`/`errors=0`/`swift_compile=ok` (no `MissingWrapperSymbol`
entries against `FlowController.create`/`update`).

**Regression coverage:** the BindingTests fixture
`ErrorHandling/NestedResultClosureFixture.swift` mirrors the Stripe
shape — nested public class `OnboardingFlow.SessionController` with
a static factory taking `(Result<Self, any Error>) -> Void`, a sibling
factory taking `((any Error)?) -> Void`, and an instance method
taking `((any Error)?) -> Void`. Runtime tests in
`RuntimeTestsApp/ErrorHandling/NestedResultClosureTests.cs` exercise
both branches of each closure shape end-to-end through the @_cdecl
wrapper.
