# Gap: Synthesized Codable conformances dropped — Swift `init(from:)` / `encode(to:)` not bridged to C#

> SDK 0.10.0 generator feature gap. Discovered 2026-05-05 during the
> WeatherKit + MusicKit cross-package consumer-experience audit (Round
> 5). See
> [`audit-weatherkit-musickit-2026-05-05.md`](../../swift-dotnet-packages/audit-weatherkit-musickit-2026-05-05.md)
> finding **O-13**.

## Summary

Swift compiler synthesizes `Codable` (the `init(from: Decoder) throws`
+ `encode(to: Encoder) throws` pair) for any struct/class whose
stored properties are themselves Codable. The generator emits the
PWT lookups for `IDecodable`/`IEncodable` conformance witnesses (used
during type metadata setup, e.g. at `WeatherKit.cs:14044` for
`Forecast<T>`) but **does not emit the actual decoder/encoder
bridges**. The wrapper records the skip as `SynthesizedCodable` in
`binding-report.json`.

WeatherKit alone reports **67** dropped synthesized-Codable conformances.

Net consumer effect: types that Swift will JSON-serialize cleanly
cannot be round-tripped from C# through `JSONEncoder`/`JSONDecoder`
(or `System.Text.Json`, or any other JSON layer that bridges through
the Swift Codable witness). This blocks "cache today's forecast" and
similar simple persistence scenarios where the natural choice is
"serialize the Swift value type."

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.1
- .NET SDK 10.0.107
- Xcode 26.3 / Swift 6.2.x / iPhoneOS26.2.sdk
- macOS 26.x, arm64

## Repro

```bash
grep -c '"SynthesizedCodable"' apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/binding-report.json
```

Result: **67** entries.

Inspect a sample:

```bash
grep -A 3 '"SynthesizedCodable"' apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/binding-report.json | head -30
```

Each entry names a Swift type (`DayWeather`, `HourWeather`,
`MinuteWeather`, `Forecast<*>`, `WeatherAlert`, etc.) and the skipped
member (`init(from:)` and `encode(to:)`). The C# side has the
matching type but no `IDecodable`/`IEncodable` implementation.

The PWT-lookup side, however, is wired:

```bash
sed -n '14040,14050p' apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs
```

```csharp
// Forecast<TElement> looks up Decodable witness as part of its metadata setup
// — but the witness's Decode method has no C# bridge to call.
```

So the PWT cookie lookups succeed; the consumer-callable surface for
JSON round-tripping doesn't exist.

## Hypothesis

The Swift compiler's synthesized `init(from:)` / `encode(to:)` are
declared in the swiftinterface but their bodies live in the
generated `_compilerProtocolWitnessTable` outputs. Bridging them
requires:

1. **Wrapper-side:** emit `@_cdecl` trampolines that invoke the
   synthesized witnesses given a Swift `Decoder`/`Encoder` PWT and a
   self pointer. This is genuinely hard because `Decoder`/`Encoder`
   are themselves protocol existentials with full-conformance witness
   tables.
2. **C# side:** emit `IDecodable.Decode(IDecoder decoder)` /
   `IEncodable.Encode(IEncoder encoder)` methods that call the
   trampolines. These would need a C#-side `IDecoder`/`IEncoder`
   interface that bridges through Foundation's `JSONDecoder`/
   `PropertyListDecoder` (or equivalent).

The minimum viable fix is the special case where `Decoder` is
`JSONDecoder` and `Encoder` is `JSONEncoder` — the wrapper emits a
`SBW_Decode_<Type>_FromJSON(IntPtr utf8Bytes, int byteLength,
out IntPtr resultBuf)` cdecl trampoline, and the C# side exposes
`Decode(byte[] json) → T` / `Encode(T) → byte[]` static methods on
each Codable type. Doesn't generalize but covers the 90%+ case.

## Affected sites

WeatherKit:

- `binding-report.json` — 67 `SynthesizedCodable` skips covering
  every Decodable struct in the module:
  - `Forecast<*>`, `CurrentWeather`, `DayWeather`, `HourWeather`,
    `MinuteWeather`, `WeatherAlert`, `Wind`, `Pressure`,
    `Visibility`, `Humidity`, `UVIndex`, `MoonEvents`, `SunEvents`,
    …

MusicKit, StoreKit2, Stripe payments line, Lottie — likely have the
same shape; not yet measured. Worth a cross-cutting audit pass.

## Impact

- "Cache the user's forecast for offline display" requires the
  consumer to either:
  - Manually map every property to a C#-side DTO (~30 manual setters
    per type), or
  - Re-fetch on every cold launch (defeats caching).
- Server-bound persistence flows (POST forecast to backend) require
  manual property mapping or a Swift-shim layer.
- The same `Forecast<DayWeather>` value is round-trippable in Swift
  with a 1-line `JSONEncoder().encode(forecast)`. The C# binding
  forces a 30-line manual mapping.

## Severity

**Low** for now (workaround exists: hand-roll DTOs). **Medium** if
caching becomes a primary use case. The fix is structural and
non-trivial. **In scope for 0.10.0** — sequenced as Session F per
`0.10.0-remaining-sessions.md`; user-locked decision 2026-05-06: ship
full round-trip, JSON-only.

## Fix gate

After fix:

```csharp
var json = JsonEncoder.Encode(forecast);    // round-trip Swift Codable
var decoded = JsonDecoder.Decode<Forecast<DayWeather>>(json);
Assert.Equal(forecast.HourlyForecast.Count, decoded.HourlyForecast.Count);
```

…should round-trip cleanly. Today the encoder/decoder don't exist.

A generator-wide CI assertion: count `SynthesizedCodable` skips
across every emitted `binding-report.json`. Today: 67 (WeatherKit) +
unmeasured for other libs. Goal: 0, or matching only types with
non-Codable stored properties (which Swift itself wouldn't synthesize).
