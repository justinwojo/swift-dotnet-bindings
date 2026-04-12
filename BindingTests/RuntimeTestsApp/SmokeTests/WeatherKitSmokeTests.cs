// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if WEATHERKIT_SMOKE
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using RuntimeTestsApp.Infrastructure;
using WeatherKit;

namespace RuntimeTestsApp.SmokeTests;

/// <summary>
/// Session 3 end-to-end smoke test for the Apple-framework direct-mode pipeline on
/// WeatherKit. Consumes the externally-built <c>WeatherKit.Swift.iOS.dll</c> +
/// <c>WeatherKitSwiftBindings.xcframework</c> from the gitignored in-tree snapshot at
/// <c>BindingTests/obj/WeatherKitSnapshot/</c> and exercises a hermetic, metadata-only
/// surface: <c>WeatherError.errorDescription</c> round-trip plus a reflection-based
/// assertion pinning the property-accessor <c>@available</c> propagation fix from
/// commit <c>b51d2ff6</c> (fix #1 in the session plan).
///
/// Gated by the <c>WEATHERKIT_SMOKE</c> compile symbol, which the csproj sets only
/// when every prerequisite (snapshot csproj, simulator wrapper slice, ProjectReference
/// targets file, iossimulator-arm64 RID, explicit <c>EnableWeatherKitSmoke=true</c>
/// opt-in) is satisfied. Regenerate the snapshot with
/// <c>nuke regenerate-apple-snapshot --framework WeatherKit</c>.
///
/// <b>Deliberately excluded:</b> Anything touching <c>WeatherKit.entitlement</c>,
/// any call to <c>WeatherService</c>, any API that fetches a <c>Weather</c>,
/// <c>Forecast</c>, or <c>DayWeather</c> value. Those paths require network access
/// and an entitlement that CI machines do not carry. The smoke test is strictly
/// metadata-only so it can run in any environment where the framework dylib is
/// reachable by dyld.
/// </summary>
public class WeatherKitSmokeTests : TestBase
{
    public WeatherKitSmokeTests(TestResults results) : base(results) { }

    /// <summary>
    /// Exercises the end-to-end direct-mode pipeline on <c>WeatherError</c>, a
    /// simple Int-backed enum conforming to <c>LocalizedError</c>:
    ///
    ///   1. <c>WeatherError.PermissionDenied</c> — a plain C# enum value, no
    ///      wrapper call yet.
    ///   2. <c>.GetErrorDescription()</c> — extension method generated from the
    ///      Swift <c>var errorDescription: String?</c> computed property on the
    ///      <c>LocalizedError</c> conformance. Routes through the wrapper thunk
    ///      <c>SBW_WeatherKit_WeatherError_get_errorDescription_FCF5721E</c>
    ///      which reads the localized description from the real WeatherKit
    ///      framework and returns it through a UTF-8 slice pointer.
    ///
    /// Assertion: the returned string is non-null and non-empty. We do NOT
    /// assert an exact value because WeatherKit's localized descriptions are
    /// sourced from Apple's localized strings tables and could change across
    /// Xcode / SDK versions — that kind of drift should flow through
    /// snapshot regen, not trip a smoke-test regression. A non-null / non-empty
    /// assertion is sufficient to prove the full pipeline is alive.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public void TestWeatherErrorErrorDescription()
    {
        try
        {
            var description = WeatherKit.WeatherError.PermissionDenied.GetErrorDescription();
            TestLogger.Info($"WeatherError.PermissionDenied.errorDescription = \"{description}\"");
            AssertTrue(description is not null,
                "WeatherError.PermissionDenied.errorDescription must be non-null — " +
                "LocalizedError conformance returns Optional<String> but WeatherKit ships a concrete value.");
            AssertTrue(!string.IsNullOrEmpty(description),
                "WeatherError.PermissionDenied.errorDescription must be non-empty.");
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// Pins the property-accessor <c>@available</c> propagation fix (commit
    /// <c>b51d2ff6</c>, fix #1 in the session plan) on a real Apple-framework
    /// snapshot. The fix propagates a Swift accessor's <c>@available</c>
    /// attribute to the emitted C# property getter / setter, so a consumer
    /// compiling against a lower <c>SupportedOSPlatformVersion</c> than the
    /// accessor requires sees the correct <c>SupportedOSPlatform</c> annotation
    /// rather than silently picking up the containing type's laxer attribute.
    ///
    /// WeatherKit's <c>DayWeather.highTemperatureTime</c> is the canonical
    /// shape: the enclosing <c>DayWeather</c> type is <c>ios16.0+</c>, but the
    /// property's accessor was backported from Apple Weather APIs in iOS 18 and
    /// is marked <c>@available(iOS 18.0, *)</c>. Without fix #1 the emitted
    /// C# property would inherit only <c>SupportedOSPlatform("ios16.0")</c>
    /// from the enclosing type, and a consumer compiling for iOS 16 would see
    /// no CA1416 warning on a property that crashes at runtime.
    ///
    /// We assert this via reflection instead of touching the property at
    /// runtime because constructing a <c>DayWeather</c> requires a
    /// <c>WeatherService.weather(for:)</c> call which is network-bound and
    /// entitlement-gated. The reflection assertion runs the exact same
    /// generated code the iOS 16 consumer would compile against and proves
    /// the attribute is present.
    /// </summary>
    public void TestDayWeatherHighTemperatureTimeIos18Availability()
    {
        try
        {
            var dayWeatherType = typeof(WeatherKit.DayWeather);
            var prop = dayWeatherType.GetProperty(
                "HighTemperatureTime",
                BindingFlags.Instance | BindingFlags.Public);
            AssertTrue(prop is not null,
                "DayWeather.HighTemperatureTime property must exist on the generated WeatherKit binding.");

            var attrs = prop!.GetCustomAttributes<SupportedOSPlatformAttribute>(inherit: false).ToArray();
            TestLogger.Info($"DayWeather.HighTemperatureTime SupportedOSPlatform attrs: " +
                $"[{string.Join(", ", attrs.Select(a => a.PlatformName))}]");

            var ios18 = attrs.FirstOrDefault(a =>
                string.Equals(a.PlatformName, "ios18.0", StringComparison.OrdinalIgnoreCase));
            AssertTrue(ios18 is not null,
                "DayWeather.HighTemperatureTime must carry SupportedOSPlatform(\"ios18.0\"). " +
                "Fix #1 (b51d2ff6) propagates the accessor-level @available(iOS 18, *) from " +
                "the Swift source to the emitted C# property. If this assertion fails, the " +
                "generator has regressed and iOS 16 consumers can call an iOS 18 API without " +
                "a CA1416 diagnostic.");
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// Dumps the full exception chain to <see cref="TestLogger"/> so reflection
    /// wrapping (<see cref="System.Reflection.TargetInvocationException"/>) does
    /// not obscure the real failure. Matches the error-logging shape used in
    /// <see cref="CryptoKitSmokeTests"/>.
    /// </summary>
    private static void LogExceptionChain(Exception ex)
    {
        var inner = ex;
        var depth = 0;
        while (inner != null)
        {
            TestLogger.Info($"  [ex{depth}] {inner.GetType().FullName}: {inner.Message}");
            if (inner.StackTrace != null)
                TestLogger.Info($"  [ex{depth}] stack: {inner.StackTrace}");
            inner = inner.InnerException;
            depth++;
        }
    }
}

#endif
