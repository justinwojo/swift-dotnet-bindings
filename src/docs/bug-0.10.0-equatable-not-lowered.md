# Bug: Swift `Equatable` lowering — `GetHashCode` stub returns 0; `Equatable` enums emit no equality at all

> SDK 0.10.0 generator correctness bug + adjacent feature gap.
> Discovered 2026-05-05 during a consumer-experience audit of
> [SwiftBindings.Mappedin](https://github.com/justinwojo/swift-dotnet-packages),
> [SwiftBindings.BlinkID](https://github.com/justinwojo/swift-dotnet-packages),
> and
> [SwiftBindings.BlinkIDUX](https://github.com/justinwojo/swift-dotnet-packages).

## Summary

Two related defects in how Swift `Equatable` (and by extension `Hashable`)
conformance is lowered into C#:

1. **For Swift classes/structs that *do* receive equality emission**
   (e.g. `MPICoordinates`), the generator wires `Equals`, `==`, `!=`, and
   `IEquatable<T>.Equals` to a Swift PInvoke `eq` — but emits
   `GetHashCode` as a constant `return 0;` stub. This silently breaks
   the `Equals == Equals → GetHashCode == GetHashCode` invariant and
   degrades every hash-bucketed collection to O(n).

2. **For Swift `enum` types declared `Equatable`** (e.g. `DocumentSide`,
   `UIEvent`, `Camera.CameraPosition` in BlinkIDUX;
   `BlinkIDSDK.AnonymizationMode` in BlinkID), no equality emission
   happens at all. The C# wrapper has `Tag` + `TryGet…` accessors, no
   `Equals`, no `==`, no `IEquatable<T>`. C# reference-equality semantics
   apply — every `new` returns a distinct instance even when the payload
   matches.

Both defects discourage idiomatic use of these types in
`HashSet`/`Dictionary`/`==` comparisons. (1) silently loses performance
and may also lose correctness in some `Equals`-then-`GetHashCode` consumer
flows. (2) makes equality outright wrong from the C# side.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64

## Repro — defect 1 (constant-zero `GetHashCode`)

```bash
sed -n '22720,22755p' libraries/Mappedin/obj/Debug/net10.0-ios/swift-binding/Mappedin.cs
```

```csharp
// Mappedin.cs:22725
public override bool Equals(object? obj)
{
    return obj is MPICoordinates other
        && PInvoke_eq(
            this.Payload.DangerousGetHandle(),
            other.Payload.DangerousGetHandle());
}
public override int GetHashCode()
{
    return 0;                                // [1] constant stub
}

public static bool operator ==(MPICoordinates? left, MPICoordinates? right) { … PInvoke_eq … }
public static bool operator !=(MPICoordinates? left, MPICoordinates? right) { … }
public bool Equals(MPICoordinates? other) { … PInvoke_eq … }
```

The `eq` PInvoke at line 22721 dispatches into Swift's synthesized
`Equatable.==`, so equality semantics are correct. But `GetHashCode` does
not call any Swift hash function — it just returns 0.

C# contract: `Equals(a, b) == true ⇒ GetHashCode(a) == GetHashCode(b)`.
This is technically satisfied by always returning 0, but the contract
also implies that "hash codes are reasonably distributed for distinct
values," and the runtime relies on that for `Dictionary`/`HashSet`
performance. With every value bucketed into slot 0, every operation on a
`HashSet<MPICoordinates>` of size N is O(N).

## Repro — defect 2 (no equality on `Equatable` enums)

```text
swiftinterface (BlinkIDUX line 128, 211, 292):
  public enum DocumentSide : Swift.Equatable, Swift.Sendable { case front, back }
  public enum UIEvent : Swift.Equatable, Swift.Sendable { … }
  extension Camera.CameraPosition : Swift.Equatable { … }
```

Generated C#:

```csharp
// BlinkIDUX.cs:3036  (DocumentSide)
public partial class DocumentSide : ISwiftObject, IDisposable, …
{
    public enum CaseTag { Front, Back }
    public CaseTag Tag => …;
    public static DocumentSide NewFront() => …;
    public static DocumentSide NewBack() => …;
    public bool TryGetFront(out …) => …;
    // ← no Equals, no GetHashCode, no operator==, no IEquatable<DocumentSide>
}
```

So:

```csharp
var a = DocumentSide.NewFront();
var b = DocumentSide.NewFront();
a == b               // false (reference equality)
a.Equals(b)          // false (default object.Equals — reference)
a.GetHashCode() == b.GetHashCode()  // almost always false
```

Same shape for every Swift `Equatable` enum lowered through the disposable
reference-wrapper path.

## Hypothesis

Two emitter sites:

- **Reference-typed value lowering** (`MPICoordinates`-class case): the
  generator already detects `Hashable` (or implicit hash-from-Equatable
  synthesis) and emits the override scaffolding for `GetHashCode`, but the
  body is a placeholder that was never filled in. There should be a Swift
  `hashValue`/`hash(into:)` PInvoke wired up the same way as the `eq`
  PInvoke at line 22721. Likely the emitter has the override-emission
  branch but no PInvoke-binding-and-call branch, so it falls through to
  `return 0`.

- **Enum lowering through the disposable-reference-wrapper path**: this
  whole path predates `Equatable` lowering. It treats the generated
  enum-as-class purely as a Swift-payload-holding wrapper with `Tag` /
  `TryGet…` accessors, and never asks "does the underlying Swift enum
  conform to `Equatable`?" The `partial class` shape would let the
  emitter synthesize `Equals`/`GetHashCode`/`==` from `Tag` + per-arm
  payload comparison without involving Swift PInvokes at all (for
  trivially-equatable arms) — or by dispatching to a Swift `eq` thunk for
  arms with non-trivial payloads.

The Mappedin case (defect 1) is the easier fix: bind to the Swift hash
PInvoke. The BlinkID/BlinkIDUX enum case (defect 2) is structural —
needs a new emission branch in the disposable-reference-wrapper path.

## Native ground truth — Swift hashing

Swift compiler synthesizes `Hashable.hash(into:)` for every `Equatable`
struct/enum where all stored properties / associated values are themselves
`Hashable`. Most types that opt into `Equatable` end up `Hashable` for
free. The runtime exposes `swift_class_hashValue` / metadata-driven hash
combining; the generator can either:

- Emit a PInvoke for the synthesized `Hashable.hash(into:)` (hash into a
  Swift `Hasher`, then call `Hasher.finalize` — yields `Int`, truncate to
  `int`).
- Or, for value types where the `eq` PInvoke is already memcmp-equivalent,
  emit a managed hash over the payload bytes.

The first option is more general and uses the same dispatch pattern the
`eq` PInvoke already uses.

## Impact

- **Defect 1 — silent perf cliff.** Every C# consumer that puts an
  `MPICoordinates` (or any other `Equatable`-bearing Swift class lowered
  through this path) into a `HashSet`/`Dictionary`/`Lookup` gets O(N)
  collection ops with zero diagnostic. The bug surfaces as "the app gets
  slow with N=1000s of coordinates" with no obvious cause.
- **Defect 2 — wrong equality.** Comparing two `DocumentSide.NewFront()`
  values returns `false`. Consumer code that does `if (side ==
  DocumentSide.NewFront()) …` is silently broken. The workaround
  (compare `.Tag`) requires manually unpacking associated values for arms
  that have payload, which most consumers won't realize they need to do.
- **Library scope.** Defect 1 hits any Swift class/struct with `eq`
  binding (likely Nuke `ImageRequest`, Nuke `ImageContainer`, Lottie
  `LottieColor`, Stripe `…Address`, etc. — needs an audit). Defect 2 hits
  any `Equatable` Swift enum with associated values (BlinkID has dozens).

## 2026-05-05 Stripe audit — additional confirmed sites

Stripe binding line cross-package audit (see
[`audit-stripe-2026-05-05.md`](../../swift-dotnet-packages/audit-stripe-2026-05-05.md))
confirmed both defect shapes recur across 5 Stripe products. Same emitter,
no new variants — adding for de-dup tracking when the fix lands.

**Defect 1 — constant-zero `GetHashCode`** — five additional sites:

- `StripeCore.cs:2101` — `NonEncodableParameters`
- `StripeConnect.cs:267` — `AccountCollectionOptions`
- `StripePaymentSheet.cs:12313` — `PaymentSheet.Appearance` (+ many nested
  Equatable structs in the same file)
- `StripePaymentSheet.cs:23195` — `BillingDetails`
- `StripeCardScan.cs:526` — `ScannedCard`

**Defect 2 — `Equatable` enum emits no equality** — two additional sites,
including a new sub-shape:

- `StripePaymentSheet.cs:30223` — `CustomerPaymentOption : Swift.Equatable`
  (reference-wrapper enum). Has only `Tag` + `TryGetStripeId`; equality
  declared at swiftinterface:912 not surfaced.
- `StripeCardScan.cs:25` — `CancellationReason : String, Equatable, Hashable`
  (a *raw-value* `Equatable` enum). Emits only `ISwiftObject, ISwiftStruct,
  IDisposable` — no equality interface, no equality method, despite
  swiftinterface:23/80 declaring both `Equatable` and `Hashable`. The
  raw-value-string-Equatable case may be its own emission gap distinct from
  the disposable-reference-wrapper case.

## Round 4 — Lottie + StoreKit2 sites (2026-05-05)

The cross-package audit of `SwiftBindings.Lottie` (4.x) and
`SwiftBindings.Apple.StoreKit2` (Apple framework, 26.2.2) confirms ~15
new sites across both defect sub-shapes. This is in addition to the
Stripe Round 3 evidence already listed above.

**Defect 1 (`GetHashCode` = 0) — Lottie value/font/text/image providers
(all classes that get full `Equals`/`PInvoke_eq` but stub hash):**

- Lottie.cs:17157-17160 — `ColorValueProvider`
- Lottie.cs:17571-17574 — `FloatValueProvider`
- Lottie.cs:18039-18042 — `GradientValueProvider`
- Lottie.cs:18478-18481 — `PointValueProvider`
- Lottie.cs:18877-18880 — `SizeValueProvider`
- Lottie.cs:19142-19145 — `DefaultFontProvider`
- Lottie.cs:21457-21460 — `DictionaryTextProvider`
- Lottie.cs:21744-21747 — `DefaultTextProvider`
- Lottie.cs:22212-22215 — `BundleImageProvider`
- Lottie.cs:24506-24509 — `FilepathImageProvider`

**Defect 1 (`GetHashCode` = 0) — StoreKit2 value types:**

- StoreKit2.cs:411-415 — `PurchaseIntent.GetHashCode`
- StoreKit2.cs:20576-20578 — `Product.PromotionInfo.GetHashCode`

**Defect 2 (no equality at all on `Equatable`/`Hashable` payload-bearing
classes) — Lottie + StoreKit2:**

- Lottie.cs:6185 — `LottiePlaybackMode : ISwiftObject, ISwiftStruct,
  IDisposable` — declared `Hashable` in swiftinterface:616 but emits no
  `IEquatable<T>`, no operators, no `Equals` override, no `GetHashCode`
  override.
- Lottie — same shape on `LottieLoopMode`, `ReducedMotionOption`,
  `RenderingEngineOption`.
- StoreKit2.cs:3471 — `VerificationResult<TSignedType>` — Swift
  conditional `Equatable where SignedType : Equatable` and `Hashable
  where SignedType : Hashable` not lowered.
- StoreKit2.cs:3815 — `VerificationResult<T>.VerificationError` —
  unconditional `Equatable`/`Hashable` in Swift; no equality plumbing in
  C#.
- StoreKit2.cs:29896 — `ExternalPurchase.NoticeResult` — declared
  `Hashable, Sendable` in swiftinterface:2653 but C# emits only
  `ISwiftObject, ISwiftStruct, IDisposable`.

The `where SignedType : Equatable` conditional case adds a small
complication on the StoreKit2 generic — the natural lowering is
conditional `IEquatable<T>` implementation only when `T` itself
implements `IEquatable<T>`, which C# can't express directly; default-
interface-method synthesis would solve this.

Doubles the prior evidence count (Mappedin + BlinkID + Stripe → +
Lottie + StoreKit2). Same emitter sub-shapes; no new emitter found.
Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / Family B.

## Round 5 — WeatherKit + MusicKit sites (2026-05-05)

The cross-package audit of `SwiftBindings.Apple.WeatherKit` and
`SwiftBindings.Apple.MusicKit` confirms ~33 new sites — the **single
largest cluster** in any round so far. WeatherKit alone contributes
31 `return 0;` `GetHashCode` sites, confirming this is the
generator's *uniform default* for any `Hashable` lowering on a
reference-typed value (Sub-shape **B-1**). Two other sub-shapes
emerged:

- **Sub-shape B-1** (stub `return 0;`) — WeatherKit standard, 31 sites
- **Sub-shape B-2** (no equality at all) — MusicKit
  `WeatherCondition`, `Precipitation`, `PressureTrend`, `MoonPhase`,
  `WeatherSeverity`
- **Sub-shape B-3** (GetHashCode returns `Payload.GetHashCode()` —
  `SwiftSafeHandle`'s identity hash, not the Swift Hasher) —
  MusicKit `MusicItemID.GetHashCode()` at `MusicKit.cs:12017-12047`

Cumulative cross-audit Round 5 count: **~55 sites across 8 libraries**.

**Defect 1 / B-1 (`GetHashCode` = 0) — WeatherKit Hashable types
(31 sites, single library):**

- WeatherKit.cs:611 — `WeatherAttribution.GetHashCode`
- WeatherKit.cs:5432 — `CurrentWeather.GetHashCode`
- WeatherKit.cs:6118 — `DayWeather.GetHashCode`
- WeatherKit.cs:9105 — `Forecast<T>.GetHashCode`
- WeatherKit.cs:10130 — `HourWeather.GetHashCode`
- WeatherKit.cs:12262 — `MinuteWeather.GetHashCode`
- WeatherKit.cs:13003 — `MoonEvents.GetHashCode`
- WeatherKit.cs:13880 — `Precipitation.GetHashCode`
- WeatherKit.cs:14097-14100 — `Forecast<MinuteWeather>.GetHashCode`
- WeatherKit.cs:15045 — `SunEvents.GetHashCode`
- WeatherKit.cs:16064 — `WeatherAlert.GetHashCode`
- WeatherKit.cs:16322 — `WeatherAlertSummary.GetHashCode`
- WeatherKit.cs:17192 — `WeatherCondition.GetHashCode` (where present)
- WeatherKit.cs:17511 — `Wind.GetHashCode`
- …plus 17 more in the same module (`Pressure`, `Visibility`,
  `Humidity`, `UVIndex`, etc.)

**Defect 2 / B-2 (no equality at all on Equatable enums/value classes)
— MusicKit + WeatherKit value classes:**

- WeatherKit.cs:8029-8086 — `WeatherCondition` (Swift `Hashable`
  at swiftinterface:393; C# bare `Tag` class)
- WeatherKit.cs:14347-14400 — `Precipitation` (Swift `Hashable`,
  C# bare `Tag` class)
- WeatherKit.cs:15292-15348 — `PressureTrend` (Swift `Hashable`,
  C# bare `Tag` class)
- WeatherKit.cs:17901 — `WeatherSeverity` (Swift `Hashable`, C#
  bare `Tag` class)
- WeatherKit.cs (`MoonPhase`, `UVIndex.ExposureCategory`) — same shape

**Defect 2-cross / B-3 (SafeHandle identity hash, ignores Swift
Hasher) — MusicKit MusicItemID:**

- MusicKit.cs:12017-12047 — `MusicItemID.GetHashCode()` returns
  `this.Payload.GetHashCode()` (where `Payload` is a
  `SwiftSafeHandle`, hashed by reference identity). Two `MusicItemID`
  instances that compare equal (`==` / `Equals` true via the Swift
  `eq` PInvoke) return *different* hash codes — direct violation of
  the equality contract.

**Defect 1-cross — MusicKit collections fall under O-6
[`bug-0.10.0-unconditional-equatable-on-conditional-swift-generic.md`](bug-0.10.0-unconditional-equatable-on-conditional-swift-generic.md):**
the inverse direction (over-broad emission). Treated separately —
that's a constraint mismatch, not a missing-emission defect.

The fix that addresses C3 / P3 should also land B-3 — when the
generator emits `Equals` via PInvoke `eq`, the matching `GetHashCode`
should use the Swift Hasher PInvoke (or the same `eq`-implied byte
hash), never the SafeHandle's identity hash.

## Workaround

- For defect 1: consumers can wrap the offender in a
  `KeyedCollection`/`SortedDictionary` based on a property they hash
  themselves. Or override `IEqualityComparer` per-collection. Both
  require knowing about the bug.
- For defect 2: consumers must compare `enum.Tag` (and recursively the
  payload, if any). Verbose at every comparison site.

## Severity

- Defect 1: **Correctness — Medium-High.** Doesn't crash but breaks the
  hash-distribution side of the equality contract. Performance bug
  masquerading as a correctness bug.
- Defect 2: **Feature gap — High** for any consumer who treats Swift
  enums as values (which is the natural Swift idiom). Currently consumers
  must write `Tag`-comparison helpers themselves.

Pair with the priority-table item P3 ("Lower Swift `Equatable` to
`IEquatable<T>` + `Equals` / `==` / `!=`") in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
in the next SDK ship — both halves of the fix live in the same emitter
subsystem.
