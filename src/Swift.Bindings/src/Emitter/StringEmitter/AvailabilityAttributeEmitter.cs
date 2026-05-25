// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits [SupportedOSPlatform], [ObsoletedOSPlatform], and [Obsolete] attributes
/// for Swift @available annotations.
/// </summary>
internal static class AvailabilityAttributeEmitter
{
    private static readonly Dictionary<string, string> PlatformMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["iOS"] = "ios",
        ["macOS"] = "macos",
        ["tvOS"] = "tvos",
        ["watchOS"] = "watchos",
        ["macCatalyst"] = "maccatalyst",
        // .NET 6+ recognizes "visionos" via System.Runtime.Versioning.OSPlatform.
        // Without this entry the `@available(visionOS 1.0, *)` clause from the
        // swiftinterface is silently elided (Family-F-5: 453 dropped markers in
        // SwiftBindings.Apple.MusicKit alone). Map verbatim so visionOS-targeting
        // consumers see CA1416 narrow correctly.
        ["visionOS"] = "visionos",
    };

    /// <summary>
    /// Emits [SupportedOSPlatform], [ObsoletedOSPlatform], and optionally [Obsolete] attributes.
    /// For method-level: set emitObsolete=false (merged into EmitSafetyObsolete to avoid conflict).
    /// For type/property/protocol-level: set emitObsolete=true.
    /// </summary>
    public static void EmitAvailabilityAttributes(
        CSharpWriter csWriter, BaseDecl decl, BaseDecl? parentDecl = null, bool emitObsolete = true)
    {
        if (decl.AvailabilityAnnotations == null || decl.AvailabilityAnnotations.Count == 0)
            return;

        // Lift an explicit macCatalyst floor to the iOS floor so the C# attribute matches the
        // floor the @_cdecl wrapper is exported at; lift the parent the same way so the dedup
        // below compares effective (lifted) versions. See AvailabilityHelpers.LiftMacCatalystFloorToIOS.
        var annotations = AvailabilityHelpers.LiftMacCatalystFloorToIOS(decl.AvailabilityAnnotations)!;
        var parentAnnotations = AvailabilityHelpers.LiftMacCatalystFloorToIOS(parentDecl?.AvailabilityAnnotations);

        // Collect parent's platform annotations for dedup
        var parentPlatforms = new HashSet<string>();
        if (parentAnnotations != null)
        {
            foreach (var pa in parentAnnotations)
            {
                if (pa.Platform != null && pa.IntroducedVersion != null && PlatformMapping.ContainsKey(pa.Platform))
                    parentPlatforms.Add($"{PlatformMapping[pa.Platform]}{NormalizeVersion(pa.IntroducedVersion)}");
            }
        }

        bool obsoleteEmitted = false;
        foreach (var annotation in annotations)
        {
            // Platform-specific attributes
            if (annotation.Platform != null && PlatformMapping.TryGetValue(annotation.Platform, out var dotnetPlatform))
            {
                // [SupportedOSPlatform] for introduced version
                if (annotation.IntroducedVersion != null)
                {
                    var platformVersion = $"{dotnetPlatform}{NormalizeVersion(annotation.IntroducedVersion)}";
                    // Skip if parent already has the same or stricter annotation
                    if (!parentPlatforms.Contains(platformVersion))
                    {
                        csWriter.WriteLine($"[global::System.Runtime.Versioning.SupportedOSPlatform(\"{platformVersion}\")]");
                    }
                }

                // [ObsoletedOSPlatform] for deprecated version
                if (annotation.DeprecatedVersion != null)
                {
                    var deprecatedPlatformVersion = $"{dotnetPlatform}{NormalizeVersion(annotation.DeprecatedVersion)}";
                    if (annotation.Message != null)
                    {
                        csWriter.WriteLine($"[global::System.Runtime.Versioning.ObsoletedOSPlatform(\"{deprecatedPlatformVersion}\", " +
                            $"\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(annotation.Message)}\")]");
                    }
                    else
                    {
                        csWriter.WriteLine($"[global::System.Runtime.Versioning.ObsoletedOSPlatform(\"{deprecatedPlatformVersion}\")]");
                    }
                }

                // [ObsoletedOSPlatform] for obsoleted version
                if (annotation.ObsoletedVersion != null)
                {
                    var obsoletedPlatformVersion = $"{dotnetPlatform}{NormalizeVersion(annotation.ObsoletedVersion)}";
                    csWriter.WriteLine($"[global::System.Runtime.Versioning.ObsoletedOSPlatform(\"{obsoletedPlatformVersion}\")]");
                }
            }

            // Unconditional deprecation → [Obsolete] (C# allows only one per declaration)
            if (emitObsolete && !obsoleteEmitted && annotation.IsUnconditionallyDeprecated)
            {
                obsoleteEmitted = true;
                var message = BuildDeprecationMessage(annotation);
                if (message != null)
                    csWriter.WriteLine($"[Obsolete(\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(message)}\")]");
                else
                    csWriter.WriteLine("[Obsolete]");
            }
        }
    }

    /// <summary>
    /// Emits [SupportedOSPlatform] attributes from a raw annotation list, deduping
    /// against a parent's floor. Used by specialization emitters that merge availability
    /// from multiple sources (method + parent + conformer) without a backing decl.
    /// </summary>
    public static void EmitSupportedOSPlatformsFromAnnotations(
        CSharpWriter csWriter,
        IReadOnlyList<AvailabilityAnnotation>? annotations,
        IReadOnlyList<AvailabilityAnnotation>? parentAnnotations = null)
    {
        // Lift an explicit macCatalyst floor to the iOS floor so the emitted attribute matches
        // the floor the @_cdecl wrapper is exported at — see AvailabilityHelpers.LiftMacCatalystFloorToIOS.
        annotations = AvailabilityHelpers.LiftMacCatalystFloorToIOS(annotations);
        if (annotations == null || annotations.Count == 0)
            return;
        parentAnnotations = AvailabilityHelpers.LiftMacCatalystFloorToIOS(parentAnnotations);

        var parentPlatforms = new HashSet<string>(StringComparer.Ordinal);
        if (parentAnnotations != null)
        {
            foreach (var pa in parentAnnotations)
            {
                if (pa.Platform != null && pa.IntroducedVersion != null && PlatformMapping.ContainsKey(pa.Platform))
                    parentPlatforms.Add($"{PlatformMapping[pa.Platform]}{NormalizeVersion(pa.IntroducedVersion)}");
            }
        }

        // Keep only the strictest introduced-version per .NET platform (max version wins).
        var strictest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ann in annotations)
        {
            if (ann.Platform == null || ann.IntroducedVersion == null) continue;
            if (!PlatformMapping.TryGetValue(ann.Platform, out var dotnetPlatform)) continue;

            if (!strictest.TryGetValue(dotnetPlatform, out var existing) ||
                IsStrictlyNewer(ann.IntroducedVersion, existing))
            {
                strictest[dotnetPlatform] = ann.IntroducedVersion;
            }
        }

        foreach (var (dotnetPlatform, version) in strictest)
        {
            var platformVersion = $"{dotnetPlatform}{NormalizeVersion(version)}";
            if (parentPlatforms.Contains(platformVersion)) continue;
            csWriter.WriteLine($"[global::System.Runtime.Versioning.SupportedOSPlatform(\"{platformVersion}\")]");
        }
    }

    /// <summary>
    /// Emits accessor-level <c>[SupportedOSPlatform]</c> attributes for a property's
    /// setter, covering only the platforms where the setter's introduced version is
    /// strictly greater than the property's. Used by <c>PropertyHandler.EmitSetter</c>
    /// so the C# setter accessor carries the stricter OS guard even when the property
    /// itself is available earlier (e.g., WorkoutKit.PowerThresholdAlert.metric: getter
    /// iOS 17.0, setter iOS 17.4). Returns true when one or more attributes were emitted,
    /// letting the caller know the matching <see cref="EmitSetterAccessorAvailabilityEpilogue"/>
    /// must follow the set accessor.
    /// </summary>
    public static bool EmitSetterAccessorAvailability(
        CSharpWriter csWriter,
        IReadOnlyList<AvailabilityAnnotation>? propertyAvailability,
        IReadOnlyList<AvailabilityAnnotation>? setterAvailability)
    {
        // Lift both sides' explicit macCatalyst floors so the setter-vs-property comparison and
        // the emitted accessor attribute use the floor the @_cdecl setter wrapper is exported at —
        // see AvailabilityHelpers.LiftMacCatalystFloorToIOS.
        setterAvailability = AvailabilityHelpers.LiftMacCatalystFloorToIOS(setterAvailability);
        if (setterAvailability == null || setterAvailability.Count == 0)
            return false;
        propertyAvailability = AvailabilityHelpers.LiftMacCatalystFloorToIOS(propertyAvailability);

        var propPlatforms = new Dictionary<string, string>(StringComparer.Ordinal);
        if (propertyAvailability != null)
        {
            foreach (var ann in propertyAvailability)
            {
                if (ann.Platform != null && ann.IntroducedVersion != null)
                    propPlatforms[ann.Platform] = ann.IntroducedVersion;
            }
        }

        var tighter = new List<(string platform, string version)>();
        foreach (var ann in setterAvailability)
        {
            if (ann.Platform == null || ann.IntroducedVersion == null)
                continue;
            if (!PlatformMapping.TryGetValue(ann.Platform, out var dotnetPlatform))
                continue;
            // Only emit when strictly tighter than the property-level annotation —
            // attributes that merely repeat the property's versions are redundant.
            if (propPlatforms.TryGetValue(ann.Platform, out var propVersion) &&
                !IsStrictlyNewer(ann.IntroducedVersion, propVersion))
                continue;
            tighter.Add((dotnetPlatform, ann.IntroducedVersion));
        }

        if (tighter.Count == 0)
            return false;

        // CA1416 does not narrow callsite OS availability based on accessor-level
        // [SupportedOSPlatform] attributes — the analyzer still treats the set body
        // as reachable from the enclosing property's (looser) floor. Suppress the
        // warning for the backing-method call inside the setter body; consumers
        // still get a proper CA1416 diagnostic at their own call site because the
        // accessor attribute DOES narrow the consumer-facing surface.
        csWriter.WriteLine("#pragma warning disable CA1416");
        foreach (var (dotnetPlatform, version) in tighter)
        {
            var platformVersion = $"{dotnetPlatform}{NormalizeVersion(version)}";
            csWriter.WriteLine($"[global::System.Runtime.Versioning.SupportedOSPlatform(\"{platformVersion}\")]");
        }
        return true;
    }

    /// <summary>
    /// Closes a CA1416 pragma pair opened by <see cref="EmitSetterAccessorAvailability"/>.
    /// Must be called immediately after the set accessor body when that helper returned true.
    /// </summary>
    public static void EmitSetterAccessorAvailabilityEpilogue(CSharpWriter csWriter)
    {
        csWriter.WriteLine("#pragma warning restore CA1416");
    }

    private static bool IsStrictlyNewer(string candidate, string baseline)
    {
        var c = ParseVersion(NormalizeVersion(candidate));
        var b = ParseVersion(NormalizeVersion(baseline));
        for (int i = 0; i < 4; i++)
        {
            if (c[i] != b[i])
                return c[i] > b[i];
        }
        return false;
    }

    private static int[] ParseVersion(string v)
    {
        var parts = v.Split('.');
        var result = new int[4];
        for (int i = 0; i < parts.Length && i < 4; i++)
        {
            int.TryParse(parts[i], out result[i]);
        }
        return result;
    }

    /// <summary>
    /// Returns a deprecation message for EmitSafetyObsolete to consume (method-level merge).
    /// Returns null if no deprecation annotations exist.
    /// </summary>
    public static string? GetDeprecationMessage(BaseDecl decl)
    {
        if (decl.AvailabilityAnnotations == null)
            return null;

        foreach (var annotation in decl.AvailabilityAnnotations)
        {
            if (annotation.IsUnconditionallyDeprecated)
                return BuildDeprecationMessage(annotation) ?? "Deprecated in Swift.";
        }
        return null;
    }

    private static string? BuildDeprecationMessage(AvailabilityAnnotation annotation)
    {
        if (annotation.Renamed != null)
            return $"Use {annotation.Renamed} instead.";
        if (annotation.Message != null)
            return annotation.Message;
        return null;
    }

    private static string NormalizeVersion(string version)
    {
        // Ensure version always has at least major.minor (e.g., "13" → "13.0")
        if (!version.Contains('.'))
            return version + ".0";
        return version;
    }
}
