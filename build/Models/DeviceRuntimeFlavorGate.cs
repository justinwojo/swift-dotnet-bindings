// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Text.RegularExpressions;

/// <summary>
/// The rule that decides whether a physical-device runtime-test run actually ran on the runtime its
/// lane claims.
///
/// <para><b>Why this exists.</b> Two device lanes share one bundle id, one device slice, one
/// wrapper build and one install path: the default NativeAOT lane
/// (<c>dotnet publish -c Release -r ios-arm64</c>, <c>PublishAot=true</c>) and the opt-in Mono
/// full-AOT lane (<c>nuke binding-tests --device --mono-aot</c> → <c>dotnet build -c Debug
/// -r ios-arm64</c>, no <c>PublishAot</c>). The only thing separating them is an MSBuild property.
/// If that property stops taking effect, everything downstream still succeeds — the app builds,
/// installs, launches and goes green — and the harness reports a NativeAOT run under the name of
/// the runtime most consumers actually ship on. That is a false green about a runtime nothing
/// tested, which is worse than no lane at all.</para>
///
/// <para><b>The evidence.</b> The app prints one line naming what <c>SwiftRuntimeInfo</c> resolved
/// in-process. That value, not the build log, is what selects the interop dispatch path, so it is
/// the only witness worth trusting. A run that never printed the banner is left alone: the app
/// either never started or died before the banner, and the surrounding launch/crash recovery
/// already classifies that — failing here would mask it with a wrong diagnosis.</para>
/// </summary>
public static class DeviceRuntimeFlavorGate
{
    /// <summary>The banner prefix the runtime-test app emits (see <c>TestBase.RuntimeFlavorDescription</c>).</summary>
    public const string BannerPrefix = "Runtime flavor: ";

    private static readonly Regex BannerPattern =
        new(@"Runtime flavor: (?<body>[^\r\n]+)", RegexOptions.CultureInvariant);

    /// <summary>What the device console output says about the runtime that ran.</summary>
    public enum Verdict
    {
        /// <summary>No banner in the output — no verdict available; the caller must not fail on this.</summary>
        NoEvidence,

        /// <summary>The banner agrees with the lane that was launched.</summary>
        Match,

        /// <summary>The banner contradicts the lane that was launched.</summary>
        Mismatch,
    }

    /// <summary>Extracts the banner body, or null when the output carries no banner.</summary>
    public static string? ExtractBanner(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return null;
        var match = BannerPattern.Match(output!);
        return match.Success ? match.Groups["body"].Value : null;
    }

    /// <summary>
    /// Judges a device run's console output against the lane that launched it.
    /// <paramref name="monoAotLane"/> is true for <c>--mono-aot</c>.
    ///
    /// <para>The Mono lane demands a positive Mono-AOT claim AND the absence of a NativeAOT claim:
    /// a build whose <c>PublishAot</c> suppression failed reports <c>IsNativeAotRuntime=True</c>,
    /// and a build that lost the runtime-flavor AppContext switch reports the same thing by
    /// heuristic fallback (the switch-less heuristic cannot tell device Mono full-AOT from
    /// NativeAOT and defaults to NativeAOT). Both are exactly the failures this gate is for, and
    /// both are silent everywhere else.</para>
    /// </summary>
    public static Verdict Judge(string? output, bool monoAotLane)
    {
        var body = ExtractBanner(output);
        if (body == null)
            return Verdict.NoEvidence;

        var reportsMonoAot = body.Contains("IsMonoAot=True", StringComparison.Ordinal);
        var reportsNativeAot = body.Contains("IsNativeAotRuntime=True", StringComparison.Ordinal);

        if (monoAotLane)
            return reportsMonoAot && !reportsNativeAot ? Verdict.Match : Verdict.Mismatch;

        return reportsNativeAot ? Verdict.Match : Verdict.Mismatch;
    }

    /// <summary>The diagnosis printed when <see cref="Judge"/> returns <see cref="Verdict.Mismatch"/>.</summary>
    public static string MismatchMessage(string laneLabel, string banner, bool monoAotLane) =>
        monoAotLane
            ? $"{laneLabel}: the app reported '{banner}' — the --mono-aot lane must run on Mono " +
              "full-AOT. A NativeAOT (or otherwise misclassified) process here takes the Direct " +
              "interop dispatch path, so the run says nothing about the Mono full-AOT runtime it " +
              "claims to gate. Check that -p:SwiftBindingsDeviceMonoAot=true reached the build and " +
              "that the app still injects the Swift.Runtime.IsNativeAot AppContext switch."
            : $"{laneLabel}: the app reported '{banner}' — the NativeAOT device lane must run on " +
              "NativeAOT. Refusing to report a non-NativeAOT run under the NativeAOT lane's name.";
}
