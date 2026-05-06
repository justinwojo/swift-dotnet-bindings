# Gap: Swift `AsyncSequence` types are not lowered to `IAsyncEnumerable<T>` — `await foreach` is blocked

> SDK 0.10.0 generator feature gap. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Apple.StoreKit2](https://github.com/justinwojo/swift-dotnet-packages)
> (Apple StoreKit framework, 26.2.2).

## Summary

Swift `AsyncSequence` types are bound only as a struct/class with a
`MakeAsyncIterator()` method that returns a struct/class with
`NextAsync(CancellationToken)`. Nothing implements
`System.Collections.Generic.IAsyncEnumerable<T>` /
`IAsyncEnumerator<T>`. The idiomatic .NET consumption pattern —

```csharp
await foreach (var verification in StoreKit2.Transaction.Updates) { ... }
```

— does not compile. Consumers must hand-roll the equivalent:

```csharp
using var iter = StoreKit2.Transaction.Updates.MakeAsyncIterator();
while (await iter.NextAsync(ct) is { } verification) { ... }
```

The shape (`MakeAsyncIterator` + `NextAsync` returning `Task<T?>` with a
trailing-null sentinel) is one short adapter away from the standard
`IAsyncEnumerable<T>` contract. The SDK could either implement the
interface directly or emit an extension method.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source framework: system StoreKit (iOS 26.2.2)

## Repro

```bash
grep -n 'IAsyncEnumerable' apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/StoreKit2.cs
# (no matches)
sed -n '10848,10970p' apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/StoreKit2.cs
sed -n '11189,11260p' apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/StoreKit2.cs
```

```csharp
// StoreKit2.cs:10848
public partial class Transactions : ISwiftObject, ISwiftStruct, IDisposable
{
    // Note: NOT : IAsyncEnumerable<VerificationResult<Transaction>>
    public StoreKit2.Transaction.Transactions.AsyncIterator MakeAsyncIterator() { ... }
}

// StoreKit2.cs:10965
public partial class AsyncIterator : ISwiftObject, ISwiftStruct, IDisposable
{
    // Note: NOT : IAsyncEnumerator<VerificationResult<Transaction>>
    public Task<StoreKit2.VerificationResult<StoreKit2.Transaction>?> NextAsync(
        CancellationToken cancellationToken = default) { ... }
}
```

Same shape on:

- `Transaction.Transactions` (backs `Transaction.Updates`, `.All`,
  `.CurrentEntitlements`, `.Unfinished`)
- `PurchaseIntent.PurchaseIntents` (StoreKit2.cs:535)
- `Message.MessagesType` (StoreKit2.cs:1554)
- `Storefront.Storefronts` (StoreKit2.cs:12661)
- `Product.SubscriptionInfo.Status.Statuses` (StoreKit2.cs:19335)

## Native ground truth

```text
swiftinterface (StoreKit framework):
  public static var updates: StoreKit.Transaction.Transactions { get }
  public struct Transactions : AsyncSequence, Sendable {
    public typealias Element = VerificationResult<Transaction>
    public func makeAsyncIterator() -> AsyncIterator
  }
```

Swift's `AsyncSequence` protocol declares `func makeAsyncIterator() ->
Self.AsyncIterator` and the iterator declares `mutating func next() async
throws -> Element?`. The C# side already has the equivalent shape; only
the interface adoption is missing.

## Hypothesis

The codegen for Swift `AsyncSequence` emits the iterator shape but
doesn't add the `IAsyncEnumerable<T>` interface declaration. Likely the
emitter doesn't currently know about `IAsyncEnumerable` as a target.

Two viable shapes for the fix:

1. **Direct interface implementation.** Make the `Transactions` class
   implement `IAsyncEnumerable<VerificationResult<Transaction>>` and have
   `GetAsyncEnumerator(CancellationToken)` return a class that adapts the
   existing `AsyncIterator`'s `NextAsync(ct)` to the
   `IAsyncEnumerator<T>.MoveNextAsync()` + `.Current` + `.DisposeAsync()`
   contract. Standard .NET consumer experience.
2. **Extension method.** Emit a static `public static IAsyncEnumerable<T>
   AsAsyncEnumerable<T>(this Foo seq)` extension. Lighter-weight; consumer
   must call `.AsAsyncEnumerable()` first. Less idiomatic.

Option 1 is the obvious target.

## Impact

- **Idiomatic .NET StoreKit consumption is blocked.** `await foreach
  (var t in Transaction.Updates) { ... }` is the documented pattern for
  every consumer. Today they must learn the manual iterator dance.
- **Affects every binding that surfaces an `AsyncSequence`.** StoreKit2
  is the heaviest user, but Stripe FinancialConnections' progress
  observer, Mappedin's location updates, and any future Apple framework
  binding (Combine, Observation) will hit the same.
- **Foundation gap, not a correctness defect.** Existing iterator works.

## Workaround

Consumer side: write a small extension method:

```csharp
public static async IAsyncEnumerable<T> AsAsyncEnumerable<T>(
    this StoreKit2.Transaction.Transactions seq,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
{
    using var iter = seq.MakeAsyncIterator();
    while (await iter.NextAsync(ct) is { } v)
        yield return v;
}
```

…then use `await foreach (var v in Transaction.Updates.AsAsyncEnumerable
(ct)) { }`. Functional, but every consumer will redo this work.

## Severity

**Feature gap — Medium.** Doesn't crash; doesn't leak; just blocks the
canonical consumption pattern for the principal StoreKit2 surface.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / M-5.

Distinct from
[`gap-0.10.0-swiftarray-at-api-boundary.md`](./gap-0.10.0-swiftarray-at-api-boundary.md)
— that one is about `IAsyncEnumerable<Swift.SwiftArray<T>>` element-type
ergonomics; this one is about `IAsyncEnumerable<T>` interface adoption
entirely.

## Round 5 — MusicKit `MusicSubscription.Updates` (2026-05-05)

The cross-package audit of `SwiftBindings.Apple.MusicKit` confirms
this gap recurs on a flagship surface.

| Site | Type | Severity |
|------|------|----------|
| MusicKit.cs:44081, :44422-44505 | `MusicSubscription.Updates : AsyncSequence` exposed as `ISwiftStruct` with manual `MakeAsyncIterator()` / `Iterator.NextAsync()`; no `IAsyncEnumerable<MusicSubscription>` | Medium |

Swift declares `Updates : AsyncSequence`, `Iterator :
AsyncIteratorProtocol`, `next() async`, and `makeAsyncIterator()` at
swiftinterface:4216, :4217, :4219, :4227. C# emits a manual iterator —
consumers must hand-roll the iteration:

```csharp
var updates = MusicSubscription.SubscriptionUpdates;
using var iter = updates.MakeAsyncIterator();
while (true)
{
    var subscription = await iter.NextAsync();
    if (subscription is null) break;
    // …
}
```

…rather than the idiomatic:

```csharp
await foreach (var subscription in MusicSubscription.SubscriptionUpdates)
{ … }
```

Same emitter shape as Round 4's StoreKit2 `Transaction.updates`. No
new emitter variant found.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 5 / M-5.
