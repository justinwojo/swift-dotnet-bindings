# WeatherKit — Binding Audit

- **Package**: SwiftBindings.Apple.WeatherKit v26.2.8   **Mode**: apple   **TFM(s)**: net10.0-ios26.2, net10.0-macos26.2, net10.0-tvos26.2, net10.0-watchos26.2
- **Native**: Apple WeatherKit system framework (iOS 16+, macOS 13+, tvOS 16+, watchOS 9+)
- **Audited at**: main 1e8c27a, generated 2026-06-27T19:50:31Z

## Verdict

The binding is **fully usable for the core fetch-weather flow**: `WeatherService.Shared.WeatherAsync(location)` returns a `Task<Weather>` that bundles `CurrentWeather`, `Forecast<HourWeather>`, `Forecast<DayWeather>`, and `Forecast<MinuteWeather>?` — all critical observation properties (temperature, condition, wind, UV index, precipitation, pressure, dew point, humidity, visibility) surface correctly as `Swift.Foundation.Measurement<T>` or primitive types. All 54 types emit; real member gaps after removing the 73 intentional SynthesizedCodable exclusions are **45 skipped members** (not 118), concentrated in two categories: (1) the 6 individually-typed `weather<T>(for:including:)` generic overloads that allow fetching a single dataset without the full-bundle round-trip, and (2) all variadic-pack iOS 18+ historical statistics methods (`dailyStatistics`, `hourlyStatistics`, `monthlyStatistics`, `dailySummary`). Both are generator limitations on method-level generic-type callbacks and parameter packs, not correctness bugs. The biggest risk is naming inconsistency (`WeatherAsync` vs. `GetAttributionAsync`) and missing tests for weather data property reads — the async bridge fires, but actual response-parsing paths are unconfirmed at the test layer.

---

## 1. Coverage

### Types

| Metric | Value |
|---|---|
| Types emitted / total | 54 / 54 (100%) |
| Members emitted / total | 263 / 397 (66%) |
| Members synthesized | 416 (Codable, factory ctors) |
| Members skipped | 118 |

Emitted members by kind: Property 241, Method 16, Operator 4, Subscript 2.

### Skip-reason breakdown

| Reason | Count | Classification |
|---|---|---|
| SynthesizedCodable | 73 | (a) Correctly excluded — `encode(to:)` / `init(from:)` via unresolvable existential Encoder/Decoder; pruned by design |
| UnsupportedSignature | 24 | (b) Real gap — see below |
| AnyTypeFallback | 12 | (b) Real gap — see below |
| GenericTypeCallback | 6 | (b) Real gap — see below |
| UnsupportedType | 2 | Mixed — see below |
| UnsatisfiedGenericConstraint | 1 | (b) Real gap — see below |

**Real gap = 45 members** (118 − 73 SynthesizedCodable).

---

### (b) Real gaps in detail

#### UnsupportedSignature: 24 items

Split into two sub-buckets:

**A. Variadic-pack statistics methods (iOS 18+) — 12 items, high value for history/climate apps**

`WeatherService` declares `dailyStatistics`, `dailySummary`, `hourlyStatistics`, and `monthlyStatistics` using Swift's `repeat each T` parameter pack syntax (3 overloads each). The generator has no C# equivalent for parameter packs. Example native signature:

```swift
func dailyStatistics<each T>(for location: CLLocation, including dataSets: repeat WeatherKit.DailyWeatherStatisticsQuery<each T>) async throws -> (repeat DailyWeatherStatistics<each T>)
```

These are entirely absent from the binding. A consumer cannot query historical or statistical weather data at all. Workaround: custom Swift wrapper that takes a fixed set of queries (e.g., temperature + precipitation) and returns a concrete tuple — tractable but wide surface.

**B. WeatherService `weather<each T>` variadic pack overload (iOS 18+) — 1 item**

The variadic-pack form of `weather<each T>(for:including:)` hits the same limit. The six explicitly-typed generic overloads are covered separately below (GenericTypeCallback).

**C. `Trend`/`TrendBaseline`/`Forecast`/stats `==` operators on generic types — 9 items**

`Trend<TDimension>::==`, `TrendBaseline::==`, `Forecast::==` (×2), `HourlyWeatherStatistics::==`, `DailyWeatherStatistics::==`, `Percentiles::==`, `MonthlyWeatherStatistics::==`, `DailyWeatherSummary::==`. These all fail because the operator is on a generic type that requires buffer marshalling. Low consumer impact (equality comparisons on weather data are unusual); marking them missing is correct but not a daily-use gap.

**D. Simple-enum extension method type mismatch — 2 items**

`WeatherChange.Direction::encode` and `Deviation::encode` — Codable `encode(to:)` on simple enum extension methods hit an unsupported parameter type. These are Codable paths; excluded as intended.

**E. `UVIndex.ExposureCategory::rangeValue` — 1 item**

`rangeValue` returns `ClosedRange<Int>` — unsupported for simple enum extension. Low impact (the `ExposureCategory` enum itself is emitted; range is a convenience). Worth a future Swift wrapper.

---

#### AnyTypeFallback: 12 items

**High-value gap: `Trend<TDimension>` missing `.baseline` and `.currentValue` — 2 items (WeatherKit.cs:4974–4975)**

`Trend<TDimension>` is a generic type parameterized by `NSDimension` subclass (e.g., `NSUnitTemperature`, `NSUnitSpeed`). It exposes three properties: `deviation` (emitted, type `Deviation` enum), `baseline` (skipped — type `TrendBaseline<Swift.AnyType>`, cannot project), and `currentValue` (skipped — type `Measurement<Swift.AnyType>`, cannot project). A consumer who gets a `Trend<NSUnitTemperature>` from a statistics query can read the direction (`MuchHigher`/`Higher`/…) but not the actual measurement value or its baseline. This renders `Trend<T>` only partially useful.

**Statistics subscripts: 3 items**

`HourlyWeatherStatistics[key]`, `DailyWeatherStatistics[key]`, and `MonthlyWeatherStatistics[key]` subscripts return AnyType. Since the statistics methods themselves are skipped (variadic pack), these are moot for now.

**`Percentiles.p10 / p50 / p90`: 3 items**

These properties on the `Percentiles` struct return `Measurement<AnyType>`. With the hosting statistics methods also skipped, secondary gap.

**`TrendBaseline.kind` and `TrendBaseline.value`: 2 items**

`TrendBaseline<TDimension>` suffers the same open-generic projection failure. `kind` (an associated `Kind` enum) and `value` (a `Measurement`) are both skipped.

**`Forecast[Int]` subscript: 1 item**

The raw subscript on `Forecast<T>` itself is skipped as AnyType. However, `Forecast<T>` is emitted as `IReadOnlyList<TElement>` with a strongly-typed indexer `this[int]` returning `TElement` (WeatherKit.cs:19433), so this is **not a real gap** — the typed indexer via `IReadOnlyList<T>` is fully usable.

---

#### GenericTypeCallback: 6 items — primary fetch-path gap

These are the six explicitly-typed generic overloads of `WeatherService.weather(for:including:)`:

```swift
func weather<T>(for: CLLocation, including: WeatherQuery<T>) async throws -> T
func weather<T1, T2>(for: CLLocation, including: WeatherQuery<T1>, _ WeatherQuery<T2>) async throws -> (T1, T2)
// … up to T1..T6
```

All 6 fail with "Async callback references method-own generic type parameters." None is emitted. The binding provides only the full-bundle overload:

```csharp
// WeatherKit.cs:23220
public Task<WeatherKit.Weather> WeatherAsync(CoreLocation.CLLocation location, ...)
```

`Weather` bundles `CurrentWeather`, `Forecast<MinuteWeather>?`, `Forecast<HourWeather>`, and `Forecast<DayWeather>` (WeatherKit.cs:16636–16881). For most use cases this is **sufficient** — the consumer fetches everything and reads what they need. The missing overloads matter for network-efficiency (fetching only current conditions without the hourly/daily payload), not for functional completeness.

A Swift wrapper approach (pre-specialize each query: emit `GetCurrentWeatherAsync`, `GetHourlyForecastAsync`, `GetDailyForecastAsync` as concrete non-generic `@_cdecl` wrappers) would close this gap. Effort: medium; value: medium (performance, API discoverability).

---

#### UnsupportedType: 2 items

- `UVIndex.ExposureCategory::<` — `Comparable` operator on simple enum not supported; low impact.
- `Forecast::summary` — constrained-extension property `Forecast<DayWeather>.summary` is suppressed on the open generic class; the report notes it is "emitted as a closed-generic extension method via ConstrainedExtensionEmitter." Verify that the `Forecast<DayWeather>` extension class (`ForecastWeatherKit_DayWeatherCsmExtensions`, WeatherKit.cs:19877) actually exposes a `Summary` — no `Summary` property found in that class. **This is a real gap**: the iOS API's `Forecast<DayWeather>.summary` string (a human-readable one-sentence weather summary) is not reachable from C#.

#### UnsatisfiedGenericConstraint: 1 item

`HourTemperatureStatistics::percentiles` — bound generic `Percentiles<Measurement<UnitTemperature>>` cannot satisfy C# `ISwiftObject` constraint. Secondary gap (part of the statistics path).

---

### Prioritized generator unlocks

| # | Gap | Mechanism | Value | Effort |
|---|---|---|---|---|
| 1 | `weather<T>(for:including:)` single-dataset overloads | Emit concrete Swift `@_cdecl` wrappers for `CurrentWeather`, `HourlyForecast`, `DailyForecast`, `MinuteForecast` queries | High — API discoverability + network efficiency | Medium |
| 2 | `dailyStatistics` / `hourlyStatistics` / `monthlyStatistics` (variadic pack) | Swift wrappers with fixed concrete query sets (temperature + precipitation are the 90% case) | High — entire history/climate API is dark | High |
| 3 | `Trend<TDimension>.baseline` / `.currentValue` (AnyTypeFallback on generic property) | Specialized concrete projected properties when TDimension is a known NSUnit subclass | Medium — Trend struct emits but half of its data is dark | Medium |
| 4 | `Forecast<DayWeather>.summary` (ConstrainedExtension drop) | Confirm whether extension-method path actually emits it; if not, add to the CsmExtension class | Low-Medium | Low |
| 5 | `UVIndex.ExposureCategory.rangeValue` (ClosedRange<Int> return) | Swift wrapper returning (Int, Int) | Low | Low |

---

## 2. C# Quality

### Naming and shape

**Good**: All 54 types follow PascalCase. No leaked Swift mangling. Enums (`WeatherError`, `MoonPhase`, `Deviation`, `UVIndex.ExposureCategory`, `Wind.CompassDirectionType`) map cleanly with correct int tags. Sum-type enums (`WeatherCondition`, `Precipitation`, `PressureTrend`, `WeatherSeverity`, `WeatherAvailability.AvailabilityKind`) emit as singleton objects with `CaseTag` discriminators and `AllCases` — the established pattern.

**Naming inconsistency** (`WeatherKit.cs:22875` vs `23220`): `GetAttributionAsync` follows the `Get` prefix convention; `WeatherAsync` does not. A consumer would expect `GetWeatherAsync`. This is a minor quality issue — searchable by IntelliSense but off-idiom.

### Async

`WeatherAsync` and `GetAttributionAsync` both surface as proper `Task<T>` with `CancellationToken` support (WeatherKit.cs:22875, 23220). The underlying async bridge (callback/task machinery at WeatherKit.cs:22747–22930) is the standard pattern; cancellation properly wires `SBW_CancelTask` + `SBW_UnregisterTask`.

### Measurement<T> ergonomics

All temperature, speed, length, pressure, angle properties surface as `Swift.Foundation.Measurement<Foundation.NSUnitXxx>` (e.g., `CurrentWeather.Temperature: Swift.Foundation.Measurement<Foundation.NSUnitTemperature>`, WeatherKit.cs:26725). Consumers must know the `Swift.Foundation.Measurement<T>` API from the `SwiftBindings.Apple` package (`.Value: double` returns the raw value in the Swift-native unit; `.Unit` returns the `NSUnit` subclass). This is a cross-package ergonomics concern — the binding is correct but the consumer must understand that `Temperature.Value` gives Celsius (Swift's native unit for `NSUnitTemperature`) and would need to call Foundation measurement conversion for Fahrenheit. The binding cannot improve this without inventing a new API surface; it is correct as-is. **A getting-started note in the package README would help**.

### Nullability

- `Weather.MinuteForecast` correctly surfaces as `Forecast<MinuteWeather>?` (WeatherKit.cs:16642–16720) — nullable since minute-precision data is not always available.
- `DayWeather.HighTemperatureTime` / `LowTemperatureTime` correctly use `DateTimeOffset?` with CFAbsoluteTime round-trip (WeatherKit.cs:2336–2339).
- `DayWeather.RestOfDayForecast` correctly nullable (WeatherKit.cs:3991).
- `WeatherAlert.Region` correctly `string?` (WeatherKit.cs:28847).
- `Wind.Gust` correctly `Measurement<NSUnitSpeed>?` (WeatherKit.cs:22106).

No missing or contradictory nullable annotations found.

### Lifetime / IDisposable

All struct wrappers implement `IDisposable` and `ISwiftStruct` consistently. `WeatherService` (a Swift class) implements `IDisposable` via `SwiftClassHandle<WeatherService>` (WeatherKit.cs:22934). All property accessors guard against payload release with `DangerousAddRef`/`DangerousRelease` in try/finally. No obvious lifetime smells found.

### DateTimeOffset bridging

`CurrentWeather.Date` and all timestamp properties bridge Swift `Date` (CFAbsoluteTime, seconds since 2001-01-01) to `DateTimeOffset` via `AddSeconds` / `TotalSeconds` (WeatherKit.cs:25886–25887). The epoch constant is inline (not from a shared helper), which is a minor duplication but matches the existing project pattern.

### Trend<TDimension> partially usable

`Trend<TDimension>` (WeatherKit.cs:4972) exposes only `Deviation` (enum) — the `.baseline` and `.currentValue` slots are commented out as unsupported. For the historical-comparison use case (is this week's temperature trend higher than average?) the binding returns the direction word but not the numeric magnitude. Flag to consumers.

---

## 3. Test Coverage

**Test file**: `tests/Tests.cs` (single file, driven from UIKit `ViewDidLoad` on iOS, from `Main` on macOS)

**Test count**: 26 named cases (Tests 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25a, 25b, 26).

### Depth assessment

| Test(s) | Type | Depth |
|---|---|---|
| 1: `WeatherService.Shared` non-null | Real call | **Strong** — proves the singleton accessor + native handle round-trip |
| 2–7: Metadata load (`WeatherService`, `WeatherAttribution`, `WeatherMetadata`, `CurrentWeather`, `DayWeather`, `HourWeather`) | Metadata pointer | **Weak** — proves type metadata loads but not any member accessor |
| 8–12: Enum values + raw value + `GetDescription` | Round-trip calls | **Strong** — proves enum P/Invokes and string marshalling |
| 13–16: `Deviation`, `WeatherCondition`, `WeatherCondition.AllCases`, `Precipitation` singletons + CaseTags | Real calls | **Strong** — proves sum-type enum discriminators |
| 17–20: `PressureTrend`, `WeatherSeverity`, `WeatherAvailability.AvailabilityKind` singletons | Real calls | **Strong** |
| 21–22: `UVIndex.ExposureCategory` values + `GetDescription` | Round-trip | **Strong** |
| 23–24: `Wind.CompassDirectionType` AllCases count + `GetDescription` | Round-trip | **Strong** |
| 25a–25b: `Forecast<HourWeather>` / `Forecast<DayWeather>` IReadOnlyList reflection | Reflection only | **Weak** — proves type-system projection but zero runtime Forecast handling |
| 26: `GetAttributionAsync` dispatch + error observable | Async bridge | **Strong** — proves async bridge fires and error propagates |

### Untested surface (significant gaps)

| Surface | Risk | Recommended test |
|---|---|---|
| `WeatherAsync(CLLocation)` response parsing | **High** — the primary API is never round-tripped. The full `Weather` struct (condition, temperature, forecast sub-properties) could be mismarshalled and no test would catch it. | Use `Weather.DecodeFromJson(sampleJson)` (emitted at WeatherKit.cs:17255) with a canned JSON blob to prove all core property reads without an API key. |
| `CurrentWeather` properties (Temperature, Condition, DewPoint, Humidity, Wind, UVIndex, PrecipitationIntensity, Pressure) | **High** — property accessors for the most-used type are never exercised | Add `var cw = Weather.DecodeFromJson(blob).CurrentWeather; assert cw.Temperature.Value is double` |
| `Forecast<DayWeather>` iteration | Medium — IReadOnlyList projection tested by reflection but `Count`, indexer, and `GetEnumerator` never called on a real instance | `Forecast.DecodeFromJson` + `foreach` loop + property reads on `DayWeather.HighTemperature` |
| `Forecast<HourWeather>` iteration + `HourWeather` properties | Medium | Same pattern |
| `UVIndex` struct properties (`.Value`, `.Category`) | Medium — emitted, untested | Attach to `CurrentWeather.UvIndex` from decoded JSON |
| `Wind` struct (Direction, Speed, Gust) | Medium | Same |
| `WeatherAlert` (Source, Summary, Severity) | Low-Medium — only users in alert-prone regions, but `Severity` P/Invoke + nullable `Region` is untested | `WeatherAlert.DecodeFromJson(blob)` |
| `WeatherAvailability` (MinuteAvailability, AlertAvailability properties) | Low | Decode from JSON |
| `Trend<TDimension>` + `Deviation` read | Low (requires stats flow) | N/A until stats methods are emitted |

### Legitimate skips

No `Skip()` calls exist in the test file. The Forecast iteration test (25a/25b) is consciously limited to reflection because constructing a real `Forecast<T>` requires a live API key — this is documented inline and is acceptable. The `WeatherAsync` live-data test would have the same constraint; the recommended workaround (JSON decode) avoids it.

---

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Quality | `WeatherAsync` should be `GetWeatherAsync` to match `GetAttributionAsync` naming (WeatherKit.cs:23220) | Rename at generation time via Swift async method name mapping | Low | Low |
| 2 | Coverage | `weather<T>(for:including:)` 6 generic overloads all skipped as GenericTypeCallback | Emit concrete Swift `@_cdecl` wrappers for the most common query types (`CurrentWeather`, `HourlyForecast`, `DailyForecast`) to restore the network-efficient single-dataset fetch path | Medium | High |
| 3 | Coverage | All iOS 18+ statistical methods (`dailyStatistics`, `hourlyStatistics`, `monthlyStatistics`, `dailySummary`) dark due to variadic pack | Swift wrappers with fixed concrete query combinations (start with temperature, precipitation as the 90% case) | High | High |
| 4 | Coverage | `Trend<TDimension>.baseline` / `.currentValue` both AnyTypeFallback; `Trend` is half-dark (WeatherKit.cs:4974–4975) | Specialized projection for known `NSDimension` subclass arguments when binding-time TDimension is resolvable | Medium | Medium |
| 5 | Coverage | `Forecast<DayWeather>.summary` may be silently dropped despite ConstrainedExtensionEmitter note | Verify `ForecastWeatherKit_DayWeatherCsmExtensions` (WeatherKit.cs:19877) — if `Summary` is absent, add it | Low | Low-Medium |
| 6 | Tests | All `CurrentWeather` property accessors and `Weather` aggregate struct reads are untested | Add `Weather.DecodeFromJson(blob)` + property assertion tests covering Temperature, Condition, Humidity, Wind, UVIndex | Low | High |
| 7 | Tests | `Forecast<T>` iteration (Count, indexer, `foreach`) never executed at runtime | Add JSON-decoded `Forecast<DayWeather>` + `HourlyForecast` iteration + `DayWeather.HighTemperature` read | Low | High |
| 8 | Tests | `WeatherAsync` async bridge not smoke-tested (only `GetAttributionAsync` is) | Add a `WeatherAsync` test with the same "dispatch fires, accept error" pattern using a mock CLLocation | Low | Medium |
