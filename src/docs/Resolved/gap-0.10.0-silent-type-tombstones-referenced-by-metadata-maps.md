# Gap: Silent type tombstones — types referenced by metadata cookie maps but absent from generated C#

> SDK 0.10.0 generator metadata-consistency gap. Discovered 2026-05-05
> during the WeatherKit + MusicKit cross-package consumer-experience
> audit (Round 5). See
> [`audit-weatherkit-musickit-2026-05-05.md`](../../swift-dotnet-packages/audit-weatherkit-musickit-2026-05-05.md)
> finding **O-12**.

## Summary

`binding-emission-report.json` lists "silent tombstones" — Swift types
that the generator decided not to emit as C# types, but whose
*metadata cookies* are still referenced from the metadata maps of
other (emitted) types. Reflection-based diagnostics, debug
inspection, or future tooling that walks the cookie maps will see
references to types that have no C# representation — broken indirect
links.

The defect is "the generator's metadata-emission pipeline and
type-emission pipeline disagree about which types exist." The
emitted-types pipeline drops the type; the metadata-emission
pipeline keeps the cookie reference.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.1
- .NET SDK 10.0.107
- Xcode 26.3 / Swift 6.2.x / iPhoneOS26.2.sdk
- macOS 26.x, arm64

## Repro

```bash
sed -n '21,28p' apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/binding-emission-report.json
```

```json
{
  "silentTombstones": [
    "MusicAttributeProperty",
    "MusicExtendedAttributeProperty",
    "MusicRelationshipProperty"
  ],
  ...
}
```

Then grep for cookie references in the generated MusicKit.cs:

```bash
grep -c "MusicAttributeProperty" apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/MusicKit.cs
```

The 3 silent tombstones each appear in 13+ metadata-cookie maps
inside MusicKit.cs — but the type itself doesn't exist (no `class`,
`struct`, or `interface` declaration with that name).

WeatherKit shows the same shape:

```bash
sed -n '19,26p' apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/binding-emission-report.json
```

```json
{
  "silentTombstones": [
    "DailyWeatherStatisticsQuery",
    "DailyWeatherSummaryQuery",
    "HourlyWeatherStatisticsQuery",
    "MonthlyWeatherStatisticsQuery"
  ]
}
```

These are the same four query types tracked under M-2
([`gap-0.10.0-multispecialization-drops-generic-property-accessors.md`](gap-0.10.0-multispecialization-drops-generic-property-accessors.md))
— they *are* visible in MusicKit.cs as `[OpaqueSwiftType(2)]` types
with no projectable members. The "silent" tombstone label here means
the metadata-emission pipeline knows about them but didn't connect
them to consumer-callable surface.

## Hypothesis

Two cooperating pipelines in the generator:

1. **Type-emission pipeline** — walks the swiftinterface and emits a
   C# type per Swift type, except when the type matches a "skip
   reason" (existential, IndeterminatePwtShape, MultiSpecialization,
   etc.).
2. **Metadata-emission pipeline** — emits cookie maps that connect
   Swift type metadata to C# type lookups, used at runtime for PWT
   resolution and at debug-time for type-name lookup.

When pipeline 1 skips a type but pipeline 2 still has cookie
references to it (because some *other* emitted type's metadata map
references the skipped type), the binding is internally
inconsistent. The `binding-emission-report.json` flags this as a
silent tombstone but the generator emits the inconsistent output
anyway.

The fix is to make pipeline 2 aware of pipeline 1's skip
decisions — either:

- Emit the skipped type as a stub `partial class { … }` so the
  cookie reference resolves to *something*, or
- Strip the cookie reference from the metadata map when the target
  type is skipped.

The first option is closer to "the type exists but is unusable" (matches
the user's mental model). The second is closer to "the type doesn't
exist at all" (cleaner but breaks any cross-type metadata that
depended on it).

## Affected sites

MusicKit:

- `binding-emission-report.json:21-25` — `MusicAttributeProperty`,
  `MusicExtendedAttributeProperty`, `MusicRelationshipProperty`
  (3 silent tombstones referenced in 13+ metadata maps each)

WeatherKit:

- `binding-emission-report.json:19-24` — `DailyWeatherStatisticsQuery`,
  `DailyWeatherSummaryQuery`, `HourlyWeatherStatisticsQuery`,
  `MonthlyWeatherStatisticsQuery` (4 silent tombstones; same as M-2
  sites but seen from the metadata-map angle)

Cross-cutting: every binding the SDK has shipped is at risk if
`binding-emission-report.json` lists silent tombstones. Worth a
generator-side audit pass.

## Impact

Reflection or runtime metadata walks may throw on the missing types:

- A debug visualizer that walks `[SwiftMetadata]` cookie maps to
  display Swift type names sees an unresolvable reference.
- A future runtime helper that resolves PWTs by walking the metadata
  map fails on the skipped type.
- Diagnostic logging that prints "Swift type for cookie X" is
  inconsistent.

Consumer-facing impact today: ~zero. No consumer code walks these
maps. Future consumer impact: depends on what tooling builds on the
metadata maps.

## Severity

**Low.** Internal-consistency issue that doesn't surface at consumer
call sites today. Fix should be low-cost (gate the metadata-map
emission on the type-emission pipeline's skip set).

## Fix gate

After fix: `binding-emission-report.json`'s `silentTombstones` array
should be empty for every emitted library, OR the corresponding
cookie references in the generated `*.cs` should resolve to existing
types.

Generator-wide CI assertion: parse every emitted
`binding-emission-report.json`, fail the build if `silentTombstones`
is non-empty.
