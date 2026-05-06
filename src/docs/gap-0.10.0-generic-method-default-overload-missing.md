# Gap: Generic methods skip the empty-literal default trim-overload emission

> SDK 0.10.0 generator default-overload gap. Spotted 2026-05-06 during the
> Bundle 04 #9 (`Set<T>` parameter projection) closure.
>
> **Status: open — roadmap candidate.** Not in scope for any in-flight bundle.

## Summary

The `DefaultParameterOverloadEmitter` lifts a Swift `= []` (or other empty-literal)
default into a no-arg trim overload that calls the Swift-defaulted function — for
sync, async, and collection-defaulted parameters on non-generic methods. Generic
methods are skipped: the trim overload is not emitted, so callers must construct
an empty container explicitly even though Swift permits omission.

## Repro

The canonical case is the StoreKit2 `Product.purchase<some UIScene>` overload —
the `confirmIn: some UIScene` family. Post Bundle 04 #9 the four non-generic
`Product.purchase` overloads each emit a trim overload that lets the caller
write `await product.PurchaseAsync()`. The generic overload at
`apple-frameworks/StoreKit2/obj/Debug/.../StoreKit2.cs:24282`:

```csharp
public Task<Product.PurchaseResult> PurchaseAsync<T0>(
    T0 viewController,
    IReadOnlySet<Product.PurchaseOption> options,    // [BAD] no default, no trim overload
    CancellationToken cancellationToken = default)
    where T0 : ...
```

The matching Swift signature does have a defaulted `options`:

```swift
@MainActor public func purchase(
    confirmIn scene: some UIScene,
    options: Set<Product.PurchaseOption> = []
) async throws -> Product.PurchaseResult
```

## Hypothesis

`DefaultParameterOverloadEmitter` likely guards trim-overload emission on the
parameter list shape but bails when the method is generic — possibly because
the trim overload would need to forward generic type arguments and the emitter's
forwarding code path was scoped to non-generic methods. Likely fix: extend the
forwarding template to thread `T0, T1, …` through the trim overload's call to
the explicit overload (or directly to the `@_cdecl` symbol), preserving any
`where` clauses on the trim overload itself.

## Severity

**Type-fidelity — Low.** Ergonomic loss only; correctness is intact (the explicit
overload still works). Generic-method-with-collection-default is uncommon enough
that no current consumer is blocked, but the StoreKit `purchase(confirmIn: some UIScene)`
case is hit on every iOS app that wants scene-aware purchase confirmation.

## Workaround

Pass an explicit empty collection (e.g. `new HashSet<Product.PurchaseOption>()`)
at the call site.

## Reference

- `gap-0.10.0-swift-set-parameter-becomes-ienumerable-default-lost.md` — the
  parent doc; the Set-projection fix in Bundle 04 #9 made the missing trim
  overload visible on the generic family.
