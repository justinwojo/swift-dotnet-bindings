// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if MUSICKIT_SMOKE
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using RuntimeTestsApp.Infrastructure;
using MusicKit;

namespace RuntimeTestsApp.SmokeTests;

/// <summary>
/// Session 7 end-to-end smoke test for the Apple-framework direct-mode pipeline on
/// MusicKit. Consumes the externally-built <c>MusicKit.Swift.iOS.dll</c> +
/// <c>MusicKitSwiftBindings.xcframework</c> from the gitignored in-tree snapshot at
/// <c>BindingTests/obj/MusicKitSnapshot/</c> and exercises a hermetic, metadata-only
/// surface: a <c>MusicItemID</c> string-backed round-trip plus a reflection-based
/// assertion pinning the per-property <c>@available</c> propagation fix (fix #2 in
/// the session plan) on a real Tier-A framework.
///
/// Gated by the <c>MUSICKIT_SMOKE</c> compile symbol, which the csproj sets only
/// when every prerequisite (snapshot csproj, simulator wrapper slice, ProjectReference
/// targets file, iossimulator-arm64 RID, explicit <c>EnableMusicKitSmoke=true</c>
/// opt-in) is satisfied. Regenerate the snapshot with
/// <c>nuke regenerate-apple-snapshot --framework MusicKit</c>.
///
/// No extern alias is required: MusicKit is a pure Swift framework and
/// Microsoft.iOS's managed projection surfaces Media Player types under the
/// <c>MediaPlayer</c> namespace, not <c>MusicKit</c>. Verified by grepping
/// <c>Microsoft.iOS.dll</c> for <c>MusicKit</c>.
///
/// <b>Deliberately excluded:</b> <c>MusicAuthorization.request()</c> (presents a
/// system sheet), <c>MusicCatalog.search(...)</c>, <c>MusicLibrary.add(...)</c>,
/// any <c>WeatherService</c>-equivalent network fetch — all of those require the
/// MusicKit entitlement and network access. This smoke test is strictly
/// metadata-only so it can run in any environment where the framework dylib is
/// reachable by dyld.
/// </summary>
public class MusicKitSmokeTests : TestBase
{
    public MusicKitSmokeTests(TestResults results) : base(results) { }

    /// <summary>
    /// Exercises the end-to-end direct-mode pipeline on <c>MusicItemID</c>, a
    /// frozen Swift struct that wraps a bare <c>String</c>:
    ///
    ///   1. <c>new MusicItemID(rawValue)</c> — wrapper thunk
    ///      <c>SBW_MusicKit_MusicItemID_init</c> takes a UTF-8 slice pointer,
    ///      constructs the Swift value, and returns it through the indirect-result
    ///      buffer protocol.
    ///   2. <c>musicItemID.RawValue</c> — <c>SBW_Get_MusicKit_MusicItemID_rawValue</c>
    ///      reads the stored string back out and returns it through another UTF-8
    ///      slice pointer.
    ///
    /// Assertion: the round-tripped string is exactly equal to the input. Any other
    /// value would indicate a marshalling bug in the <c>String</c> parameter /
    /// return path through the wrapper — not an environment variance.
    ///
    /// This is the Session 7 equivalent of
    /// <see cref="CryptoKitSmokeTests.TestSymmetricKeyBitCount"/>: the minimum
    /// viable success signal for end-to-end Apple-framework direct-mode pipeline on
    /// MusicKit (fix #16 in the session plan).
    /// </summary>
    [SupportedOSPlatform("ios15.0")]
    public void TestMusicItemIDRoundTrip()
    {
        try
        {
            const string expected = "musickit-smoke-test-id";
            using var musicItemId = new MusicKit.MusicItemID(expected);
            var actual = musicItemId.RawValue;
            TestLogger.Info($"MusicItemID(\"{expected}\").RawValue = \"{actual}\"");
            AssertEqual(expected, actual,
                "MusicItemID round-trip must preserve the raw string value — any " +
                "divergence indicates a String marshalling bug in the MusicKit wrapper.");
        }
        catch (Exception ex)
        {
            LogExceptionChain(ex);
            throw;
        }
    }

    /// <summary>
    /// Pins the per-property <c>@available</c> propagation fix (fix #2 in the
    /// session plan) on a real Apple-framework snapshot. The fix propagates a
    /// Swift member's <c>@available</c> attribute to the emitted C# property
    /// getter / setter, so a consumer compiling against a lower
    /// <c>SupportedOSPlatformVersion</c> than the member requires sees the
    /// correct <c>SupportedOSPlatform</c> annotation rather than silently picking
    /// up the containing type's laxer attribute.
    ///
    /// MusicKit's <c>Song.audioVariants</c> is the canonical shape: the enclosing
    /// <c>Song</c> type is <c>ios15.0+</c> (<c>@available(iOS 15.0, ...)</c>) but
    /// the property was added in the iOS 16 MusicKit update and is marked
    /// <c>@available(iOS 16.0, macOS 13.0, tvOS 16.0, watchOS 9.0, *)</c>. Without
    /// fix #2 the emitted C# property would inherit only
    /// <c>SupportedOSPlatform("ios15.0")</c> from the enclosing type, and a
    /// consumer compiling for iOS 15 would see no CA1416 warning on a property
    /// that is unavailable at runtime.
    ///
    /// We assert this via reflection instead of touching the property at runtime
    /// because constructing a <c>Song</c> requires a <c>MusicCatalog</c> network
    /// fetch which is entitlement-gated and network-bound. The reflection
    /// assertion runs the exact same generated code the iOS 15 consumer would
    /// compile against and proves the attribute is present. Same pattern as
    /// <see cref="WeatherKitSmokeTests.TestDayWeatherHighTemperatureTimeIos18Availability"/>.
    /// </summary>
    public void TestSongAudioVariantsIos16Availability()
    {
        try
        {
            var songType = typeof(MusicKit.Song);
            var prop = songType.GetProperty(
                "AudioVariants",
                BindingFlags.Instance | BindingFlags.Public);
            AssertTrue(prop is not null,
                "Song.AudioVariants property must exist on the generated MusicKit binding.");

            var attrs = prop!.GetCustomAttributes<SupportedOSPlatformAttribute>(inherit: false).ToArray();
            TestLogger.Info($"Song.AudioVariants SupportedOSPlatform attrs: " +
                $"[{string.Join(", ", attrs.Select(a => a.PlatformName))}]");

            var ios16 = attrs.FirstOrDefault(a =>
                string.Equals(a.PlatformName, "ios16.0", StringComparison.OrdinalIgnoreCase));
            AssertTrue(ios16 is not null,
                "Song.AudioVariants must carry SupportedOSPlatform(\"ios16.0\"). " +
                "Fix #2 propagates the member-level @available(iOS 16.0, *) from the " +
                "Swift source to the emitted C# property — without it, the property " +
                "would inherit only the enclosing Song type's ios15.0 annotation and " +
                "iOS 15 consumers would see no CA1416 diagnostic on an iOS 16-only API.");

            var ios15OnProperty = attrs.FirstOrDefault(a =>
                string.Equals(a.PlatformName, "ios15.0", StringComparison.OrdinalIgnoreCase));
            AssertTrue(ios15OnProperty is null,
                "Song.AudioVariants must NOT carry SupportedOSPlatform(\"ios15.0\") on the " +
                "property itself — that would indicate fix #2 regressed into a weaker " +
                "'append-member-attribute-on-top-of-inherited-type-attribute' shape. The " +
                "property must only declare its own ios16.0+ baseline; the ios15.0 " +
                "annotation belongs exclusively on the enclosing Song type.");
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
    /// <see cref="WeatherKitSmokeTests"/>.
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
