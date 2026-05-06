# Bug: `Equals(other)` and nullable-struct setter PInvokes skip `DangerousAddRef` — GC finalization can free Swift heap mid-call

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during the
> WeatherKit + MusicKit cross-package consumer-experience audit (Round 5).
> See
> [`audit-weatherkit-musickit-2026-05-05.md`](../../swift-dotnet-packages/audit-weatherkit-musickit-2026-05-05.md)
> finding **O-9**.

## Summary

Property *getters* on Swift-projected types correctly wrap
`DangerousAddRef`/`DangerousRelease` around their PInvoke to prevent
GC finalization of the SafeHandle while Swift is reading from it. Two
adjacent code paths skip this protection:

- **`Equals(T?)` overloads** — the typed `IEquatable<T>.Equals`
  implementation calls `PInvoke_eq(thisPayload, otherPayload)` with
  raw `DangerousGetHandle()` calls and no AddRef bracket on either
  side.
- **Nullable-struct property setters** — most setters that accept
  `Foundation.Measurement<…>?` or other nullable struct payloads call
  the Swift setter PInvoke without an AddRef on the incoming `value`'s
  SafeHandle (or on `this`).

If the GC finalizes either side's payload while the witness is reading
or writing — possible during the GC-suspend-resume window between the
managed handle access and the Swift function entry — the call
dereferences a freed Swift heap object.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.1
- .NET SDK 10.0.107
- Xcode 26.3 / Swift 6.2.x / iPhoneOS26.2.sdk
- macOS 26.x, arm64

## Repro — `Equals(T?)`

```bash
sed -n '608,620p' apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs
```

```csharp
// WeatherKit.cs:608  (WeatherAttribution.Equals)
public bool Equals(WeatherAttribution? other)
{
    if (other is null) return false;
    return PInvoke_eq(
        this.Payload.DangerousGetHandle(),    // [1] no AddRef
        other.Payload.DangerousGetHandle());  // [2] no AddRef
}
```

Compare to a property getter on the same type, which threads
AddRef/Release correctly:

```csharp
// (typical getter shape)
public string Description
{
    get
    {
        bool addedRef = false;
        try
        {
            this.Payload.DangerousAddRef(ref addedRef);
            return PInvoke_description(this.Payload.DangerousGetHandle());
        }
        finally
        {
            if (addedRef) this.Payload.DangerousRelease();
        }
    }
}
```

The asymmetry is the bug.

## Repro — nullable-struct setter

```bash
sed -n '15981,15995p' apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs
```

```csharp
// WeatherKit.cs:15983  (Wind.Gust setter, nullable Measurement)
public Foundation.Measurement<UnitSpeed>? Gust
{
    set
    {
        IntPtr cdeclBuf = NativeMemory.Alloc(MeasurementSize);
        try
        {
            if (value is null) PInvoke_gust_set_nullable(cdeclBuf, this.Payload.DangerousGetHandle(), 0, IntPtr.Zero);
            else
            {
                // [3] value.Payload.DangerousGetHandle() called without AddRef
                PInvoke_gust_set_nullable(cdeclBuf, this.Payload.DangerousGetHandle(), 1, value.Payload.DangerousGetHandle());
            }
        }
        finally { NativeMemory.Free(cdeclBuf); }
    }
}
```

`value` is typically a transient (`wind.Gust = new Measurement<UnitSpeed>(5,
.metersPerSecond)`); if the GC finalizes that transient between line
construction and the PInvoke call (possible during the cross-AOT
boundary on slow paths), the Swift setter dereferences freed memory.

## Affected sites

WeatherKit Equals(T?) overloads:

- WeatherKit.cs:608-615 — `WeatherAttribution.Equals`
- WeatherKit.cs:5429-5430 — `CurrentWeather.Equals`
- WeatherKit.cs:12259-12260 — `MinuteWeather.Equals`
- WeatherKit.cs:13877-13878 — `Precipitation.Equals`
- WeatherKit.cs:14116-14123 — `Forecast<MinuteWeather>.Equals`
- …every `Equals(T?)` overload across the module (~31 sites,
  matching the Family B B-1 `GetHashCode`-stub site count)

WeatherKit nullable-struct setters:

- WeatherKit.cs:15981-15990 — `Wind.Gust` (nullable `Measurement<UnitSpeed>`)
- ~24 more across `CurrentWeather`/`HourWeather`/`DayWeather` for
  optional measurement properties (`HourlyHeatIndex`,
  `Precipitation`, `Visibility`, `Pressure`, `UVIndex.Value`, etc.)

Cross-cutting risk: any SDK-emitted `IEquatable<T>.Equals` PInvoke
and any nullable-struct setter PInvoke. Likely affects every
binding the SDK has shipped. WeatherKit is a useful audit target
because it has both clusters in the same module.

## Hypothesis

The PInvoke-emit pipeline has separate code paths for "property
getter" vs. "operator/method" vs. "setter," and only the property-
getter path threads the GC-pinning bracket. Look for the emit code
that wraps a PInvoke call with `DangerousAddRef`/`DangerousRelease`
— it's almost certainly only invoked from one of three sibling
methods. The fix is to factor the bracket emission into a helper
shared across all three paths.

## Impact

Risk is low under "compare two locals + immediately use the result"
usage but real for:

- **Collections.** `HashSet<T>.Contains` and `Dictionary<T, V>` lookups
  call `Equals` after computing `GetHashCode`; the hash bucket walk
  may insert a finalization point (especially under concurrent GC).
- **Parallel comparators.** `Enumerable.Distinct(comparer)` /
  `OrderBy` invocations across many threads can finalize transients
  between equality checks.
- **Setter chains.** `wind.Gust = wind.Direction.Equals(…) ?
  newGust : null;` constructs the right-hand side as a transient,
  passes it through a setter, and may finalize the transient before
  the setter completes.

Hard to reproduce in tight reproducer code (the GC has to schedule
finalization at exactly the wrong moment) but architecturally
present on every call. Production-scale workloads will hit it.

## Severity

**Medium.** Real ARC-side correctness defect; conditions for
manifestation are real but require GC scheduling to land exactly
wrong. Should be folded into the same SDK fix unit as **O-4**
([`bug-0.10.0-deferredsafehandlerelease-refcount-underflow.md`](bug-0.10.0-deferredsafehandlerelease-refcount-underflow.md))
since they're the same family of "SafeHandle lifecycle around
PInvoke."

## Fix gate

`WeatherKit.cs:608-615` and adjacent `Equals(T?)` overloads should
wrap `DangerousAddRef`/`DangerousRelease` around `this` and `other`
before invoking `PInvoke_eq`. Same for nullable-struct setters
(`WeatherKit.cs:15981-15990`).

A test that calls `wind.Equals(otherWind)` in a tight loop with
`GC.Collect()` interspersed (or a `WeakReference` finalization
trigger) should not crash. Today it does occasionally.

A generator-side audit pass: every PInvoke call site that takes
`SafeHandle.DangerousGetHandle()` should be inside a
`DangerousAddRef`/`DangerousRelease` bracket. Currently only
property getters satisfy this; should be uniform.
