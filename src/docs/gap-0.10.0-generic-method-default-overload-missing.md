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

`DefaultParameterOverloadEmitter` guards trim-overload emission on the
parameter list shape but bails when the method is generic — `methodDecl.IsGeneric`
short-circuits at the top of `TryEmitOverloads`. The bail exists because the
trim overload would need to forward generic type arguments through the
`@_silgen_name` shim and into the C# trim-overload P/Invoke signature.

## Why this is bigger than just lifting the bail

Initial attempt (D2 work, 2026-05-07): lift the bail and thread method-own
generic params + a `where` clause through `EmitSwiftWrapper` using
`AsyncHarnessEmitter.BuildMethodOwnGenericParams` + `WrapperEmitterHelpers
.BuildSwiftWhereClause`. The Swift-side change is small — both helpers already
exist and the @_silgen_name shim shape `public static func _dbw_…<T0>(…)
async throws -> Result where T0: ConstraintProto` compiles cleanly.

However, the C# side does not currently emit the **primary** explicit overload
for the StoreKit-shape input (`AsyncSceneMarker` is a custom class-bound
protocol with no entry in `specialization-hints.json`, and
`MethodGenericBridgeEmitter` rejects async + throws). So the trim overload has
nothing to attach to: we'd be emitting a new `DBW_…` symbol that no C# call
site ever resolves. Verified end-to-end during D2 by adding an exact-shape
fixture (`AsyncPurchaseReceipt.confirm<S: AsyncSceneMarker>(…, options: Set<…> =
[])`) and observing the generated `SwiftBindingsTestLib.cs`:

```text
// Unsupported: method 'confirm' — parameter or return type not yet supported
//   (wrapper not emitted; direct call would be ABI-unsafe)
```

A full fix needs **two layers** beyond the bail lift:

1. **Trim-overload @_silgen_name generic threading** — thread
   `methodOwnGenericParams` (`<T0>`) and `methodOwnWhereClause` (` where T0:
   Constraint`) into the three func-decl emit sites in
   `EmitSwiftWrapper` (free function, constructor, type method). Mechanical.

2. **C# trim-overload P/Invoke binding to the new DBW_ symbol** — extend
   either (a) `MethodGenericBridgeEmitter` to handle async/throws so the
   primary explicit overload's existential-opening dispatch covers async +
   class-bound-protocol generics, or (b) the async-generic emission path so
   it emits per-conformer specialized trim overloads alongside the primary
   specialized overloads (mirroring the `Sequence`-element CSM machinery
   used for `AnimalAsyncRoster.insertAsync`). Both paths require the trim
   overload's P/Invoke to bind the new `DBW_…` symbol with matching
   metadata + witness threading; today the trim emitter generates a P/Invoke
   that targets a symbol the wrapper dylib never exports for these shapes.

Layer 1 alone is dead code without layer 2 — the @_silgen_name shim emits but
nothing calls it, so the dylib carries an unused symbol and no consumer
benefit lands. Layer 2 is non-trivial: extending `MethodGenericBridgeEmitter`
to async/throws was previously deferred ("@_cdecl can't throw; skip for v1"
at the top of that emitter), and the per-conformer specialized trim path
needs the trim overload's signature collision logic to dedup against the
specialized primary overloads.

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
- D2 investigation summary (2026-05-07): scoped the fix at two layers, ruled
  out a single-layer Swift-only patch as dead code, and kept the gap open
  pending broader async-generic dispatch work. The session confirmed that
  the trim-overload bail at `DefaultParameterOverloadEmitter.cs:59-60` is
  the right place to lift once the C#-side dispatch lands.
