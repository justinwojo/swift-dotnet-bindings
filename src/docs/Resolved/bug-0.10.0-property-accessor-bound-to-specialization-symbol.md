# Bug: Property accessor on a generic type binds to a specialization-specific Swift symbol without gating the C# property on the matching constraint

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during the
> WeatherKit + MusicKit cross-package consumer-experience audit (Round 5).
> See
> [`audit-weatherkit-musickit-2026-05-05.md`](../../swift-dotnet-packages/audit-weatherkit-musickit-2026-05-05.md)
> finding **O-7**.

## Summary

Swift extends `Forecast<TElement>` with `var summary: String { get }`
**only when `TElement == MinuteWeather`**:

```swift
extension Forecast where Element == MinuteWeather {
    public var summary: String { get }
}
```

The C# binding emits the property unconditionally on the open generic
`Forecast<TElement>` and binds the getter PInvoke to the Swift
specialization-specific mangled symbol
`$s10WeatherKit8ForecastVA2A06MinuteA0VRszrlE7summarySSvg`. Calling
`forecast.Summary` on `Forecast<HourWeather>` or `Forecast<DayWeather>`
dispatches a function whose generic preconditions don't hold.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.1
- .NET SDK 10.0.107
- Xcode 26.3 / Swift 6.2.x / iPhoneOS26.2.sdk
- macOS 26.x, arm64

## Repro

```bash
sed -n '14037,14071p' apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs
```

```csharp
// WeatherKit.cs:14040
public string Summary
{
    get
    {
        IntPtr cdeclBuf = NativeMemory.Alloc(StringSwiftSize);
        try
        {
            PInvoke_summary_getter(cdeclBuf, this.Payload.DangerousGetHandle());
            return MarshalFromSwift<string>(cdeclBuf);
        }
        finally
        {
            VWT.String.Destroy(cdeclBuf);
            NativeMemory.Free(cdeclBuf);
        }
    }
}

[LibraryImport(
    "@rpath/WeatherKitSwiftBindings.framework/WeatherKitSwiftBindings",
    EntryPoint = "$s10WeatherKit8ForecastVA2A06MinuteA0VRszrlE7summarySSvg")]
[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
internal static partial void PInvoke_summary_getter(
    SwiftIndirectResult result, IntPtr self);
```

The mangled name decodes (`A2A06MinuteA0VRszrl`) as
`Forecast<MinuteWeather>` — i.e., the symbol is for the
specialization where `Element == MinuteWeather`. Calling it on
`Forecast<HourWeather>` would reach a function whose body assumes
`self.Element` is layout-compatible with `MinuteWeather` (different
size/layout from `HourWeather`/`DayWeather`).

C# offers no compile-time signal that `Summary` is invalid on
`Forecast<HourWeather>`.

## Hypothesis

The emit pipeline saw the swiftinterface declaration `extension
Forecast where Element == MinuteWeather { public var summary: String
{ get } }` and lowered it to:

- A property `Summary` on the open generic `Forecast<TElement>`.
- A `[LibraryImport]` PInvoke whose `EntryPoint` is the
  specialization-specific mangled name.

The constraint `Element == MinuteWeather` was not threaded into the
C# property's declaration. Two structurally clean fixes:

- **Constrain the C# property:** emit it on a closed generic
  `Forecast<MinuteWeather>` extension class, or on a separate
  `MinuteWeatherForecast` type alias. Disambiguates by type.
- **Constrain the C# type parameter:** emit `Summary` only when
  `TElement` matches `MinuteWeather`. C# can't express
  `where TElement == MinuteWeather` directly; closed-generic
  extension is the cleaner path.

Either prevents the consumer from typing the call wrong.

## Affected sites

- `WeatherKit.cs:14037-14071` — `Forecast<TElement>.Summary`
  (specialization: `MinuteWeather`)

Cross-cutting risk: any Swift `extension Foo where T == ConcreteType`
declaration is at risk of the same defect. Worth a generator-side
audit pass to enumerate every PInvoke targeting a specialization-
specific mangled name and verify the C# wrapper gates the call site
on the matching constraint.

## Impact

Calling `Forecast<HourWeather>.Summary` (the natural typo / "I want
the daily summary") invokes a function whose body assumes
`MinuteWeather` layout. Possible outcomes:

- Reads garbage `String` payload from a wrong-layout self.
- Crashes in the function body when accessing `self.fields[k]` for k
  out of range for HourWeather's struct layout.
- Returns a String that happens to look plausible but reflects the
  bytes of some other field — silent corruption.

WeatherKit's primary forecast fetch returns `Forecast<DayWeather>` and
`Forecast<HourWeather>` (not `Forecast<MinuteWeather>` — that's its
own `WeatherService.minuteForecast` entry point). So a consumer
loading the daily forecast and calling `.Summary` on it hits this on
exactly the most common code path.

## Severity

**High.** Type system tells the consumer the property exists; runtime
disagrees. Silent or crashing corruption depending on layout overlap.

## Fix gate

`WeatherKit.cs:14037-14071` should either:

- Move `Summary` to a `Forecast<MinuteWeather>` extension class /
  closed-generic projection, or
- Drop the property on the open generic if the specialization can't be
  expressed in C#'s type system.

A test that constructs `forecast = WeatherService.Shared.WeatherAsync
.Result.HourlyForecast` and tries to call `forecast.Summary` should
either fail to compile or surface as a typed error — not call into a
wrongly-specialized function.

Generator audit gate: every `[LibraryImport(EntryPoint = "$s…")]`
where the mangled name encodes a specialization (look for `…RszrlE…`
or `…RtzlE…` runs) should have a matching constraint on the C# type
or method declaration.
