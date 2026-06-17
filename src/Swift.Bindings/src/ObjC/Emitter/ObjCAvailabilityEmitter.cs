// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;

namespace BindingsGeneration.ObjC;

/// <summary>
/// Emits .NET platform-availability attributes for Objective-C declarations whose availability was
/// recovered from header source (Finding 22, recovery option a2).
/// <para/>
/// This mirrors the Swift <c>@available</c> path's emission shape
/// (<see cref="BindingsGeneration.AvailabilityAttributeEmitter"/>): fully-qualified
/// <c>[global::System.Runtime.Versioning.SupportedOSPlatform]</c> (introduced),
/// <c>[global::System.Runtime.Versioning.ObsoletedOSPlatform]</c> (deprecated / obsoleted), and
/// <c>[global::System.Runtime.Versioning.UnsupportedOSPlatform]</c> (unavailable). Because the
/// emitted text is fully qualified, no <c>using</c> directive is required in the generated file.
/// <para/>
/// Unlike the deleted bgen-era emitter, this one never <em>skips</em> a declaration on
/// unavailability — it annotates with <c>UnsupportedOSPlatform</c> and lets the .NET platform
/// analyzer narrow the consumer's call site, matching the Swift "annotate, don't drop" policy.
/// </summary>
public static class ObjCAvailabilityEmitter
{
    /// <summary>
    /// Appends availability attribute lines for <paramref name="availability"/>, each prefixed with
    /// <paramref name="indent"/>. Emitted strings are deduped (a declaration may carry both an
    /// <c>API_AVAILABLE</c> and an <c>API_DEPRECATED</c> for the same platform/version) and appended
    /// in the deterministic order they were recovered from source. A no-op for an empty list.
    /// </summary>
    public static void EmitAvailabilityAttributes(StringBuilder sb, IReadOnlyList<ObjCAvailability> availability, string indent)
    {
        if (availability == null || availability.Count == 0)
            return;

        // Match the Swift path's macCatalyst-floor lift before emission (see LiftMacCatalystFloorToIOS).
        var effective = LiftMacCatalystFloorToIOS(availability);

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();

        void Add(string attr)
        {
            if (emitted.Add(attr))
                lines.Add(attr);
        }

        foreach (var avail in effective)
        {
            if (string.IsNullOrEmpty(avail.Platform))
                continue;

            if (avail.IsUnavailable)
            {
                Add($"[global::System.Runtime.Versioning.UnsupportedOSPlatform(\"{avail.Platform}\")]");
                continue;
            }

            if (avail.IntroducedVersion != null)
            {
                var pv = $"{avail.Platform}{NormalizeVersion(avail.IntroducedVersion)}";
                Add($"[global::System.Runtime.Versioning.SupportedOSPlatform(\"{pv}\")]");
            }

            if (avail.DeprecatedVersion != null)
                Add(BuildObsoleted(avail.Platform, avail.DeprecatedVersion, avail.Message));

            if (avail.ObsoletedVersion != null)
                Add(BuildObsoleted(avail.Platform, avail.ObsoletedVersion, avail.Message));
        }

        foreach (var line in lines)
            sb.AppendLine($"{indent}{line}");
    }

    private static string BuildObsoleted(string platform, string version, string? message)
    {
        var pv = $"{platform}{NormalizeVersion(version)}";
        if (!string.IsNullOrEmpty(message))
            return $"[global::System.Runtime.Versioning.ObsoletedOSPlatform(\"{pv}\", \"{EscapeStringLiteral(message)}\")]";
        return $"[global::System.Runtime.Versioning.ObsoletedOSPlatform(\"{pv}\")]";
    }

    /// <summary>
    /// Ensures a version string carries at least <c>major.minor</c> (e.g. <c>"13" → "13.0"</c>),
    /// matching the Swift path's <c>NormalizeVersion</c> so the two emitters produce identical
    /// platform-version strings.
    /// </summary>
    internal static string NormalizeVersion(string version)
    {
        if (!version.Contains('.'))
            return version + ".0";
        return version;
    }

    private static string EscapeStringLiteral(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("\"", "\\\"")
         .Replace("\r", "\\r")
         .Replace("\n", "\\n")
         .Replace("\t", "\\t");

    /// <summary>
    /// Mirrors the Swift path's
    /// <see cref="BindingsGeneration.AvailabilityHelpers.LiftMacCatalystFloorToIOS"/>: when a
    /// declaration carries BOTH an explicit <c>maccatalyst</c> floor and an <c>ios</c> floor &gt;= 13.0
    /// where the iOS floor is higher, raise the maccatalyst introduced version to the iOS floor (and
    /// clear any deprecated/obsoleted version the lift would push the introduced version above —
    /// a deprecation below an introduced floor is vacuous). swiftc maps iOS&gt;=13 floors onto
    /// macCatalyst 1:1 for the unified SDK, so the recovered ObjC binding must agree with that floor;
    /// otherwise a Mac Catalyst consumer between the declared maccatalyst floor and the iOS floor sees
    /// no CA1416 diagnostic yet hits a missing symbol at runtime.
    /// <para/>
    /// Gated on an EXPLICIT maccatalyst entry being present (mirrors the Swift gate): when the source
    /// names only <c>ios</c>, .NET's ios→maccatalyst child-platform inheritance already narrows Catalyst
    /// consumers to the iOS floor, so no maccatalyst entry is invented. Pure: returns the input
    /// unchanged when no lift applies.
    /// </summary>
    internal static IReadOnlyList<ObjCAvailability> LiftMacCatalystFloorToIOS(IReadOnlyList<ObjCAvailability> availability)
    {
        string? maxIOS = null;
        var hasExplicitCatalyst = false;
        foreach (var a in availability)
        {
            if (a.IntroducedVersion is null)
            {
                if (a.Platform == "maccatalyst")
                    hasExplicitCatalyst = true;
                continue;
            }
            if (a.Platform == "ios")
            {
                if (maxIOS is null || CompareVersions(a.IntroducedVersion, maxIOS) > 0)
                    maxIOS = a.IntroducedVersion;
            }
            else if (a.Platform == "maccatalyst")
            {
                hasExplicitCatalyst = true;
            }
        }

        if (maxIOS is null || !hasExplicitCatalyst || CompareVersions(maxIOS, "13.0") < 0)
            return availability;

        List<ObjCAvailability>? lifted = null;
        for (var i = 0; i < availability.Count; i++)
        {
            var a = availability[i];
            if (a.Platform != "maccatalyst"
                || a.IntroducedVersion is null
                || CompareVersions(maxIOS, a.IntroducedVersion) <= 0)
            {
                continue;
            }

            lifted ??= new List<ObjCAvailability>(availability);
            var deprecated = a.DeprecatedVersion is { } dep && CompareVersions(dep, maxIOS) < 0
                ? null : a.DeprecatedVersion;
            var obsoleted = a.ObsoletedVersion is { } obs && CompareVersions(obs, maxIOS) < 0
                ? null : a.ObsoletedVersion;
            lifted[i] = a with
            {
                IntroducedVersion = maxIOS,
                DeprecatedVersion = deprecated,
                ObsoletedVersion = obsoleted,
            };
        }

        return lifted ?? availability;
    }

    /// <summary>
    /// Compares two dotted numeric version strings (e.g. <c>"15"</c> vs <c>"15.0"</c>) component-wise,
    /// treating a missing component as 0. Returns &lt;0, 0, or &gt;0.
    /// </summary>
    private static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        var n = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < n; i++)
        {
            var va = i < pa.Length && int.TryParse(pa[i], out var x) ? x : 0;
            var vb = i < pb.Length && int.TryParse(pb[i], out var y) ? y : 0;
            if (va != vb)
                return va.CompareTo(vb);
        }
        return 0;
    }
}
