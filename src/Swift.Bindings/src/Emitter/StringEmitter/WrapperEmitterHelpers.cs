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
    /// before <c>Self()</c> in the factory body type-checks.
    /// </para>
    /// Returns an empty string when no applicable parent constraint exists.
    /// </summary>
    public static string BuildParentSameTypeExtensionWhere(
        MethodDecl methodDecl, TypeDecl? parentType, bool includeConformanceConstraints = false)
    {
        if (parentType?.GenericParameters == null || parentType.GenericParameters.Count == 0)
            return string.Empty;
        var sig = methodDecl.RawGenericSig;
        if (string.IsNullOrEmpty(sig))
            return string.Empty;

        var whereStart = sig.IndexOf(" where ", StringComparison.Ordinal);
        if (whereStart < 0)
            return string.Empty;
        var afterWhere = sig.Substring(whereStart + " where ".Length).TrimEnd('>');

        var parentParamNames = parentType.GenericParameters
            .Select(p => p.SugaredTypeName)
            .ToHashSet(StringComparer.Ordinal);

        // Per-param set of conformance targets already declared at the parent type level
        // (both module-qualified and short names). A constructor-only conformance clause whose
        // target is already required by the parent must be skipped — re-stating it on the
        // conditional conformance is a redundant-constraint error in Swift.
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
                parentLevelConstraints[p.SugaredTypeName] = set;
            }
        }

        var clauses = new List<string>();
        foreach (var rawClause in afterWhere.Split(','))
        {
            var clause = rawClause.Trim();
            var sameType = System.Text.RegularExpressions.Regex.Match(clause, @"^(\w+)\s*==\s*(.+)$");
            if (sameType.Success)
            {
                var paramName = sameType.Groups[1].Value;
                if (!parentParamNames.Contains(paramName)) continue;
                clauses.Add($"{paramName} == {sameType.Groups[2].Value.Trim()}");
                continue;
            }

            if (!includeConformanceConstraints) continue;

            var conformance = System.Text.RegularExpressions.Regex.Match(clause, @"^(\w+)\s*:\s*(.+)$");
            if (!conformance.Success) continue;
            var cParam = conformance.Groups[1].Value;
            if (!parentParamNames.Contains(cParam)) continue;
            var cTarget = conformance.Groups[2].Value.Trim();
            // Skip constraints the parent already requires — re-stating them is redundant.
            if (parentLevelConstraints.TryGetValue(cParam, out var declared) && declared.Contains(cTarget))
                continue;
            clauses.Add($"{cParam} : {cTarget}");
        }
        return clauses.Count == 0 ? string.Empty : " where " + string.Join(", ", clauses);
    }

    /// <summary>
    /// Builds a Swift where clause from generic parameter constraints.
    /// Returns an empty string if no constraints exist, or " where T : Proto, U : Proto2" etc.
    /// </summary>
    /// <param name="genericParams">The generic parameters with conformance information.</param>
    /// <param name="moduleQualify">When true, uses module-qualified conformance names (e.g., GRDB.Cursor).
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
    /// Emits a safe tag-only enum return for @_cdecl wrappers.
    /// Tag-only enums (no RawRepresentable conformance) have a memory layout smaller than
    /// the cdecl return type (e.g., a 4-case enum is 1 byte, but the return type is Int/8 bytes).
    /// Using <c>UnsafeRawPointer.load(as: Int.self)</c> reads past the enum's allocation,
    /// causing "load from misaligned raw pointer" crashes on ARM64.
    /// Instead, zero-initialize an Int and copy only the enum's actual bytes into it.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer for the wrapper .swift file.</param>
    /// <param name="callExpr">The Swift expression that produces the enum value.</param>
    /// <param name="cdeclReturnType">The cdecl return type name (e.g., "Int").</param>
    public static void EmitTagOnlyEnumReturn(SwiftWriter swiftWriter, string callExpr, string cdeclReturnType)
    {
        // Compute size BEFORE the closures to avoid Swift exclusivity checker error
        // ("overlapping accesses to 'result'") — MemoryLayout.size(ofValue:) reads result
        // while withUnsafePointer(to: &result) takes exclusive access.
        swiftWriter.WriteLine($"var result = {callExpr}");
        swiftWriter.WriteLine("let resultSize = MemoryLayout.size(ofValue: result)");
        swiftWriter.WriteLine($"var tag: {cdeclReturnType} = 0");
        swiftWriter.WriteLine("withUnsafeMutablePointer(to: &tag) { tagPtr in withUnsafePointer(to: &result) { resultPtr in UnsafeMutableRawPointer(tagPtr).copyMemory(from: UnsafeRawPointer(resultPtr), byteCount: resultSize) } }");
        swiftWriter.WriteLine("return tag");
    }

    /// <summary>
    /// Returns the Swift code lines for a tag-only enum return as a list of strings,
    /// for use in extension body lines (e.g., generic extension method emission).
    /// See <see cref="EmitTagOnlyEnumReturn"/> for the rationale.
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
