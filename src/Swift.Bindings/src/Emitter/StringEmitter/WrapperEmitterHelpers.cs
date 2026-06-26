// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared helpers for the wrapper emitters (Method, Constructor, Property, Subscript).
/// Contains code-emission utilities that are identical across all wrapper types.
/// </summary>
public static class WrapperEmitterHelpers
{
    /// <summary>
    /// Emits the @MainActor (if needed) and @_cdecl annotations for a Swift wrapper function.
    /// Consolidates the identical annotation pattern used by MethodWrapperEmitter,
    /// PropertyWrapperEmitter, and ConstructorWrapperEmitter.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer for the wrapper .swift file.</param>
    /// <param name="symbolName">The @_cdecl symbol name string.</param>
    /// <param name="needsMainActor">Whether to prepend @MainActor before @_cdecl.</param>
    public static void EmitCdeclAnnotation(SwiftWriter swiftWriter, string symbolName, bool needsMainActor,
        IReadOnlyList<AvailabilityAnnotation>? availabilityAnnotations = null)
    {
        // Emit Swift @available annotations so the wrapper compiles on device SDKs
        // where the wrapped API may have platform availability requirements.
        EmitSwiftAvailability(swiftWriter, availabilityAnnotations);

        if (needsMainActor)
        {
            swiftWriter.WriteLine("@MainActor");
        }

        swiftWriter.WriteLines($$"""
            @_cdecl("{{symbolName}}")
            """);
    }

    /// <summary>
    /// Emits Swift @available annotations for a @_cdecl wrapper function.
    /// Without these, the wrapper calls an API that requires a newer OS version
    /// and fails to compile on device SDKs with stricter availability checking.
    ///
    /// For each platform, emits only the MAX (strictest) introduced version across
    /// all annotations. CSM wrappers stack annotations from containing-type + method +
    /// per-conformer sources, and any platform with conflicting floors (e.g., HMAC =
    /// iOS 13 plus SHA3_256 = iOS 26) must pick up the stricter iOS 26 floor so the
    /// generated Swift call-site passes availability-checking on device SDKs.
    /// </summary>
    internal static void EmitSwiftAvailability(SwiftWriter swiftWriter, IReadOnlyList<AvailabilityAnnotation>? annotations)
    {
        foreach (var key in CollectStrictestAvailabilityKeys(annotations))
            swiftWriter.WriteLine($"@available({key}, *)");
    }

    /// <summary>
    /// Computes the strictest (max) introduced version per platform across
    /// <paramref name="annotations"/>, returning one <c>"Platform Version"</c>
    /// entry per platform. Used by both <see cref="EmitSwiftAvailability"/> and
    /// <see cref="BuildAvailabilityHeredocPrefix"/> so multi-source availability
    /// (parent + method + conformer) collapses consistently.
    /// </summary>
    internal static IReadOnlyList<string> CollectStrictestAvailabilityKeys(IReadOnlyList<AvailabilityAnnotation>? annotations)
    {
        // Mac Catalyst tracks iOS for the unified-SDK era (iOS 13.0+): an API gated above its
        // explicit macCatalyst floor must lift that floor or the `@_cdecl` fails to compile for
        // `-target arm64-apple-ios<X>-macabi`. Routed through the shared helper so the Swift
        // `@available` and the C# `[SupportedOSPlatform]` emitters apply one identical rule —
        // see AvailabilityHelpers.LiftMacCatalystFloorToIOS.
        annotations = AvailabilityHelpers.LiftMacCatalystFloorToIOS(annotations);
        if (annotations == null || annotations.Count == 0)
            return Array.Empty<string>();

        var perPlatformMax = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var annotation in annotations)
        {
            if (annotation.Platform == null || annotation.IntroducedVersion == null)
                continue;
            if (!perPlatformMax.TryGetValue(annotation.Platform, out var existing)
                || AvailabilityHelpers.CompareOsVersions(annotation.IntroducedVersion, existing) > 0)
            {
                perPlatformMax[annotation.Platform] = annotation.IntroducedVersion;
            }
        }

        var result = new List<string>(perPlatformMax.Count);
        foreach (var kvp in perPlatformMax)
            result.Add($"{kvp.Key} {kvp.Value}");
        return result;
    }

    /// <summary>
    /// Builds the inline <c>#available(...)</c> guard expression a sibling-fan-out branch
    /// needs when the branch's strictest-per-platform floor is greater than the enclosing
    /// extension's floor on any platform. Returns the empty string when no guard is needed
    /// (branch floor is at or below the extension floor on every platform).
    ///
    /// <para>The returned expression is suitable for chaining inside an <c>if</c> /
    /// <c>else if</c> condition list, e.g. <c>else if #available(iOS 15.4, *), let fn = ...</c>.
    /// Without it, the fan-out body — emitted inside <c>extension EveryProtocol: Owner</c>
    /// at the owner's <c>@available</c> floor — would reference a sibling protocol type
    /// with a stricter floor and fail to compile on SDKs where the sibling is unavailable
    /// (regression first observed on MusicKit's AlbumFilter / CuratorFilter / LibraryArtistFilter
    /// sibling group, where AlbumFilter sits at iOS 15.0 but CuratorFilter is iOS 15.4+ and
    /// LibraryArtistFilter is iOS 16.0+).</para>
    /// </summary>
    public static string BuildBranchAvailabilityGuard(
        IReadOnlyList<AvailabilityAnnotation>? branchAnnotations,
        IReadOnlyList<AvailabilityAnnotation>? extensionAnnotations)
    {
        var branchKeys = CollectStrictestAvailabilityKeys(branchAnnotations);
        if (branchKeys.Count == 0)
            return string.Empty;

        var extensionByPlatform = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in CollectStrictestAvailabilityKeys(extensionAnnotations))
        {
            var parts = key.Split(' ', 2);
            if (parts.Length == 2)
                extensionByPlatform[parts[0]] = parts[1];
        }

        var guardEntries = new List<string>();
        foreach (var branchKey in branchKeys)
        {
            var parts = branchKey.Split(' ', 2);
            if (parts.Length != 2) continue;
            var platform = parts[0];
            var branchVersion = parts[1];
            if (!extensionByPlatform.TryGetValue(platform, out var extVersion)
                || CompareOsVersions(branchVersion, extVersion) > 0)
            {
                guardEntries.Add(branchKey);
            }
        }

        if (guardEntries.Count == 0)
            return string.Empty;
        return "#available(" + string.Join(", ", guardEntries) + ", *)";
    }

    /// <summary>
    /// Builds a full <c>#available(...)</c> runtime-guard expression covering every
    /// strictest-per-platform floor in <paramref name="annotations"/>, or the empty string
    /// when there are no floors (the type is available at the module's deployment target).
    ///
    /// <para>Unlike <see cref="BuildBranchAvailabilityGuard"/>, this is NOT relative to an
    /// enclosing extension floor — it guards the body of a top-level <c>@_cdecl</c> wrapper,
    /// whose enclosing scope carries no availability context, so every per-platform floor is
    /// emitted. The wrapper must NOT also carry a declaration-level <c>@available</c>: that
    /// would raise the wrapper's own availability context to the floor and make this inner
    /// <c>#available</c> always-true (Swift "unnecessary check ... guard will always be true"),
    /// dead-coding the else branch the guard exists to reach.</para>
    /// </summary>
    public static string BuildAvailabilityGuardExpression(IReadOnlyList<AvailabilityAnnotation>? annotations)
    {
        var keys = CollectStrictestAvailabilityKeys(annotations);
        if (keys.Count == 0)
            return string.Empty;
        return "#available(" + string.Join(", ", keys) + ", *)";
    }

    /// <summary>
    /// Renders the strictest-per-platform availability floors as a human-readable
    /// "iOS 26.2, macOS 26.2" string for diagnostic messages (e.g. the
    /// <see cref="System.PlatformNotSupportedException"/> thrown when a gated type's
    /// metadata is requested below its floor). Returns the empty string when there are
    /// no floors.
    /// </summary>
    public static string DescribeAvailabilityFloors(IReadOnlyList<AvailabilityAnnotation>? annotations)
        => string.Join(", ", CollectStrictestAvailabilityKeys(annotations));

    /// <summary>
    /// Compares two dot-separated OS version strings numerically (e.g., "13.0" &lt; "26.0").
    /// Returns 1 when <paramref name="left"/> &gt; <paramref name="right"/>, -1 when smaller,
    /// 0 when equal. Missing components are treated as 0 so "13" == "13.0".
    /// </summary>
    private static int CompareOsVersions(string left, string right)
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
    /// Builds a heredoc-interpolation prefix that emits <c>@available(...)</c> lines
    /// immediately before a top-level Swift declaration. When there are no annotations,
    /// returns the empty string so the interpolated heredoc compiles unchanged.
    /// Otherwise returns a string like <c>"@available(iOS 16.0, *)\n{indent}"</c>, deduped
    /// by platform+version. Used for emitting availability on protocol vtable accessors,
    /// EveryProtocol extensions, and witness table getters — all of which are top-level
    /// declarations that don't inherit their protocol's availability.
    /// </summary>
    public static string BuildAvailabilityHeredocPrefix(IReadOnlyList<AvailabilityAnnotation>? annotations, string heredocIndent)
    {
        if (annotations == null || annotations.Count == 0)
            return string.Empty;

        // Use the strictest (max) introduced version per platform so stacked annotations
        // (parent + method + per-conformer) collapse to one line per platform with the
        // tightest floor. Matches EmitSwiftAvailability so multi-source availability
        // dedupes consistently across both call sites.
        var keys = CollectStrictestAvailabilityKeys(annotations);
        if (keys.Count == 0)
            return string.Empty;

        var parts = new List<string>(keys.Count);
        foreach (var key in keys)
            parts.Add($"@available({key}, *)");
        return string.Join("\n" + heredocIndent, parts) + "\n" + heredocIndent;
    }

    /// <summary>
    /// Merges member-level and parent-type availability annotations for a @_cdecl wrapper.
    /// @_cdecl wrappers are top-level Swift functions and do NOT inherit the enclosing
    /// type's availability, so both must be explicitly applied to the wrapper.
    ///
    /// Delegates to <see cref="AvailabilityHelpers.MergeAvailability"/>; kept here so existing
    /// emitter call sites read naturally. EmitSwiftAvailability dedupes by platform+version
    /// key, so repeated entries from intermediate ancestors are collapsed automatically.
    /// </summary>
    public static IReadOnlyList<AvailabilityAnnotation>? MergeAvailability(
        IReadOnlyList<AvailabilityAnnotation>? memberAnnotations,
        BaseDecl? parentDecl)
        => AvailabilityHelpers.MergeAvailability(memberAnnotations, parentDecl);

    /// <summary>
    /// Walks the parent chain of <paramref name="startDecl"/> and merges every TypeDecl's
    /// availability annotations with <paramref name="memberAnnotations"/>. Delegates to
    /// <see cref="AvailabilityHelpers.MergeAvailabilityFromAncestors"/>.
    /// </summary>
    public static IReadOnlyList<AvailabilityAnnotation>? MergeAvailabilityFromAncestors(
        IReadOnlyList<AvailabilityAnnotation>? memberAnnotations,
        BaseDecl? startDecl)
        => AvailabilityHelpers.MergeAvailabilityFromAncestors(memberAnnotations, startDecl);

    /// <summary>
    /// Builds a Swift where clause of constraints on the parent type's generic parameters,
    /// parsed from <see cref="MethodDecl.RawGenericSig"/>. Emitted on the wrapper's
    /// <c>extension ... { }</c> line so that the body call resolves to the correct
    /// specialization. Without this, a specialized wrapper (e.g. Tips.Event.donate()
    /// requiring <c>DonationInfo == EmptyDonation</c>) emitted in a generic extension
    /// fails Swift type-checking because the constraint is never re-established.
    /// <para>
    /// Same-type (<c>==</c>) clauses are always emitted — they are never expressible at the
    /// parent type's own declaration on a single generic param. Conformance (<c>:</c>) clauses
    /// are emitted only when <paramref name="includeConformanceConstraints"/> is true, and only
    /// when the constraint is strictly stricter than the parent type's own declaration (any
    /// target already declared on the parent generic param is skipped to avoid a redundant
    /// conditional-conformance constraint). The conformance path is used by the generic static
    /// factory constructor emitter, whose <c>extension Parent: _SBW_GSF_x</c> conformance must
    /// inherit a constructor-only conformance constraint (e.g.
    /// <c>MusicCatalogResourceRequest.init() where MusicItemType : MusicCatalogTopLevelResourceRequesting</c>)
    /// before <c>Self()</c> in the factory body type-checks. Stdlib <c>@_marker</c> conformances
    /// (<c>Sendable</c> etc.) are an exception: they are NOT emitted, because a non-marker
    /// protocol's conditional conformance may not depend on a marker (Swift rejects it) and a
    /// marker has no runtime witness, so the unconditional conformance is both legal and correct.
    /// </para>
    /// Returns an empty string when no applicable parent constraint exists.
    /// </summary>
    public static string BuildParentSameTypeExtensionWhere(
        MethodDecl methodDecl, TypeDecl? parentType, bool includeConformanceConstraints = false)
    {
        if (parentType?.GenericParameters == null || parentType.GenericParameters.Count == 0)
            return string.Empty;

        // The requirement roots in `methodDecl.ParsedGenericSignature` come from the RAW
        // `RawGenericSig` (api-digester form), so their `SubjectRoot` is the raw token
        // (`τ_0_0`), NOT the sugared name. The parent's generic params carry BOTH the raw
        // `TypeName` and the `SugaredTypeName` that the emitted `extension Parent` line refers
        // to params by. Match the requirement root against the raw token, but EMIT the sugared
        // name — `τ_0_0` is not a visible identifier in the generated Swift extension. (When a
        // parent has no sugared signature, `TypeName == SugaredTypeName == τ_0_0`, so the
        // legacy `τ_0_0`-fallback behaviour is preserved exactly.)
        var rawToSugared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in parentType.GenericParameters)
            rawToSugared[p.TypeName] = p.SugaredTypeName;

        // Per-param set of conformance targets already declared at the parent type level
        // (both module-qualified and short names), keyed by the same RAW token the requirement
        // root is matched against. A constructor-only conformance clause whose target is already
        // required by the parent must be skipped — re-stating it on the conditional conformance
        // is a redundant-constraint error in Swift.
        var parentLevelConstraints = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (includeConformanceConstraints)
        {
            foreach (var p in parentType.GenericParameters)
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var gc in p.GenericConformances)
                {
                    if (gc.Kind != ConformanceKind.Protocol) continue;
                    var target = gc.ConformanceTarget;
                    if (string.IsNullOrEmpty(target.Name)) continue;
                    set.Add(target.ModuleQualifiedName);
                    set.Add(target.Name);
                }
                parentLevelConstraints[p.TypeName] = set;
            }
        }

        var clauses = new List<string>();
        // Finding 19: query the parsed signature instead of re-splitting the raw `where` text. Only
        // DIRECT constraints on a parent generic param are emitted; the target is reproduced verbatim
        // — the parser preserves a generic same-type target's inner punctuation (e.g.
        // `== Swift.Dictionary<Swift.String, Swift.Int>`), so the emitted Swift `where` clause stays
        // valid.
        foreach (var r in methodDecl.ParsedGenericSignature.Requirements)
        {
            // Match the requirement's RAW root token (`τ_0_0`) against the parent's raw param
            // names, but emit the SUGARED name — the generated `extension Parent` refers to its
            // params by their sugared identifiers, and `τ_0_0` is not a visible identifier there.
            if (!r.IsDirect || !rawToSugared.TryGetValue(r.SubjectRoot, out var paramName)) continue;

            if (r.Kind == GenericRequirementKind.SameType)
            {
                clauses.Add($"{paramName} == {r.Target}");
                continue;
            }

            if (!includeConformanceConstraints) continue;
            // Stdlib MARKER protocols (Sendable/Escapable/Copyable/…) are dropped, NOT emitted.
            // `_SBW_GSF_*` is an ordinary (non-marker) protocol, and Swift forbids a non-marker
            // protocol's conditional conformance from depending on a marker protocol
            // ("conditional conformance to non-marker protocol '…' cannot depend on conformance
            // of '…' to marker protocol 'Sendable'") — emitting `where Value : Swift.Sendable`
            // fails to compile, so the wrapper is stripped and the C# constructor is left
            // dangling. Markers carry no runtime witness anyway, so the factory body type-checks
            // without the clause and unconditional erased dispatch is correct. This mirrors the
            // parser's own marker-drop (`GenericSignatureParser.ParseConstraint`) and the shared
            // `IsStdlibMarkerProtocol` set. Real protocol constraints stricter than the parent's
            // declaration never reach here — they are refused upstream by
            // `HasUnsatisfiableParentGenericExtensionConstraint`, which ALSO refuses the one marker
            // an unconditional GSF body cannot satisfy (`BitwiseCopyable`, via
            // `HasUnerasableParentMarkerConstraint`): so for BitwiseCopyable this drop is defensive
            // (the constructor is gone before the where clause is built); for the erasure-safe
            // markers (Sendable/Copyable/Escapable/SendableMetatype) the drop is load-bearing.
            if (IsStdlibMarkerProtocol(r.Target)) continue;
            // Skip constraints the parent already requires — re-stating them is redundant.
            if (parentLevelConstraints.TryGetValue(r.SubjectRoot, out var declared) && declared.Contains(r.Target))
                continue;
            clauses.Add($"{paramName} : {r.Target}");
        }
        return clauses.Count == 0 ? string.Empty : " where " + string.Join(", ", clauses);
    }

    /// <summary>
    /// True if <paramref name="target"/> is a stdlib <c>@_marker</c> protocol (carries no runtime
    /// witness table). Operates on the verbatim conformance-target string from the parsed signature
    /// (e.g. <c>Swift.Sendable</c> or bare <c>Sendable</c>). Kept in sync with the canonical set in
    /// <c>GenericTypeEmitter.IsStdlibMarkerProtocol</c> / <c>PInvokeHelperEmitter.IsStdlibMarkerProtocol</c>
    /// / <c>GenericSignatureParser.ParseConstraint</c>. Deliberately excludes the layout constraints
    /// <c>AnyObject</c>/<c>Any</c>: those are NOT marker protocols, so a conditional conformance may
    /// legally depend on them and their factory body needs the clause to type-check.
    /// </summary>
    private static bool IsStdlibMarkerProtocol(string target)
    {
        var lastDot = target.LastIndexOf('.');
        var module = lastDot >= 0 ? target[..lastDot] : null;
        var simpleName = lastDot >= 0 ? target[(lastDot + 1)..] : target;
        return (module is null or "Swift")
            && simpleName is "Sendable" or "Escapable" or "Copyable"
                          or "SendableMetatype" or "BitwiseCopyable";
    }

    /// <summary>
    /// Builds a Swift where clause from generic parameter constraints.
    /// Returns an empty string if no constraints exist, or " where T : Proto, U : Proto2" etc.
    /// </summary>
    /// <param name="genericParams">The generic parameters with conformance information.</param>
    /// <param name="moduleQualify">When true, uses module-qualified conformance names (e.g., Module.ProtocolName).
    /// Use true for free functions, false for code inside an extension of the module's type.</param>
    public static string BuildSwiftWhereClause(IEnumerable<GenericArgumentDecl> genericParams, bool moduleQualify = false)
    {
        var clauses = new List<string>();
        foreach (var p in genericParams)
        {
            foreach (var gc in p.GenericConformances)
            {
                var target = moduleQualify ? gc.ConformanceTarget.ModuleQualifiedName : gc.ConformanceTarget.Name;
                clauses.Add($"{p.SugaredTypeName} : {target}");
            }
            foreach (var tc in p.AssosiatedTypeConformances)
            {
                var target = moduleQualify ? tc.ConformanceTarget.ModuleQualifiedName : tc.ConformanceTarget.Name;
                var op = tc.Kind == ConformanceKind.Protocol ? " : " : " == ";
                clauses.Add($"{p.SugaredTypeName}.{string.Join(".", tc.Path.Skip(1))}{op}{target}");
            }
        }
        return clauses.Count > 0 ? " where " + string.Join(", ", clauses) : "";
    }

    /// <summary>
    /// Returns the Swift code lines for a tag-only enum return as a list of strings.
    /// Tag-only enums (no RawRepresentable conformance) have a memory layout smaller than
    /// the cdecl return type (e.g., a 4-case enum is 1 byte, but the return type is Int/8 bytes).
    /// Using <c>UnsafeRawPointer.load(as: Int.self)</c> reads past the enum's allocation,
    /// causing "load from misaligned raw pointer" crashes on ARM64. Instead, zero-initialize
    /// an Int and copy only the enum's actual bytes into it. The single source of truth for the
    /// tag-only return shape; <see cref="CdeclReturnRenderer"/> consumes it for all @_cdecl
    /// return rendering (writer and lines forms alike).
    /// </summary>
    /// <param name="callExpr">The Swift expression that produces the enum value.</param>
    /// <param name="cdeclReturnType">The cdecl return type name (e.g., "Int").</param>
    public static List<string> GetTagOnlyEnumReturnLines(string callExpr, string cdeclReturnType)
    {
        return new List<string>
        {
            $"var result = {callExpr}",
            "let resultSize = MemoryLayout.size(ofValue: result)",
            $"var tag: {cdeclReturnType} = 0",
            "withUnsafeMutablePointer(to: &tag) { tagPtr in withUnsafePointer(to: &result) { resultPtr in UnsafeMutableRawPointer(tagPtr).copyMemory(from: UnsafeRawPointer(resultPtr), byteCount: resultSize) } }",
            "return tag"
        };
    }
}
