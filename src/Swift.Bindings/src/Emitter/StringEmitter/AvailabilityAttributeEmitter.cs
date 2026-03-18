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

        // Collect parent's platform annotations for dedup
        var parentPlatforms = new HashSet<string>();
        if (parentDecl?.AvailabilityAnnotations != null)
        {
            foreach (var pa in parentDecl.AvailabilityAnnotations)
            {
                if (pa.Platform != null && pa.IntroducedVersion != null && PlatformMapping.ContainsKey(pa.Platform))
                    parentPlatforms.Add($"{PlatformMapping[pa.Platform]}{NormalizeVersion(pa.IntroducedVersion)}");
            }
        }

        bool obsoleteEmitted = false;
        foreach (var annotation in decl.AvailabilityAnnotations)
        {
            // Skip visionOS (no .NET equivalent yet)
            if (annotation.Platform == "visionOS")
                continue;

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
