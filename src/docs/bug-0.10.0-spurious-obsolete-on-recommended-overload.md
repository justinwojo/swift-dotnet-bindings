# Bug: `[Obsolete]` from `@available(*, deprecated:)` is broadcast across an overload set

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Nuke](https://github.com/justinwojo/swift-dotnet-packages)
> 13.0.5 generated bindings.

## Summary

When a Swift name has multiple overloads and only *one* is annotated with
`@available(*, deprecated, message: "…")`, the generator emits the C#
`[Obsolete(...)]` attribute on **every** overload in the set rather than
just the deprecated one. The other overloads compile fine but every call
from C# raises the deprecation warning, pointing consumers away from the
recommended API.

In Nuke 13.0.5 specifically, the `data(for url: URL)` overload is
correctly deprecated in Swift, with the message *"Please the variant that
accepts `ImageRequest` as a parameter."* The generator copies that
message onto **both** C# overloads — including
`DataAsync(ImageRequest url, …)`, which is the very overload the message
tells you to use. The consumer sees:

```
warning CS0618: 'ImagePipeline.DataAsync(ImageRequest, …)' is obsolete:
  'Deprecated: Please the variant that accepts `ImageRequest` as a parameter.'
```

…on a call to the recommended API.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source library: Nuke 13.0.5

## Repro

```bash
sed -n '15083,15090p' libraries/Nuke/obj/Debug/net10.0-ios/swift-binding/Nuke.cs
sed -n '15515,15517p' libraries/Nuke/obj/Debug/net10.0-ios/swift-binding/Nuke.cs
```

```csharp
// Nuke.cs:15083 — ImageRequest variant (recommended in Swift)
[Obsolete("Deprecated: Please the variant that accepts `ImageRequest` as a parameter.")]
/// <summary>Returns image data for the given request.</summary>
public Task<(byte[], Foundation.NSUrlResponse?)> DataAsync(
    Nuke.ImageRequest url, CancellationToken cancellationToken = default)
{ … }

// Nuke.cs:15515 — NSUrl variant (correctly deprecated in Swift)
[Obsolete("Deprecated: Please the variant that accepts `ImageRequest` as a parameter.")]
public Task<(byte[], Foundation.NSUrlResponse?)> DataAsync(
    Foundation.NSUrl url, CancellationToken cancellationToken = default)
{ … }
```

## Native ground truth

```bash
grep -n -B 2 "func data(" Modules/Nuke.swiftmodule/arm64-apple-ios-simulator.swiftinterface
```

```text
swiftinterface (line 786-820):
  786  final public func image(for request: Nuke.ImageRequest) async throws -> Nuke.PlatformImage
  787  #if compiler(>=5.3) && $NonescapableTypes
  788  final public func data(for request: Nuke.ImageRequest) async throws
              -> (Foundation.Data, Foundation.URLResponse?)
  789  #endif
  ...
  818  @available(*, deprecated, message: "Please the variant that accepts `ImageRequest` as a parameter")
  819  @discardableResult
  820  final public func data(for url: Foundation.URL) async throws
              -> (Foundation.Data, Foundation.URLResponse?)
```

The `@available(*, deprecated, …)` annotation is on the URL overload
*only*. The ImageRequest overload at line 788 has no `@available`
attribute.

## Hypothesis

The generator's `@available` parsing appears to be name-keyed (or
swift-API-base-name keyed) rather than overload-keyed. When it sees
`data(for:)` annotated `deprecated` once, it stores the deprecation under
the name `data` (or `data:`) and applies it to every overload of `data`
in the same scope when emitting the C# attributes.

Likely fix site: the attribute-emission step that walks each Swift
function and emits `[Obsolete]` should key the deprecation lookup on the
*full* Swift signature (including parameter types), not just the base
name.

A useful adjacent invariant: `@available` in Swift is always
declaration-attached and is **not** inherited by overloads — even when
two overloads share a base name, an `@available` on one says nothing
about the other. The C# emitter should mirror that.

Worth a same-fix audit of `@available(*, unavailable, message: "…")` and
`@available(*, renamed: "…")` — both lower to `[Obsolete]` and probably
share the same broadcast bug.

## Impact

- **Wrong direction.** The Nuke 13.0.5 case is particularly visible: the
  deprecation message tells the consumer to switch to the variant that
  *is* itself marked obsolete. There's no correct action the consumer can
  take from the C# warning text alone.
- **Diagnostic noise.** Every C# call to a non-deprecated overload that
  shares a name with a deprecated overload raises `CS0618` at compile
  time. Consumers either suppress the warning blanket-style (losing the
  signal entirely) or work around case-by-case.
- **Library scope.** Anywhere a Swift type uses `@available(*,
  deprecated, …)` on one overload of an overload set. Common pattern in
  evolving Swift APIs (`init(…)` vs. `init(…)`, `image(for:)` URL vs.
  ImageRequest, etc.). Bigger libraries — Stripe, BlinkID — likely have
  multiple instances; needs an audit.

## 2026-05-05 Stripe audit — `@available` propagation also drops, not just broadcasts

The 2026-05-05 Stripe audit (see
[`audit-stripe-2026-05-05.md`](../../swift-dotnet-packages/audit-stripe-2026-05-05.md))
surfaced the **inverse** failure of this bug: the generator's `@available`
propagation also *drops* native platform-availability attributes where
Swift has them.

```text
swiftinterface (StripeApplePay line 171):
  @available(iOS 15.0, *)
  optional func paymentAuthorizationController(_ controller: PKPaymentAuthorizationController,
                                               didChangeCouponCode: String,
                                               handler completion: @escaping …)
  @available(iOS 15.0, *)
  optional func paymentAuthorizationController(_ controller: PKPaymentAuthorizationController,
                                               didSelectShippingMethod: PKShippingMethod,
                                               handler completion: @escaping …)
```

Generated C# (StripeApplePay.cs:31, line 810): no `[SupportedOSPlatform("ios15.0")]`,
no `[UnsupportedOSPlatform("ios14.0")]`, no `[Obsolete]` either — the
attribute set is empty.

Consumer-side: a project targeting iOS 14 deployment that calls these
methods will compile clean and crash at runtime on iOS < 15 with
"selector not found" or equivalent.

The fix is the same emitter site as the broadcast bug: the
attribute-emission walk needs to faithfully mirror Swift's `@available`
declarations *per overload*. Today it broadcasts in one direction
(deprecated copied across overloads) and silently drops in the other
(platform-availability not copied to the lowered C# attributes).

The bug spans both sides of the emitter: `@available(*, deprecated)`
broadcast (the Nuke case) and `@available(iOS X.Y, *)` drop (the
StripeApplePay case). Worth the same fix audit.

## Round 4 — Lottie + StoreKit2 audit (2026-05-05)

The Lottie + StoreKit2 audit surfaced two more sub-shapes of the same
emitter bug — both are *drops*, the Nuke case's broadcast counterpart.

**Sub-shape F-3 — `@available(*, deprecated)` on enum cases not propagated
to the C# factory methods:**

Lottie's `LottiePlaybackMode` (`Lottie.swiftinterface`) marks 9 enum cases
deprecated:

```text
@available(*, deprecated, message: "Use … directly")
case progress(_ progress: AnimationProgressTime)
@available(*, deprecated, message: "…")
case frame(_ frame: AnimationFrameTime)
…
```

C# lowers each case to a static factory method on the
`LottiePlaybackMode` static class. None carry `[Obsolete]`:

| Site | C# line | Swift annotation |
|---|---|---|
| `LottiePlaybackMode.Progress(double)` | Lottie.cs:6249 | `@available(*, deprecated)` |
| `LottiePlaybackMode.Frame(double)` | Lottie.cs:6266 | `@available(*, deprecated)` |
| `LottiePlaybackMode.Time(double)` | Lottie.cs:6283 | `@available(*, deprecated)` |
| `LottiePlaybackMode.Pause()` | Lottie.cs:6300 | `@available(*, deprecated)` |
| `LottiePlaybackMode.FromProgress(...)` | Lottie.cs:6319 | `@available(*, deprecated)` |
| `LottiePlaybackMode.FromFrame(...)` | Lottie.cs:6338 | `@available(*, deprecated)` |
| `LottiePlaybackMode.FromTime(...)` | Lottie.cs:6371 | `@available(*, deprecated)` |
| `LottiePlaybackMode.LoopFrom(...)` | Lottie.cs:6393 | `@available(*, deprecated)` |
| `LottiePlaybackMode.PlayFrom(...)` | Lottie.cs:6436 | `@available(*, deprecated)` |

Same pattern at `Lottie.cs:553` for `DotLottieError` cases that Swift
marks deprecated.

**Sub-shape F-4 — `@available(iOS X.Y, *)` lowered to wrong platform
version:**

StoreKit2's `Product.purchase(...)` overloads are gated by Swift
`@available(iOS 18.2, macOS 15.2, …)`. The C# emitter writes
`[SupportedOSPlatform("ios17.0")]` instead:

```text
swiftinterface (StoreKit framework, line 1957):
  @available(iOS 18.2, macOS 15.2, *)
  public func purchase(compactJWS: Swift.String,
                       confirmIn viewController: UIKit.UIViewController,
                       options: Set<AdvancedCommerceProduct.PurchaseOption> = []) ...
```

Generated C# (StoreKit2.cs:24459):

```csharp
[SupportedOSPlatform("ios17.0")]   // ← wrong; Swift says 18.2
public Task<Product.PurchaseResult> PurchaseAsync(
    string compactJWS, UIKit.UIViewController viewController,
    IEnumerable<AdvancedCommerceProduct.PurchaseOption> options, ...)
```

A consumer targeting iOS 17.0 that calls this overload compiles clean and
crashes at runtime on iOS 17.x with "Symbol not found" because the
underlying Swift symbol does not exist before iOS 18.2.

**Family attribution.** F-3 and F-4 are the third and fourth
manifestations of the same emitter bug:

- F-1 (Nuke): `@available(*, deprecated)` on one overload broadcast across
  the set.
- F-2 (StripeApplePay): `@available(iOS 15.0, *)` on the Swift method
  silently dropped from C#.
- F-3 (Lottie): `@available(*, deprecated)` on enum cases not propagated
  to the lowered C# factory methods.
- F-4 (StoreKit2): `@available(iOS 18.2, *)` lowered to a *different*
  platform version (ios17.0) — silent platform-version corruption rather
  than a clean drop.

All four resolve to the same fix site: faithful per-declaration mirroring
of Swift `@available` annotations into the C# emission. Cross-reference
in [SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / F-3 / F-4.

## Workaround

Consumer side: `#pragma warning disable CS0618` around the call. Loses
the deprecation signal entirely — including for actually-deprecated APIs
in the same file.

Proper fix in
[swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings):
attribute emission keyed on full overload signature.

## Severity

**Correctness — Low-Medium.** Compile-time only — no runtime impact, no
memory corruption, no leak. But actively misleads consumers and may push
them onto the deprecated overload that the Swift author intended to phase
out. Easy fix; landing it cleans up an otherwise-noisy DX in the next
SDK ship.

## Round 5 — visionOS platform silently dropped (sub-shape F-5)

The cross-package audit of `SwiftBindings.Apple.MusicKit` (2026-05-05)
surfaces a fifth sub-shape: **silent drop of `@available(visionOS
1.0, *)`** across an entire library. Broader than the per-overload
F-2 drop seen on Stripe PassKit; visionOS is uniformly absent from
the C# attribute set across **453 swiftinterface markers** in
MusicKit alone.

**Repro:**

```bash
sed -n '23,30p' apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/MusicKit.cs
```

```csharp
// MusicKit.cs:23
[SupportedOSPlatform("ios15.0")]
[SupportedOSPlatform("macos12.0")]
[SupportedOSPlatform("tvos15.0")]
[SupportedOSPlatform("watchos8.0")]
public partial class Artwork : ISwiftObject, ISwiftStruct, IDisposable
{ … }
```

Swiftinterface declaration:

```swift
@available(iOS 15.0, macOS 12.0, tvOS 15.0, watchOS 8.0, visionOS 1.0, *)
public class Artwork { … }
```

The `visionOS 1.0` clause from the swiftinterface is dropped during
emission. Same shape across ~every public type in MusicKit (`Album`,
`Song`, `Track`, `Playlist`, `Artist`, `MusicSubscription`,
`MusicPlayer`, …). 453 declarations carry the `visionOS 1.0`
attribute in the swiftinterface; none of them carry the
`[SupportedOSPlatform("visionos1.0")]` attribute in C#.

**Hypothesis:** the SDK's platform-mapping table predates visionOS
support; the swiftinterface parser sees `visionOS` and the
unmapped-platform branch silently elides the attribute. Should be a
table-extension fix, not a structural change.

**Impact:** consumers building visionOS apps with MusicKit get
spurious platform-availability warnings — the type *exists* on
visionOS 1.0+ per Swift but the C# binding doesn't know that.
Workaround is `#pragma warning disable` or hand-applied
`[SupportedOSPlatform("visionos1.0")]` in consumer code, neither of
which scale.

**Severity:** Medium — material gap for visionOS-targeting consumers.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 5 / Family F.
