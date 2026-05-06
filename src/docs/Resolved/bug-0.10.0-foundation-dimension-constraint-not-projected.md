# Bug: Generic types constrained over `Foundation.Dimension` typedef are silently emitted as unsupported tombstones

> SDK 0.10.0 generator typedb / constraint-projection gap. Discovered
> 2026-05-05 during the WeatherKit + MusicKit cross-package
> consumer-experience audit (Round 5). See
> [`audit-weatherkit-musickit-2026-05-05.md`](../../swift-dotnet-packages/audit-weatherkit-musickit-2026-05-05.md)
> finding **O-10**.
>
> **Status: RESOLVED in Bundle 04 #5.** The generator now recognises
> class-bound generic constraints — both at the type level
> (`struct Trend<Dimension> where Dimension : Foundation.Dimension`)
> and at the method level — across four cooperating sites:
>
> - `PInvokeHelperEmitter.FlattenConformances` recognises the
>   `TypeRecordKind.Class` record and skips silently. Per the Swift
>   ABI, class constraints don't add a witness-table arg per
>   constraint; the metadata accessor takes only the `TypeMetadata`
>   arg already counted by `typeParams.Count`. Pre-fix the class
>   record fell through the `record.Kind != Protocol` branch and got
>   added to `unresolved`, tombstoning the parent type with
>   `IndeterminatePwtShape`.
> - `GenericTypeEmitter.GetWhereClause` and
>   `WrapperEmitter.Signature.BuildWhereClause` emit the projected
>   C# class name (e.g. `SwiftBindingsTestLib.UnitBase`,
>   `Foundation.NSDimension`) instead of the `I{Name}` interface
>   form, AND skip the `ISwiftObject` seed — a class constraint
>   already implies `ISwiftObject` and must come first per
>   CS0405/CS0406 ordering rules.
> - `BoundGenericsHandler.SatisfiesConstraint` walks the
>   `SuperclassNames` chain on `ClassDecl` to accept subclass
>   type arguments AND walks `TypeRecord.SuperclassTypeName`
>   transitively via the TypeDatabase to accept external/XML-only
>   subclasses (e.g. `Foundation.UnitTemperature` → `Foundation.Dimension`).
>   The TypeDatabase walk runs BEFORE the local-decl resolution so
>   external concrete subclasses don't fall through the
>   `typeArgumentDecl == null` short-circuit and silently tombstone
>   the consuming member.
> - `FoundationDatabase.xml` registers `Foundation.Dimension` →
>   `Foundation.NSDimension` (objcBridged class) so the cross-module
>   WeatherKit case (`Trend<Dimension>` from the `WeatherKit` module)
>   resolves through the same path as the local fixture. Each unit
>   subclass entry (`UnitTemperature`, `UnitLength`, `UnitMass`,
>   `UnitSpeed`, `UnitDuration`, `UnitPressure`, `UnitAngle`,
>   `UnitInformationStorage`) declares
>   `superclass="Foundation.Dimension"` so the
>   `IsSubclassOfViaTypeDatabase` walk recognises them as
>   `Dimension`-bounded.
>
> Coverage: BindingTests fixture
> (`Generics/Constraints.swift::UnitBase`/`UnitKilometer`/`UnitBox<U>`
> mirroring WeatherKit's `Trend<Dimension>` shape locally without a
> Foundation dependency); runtime tests
> (`Generics/UnitBoxClassConstraintTests.cs`) covering factory
> construction, metadata-accessor-driven property getter, class-typed
> property getter returning the concrete subclass, and direct C#
> constructor; unit tests
> (`GenericTypeEmitterTests.GetWhereClause_ClassBoundConstraint_*`
> and `_ClassPlusProtocolConstraint_*`) verifying class-name emission,
> ISwiftObject seed suppression, and CS0405-correct ordering.
>
> Out of scope: existential lowering for `any Foundation.Dimension`
> at use sites (the second hypothesis below) — not needed because
> the class-constraint branch resolves the projection directly.
> The `HistoricalComparison` enum-case payload cascade in WeatherKit
> still depends on Family C async availability and on
> [`gap-0.10.0-multispecialization-drops-generic-property-accessors.md`](gap-0.10.0-multispecialization-drops-generic-property-accessors.md)
> (M-2); those are tracked separately.

## Summary

Swift declares several generic structs with a `Dimension :
Foundation.Dimension` constraint:

```swift
public struct Trend<Dimension> where Dimension : Foundation.Dimension { … }
public struct TrendBaseline<Dimension> where Dimension : Foundation.Dimension { … }
public struct Percentiles<Dimension> where Dimension : Foundation.Dimension { … }
```

The generator cannot project the `Foundation.Dimension` typedef
constraint and silently emits these as unsupported tombstones with
"`IndeterminatePwtShape`" skip reason. The consumer-visible C# has
`// Unsupported: type 'Trend' — IndeterminatePwtShape` placeholders
where the generic struct should have been; no usable lowering.

Cascades through any Swift API that *uses* these generic types as
property/return types or as enum-case payloads.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.1
- .NET SDK 10.0.107
- Xcode 26.3 / Swift 6.2.x / iPhoneOS26.2.sdk
- macOS 26.x, arm64

## Repro

```bash
sed -n '4020,4030p' apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs
sed -n '11894,11904p' apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs
sed -n '13334,13344p' apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs
```

```csharp
// WeatherKit.cs:4024
// Unsupported: type 'Trend' — IndeterminatePwtShape
//   Public Swift declaration:
//     public struct Trend<Dimension> where Dimension : Foundation.Dimension
//   Public Swift members:
//     public init(slope: Foundation.Measurement<Dimension>)
//     public var slope: Foundation.Measurement<Dimension> { get }
//     public var trendBaseline: WeatherKit.TrendBaseline<Dimension> { get }

// WeatherKit.cs:11898
// Unsupported: type 'TrendBaseline' — IndeterminatePwtShape

// WeatherKit.cs:13338
// Unsupported: type 'Percentiles' — IndeterminatePwtShape
```

Cascades into `HistoricalComparison<UnitType>` payload extraction:

```bash
sed -n '10293,10330p' apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs
```

```csharp
// WeatherKit.cs:10293
public partial class HistoricalComparison
{
    public CaseTag Tag { get; }
    // Each enum case carries a Trend<MeasurementDimension> payload that's
    // unreachable because Trend is tombstoned above.
    // No As<Trend<...>>() / payload accessor emitted.
}
```

The HistoricalComparison enum cases all carry payloads of type
`Trend<MeasurementDimension>` per swiftinterface:575. C# can read the
case `Tag` but cannot extract the payload — the case-payload type
doesn't exist in the C# binding to extract into.

## Hypothesis

Swift's `Foundation.Dimension` is a class hierarchy whose
subclasses are bridged into Foundation.Measurement's PWT lookup
machinery. The generator's PWT-shape inference treats the constraint
as opaque — it can't determine the conformance witness table layout
for "any class extending Foundation.Dimension," so it bails out with
`IndeterminatePwtShape`.

Two structurally clean fixes:

- **Recognize `Foundation.Dimension` as a special-cased typedb
  entry.** The Foundation.Measurement family is already special-cased
  for the corresponding non-generic Measurement<Unit>; extend that
  special-case to `Foundation.Dimension`-bound generic types.
- **Use existential lowering.** Treat the `Dimension` parameter as an
  existential `any Foundation.Dimension` and route through the
  protocol-existential PWT lookup. Less efficient but more general.

## Affected sites

- WeatherKit.cs:4024 — `Trend<Dimension>` tombstone
- WeatherKit.cs:11898 — `TrendBaseline<Dimension>` tombstone
- WeatherKit.cs:13338 — `Percentiles<Dimension>` tombstone
- WeatherKit.cs:10293-10324 — `HistoricalComparison` cases (cascade
  from the Trend tombstone — case payloads unreachable)

Cross-cutting risk: any Apple-framework type bound on
`Foundation.Unit` / `Foundation.Dimension` / similar Foundation
typedef has the same shape. WeatherKit is the most exposed
because of its iOS 18 historical-comparison surface.

## Impact

`WeatherQuery.HistoricalComparisons` can produce values whose case
can be inspected (`Tag`) but the actual trend data is inaccessible.
That makes the iOS 18 historical-comparison feature
**effectively read-blocked for consumers**.

Combined with the cascade, ~10% of the iOS 18 WeatherKit historical/
statistics surface is unreadable from C# even when the request goes
through (which it can't via Family C anyway — see also).

## Severity

**Medium.** Not the first APIs most WeatherKit apps hit, but they
matter for iOS 18 historical-comparison fidelity. Pairs with **M-2**
([`gap-0.10.0-multispecialization-drops-generic-property-accessors.md`](gap-0.10.0-multispecialization-drops-generic-property-accessors.md))
in the same SDK fix unit.

## Fix gate

`WeatherKit.cs:4024`, `:11898`, `:13338` should emit usable
`Trend<TDimension>`, `TrendBaseline<TDimension>`,
`Percentiles<TDimension>` types where `TDimension` resolves to (or is
constrained to) the appropriate Foundation.Measurement-compatible C#
type. Once those exist, the `HistoricalComparison` payload
accessors should emit naturally.

A test that reads
`weather.HistoricalComparisons[0].TryGetTemperature(out var trend)` and
inspects `trend.Slope` would catch the regression.
