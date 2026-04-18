// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.EdgeCases;

/// <summary>
/// Runtime coverage for fixes #1, #2, and #12: availability propagation from
/// Swift accessors, per-enum-case availability, and @_silgen_name extension
/// wrappers on @available-gated extensions. All three were exercised only
/// indirectly by the WeatherKit/AuthenticationServices compile gates before
/// this fixture existed — the WeatherKit smoke test pins fix #1 reflectively
/// against the real WeatherKit snapshot, but the synthetic path was missing.
///
/// The test shape mirrors WeatherKitSmokeTests.TestDayWeatherHighTemperatureTimeIos18Availability
/// so a regression is diagnosable identically to the smoke path: we reach
/// into the generated C# type with reflection and check for the specific
/// <see cref="SupportedOSPlatformAttribute"/> that fix #1/#2 must emit.
/// We do not actually call the iOS 18 APIs at runtime — the simulator
/// floor is iOS 16 in CI, and the compile-clean assertion is what matters.
/// </summary>
public class AvailabilityPropagationTests : TestBase
{
    public AvailabilityPropagationTests(TestResults results) : base(results) { }

    /// <summary>
    /// Fix #1: pins the accessor-level [SupportedOSPlatform("ios18.0")]
    /// propagation on <c>VersionedContainer.FuturePayload</c>. The enclosing
    /// type <c>VersionedContainer</c> is <c>@available(iOS 16, *)</c> and
    /// the <c>futurePayload</c> accessor is <c>@available(iOS 18, *)</c>.
    /// Without fix #1 the emitted property inherits only the type's ios16
    /// floor and the iOS 16 consumer compiles the call with no CA1416.
    /// </summary>
    public void TestVersionedContainerFuturePayloadIos18Availability()
    {
        var containerType = typeof(VersionedContainer);
        var prop = containerType.GetProperty(
            "FuturePayload",
            BindingFlags.Instance | BindingFlags.Public);
        AssertTrue(prop is not null,
            "VersionedContainer.FuturePayload property must exist on the generated binding. " +
            "If this assertion fails, fix #1 has regressed and the property was dropped " +
            "entirely instead of emitted with the tighter availability.");

        var attrs = prop!.GetCustomAttributes<SupportedOSPlatformAttribute>(inherit: false).ToArray();
        TestLogger.Info($"VersionedContainer.FuturePayload SupportedOSPlatform attrs: " +
            $"[{string.Join(", ", attrs.Select(a => a.PlatformName))}]");

        var ios18 = attrs.FirstOrDefault(a =>
            string.Equals(a.PlatformName, "ios18.0", StringComparison.OrdinalIgnoreCase));
        AssertTrue(ios18 is not null,
            "VersionedContainer.FuturePayload must carry SupportedOSPlatform(\"ios18.0\"). " +
            "Fix #1 (b51d2ff6) propagates the accessor-level @available(iOS 18, *) to the " +
            "emitted C# property. If this assertion fails, the generator has regressed and " +
            "iOS 16 consumers can call an iOS 18 API without a CA1416 diagnostic.");
    }

    /// <summary>
    /// Per-case <c>@available</c> propagation on simple integer-raw-value enums.
    /// The enclosing <c>StagedFeature</c> enum is <c>@available(iOS 16, *)</c>;
    /// <c>.enhanced</c> (iOS 17) and <c>.experimental</c> (iOS 18) have tighter
    /// per-case floors. The emitted C# enum fields must carry matching
    /// <c>[SupportedOSPlatform]</c> attributes so consumers get CA1416
    /// diagnostics when referencing platform-gated cases.
    /// </summary>
    public void TestStagedFeaturePerCaseAvailability()
    {
        var enumType = typeof(StagedFeature);

        var enhancedField = enumType.GetField("Enhanced", BindingFlags.Public | BindingFlags.Static);
        AssertTrue(enhancedField is not null, "StagedFeature.Enhanced field must exist.");
        var enhancedAttrs = enhancedField!.GetCustomAttributes<SupportedOSPlatformAttribute>(inherit: false).ToArray();
        TestLogger.Info($"StagedFeature.Enhanced SupportedOSPlatform attrs: " +
            $"[{string.Join(", ", enhancedAttrs.Select(a => a.PlatformName))}]");
        var ios17 = enhancedAttrs.FirstOrDefault(a =>
            string.Equals(a.PlatformName, "ios17.0", StringComparison.OrdinalIgnoreCase));
        AssertTrue(ios17 is not null,
            "StagedFeature.Enhanced must carry SupportedOSPlatform(\"ios17.0\"). " +
            "EnumHandler.SimpleEnum emits per-case availability via " +
            "AvailabilityAttributeEmitter. A failure here means the per-case " +
            "@available propagation has regressed.");

        var experimentalField = enumType.GetField("Experimental", BindingFlags.Public | BindingFlags.Static);
        AssertTrue(experimentalField is not null, "StagedFeature.Experimental field must exist.");
        var experimentalAttrs = experimentalField!.GetCustomAttributes<SupportedOSPlatformAttribute>(inherit: false).ToArray();
        TestLogger.Info($"StagedFeature.Experimental SupportedOSPlatform attrs: " +
            $"[{string.Join(", ", experimentalAttrs.Select(a => a.PlatformName))}]");
        var ios18 = experimentalAttrs.FirstOrDefault(a =>
            string.Equals(a.PlatformName, "ios18.0", StringComparison.OrdinalIgnoreCase));
        AssertTrue(ios18 is not null,
            "StagedFeature.Experimental must carry SupportedOSPlatform(\"ios18.0\"). " +
            "A failure here means the per-case @available propagation has regressed.");
    }

    /// <summary>
    /// Fix #2 runtime half: call the Swift free function that reads a
    /// StagedFeature raw value at the iOS 16 baseline to confirm the
    /// enclosing enum's emission is intact and the simple cases still
    /// round-trip even though per-case availability exists. The test's
    /// <c>[SupportedOSPlatform]</c> localized suppression is deliberate —
    /// fix #1 is *working* here: the type-level attribute that reached
    /// <c>StagedFeature</c> raises CA1416 at this call site (the RuntimeTestsApp
    /// baseline is iOS 15). Suppressing is the observable proof that the
    /// attribute is landing and enforced.
    /// </summary>
    public void TestStagedFeatureRawValueAtIos16Baseline()
    {
#pragma warning disable CA1416
        var raw = TestLibFunctions.StagedFeatureRawValue(StagedFeature.Legacy);
#pragma warning restore CA1416
        TestLogger.Info($"StagedFeature.Legacy.rawValue = {raw}");
        AssertEqual(0, raw, "StagedFeature.Legacy must round-trip with raw value 0.");
    }

    /// <summary>
    /// Fix #12: pins the @_silgen_name extension wrapper's @available
    /// inheritance. <c>AvailabilityBase.FutureExtensionMethod</c> lives on
    /// an <c>@available(iOS 18, *)</c> extension of an iOS 16 type. The
    /// generated C# method must carry SupportedOSPlatform("ios18.0") or the
    /// iOS 16 consumer compiles a call that will crash at runtime when the
    /// extension's emit target isn't available. The Swift-side wrapper that
    /// fix #12 fixed is invisible here; the assertion on the C# attribute is
    /// the directly-observable consequence of that fix.
    /// </summary>
    public void TestAvailabilityBaseFutureExtensionMethodIos18Availability()
    {
        var baseType = typeof(AvailabilityBase);
        // Match by substring: the generator PascalCases and may prefix verbs
        // (`futureExtensionMethod` → `GetFutureExtensionMethod`). We do not
        // pin the exact emitted name — CLAUDE.md says assert behavior, not
        // the emitter's naming strategy — but the token "FutureExtensionMethod"
        // must survive in whatever form the generator lands on.
        var methods = baseType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name.Contains("FutureExtensionMethod"))
            .ToArray();
        TestLogger.Info($"AvailabilityBase methods matching 'FutureExtensionMethod': " +
            $"[{string.Join(", ", methods.Select(m => m.Name))}]");
        AssertTrue(methods.Length > 0,
            "AvailabilityBase must expose a method whose name contains 'FutureExtensionMethod'. " +
            "If missing, fix #12 has regressed and the @available-gated extension was " +
            "dropped entirely instead of emitted.");

        var method = methods[0];
        var attrs = method.GetCustomAttributes<SupportedOSPlatformAttribute>(inherit: false).ToArray();
        TestLogger.Info($"{method.Name} SupportedOSPlatform attrs: " +
            $"[{string.Join(", ", attrs.Select(a => a.PlatformName))}]");

        var ios18 = attrs.FirstOrDefault(a =>
            string.Equals(a.PlatformName, "ios18.0", StringComparison.OrdinalIgnoreCase));
        AssertTrue(ios18 is not null,
            "AvailabilityBase.FutureExtensionMethod must carry SupportedOSPlatform(\"ios18.0\"). " +
            "Fix #12 (26f764f1) propagates the extension's @available to the emitted Swift " +
            "wrapper AND the C# surface. A failure here means the extension's availability " +
            "floor was lost in emission.");
    }

}
