// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Layer-neutral helpers for combining <see cref="AvailabilityAnnotation"/> lists across a
/// member and its enclosing type chain. Lives under <c>Model/</c> so parser and type-database
/// construction code (e.g. <c>ModuleProcessor</c> populating
/// <c>TypeRecord.AvailabilityAnnotations</c>) can use the same merge semantics as the emitter
/// without depending on string-emitter infrastructure.
/// </summary>
public static class AvailabilityHelpers
{
    /// <summary>
    /// Merges member-level and parent-type availability annotations for a top-level emission
    /// target (e.g. <c>@_cdecl</c> wrapper) that does not implicitly inherit the enclosing
    /// type's availability. Walks the full parent chain so nested types like
    /// <c>StoreKit.Product.SubscriptionInfo.RenewalInfo.AdvancedCommerceInfo.Item</c> pick up
    /// availability declared on any ancestor. Repeated platform+version pairs are deduped at
    /// the @available emission step.
    /// </summary>
    public static IReadOnlyList<AvailabilityAnnotation>? MergeAvailability(
        IReadOnlyList<AvailabilityAnnotation>? memberAnnotations,
        BaseDecl? parentDecl)
    {
        return MergeAvailabilityFromAncestors(memberAnnotations, parentDecl);
    }

    /// <summary>
    /// Walks the parent chain of <paramref name="startDecl"/> and merges every TypeDecl's
    /// availability annotations with <paramref name="memberAnnotations"/>. Returns null when
    /// neither the member nor any ancestor declares availability.
    /// </summary>
    public static IReadOnlyList<AvailabilityAnnotation>? MergeAvailabilityFromAncestors(
        IReadOnlyList<AvailabilityAnnotation>? memberAnnotations,
        BaseDecl? startDecl)
    {
        List<AvailabilityAnnotation>? merged = null;

        BaseDecl? current = startDecl;
        while (current is TypeDecl td)
        {
            if (td.AvailabilityAnnotations is { Count: > 0 } parentAnnotations)
            {
                merged ??= new List<AvailabilityAnnotation>();
                merged.AddRange(parentAnnotations);
            }
            current = td.ParentDecl;
        }

        if (memberAnnotations is { Count: > 0 })
        {
            merged ??= new List<AvailabilityAnnotation>();
            merged.AddRange(memberAnnotations);
        }

        return merged;
    }

    /// <summary>
    /// The annotation list that governs a property's SETTER. Swift can introduce a setter after
    /// the property itself (a get-only requirement later made settable), and the parser records
    /// that as <see cref="PropertyDecl.SetterAvailabilityAnnotations"/> — already the property's
    /// own list merged with the setter-specific per-platform overrides, so it REPLACES rather
    /// than supplements the property list. When the setter declares nothing of its own the
    /// property's list governs.
    ///
    /// <para>Single source of truth for that preference order: the Swift <c>@_cdecl</c> setter
    /// forwarder, the C# setter P/Invoke, the proxy's <c>set</c> accessor and the interface's
    /// <c>set</c> accessor must all be gated at the same floor, or the attribute a consumer sees
    /// disagrees with the floor the symbol is exported at.</para>
    /// </summary>
    public static IReadOnlyList<AvailabilityAnnotation>? SelectSetterAnnotations(PropertyDecl property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.SetterAvailabilityAnnotations is { Count: > 0 } setterAnnotations
            ? setterAnnotations
            : property.AvailabilityAnnotations;
    }

    /// <summary>
    /// Component-wise numeric comparison of dotted OS-version strings: "13.0" &lt; "26.0" and
    /// "9.0" &lt; "10.0" (not lexicographic). Missing components are treated as 0 so "13" == "13.0".
    /// Returns &gt;0 when <paramref name="left"/> is newer, &lt;0 when older, 0 when equal. Single
    /// source of truth for OS-version ordering shared by the Swift <c>@available</c> collector
    /// (<c>WrapperEmitterHelpers.CollectStrictestAvailabilityKeys</c>) and the C#
    /// <c>[SupportedOSPlatform]</c> emitters.
    /// </summary>
    public static int CompareOsVersions(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        int len = Math.Max(leftParts.Length, rightParts.Length);
        for (int i = 0; i < len; i++)
        {
            int l = i < leftParts.Length && int.TryParse(leftParts[i], out var lv) ? lv : 0;
            int r = i < rightParts.Length && int.TryParse(rightParts[i], out var rv) ? rv : 0;
            if (l != r) return l < r ? -1 : 1;
        }
        return 0;
    }

    /// <summary>
    /// Mac Catalyst tracks iOS for the unified-SDK era (iOS 13.0+): an API marked
    /// <c>@available(iOS 18.0, *)</c> is unavailable on macCatalyst &lt; 18.0 even when an explicit
    /// <c>@available(macCatalyst 17.0, *)</c> is also present, because swiftc maps iOS&gt;=13 floors
    /// onto macCatalyst 1:1 when compiling for <c>-target arm64-apple-ios&lt;X&gt;-macabi</c>. The
    /// generated <c>@_cdecl</c> wrapper is therefore force-lifted to the iOS floor (otherwise it
    /// fails to compile), so the native symbol is exported gated at the iOS floor. This returns the
    /// annotation list with every explicit macCatalyst floor raised to match, so the C#
    /// <c>[SupportedOSPlatform("maccatalyst…")]</c> a consumer sees agrees with the floor the symbol
    /// is actually exported at. Without it, a Mac Catalyst consumer between the declared macCatalyst
    /// floor and the iOS floor sees no CA1416 diagnostic yet hits a missing symbol at runtime.
    ///
    /// <para>Gated on an EXPLICIT macCatalyst entry being present (mirrors the Swift collector's
    /// gate): when the source <c>@available</c> names only iOS, .NET's ios→maccatalyst
    /// child-platform inheritance already narrows Catalyst consumers to the iOS floor, so no
    /// macCatalyst entry is invented. When a lifted macCatalyst introduced version would sit above
    /// that annotation's own deprecated / obsoleted version, those are cleared — a deprecation below
    /// an introduced floor is vacuous (the API never existed there) and would otherwise emit a
    /// backwards <c>[ObsoletedOSPlatform]</c>.</para>
    ///
    /// <para>Pure: returns the input reference unchanged when no lift applies and never mutates the
    /// input list or its records.</para>
    /// </summary>
    public static IReadOnlyList<AvailabilityAnnotation>? LiftMacCatalystFloorToIOS(
        IReadOnlyList<AvailabilityAnnotation>? annotations)
    {
        if (annotations is null || annotations.Count == 0)
            return annotations;

        string? maxIOS = null;
        bool hasExplicitCatalyst = false;
        foreach (var ann in annotations)
        {
            if (ann.IntroducedVersion is null)
                continue;
            if (string.Equals(ann.Platform, "iOS", StringComparison.Ordinal))
            {
                if (maxIOS is null || CompareOsVersions(ann.IntroducedVersion, maxIOS) > 0)
                    maxIOS = ann.IntroducedVersion;
            }
            else if (string.Equals(ann.Platform, "macCatalyst", StringComparison.Ordinal))
            {
                hasExplicitCatalyst = true;
            }
        }

        if (maxIOS is null || !hasExplicitCatalyst || CompareOsVersions(maxIOS, "13.0") < 0)
            return annotations;

        List<AvailabilityAnnotation>? lifted = null;
        for (int i = 0; i < annotations.Count; i++)
        {
            var ann = annotations[i];
            if (!string.Equals(ann.Platform, "macCatalyst", StringComparison.Ordinal)
                || ann.IntroducedVersion is null
                || CompareOsVersions(maxIOS, ann.IntroducedVersion) <= 0)
            {
                continue;
            }

            lifted ??= new List<AvailabilityAnnotation>(annotations);
            // Clear a deprecated / obsoleted version that the lift would push the introduced
            // version above — see the doc comment's vacuous-deprecation note.
            var deprecated = ann.DeprecatedVersion is { } dep && CompareOsVersions(dep, maxIOS) < 0
                ? null : ann.DeprecatedVersion;
            var obsoleted = ann.ObsoletedVersion is { } obs && CompareOsVersions(obs, maxIOS) < 0
                ? null : ann.ObsoletedVersion;
            lifted[i] = ann with
            {
                IntroducedVersion = maxIOS,
                DeprecatedVersion = deprecated,
                ObsoletedVersion = obsoleted,
            };
        }

        return lifted ?? annotations;
    }
}
