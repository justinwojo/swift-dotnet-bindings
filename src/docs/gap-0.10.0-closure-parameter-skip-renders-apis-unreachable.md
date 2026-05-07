# Gap: `UnsupportedClosure` skips drop entire constructors and registrar APIs

> SDK 0.10.0 generator feature gap. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Nuke](https://github.com/justinwojo/swift-dotnet-packages)
> 13.0.5 generated bindings.

## Summary

When a Swift method has a closure parameter whose shape the generator
cannot marshal, the entire method is dropped and recorded in
`binding-report.json` as `UnsupportedClosure`. For ordinary methods this
is a small ergonomic loss — the consumer can usually reach the
functionality some other way. But the same skip applied to a
**constructor** (`init`) or to a **registrar method** (the *only* way to
add behavior to a configuration object) makes the surrounding type
effectively unconfigurable from C#.

Concrete consumer impact in Nuke 13.0.5:

- **`DataLoader` cannot be constructed from C#.** The Swift type's only
  public initializer takes a `validate: @escaping @Sendable (URLResponse)
  -> (any Error)?` closure. The closure is `UnsupportedClosure`, the
  whole `init` is skipped, and the resulting C# class has no public
  constructor. Consumers can read static defaults but cannot wire a
  custom `URLSessionConfiguration` or alternative validation policy.
- **Custom image decoders cannot be registered.**
  `ImageDecoderRegistry.register(_:)` takes a closure and is skipped.
  The registry is the only sanctioned extension point for
  consumer-defined image formats; without it, custom format support is
  unreachable from C#.
- **`ImagePipeline.Configuration.makeImageDecoder` is skipped** for the
  same reason. The configuration's per-instance decoder factory is
  unreachable.
- **`ImagePipeline.loadData` (one overload) is skipped** — `completion`
  closure unsupported. (Other `loadData` overloads survive.)
- **`ImageProcessors.Anonymous.init`** is skipped — its purpose is
  literally "wrap a consumer-provided closure as an `ImageProcessing`
  instance." Skipping the closure removes the entire feature.

Every one of these is a closure on a method that *exists to take a
closure*. Skipping it means the surrounding API no longer has a story.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source library: Nuke 13.0.5

## Repro — affected sites in Nuke 13.0.5

```bash
jq -r '.. | objects
  | select(.Reason? == "UnsupportedClosure" or
           (.Reason? == "UnsupportedSignature" and (.Details? // "" | test("closure"; "i"))))
  | "\(.ContainingType)::\(.Name) [\(.Reason)] — \(.Details)"' \
  libraries/Nuke/obj/Debug/net10.0-ios/swift-binding/binding-report.json
```

```text
Nuke.DataLoader::init                          [UnsupportedClosure]      validate
Nuke.ImageDecoderRegistry::register            [UnsupportedClosure]      arg0
Nuke.ImagePipeline.Configuration::makeImageDecoder [UnsupportedClosure]  -
Nuke.ImagePipeline::loadData                   [UnsupportedClosure]      completion
Nuke.ImageProcessors.Anonymous::init           [UnsupportedClosure]      arg1
Nuke.ImageRequest::init                        [UnsupportedSignature]    Async-throwing closure parameter cannot be bridged
```

The `UnsupportedSignature` case (`Nuke.ImageRequest::init` with an
async-throwing closure) is the same family — different reason string,
same outcome (constructor unreachable).

## Native ground truth

```text
swiftinterface (Nuke line 549):
  public init(configuration: Foundation.URLSessionConfiguration = …,
              validate: @escaping @Sendable (Foundation.URLResponse)
                  -> (any Swift.Error)? = DataLoader.validate)
```

Plain `@escaping` closure, plain return of a single optional existential.
Not exotic — this is the canonical "validation policy" parameter shape
across thousands of Swift APIs.

## Hypothesis

The generator's closure-marshalling subsystem has a "supported shape"
allowlist (sync + small fixed arg count + non-existential return + …).
Closures outside that shape — `@escaping` + `@Sendable` + existential
return + default-value-pointing-at-static-method, in `DataLoader`'s case
— bail out and the whole containing method gets skipped.

What the closure-marshalling subsystem needs to grow:

1. **`@Sendable` should be a no-op for marshalling.** It's a Swift
   concurrency annotation; the C# closure's thread-safety is the
   consumer's problem. Currently this is presumably contributing to the
   skip.
2. **Existential returns from closures** (`(URLResponse) -> (any Error)?`)
   need the same indirect-result handling that the rest of the SDK
   already does for direct method returns. This intersects with
   [bug-0.10.0-dataloader-validate-uninitialized-buffer.md](./bug-0.10.0-dataloader-validate-uninitialized-buffer.md):
   the `validate` closure return type is *the same shape* as the
   `DataLoader.Validate` static method's return type, so fixing the
   indirect-result invocation for the static method should unblock the
   closure case too.
3. **Default values pointing at static methods** (`= DataLoader.validate`)
   should fall back to "no default in C#, consumer must pass" rather
   than skipping the whole method. The default expression doesn't change
   the closure's marshallability — only its absence/presence in the
   C# signature.
4. **Async-throwing closure parameters** need a separate code path
   (similar to the existing async-throwing return wrapper but applied to
   a closure-arg). At minimum, the skip should narrow to "method dropped
   only if the closure is async-throwing AND the surrounding method is
   not itself a `@_cdecl` async-throws wrapper" — the current message
   suggests this nuance exists but not as logic that prevents the skip
   in cases like `Nuke.ImageRequest::init` where the surrounding method
   has nothing to do with concurrency.

The first three changes alone would unblock ~all five concrete sites
above. (4) is needed for `ImageRequest::init`.

## Why this matters more than the priority-table line conveys

Tracking this only as P4 ("Closure / `@escaping` parameter marshalling")
understates what consumers see. The visible failure mode isn't "this
method is missing" — it's "this *type* is unconfigurable" or "this
extension point is sealed off." Examples:

| Skipped | Net consumer impact |
|---|---|
| `DataLoader::init` | `DataLoader` has no public constructor. Cannot use a custom `URLSessionConfiguration`, alternate session, custom validation. |
| `ImageDecoderRegistry::register` | Cannot add support for custom image formats. The registry is the only entry point. |
| `ImagePipeline.Configuration::makeImageDecoder` | Cannot supply a per-pipeline decoder factory. |
| `ImageProcessors.Anonymous::init` | Cannot define ad-hoc image processors from C#. The whole `Anonymous` type loses its reason to exist. |

Other libraries almost certainly have similar shapes. SwiftUI bridges,
Combine publishers, vendor SDKs that take "configure this thing with a
closure" patterns are common; they'll all hit the same wall.

## Impact

- **Consumer-experience.** Configurability of bound types degrades
  silently. The consumer doesn't see "skipped — closure marshal missing"
  in their IDE; they see "no constructor matches" or "no such method."
- **Library scope.** Cross-library prevalence: every binding that
  exposes a `init(…closure…)`, `register(…)`, or `Configuration.make*`
  method with a closure param is affected. A cross-library scan of
  `binding-report.json` `UnsupportedClosure` entries gives the full
  picture.

## Round 4 — Lottie + StoreKit2 audit (2026-05-05)

The Lottie + StoreKit2 audit confirms the same family on a third-party
library and a first-party Apple framework — both with consumer-visible
"this type is unconfigurable" outcomes.

**Lottie:**

- `LottieLogger::init` — the only public initializer takes a logger
  closure. Skipped as `UnsupportedClosure`. Consumer impact:
  `LottieLogger` has no public constructor. The `LottieLogger.shared`
  default singleton is reachable, but consumers cannot wire a custom
  logger. Confirmed in `binding-report.json`:
  `Lottie.LottieLogger::init [UnsupportedClosure]`.

**StoreKit2 — closure/async-property skip-driven feature gaps:**

These are not all `UnsupportedClosure` literally; the wider family is
"closure-shaped or AsyncSequence-shaped property gets skipped, sealing
off the surrounding feature." Confirmed sites:

| Skipped | Consumer impact |
|---|---|
| `Product.latestTransaction` (async var) | Cannot read latest transaction for a product without iterating `Transaction.all`. |
| `Product.currentEntitlement` (async var) | Cannot check active entitlement; consumers must scan all transactions. |
| `Product.currentEntitlements` (AsyncSequence) | Cannot enumerate active entitlements. |
| `Product.priceFormatStyle` | Consumers must construct `NumberFormatter` manually for price display — no localized format. |
| `Status.all` (AsyncSequence stream) | Cannot subscribe to subscription status changes; the canonical "renewal lifecycle" listener is unreachable. |
| `onStorefrontChange(_:)` | Storefront-change observer skipped (also intersects with the GCHandle leak family in [Resolved/bug-0.10.0-callback-trampoline-gchandle-leak.md](./Resolved/bug-0.10.0-callback-trampoline-gchandle-leak.md)). |

The `Status.all` and `currentEntitlements` cases share a root with
[gap-0.10.0-asyncsequence-not-lowered-to-iasyncenumerable.md](./gap-0.10.0-asyncsequence-not-lowered-to-iasyncenumerable.md) —
AsyncSequence not yet bridged to `IAsyncEnumerable<T>`. The other
async-property cases are simpler: the generator skips properties that
return Swift's `async var` shape, treating them like the closure-taking
methods catalogued above.

Consumer-side, the StoreKit2 set is particularly visible because every
StoreKit consumer hits at least 2-3 of these in a typical purchase flow.
"Listen for renewal events" without `Status.all` requires the consumer
to poll `Transaction.all` periodically — a regression in both ergonomics
and correctness vs. the Swift API.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / M-12.

## Workaround

Consumer side: depending on the case, sometimes a static factory or a
sibling overload provides similar functionality. Often there's no path
— the closure-taking method is the only API.

Vendor-side workaround (per the report's own suggestion): "Write a
Swift wrapper with a simplified signature." Realistic when the binding
author *is* the upstream, but not for vendor-consumed `xcframework`
bindings.

Proper fix in
[swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings):
expand the closure-marshalling subsystem to handle the four shapes
above. Tracked as P4 in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md).

## Severity

**Feature gap — High.** Doesn't break anything that already exists,
but seals off configuration paths on flagship types in flagship
libraries. The combination of (a) high consumer visibility and (b) no
workaround makes this the largest single "this SDK can't do that yet"
gap on the list, after the existential/proxy work in
[gap-0.10.0-everyprotocol-and-existentials.md](./gap-0.10.0-everyprotocol-and-existentials.md).

## Round 5 — WeatherKit + MusicKit sites (2026-05-05)

The cross-package audit of `SwiftBindings.Apple.WeatherKit` and
`SwiftBindings.Apple.MusicKit` confirms this gap recurs at *massive*
scale on Apple frameworks. The same emitter shape, plus a new
skip-reason variant: **`closure_or_async_in_generic_type_member`**.
The pattern is "Swift generic type T<...> exposes async or closure
methods on its instantiations; those instantiations are dropped during
MultiSpecialization." Cascades through whole request-type families.

**MusicKit — request types (Family C cascade):**

- MusicKit.cs:3168, :3255, :3281-3290 — `MusicLibraryRequest<T>`
  filter, sort, limit, offset, includeOnlyDownloadedContent, response
  — **13 skips**. The library's primary catalog/library query API is
  *unconfigurable and unexecutable*.
- MusicKit.cs (`MusicLibrarySectionedRequest`) — **21 skips**, the
  most-skipped type in the module.
- MusicKit.cs (`MusicCatalogResourceRequest<T>`) — **8 skips**
  including `response()`, `limit`, `properties`, filter
  initializers. (See also **M-3**
  [`bug-0.10.0-some-protocol-generic-constraint-over-broad.md`](bug-0.10.0-some-protocol-generic-constraint-over-broad.md)
  for the unsound-generic-constraint angle.)
- MusicKit.cs (`MusicCatalogSearchRequest`) — **4 skips** including
  `init(term:types:)` and `types` (existential
  `[any MusicCatalogSearchable.Type]`). No public constructor for
  catalog search — the central MusicKit scenario.
- MusicKit.cs (`MusicRecentlyPlayedRequest<T>`) — **6 skips**
  including `response()`.

**WeatherKit — entire `weather<T>(for:including:)` family + Statistics:**

- WeatherKit.cs:16873-16899 — `WeatherService.weather<T>(for:
  including:)` parameter-pack overloads (1-6 dataset variants) and
  `dailyStatistics`, `hourlyStatistics`, `monthlyStatistics`,
  `dailySummary` — **18 methods silently dropped** as
  `GenericTypeCallback` skips.

This is the worst Round-5 feature gap: Apple positions `weather<T>`
as the recommended cost-aware entry point (subset-only fetches that
avoid the bundled-snapshot cost), and the Statistics methods are the
iOS 18+ historical-data API. Consumers can call only the unbounded
full-snapshot `WeatherService.weather(for: CLLocation)` overload —
roughly 18 of the ~25 callable async data-fetch endpoints aren't
reachable from C#.

Cross-cutting: this is structurally the same closure-marshalling
gap as Nuke `DataLoader.init`, but the consumer impact compounds
through the request-builder pattern. A 13-skip type leaves a public
class that exists, can be type-loaded, but does nothing.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 5 / Family C.
