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

    /// Display names for the runtime-guard exception message, keyed by .NET platform id.
    private static readonly Dictionary<string, string> PlatformDisplayNames = new(StringComparer.Ordinal)
    {
        ["ios"] = "iOS",
        ["macos"] = "macOS",
        ["tvos"] = "tvOS",
        ["watchos"] = "watchOS",
        ["maccatalyst"] = "Mac Catalyst",
        ["visionos"] = "visionOS",
    };

    /// <summary>
    /// Emits a runtime OS-version guard at the top of a generated member body that throws
    /// <see cref="System.PlatformNotSupportedException"/> when the current OS is below the
    /// member's <b>effective</b> availability floor. <paramref name="effectiveAnnotations"/> must
    /// be the member's availability MERGED with every enclosing type's (e.g. via
    /// <see cref="AvailabilityHelpers.MergeAvailabilityFromAncestors"/>) — the guard emits one
    /// clause per floored platform with NO dedup against the parent type.
    ///
    /// <para>The guard deliberately does NOT dedup against the enclosing type's floor the way the
    /// <c>[SupportedOSPlatform]</c> attribute emitters (<see cref="EmitAvailabilityAttributes"/> /
    /// <see cref="EmitSupportedOSPlatformsFromAnnotations"/>) do. That dedup is correct for the
    /// attribute because C# type-nesting genuinely inherits a type's <c>[SupportedOSPlatform]</c>
    /// onto its members at COMPILE time — but there is no equivalent at RUNTIME. A static method,
    /// constructor, or operator on an OS-gated type is reachable on an older OS with no metadata
    /// access in between, so its weak-linked <c>@_cdecl</c> symbol can still be null even though
    /// the member itself declares no stricter floor. The guard must therefore fire on the full
    /// inherited floor, not just the portion the member adds beyond its parent.</para>
    ///
    /// <para>Why this is necessary at all: <c>[SupportedOSPlatform]</c> is a COMPILE-TIME analyzer
    /// hint only (CA1416). At runtime, a Swift symbol whose availability floor is newer than the
    /// binary's minimum-OS is weak-linked and resolves to null on an older OS; our generated
    /// <c>@_cdecl</c> wrapper body calls it unconditionally, so the call lands on a null function
    /// pointer and SIGSEGVs (pc=0) — a native fault that no C# <c>try/catch</c> can intercept.
    /// Throwing a managed exception BEFORE the P/Invoke converts that uncatchable crash into a
    /// catchable, self-explanatory error. The guard uses the platform-agnostic
    /// <c>OperatingSystem.IsOSPlatform</c>/<c>IsOSPlatformVersionAtLeast</c> APIs (which cover
    /// every Apple platform uniformly, including visionOS) and only fires on a platform that is
    /// explicitly floored — platforms covered by Swift's trailing <c>*</c> are left unrestricted,
    /// matching <c>@available(iOS X, *)</c> semantics.</para>
    /// </summary>
    public static void EmitRuntimeAvailabilityGuard(
        CSharpWriter csWriter,
        IReadOnlyList<AvailabilityAnnotation>? effectiveAnnotations,
        string apiDescription)
    {
        var guarded = ResolveStrictestFloors(effectiveAnnotations);
        if (guarded.Count == 0)
            return;

        var condition = BuildBelowFloorCondition(guarded);

        var floors = string.Join(" / ", guarded.Select(g =>
            $"{(PlatformDisplayNames.TryGetValue(g.platform, out var disp) ? disp : g.platform)} {g.version}"));
        var message = $"{apiDescription} is not available on this OS version; it requires {floors} or later.";

        csWriter.WriteLine($"if ({condition})");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"throw new global::System.PlatformNotSupportedException(\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(message)}\");");
        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    /// <summary>
    /// Builds the boolean expression that is true when the type's effective availability floor is
    /// SATISFIED on the running OS — the positive form of the runtime guard's "below floor" test.
    /// Returns <c>null</c> when the type carries no platform floor (always available), letting the
    /// caller emit the operation unconditionally. Used by the module initializer to wrap eager
    /// generic registration / metadata warmup of an OS-gated <c>ISwiftObject</c> type so an
    /// availability-blind launch-time touch cannot abort on a host OS below the floor — a native
    /// Mono generic-instantiation abort that no managed <c>try/catch</c> can intercept.
    /// </summary>
    public static string? BuildIsAvailableCondition(
        IReadOnlyList<AvailabilityAnnotation>? effectiveAnnotations)
    {
        var guarded = ResolveStrictestFloors(effectiveAnnotations);
        if (guarded.Count == 0)
            return null;

        return $"!({BuildBelowFloorCondition(guarded)})";
    }

    /// <summary>
    /// Resolves the strictest introduced-version per .NET platform from a set of Swift @available
    /// annotations, lifting macCatalyst→iOS so the floor matches the floor the @_cdecl wrapper is
    /// exported at. Returns a stably-ordered list of (platform, normalized-version) pairs; empty
    /// when the annotations declare no platform floor.
    /// </summary>
    private static List<(string platform, string version)> ResolveStrictestFloors(
        IReadOnlyList<AvailabilityAnnotation>? effectiveAnnotations)
    {
        // Lift macCatalyst→iOS exactly as the attribute/Swift-availability emitters do, so the
        // guarded floor matches the floor the @_cdecl wrapper is actually exported at.
        var annotations = AvailabilityHelpers.LiftMacCatalystFloorToIOS(effectiveAnnotations);
        if (annotations == null || annotations.Count == 0)
            return new List<(string, string)>();

        // Strictest introduced-version per .NET platform.
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

        // Stable order for deterministic output.
        return strictest
            .Select(kv => (platform: kv.Key, version: NormalizeVersion(kv.Value)))
            .OrderBy(g => g.platform, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Renders the "running below floor" boolean expression: one OR'd clause per floored platform,
    /// each true iff we are running ON that platform BELOW its floor. Platforms not listed (Swift's
    /// trailing <c>*</c>) match no clause, so the expression is false there. Emits every numeric
    /// component the floor declares (major.minor[.build[.revision]]) so a patch-level floor like
    /// iOS 17.4.1 is not silently rounded down to 17.4 and under-fired.
    /// </summary>
    private static string BuildBelowFloorCondition(List<(string platform, string version)> guarded)
    {
        var conditions = guarded.Select(g =>
            $"(global::System.OperatingSystem.IsOSPlatform(\"{g.platform}\") && " +
            $"!global::System.OperatingSystem.IsOSPlatformVersionAtLeast(\"{g.platform}\", {BuildVersionArguments(g.version)}))");
        return string.Join(" || ", conditions);
    }

    /// <summary>
    /// Renders the numeric arguments for <c>OperatingSystem.IsOSPlatformVersionAtLeast</c> from a
    /// normalized version string: always at least <c>major, minor</c>, plus the build and revision
    /// components when the floor declares them (so <c>17.4.1</c> → <c>17, 4, 1</c>, not <c>17, 4</c>).
    /// </summary>
    private static string BuildVersionArguments(string normalizedVersion)
    {
        var declaredComponents = normalizedVersion.Split('.').Length;
        var count = Math.Clamp(declaredComponents, 2, 4);
        var parsed = ParseVersion(normalizedVersion);
        return string.Join(", ", parsed.Take(count));
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
