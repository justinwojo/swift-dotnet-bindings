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
    /// <summary>
    /// Whether <see cref="EmitAvailabilityAttributes"/> with <c>emitObsolete: true</c> will write a
    /// bare <c>[Obsolete]</c> for this declaration. <c>[Obsolete]</c> is <c>AllowMultiple = false</c>,
    /// so any site that emits its own diagnostic <c>[Obsolete]</c> onto a declaration that also
    /// carries availability attributes must consult this first — two of them is CS0579, not a
    /// warning.
    /// </summary>
    public static bool EmitsUnconditionalObsolete(BaseDecl decl) =>
        decl.AvailabilityAnnotations?.Any(a => a.IsUnconditionallyDeprecated) == true;

    public static void EmitAvailabilityAttributes(
        CSharpWriter csWriter, BaseDecl decl, BaseDecl? parentDecl = null, bool emitObsolete = true)
        => EmitAvailabilityAttributes(csWriter, decl.AvailabilityAnnotations, parentDecl, emitObsolete);

    /// <summary>
    /// Same as <see cref="EmitAvailabilityAttributes(CSharpWriter, BaseDecl, BaseDecl, bool)"/> but
    /// driven by an explicit annotation list rather than the declaration's own. Used where the
    /// governing floor is not the declaration's — notably a property's SETTER, whose floor comes
    /// from <see cref="AvailabilityHelpers.SelectSetterAnnotations"/>.
    /// </summary>
    public static void EmitAvailabilityAttributes(
        CSharpWriter csWriter,
        IReadOnlyList<AvailabilityAnnotation>? declAnnotations,
        BaseDecl? parentDecl = null,
        bool emitObsolete = true)
    {
        if (declAnnotations == null || declAnnotations.Count == 0)
            return;

        // Lift an explicit macCatalyst floor to the iOS floor so the C# attribute matches the
        // floor the @_cdecl wrapper is exported at; lift the parent the same way so the dedup
        // below compares effective (lifted) versions. See AvailabilityHelpers.LiftMacCatalystFloorToIOS.
        var annotations = AvailabilityHelpers.LiftMacCatalystFloorToIOS(declAnnotations)!;
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
    /// catchable, self-explanatory error. The null-symbol case is what a binding over an OS
    /// framework hits; when the Swift library ships inside the app the symbol is defined and the
    /// call would resolve, but the floor still holds — the body behind it is free to use APIs
    /// from its own OS version — so the guard is applied on the declared floor rather than on a
    /// per-binding-shape guess. The guard uses the platform-agnostic
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
    /// Builds a ONE-STATEMENT runtime availability guard for a member whose effective floor is
    /// stricter than the floor its enclosing type already guards, or <c>null</c> when the member
    /// adds nothing beyond that type. The statement throws
    /// <see cref="System.PlatformNotSupportedException"/> when the running OS is below the member's
    /// merged (member + enclosing type) floor — the same floor the member's
    /// <c>[SupportedOSPlatform]</c> attributes advertise.
    ///
    /// <para>The "stricter than parent" gate is deliberately
    /// <see cref="DeclaresStricterFloorThanParent(IReadOnlyList{AvailabilityAnnotation}, BaseDecl)"/>
    /// rather than a bare merge test: the enclosing generated type is itself gated at the parent's
    /// floor, so a member that adds no floor of its own is already covered and a second guard there
    /// would be dead code on every member of every OS-gated protocol. Only the member that raises
    /// the floor introduces a reachable window — the parent's floor satisfied, the member's not —
    /// and that window is exactly what this guards. The THROWN condition still uses the full merged
    /// floor, not just the added part, so the exception message names the real requirement.</para>
    ///
    /// <para>What the guard prevents depends on where the Swift forwarder's callee lives. In a
    /// binding over an OS framework, the symbol behind the forwarder is weak-linked and resolves
    /// to null below the floor, so a below-floor call lands on a null function pointer and faults
    /// natively — nothing a managed <c>try/catch</c> can intercept. In a binding over a library
    /// that ships inside the app, the forwarder and the witness it calls are both defined, so the
    /// call would have gone through; the floor still holds because Swift's own rule is that a
    /// requirement introduced after its protocol is callable only above its own floor, and the
    /// witness body may use APIs from that same OS version. The floor is applied uniformly rather
    /// than per binding shape, and the accepted cost is that on an in-app library a below-floor
    /// call that happened to work now throws instead.</para>
    ///
    /// <para>The message names the required floor and the remedy, since the exception is what the
    /// consumer sees first.</para>
    ///
    /// <para>Rendered as a single statement (rather than through
    /// <see cref="EmitRuntimeAvailabilityGuard"/>) so it can be interpolated into an accessor body
    /// that is written as one raw-string block.</para>
    /// </summary>
    public static string? BuildStricterFloorGuardStatement(
        IReadOnlyList<AvailabilityAnnotation>? memberAnnotations,
        BaseDecl? parentDecl,
        string apiDescription)
    {
        if (!DeclaresStricterFloorThanParent(memberAnnotations, parentDecl))
            return null;

        var guarded = ResolveStrictestFloors(
            AvailabilityHelpers.MergeAvailability(memberAnnotations, parentDecl));
        if (guarded.Count == 0)
            return null;

        var floors = string.Join(" / ", guarded.Select(g =>
            $"{(PlatformDisplayNames.TryGetValue(g.platform, out var disp) ? disp : g.platform)} {g.version}"));
        var message =
            $"{apiDescription} was introduced after the protocol that declares it and requires {floors} or later. " +
            "Call it behind an OS version check (OperatingSystem.IsOSPlatformVersionAtLeast, or a per-platform " +
            "helper such as OperatingSystem.IsIOSVersionAtLeast), or raise the app's minimum OS version.";

        return $"if ({BuildBelowFloorCondition(guarded)}) throw new global::System.PlatformNotSupportedException(" +
            $"\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(message)}\");";
    }

    /// <summary>
    /// Builds the guard statement plus the line break and relative indentation needed to prefix it
    /// onto the first statement of an accessor body written as one raw-string block, or the empty
    /// string when no guard is required. Emitting the guard as a prefix rather than as its own
    /// placeholder line keeps the un-guarded (overwhelmingly common) case byte-identical — a
    /// placeholder line would otherwise leave a whitespace-only line in every generated accessor.
    /// </summary>
    public static string BuildStricterFloorGuardPrefix(
        IReadOnlyList<AvailabilityAnnotation>? memberAnnotations,
        BaseDecl? parentDecl,
        string apiDescription,
        string continuationIndent)
    {
        var guard = BuildStricterFloorGuardStatement(memberAnnotations, parentDecl, apiDescription);
        return guard is null ? string.Empty : guard + "\n" + continuationIndent;
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
    /// Whether <paramref name="decl"/>'s effective availability floor is strictly newer than
    /// <paramref name="parentDecl"/>'s on at least one platform — i.e. the member declares a
    /// floor its enclosing type does not already guarantee, so a call to the member from code
    /// gated only at the parent's floor draws CA1416.
    ///
    /// <para>Both sides are compared after the macCatalyst→iOS lift and after collapsing to the
    /// strictest introduced version per platform, so the answer matches what
    /// <see cref="EmitAvailabilityAttributes"/> would actually write. A platform the member
    /// floors and the parent does not counts as stricter.</para>
    /// </summary>
    public static bool DeclaresStricterFloorThanParent(BaseDecl decl, BaseDecl? parentDecl)
        => DeclaresStricterFloorThanParent(decl.AvailabilityAnnotations, parentDecl);

    /// <summary>
    /// Annotation-list form of
    /// <see cref="DeclaresStricterFloorThanParent(BaseDecl, BaseDecl)"/>, for a floor that does not
    /// come from a declaration's own list — notably a property's SETTER
    /// (<see cref="AvailabilityHelpers.SelectSetterAnnotations"/>).
    /// </summary>
    public static bool DeclaresStricterFloorThanParent(
        IReadOnlyList<AvailabilityAnnotation>? memberAnnotations, BaseDecl? parentDecl)
    {
        var memberFloors = ResolveStrictestFloors(
            AvailabilityHelpers.MergeAvailability(memberAnnotations, parentDecl));
        if (memberFloors.Count == 0)
            return false;

        var parentFloors = ResolveStrictestFloors(parentDecl?.AvailabilityAnnotations)
            .ToDictionary(f => f.platform, f => f.version, StringComparer.Ordinal);

        foreach (var (platform, version) in memberFloors)
        {
            if (!parentFloors.TryGetValue(platform, out var parentVersion))
                return true;
            if (IsStrictlyNewer(version, parentVersion))
                return true;
        }

        return false;
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
        IReadOnlyList<AvailabilityAnnotation>? setterAvailability,
        bool emitCa1416Suppression = true)
    {
        var tighter = ResolveTighterSetterFloors(propertyAvailability, setterAvailability);
        if (tighter.Count == 0)
            return false;

        // CA1416 does not narrow callsite OS availability based on accessor-level
        // [SupportedOSPlatform] attributes — the analyzer still treats the set body
        // as reachable from the enclosing property's (looser) floor. Suppress the
        // warning for the backing-method call inside the setter body; consumers
        // still get a proper CA1416 diagnostic at their own call site because the
        // accessor attribute DOES narrow the consumer-facing surface. A bodiless
        // accessor (an interface requirement) has no such call to suppress, so the
        // caller can opt out and keep the pragma out of the emitted interface.
        if (emitCa1416Suppression)
            csWriter.WriteLine("#pragma warning disable CA1416");
        foreach (var (dotnetPlatform, version) in tighter)
        {
            var platformVersion = $"{dotnetPlatform}{NormalizeVersion(version)}";
            csWriter.WriteLine($"[global::System.Runtime.Versioning.SupportedOSPlatform(\"{platformVersion}\")]");
        }
        return true;
    }

    /// <summary>
    /// Whether a property's setter is gated at a strictly newer introduced version than the
    /// property itself on at least one platform — i.e. whether the <c>set</c> accessor needs its
    /// own <c>[SupportedOSPlatform]</c>. Asks exactly the question
    /// <see cref="EmitSetterAccessorAvailability"/> answers by emitting, so a caller that must
    /// decide the SHAPE of the property (single-line auto-accessors vs an accessor block) before
    /// writing anything cannot disagree with what the emitter would then write.
    /// </summary>
    public static bool SetterFloorIsStricterThanProperty(
        IReadOnlyList<AvailabilityAnnotation>? propertyAvailability,
        IReadOnlyList<AvailabilityAnnotation>? setterAvailability)
        => ResolveTighterSetterFloors(propertyAvailability, setterAvailability).Count > 0;

    /// <summary>
    /// The (platform, version) pairs on which the setter's introduced version is strictly newer
    /// than the property's. Both sides are macCatalyst→iOS lifted first so the comparison — and
    /// the attribute built from it — use the floor the <c>@_cdecl</c> setter wrapper is exported at.
    /// </summary>
    private static List<(string platform, string version)> ResolveTighterSetterFloors(
        IReadOnlyList<AvailabilityAnnotation>? propertyAvailability,
        IReadOnlyList<AvailabilityAnnotation>? setterAvailability)
    {
        var tighter = new List<(string platform, string version)>();

        setterAvailability = AvailabilityHelpers.LiftMacCatalystFloorToIOS(setterAvailability);
        if (setterAvailability == null || setterAvailability.Count == 0)
            return tighter;
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

        return tighter;
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
