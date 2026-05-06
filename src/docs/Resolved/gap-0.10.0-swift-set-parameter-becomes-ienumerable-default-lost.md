# Gap: Swift `Set<T>` parameter lowers to `IEnumerable<T>` and the default value is dropped

> SDK 0.10.0 generator type-fidelity gap. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Apple.StoreKit2](https://github.com/justinwojo/swift-dotnet-packages)
> (Apple StoreKit framework, 26.2.2).
>
> **Status: RESOLVED in Bundle 04 #9.**
>
> - **Type fidelity:** `SetProjection.PublicType` now returns
>   `IReadOnlySet<T>` for both parameter and return positions (was
>   `IEnumerable<T>` on the parameter side, dropping the uniqueness
>   invariant at the public API surface). Callers must materialise an
>   actual set on the C# side (`HashSet<T>` is the natural shape).
> - **Default surface:** the empty-literal `= []` default already
>   surfaces as a no-arg trim overload via
>   `DefaultParameterOverloadEmitter` for sync, async, and
>   collection-defaulted parameters. The bug doc's "default lost"
>   claim was overstated — the generic-method skip
>   (`PurchaseAsync<T0>` at StoreKit2.cs:24282) is the only family
>   where the trim overload is currently missing, and it's tracked
>   separately under generic-method default-overload emission.
> - **Validation:** `BindingTests` Layer-B coverage in
>   `SetParameterDefaultTests` (sync explicit, sync trim, dedupe
>   semantics, async explicit, async trim) — sync paths pass on
>   Mono JIT and NativeAOT. Async paths pass on Mono JIT and are
>   skipped on NativeAOT pending Bundle 10 #50 (Defect B in
>   `bug-0.10.0-ienumerable-iswiftstruct-raw-intptr-…` — async
>   `using var` lifetime mismatch; same fix shape applies to every
>   `SwiftSet<T>` / `SwiftArray<T>` / `SwiftDictionary<K,V>` async
>   parameter). Existing Set-parameter callers in
>   `URLContainerBridgeTests`, `ClosureOverloadCollisionTests` updated
>   to construct `HashSet<T>` to match the new public surface.

## Summary

Swift parameters typed `Set<T>` with a default value of `[]` lower to C#
`IEnumerable<T>` with no default. Consumers wanting the equivalent of
`await product.purchase()` must construct an empty enumerable explicitly.

The Set semantic — uniqueness — is also lost at the API surface. The
Swift wrapper rebuilds a `Set` from the iterated handles, so duplicates
collapse at runtime, but the API contract does not communicate this.

The natural .NET counterpart for `Set<T>` is `IReadOnlySet<T>`
(.NET 5+) or `HashSet<T>`; either preserves uniqueness in the type
system.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source framework: system StoreKit (iOS 26.2.2)

## Repro

```bash
sed -n '23942,24470p' apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/StoreKit2.cs | grep -n PurchaseAsync
```

All four `Product.purchase` overloads:

```csharp
// StoreKit2.cs:23942
public Task<Product.PurchaseResult> PurchaseAsync(
    IEnumerable<Product.PurchaseOption> options,        // [BAD] no default
    CancellationToken cancellationToken = default)

// StoreKit2.cs:24110
public Task<Product.PurchaseResult> PurchaseAsync(
    IEnumerable<Product.PurchaseOption> options, ...)

// StoreKit2.cs:24282 (UIScene generic)
public Task<Product.PurchaseResult> PurchaseAsync<T0>(
    T0 viewController, IEnumerable<Product.PurchaseOption> options, ...)

// StoreKit2.cs:24465 (UIViewController)
public Task<Product.PurchaseResult> PurchaseAsync(
    UIKit.UIViewController viewController,
    IEnumerable<Product.PurchaseOption> options, ...)

// StoreKit2.cs:27331 (AdvancedCommerce variant)
public Task<Product.PurchaseResult> PurchaseAsync(
    string compactJWS, UIKit.UIViewController viewController,
    IEnumerable<AdvancedCommerceProduct.PurchaseOption> options, ...)
```

## Native ground truth

```text
swiftinterface (StoreKit framework, lines 1649-1957):
  @MainActor public func purchase(
    options: Set<Product.PurchaseOption> = []
  ) async throws -> Product.PurchaseResult

  @MainActor public func purchase(
    confirmIn scene: some UIScene,
    options: Set<Product.PurchaseOption> = []
  ) ...

  public func purchase(
    confirmIn viewController: UIKit.UIViewController,
    options: Set<Product.PurchaseOption> = []
  ) ...

  public func purchase(
    compactJWS: Swift.String,
    confirmIn viewController: UIKit.UIViewController,
    options: Set<AdvancedCommerceProduct.PurchaseOption> = []
  ) ...
```

## Hypothesis

The emitter normalizes Swift `Collection` / `Sequence` / `Set` /
`Array` / `Dictionary` parameters to a common `IEnumerable<T>` lowering
on the C# side, losing the precise collection semantic. For `Array<T>`
this is roughly fine; for `Set<T>` it is a contract loss.

The default value `= []` is also dropped. Swift lets defaulted
parameters be omitted at call sites; C# preserves that with `= default`
on the parameter declaration. The emitter is presumably skipping the
defaulted-parameter emission for collection types.

Likely fix: emit `Set<T>` parameters as `IReadOnlySet<T>` (or
`HashSet<T>`) and emit a `default` of `null` (with documented null-as-
empty semantics) or a static empty-set sentinel.

## Impact

- **Ergonomics.** `await product.PurchaseAsync(Array.Empty<PurchaseOption>())`
  is uglier than the Swift `await product.purchase()` — verbose for the
  most common no-options case.
- **Type-system contract loss.** A consumer who passes a list with
  duplicates gets no compile-time complaint and silently has duplicates
  collapsed by the wrapper.
- **Affects every Apple framework binding.** `Set<T>` is heavily used in
  modern Swift APIs (entitlements, capabilities, options) — StoreKit2 is
  one of many candidates.

## Workaround

Consumer side: pass `Array.Empty<PurchaseOption>()` for the empty case
or pre-deduplicate via `.Distinct().ToList()`.

## Severity

**Type-fidelity — Low.** No correctness defect; ergonomic + contract
loss.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / M-9.
