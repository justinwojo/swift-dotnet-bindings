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
        // pin the exact emitted name — we assert behavior, not the emitter's
        // naming strategy — but the token "FutureExtensionMethod"
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

    // ---------------------------------------------------------------------
    // Family-F sub-shapes — Layer A coverage of the spurious-Obsolete-on-recommended-overload
    // bug shapes. Synthetic fixtures live in BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/
    // AvailabilityFamilyF.swift.
    // ---------------------------------------------------------------------

    /// <summary>
    /// F-1. <c>OverloadDeprecationCarrier</c> has two
    /// <c>lookup(_:)</c> overloads; only the <c>String</c> variant is
    /// <c>@available(*, deprecated, ...)</c>. The lowered C# binding must
    /// place <c>[Obsolete]</c> on the deprecated overload only — the
    /// pre-fix emitter looked the deprecation up by printedName and
    /// broadcast it across the overload set, including onto the very
    /// overload the deprecation message recommended switching to.
    /// </summary>
    public void TestF1_DeprecationDoesNotBroadcastAcrossOverloads()
    {
        var carrierType = typeof(OverloadDeprecationCarrier);
        var lookupOverloads = carrierType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name.Equals("Lookup", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        TestLogger.Info($"OverloadDeprecationCarrier.Lookup overloads: {lookupOverloads.Length}");
        AssertTrue(lookupOverloads.Length == 2,
            $"Expected 2 OverloadDeprecationCarrier.Lookup overloads, found {lookupOverloads.Length}. " +
            "If this fires, the Swift fixture or emitter dropped one overload — F-1 cannot be tested.");

        var intOverload = lookupOverloads.FirstOrDefault(m =>
            m.GetParameters().Length == 1 &&
            m.GetParameters()[0].ParameterType == typeof(int));
        var stringOverload = lookupOverloads.FirstOrDefault(m =>
            m.GetParameters().Length == 1 &&
            m.GetParameters()[0].ParameterType == typeof(string));
        AssertTrue(intOverload is not null, "Lookup(int) overload must exist on the lowered binding.");
        AssertTrue(stringOverload is not null, "Lookup(string) overload must exist on the lowered binding.");

        var intObsolete = intOverload!.GetCustomAttributes<ObsoleteAttribute>(inherit: false).ToArray();
        var stringObsolete = stringOverload!.GetCustomAttributes<ObsoleteAttribute>(inherit: false).ToArray();
        TestLogger.Info($"Lookup(int) [Obsolete] count: {intObsolete.Length}");
        TestLogger.Info($"Lookup(string) [Obsolete] count: {stringObsolete.Length}");

        AssertTrue(intObsolete.Length == 0,
            "Lookup(int) is the recommended overload and must NOT carry [Obsolete]. " +
            "If this fires, F-1 has regressed and deprecation is being broadcast across " +
            "the overload set again. Consumers will see CS0618 on the API the deprecation " +
            "message tells them to switch to.");

        AssertTrue(stringObsolete.Length == 1,
            "Lookup(string) is the deprecated overload and must carry exactly one [Obsolete]. " +
            "If this fires, the per-overload disambiguation lost the deprecation entirely.");
    }

    // F-2 is covered by the unit test
    // `Availability_OnProtocolRequirementsWithoutAccessModifier_IsHarvested`
    // in SwiftSyntaxInterfaceFactsProducerTests.cs. A BindingTests fixture for F-2
    // is intentionally omitted — see the matching note in
    // BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/AvailabilityFamilyF.swift.

    /// <summary>
    /// F-3. <c>PlaybackTransport.progressAt(_:)</c> is the only
    /// enum case marked <c>@available(*, deprecated, ...)</c>. The lowered
    /// C# factory method (<c>PlaybackTransport.ProgressAt(double)</c> or
    /// whatever the emitter PascalCases it to) must carry
    /// <c>[Obsolete]</c>; the non-deprecated factory methods must not.
    /// </summary>
    public void TestF3_DeprecatedEnumCaseFactoryCarriesObsolete()
    {
        // PlaybackTransport lowers to a static factory class (one factory
        // per case). We look up the factory method by name-substring so a
        // naming-strategy change doesn't false-positive.
        var modeType = typeof(PlaybackTransport);
        var factories = modeType.GetMethods(BindingFlags.Public | BindingFlags.Static).ToArray();
        TestLogger.Info($"PlaybackTransport static factories: [{string.Join(", ", factories.Select(m => m.Name))}]");

        var progressFactory = factories.FirstOrDefault(m => m.Name.Contains("ProgressAt"));
        var frameFactory = factories.FirstOrDefault(m => m.Name.Contains("FrameAt"));
        AssertTrue(progressFactory is not null,
            "PlaybackTransport must expose a ProgressAt factory method. " +
            "If this fires, the per-case lowering dropped the case — F-3 cannot be tested.");
        AssertTrue(frameFactory is not null,
            "PlaybackTransport must expose a FrameAt factory method. " +
            "If this fires, the per-case lowering dropped the case — F-3 cannot be tested.");

        var progressObsolete = progressFactory!.GetCustomAttributes<ObsoleteAttribute>(inherit: false).ToArray();
        var frameObsolete = frameFactory!.GetCustomAttributes<ObsoleteAttribute>(inherit: false).ToArray();
        TestLogger.Info($"ProgressAt [Obsolete] count: {progressObsolete.Length}");
        TestLogger.Info($"FrameAt [Obsolete] count: {frameObsolete.Length}");

        AssertTrue(progressObsolete.Length == 1,
            "PlaybackTransport.ProgressAt factory must carry exactly one [Obsolete]. " +
            "If this fires, F-3 has regressed: deprecation on enum cases is no " +
            "longer flowing through to the lowered C# factory method.");
        AssertTrue(frameObsolete.Length == 0,
            "PlaybackTransport.FrameAt is the recommended factory and must NOT carry [Obsolete]. " +
            "If this fires, deprecation is leaking to non-deprecated cases (the F-1 " +
            "broadcast bug recurring at the case-factory level).");
    }

    /// <summary>
    /// F-4 (StoreKit2). Two <c>commit(...)</c> overloads are gated to
    /// different iOS versions. The lowered overloads must keep their own
    /// versions — pre-fix, <c>AddRange</c>-style accumulation merged the
    /// two sets and broadcast the union across both overloads, so a
    /// consumer at iOS 17 could compile a call to the iOS-18-only
    /// overload and crash at runtime.
    /// </summary>
    public void TestF4_DistinctOverloadVersionsStayDistinct()
    {
        var carrierType = typeof(VersionedOverloadCarrier);
        var commitOverloads = carrierType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name.Equals("Commit", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        TestLogger.Info($"VersionedOverloadCarrier.Commit overloads: {commitOverloads.Length}");
        AssertTrue(commitOverloads.Length == 2,
            $"Expected 2 Commit overloads, found {commitOverloads.Length}. " +
            "If this fires, the Swift fixture or emitter dropped one overload — F-4 cannot be tested.");

        var intOverload = commitOverloads.FirstOrDefault(m =>
            m.GetParameters().Length == 1 &&
            m.GetParameters()[0].ParameterType == typeof(int));
        var stringOverload = commitOverloads.FirstOrDefault(m =>
            m.GetParameters().Length == 1 &&
            m.GetParameters()[0].ParameterType == typeof(string));
        AssertTrue(intOverload is not null, "Commit(int) overload must exist.");
        AssertTrue(stringOverload is not null, "Commit(string) overload must exist.");

        var intAttrs = intOverload!.GetCustomAttributes<SupportedOSPlatformAttribute>(inherit: false).ToArray();
        var stringAttrs = stringOverload!.GetCustomAttributes<SupportedOSPlatformAttribute>(inherit: false).ToArray();
        TestLogger.Info($"Commit(int) SupportedOSPlatform: " +
            $"[{string.Join(", ", intAttrs.Select(a => a.PlatformName))}]");
        TestLogger.Info($"Commit(string) SupportedOSPlatform: " +
            $"[{string.Join(", ", stringAttrs.Select(a => a.PlatformName))}]");

        var intHasIos17 = intAttrs.Any(a =>
            string.Equals(a.PlatformName, "ios17.0", StringComparison.OrdinalIgnoreCase));
        var intHasIos18 = intAttrs.Any(a =>
            string.Equals(a.PlatformName, "ios18.0", StringComparison.OrdinalIgnoreCase));
        var stringHasIos17 = stringAttrs.Any(a =>
            string.Equals(a.PlatformName, "ios17.0", StringComparison.OrdinalIgnoreCase));
        var stringHasIos18 = stringAttrs.Any(a =>
            string.Equals(a.PlatformName, "ios18.0", StringComparison.OrdinalIgnoreCase));

        AssertTrue(intHasIos17,
            "Commit(int) is gated @available(iOS 17.0, *) and must carry SupportedOSPlatform(\"ios17.0\").");
        AssertTrue(!intHasIos18,
            "Commit(int) MUST NOT carry SupportedOSPlatform(\"ios18.0\"). " +
            "If this fires, F-4 has regressed: the iOS 18 floor from the sibling " +
            "Commit(string) overload is being broadcast across the set.");
        AssertTrue(stringHasIos18,
            "Commit(string) is gated @available(iOS 18.0, *) and must carry SupportedOSPlatform(\"ios18.0\").");
        AssertTrue(!stringHasIos17,
            "Commit(string) MUST NOT carry SupportedOSPlatform(\"ios17.0\"). " +
            "If this fires, the merged-version regression has returned and a consumer " +
            "at iOS 17 can compile a call that crashes at runtime on iOS 17.x.");
    }

    /// <summary>
    /// F-2 emitter half. The auto-emitted C# protocol-proxy class for any
    /// <c>@available</c>-gated protocol must inherit the protocol's
    /// <c>[SupportedOSPlatform]</c> attribute(s). Pre-fix, the proxy class
    /// declaration was bare — only the interface carried the attributes —
    /// so CA1416 fired on the proxy's internal call sites at any consumer
    /// baseline below the protocol's floor (iOS 15 baseline vs an iOS 16
    /// protocol). The fix is in
    /// <c>ProtocolProxyEmitter.EmitProxyClass</c>: emit
    /// <c>AvailabilityAttributeEmitter.EmitAvailabilityAttributes(..., emitObsolete: false)</c>
    /// on the proxy class declaration, mirroring the interface emission in
    /// <c>ProtocolHandler</c>.
    /// </summary>
    public void TestF2_ProtocolProxyClassInheritsAvailability()
    {
        // The proxy class is named `{ProtocolName}Proxy` and lives in the
        // SwiftInterop sub-namespace (proxies are deliberately segregated
        // there so they don't clutter the public surface). Resolve via
        // reflection — the proxy is EditorBrowsableState.Never and the
        // test verifies it exists with the expected availability metadata.
        var proxyTypeName = "SwiftBindingsTestLib.SwiftInterop.AvailabilityGatedProtocolF2Proxy";
        var proxyType = typeof(SwiftBindingsTestLib.AvailabilityGatedProtocolF2Conformer)
            .Assembly.GetType(proxyTypeName);
        AssertTrue(proxyType is not null,
            $"Proxy class {proxyTypeName} must exist on the generated assembly. " +
            "If this assertion fails, the proxy emitter has regressed and " +
            "consumer-side IFoo implementations cannot be passed back to Swift.");

        var attrs = proxyType!.GetCustomAttributes<SupportedOSPlatformAttribute>(inherit: false).ToArray();
        TestLogger.Info($"{proxyTypeName} SupportedOSPlatform attrs: " +
            $"[{string.Join(", ", attrs.Select(a => a.PlatformName))}]");

        var ios16 = attrs.FirstOrDefault(a =>
            string.Equals(a.PlatformName, "ios16.0", StringComparison.OrdinalIgnoreCase));
        AssertTrue(ios16 is not null,
            $"{proxyTypeName} must carry SupportedOSPlatform(\"ios16.0\") inherited " +
            "from the source protocol AvailabilityGatedProtocolF2. If this fires, " +
            "the proxy class @available inheritance has regressed and CA1416 will " +
            "fire on consumer call sites at the iOS 15 baseline.");

        // Sanity: the interface must still carry the same attribute. The
        // fix adds the proxy-class side without disturbing the interface
        // emission ProtocolHandler already produces.
        var ifaceType = typeof(SwiftBindingsTestLib.IAvailabilityGatedProtocolF2);
        var ifaceAttrs = ifaceType.GetCustomAttributes<SupportedOSPlatformAttribute>(inherit: false).ToArray();
        var ifaceIos16 = ifaceAttrs.FirstOrDefault(a =>
            string.Equals(a.PlatformName, "ios16.0", StringComparison.OrdinalIgnoreCase));
        AssertTrue(ifaceIos16 is not null,
            "IAvailabilityGatedProtocolF2 must still carry SupportedOSPlatform(\"ios16.0\"). " +
            "If this fires, the proxy-side fix accidentally regressed the interface emission.");
    }

    /// <summary>
    /// F-5 (MusicKit). A type whose Swift <c>@available</c> list explicitly
    /// names <c>visionOS 1.0</c> must lower to a C# type with
    /// <c>[SupportedOSPlatform("visionos1.0")]</c>. Pre-fix, PlatformMapping
    /// had no entry for visionOS and the emitter silently dropped the
    /// clause across ~every MusicKit type.
    /// </summary>
    public void TestF5_VisionOSPlatformSurvivesLowering()
    {
        var carrierType = typeof(VisionPlatformCarrier);
        var attrs = carrierType.GetCustomAttributes<SupportedOSPlatformAttribute>(inherit: false).ToArray();
        TestLogger.Info($"VisionPlatformCarrier SupportedOSPlatform attrs: " +
            $"[{string.Join(", ", attrs.Select(a => a.PlatformName))}]");

        var visionOS = attrs.FirstOrDefault(a =>
            string.Equals(a.PlatformName, "visionos1.0", StringComparison.OrdinalIgnoreCase));
        AssertTrue(visionOS is not null,
            "VisionPlatformCarrier must carry SupportedOSPlatform(\"visionos1.0\"). " +
            "If this fires, F-5 has regressed: visionOS is being silently dropped " +
            "from the lowered C# attribute set. The MusicKit consumer experience " +
            "regresses to spurious platform-availability warnings on visionOS.");

        // Sanity: the iOS / macOS / tvOS clauses must still come through —
        // the visionOS fix must not disrupt the existing platform mappings.
        var hasIos = attrs.Any(a =>
            string.Equals(a.PlatformName, "ios15.0", StringComparison.OrdinalIgnoreCase));
        AssertTrue(hasIos,
            "Sibling iOS clause must still survive lowering alongside visionOS — " +
            "if this fires, the F-5 fix accidentally regressed the existing platform mappings.");
    }

}
