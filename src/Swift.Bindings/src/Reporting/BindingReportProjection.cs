// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Rederives a <see cref="BindingReport"/> from a <see cref="BindingArtifactManifest"/>.
/// The manifest is the source of truth; the projected report reflects post-cogating
/// reality, not the generator's mid-pipeline guess.
/// </summary>
/// <remarks>
/// Each cogated member moves one slot from emitted to skipped: the top-level scalar
/// counts, the per-kind dictionaries, and the <see cref="BindingReport.SkippedItems"/>
/// list all stay internally consistent. Duplicates in the cogated lists are preserved
/// — overload-correct.
/// </remarks>
public static class BindingReportProjection
{
    public static BindingReport Project(BindingArtifactManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var report = new BindingReport { ModuleName = manifest.Module };

        if (manifest.Generation is { } g)
        {
            report.TotalTypes = g.TotalTypes;
            report.EmittedTypes = g.EmittedTypes;
            report.SkippedTypes = g.SkippedTypes;
            report.TotalMembers = g.TotalMembers;
            report.EmittedMembers = g.EmittedMembers;
            report.SkippedMembers = g.SkippedMembers;
            report.SynthesizedMembers = g.SynthesizedMembers;

            foreach (var kv in g.EmittedMembersByKind)
                report.EmittedMembersByKind[kv.Key] = kv.Value;
            foreach (var kv in g.SkippedMembersByKind)
                report.SkippedMembersByKind[kv.Key] = kv.Value;

            report.SkippedItems.AddRange(g.SkippedItems);
            report.WrappedItems.AddRange(g.WrappedItems);
            report.BridgedViews.AddRange(g.BridgedViews);
            report.ThemeBridgedProperties.AddRange(g.ThemeBridgedProperties);
            report.OverloadRenames.AddRange(g.OverloadRenames);
            report.BridgeSummary = g.BridgeSummary;
            // Finding 53: the projected report is what gets written to binding-report.json, so the
            // SWIFTBIND025/026 lists must be restored here or the diagnostics' "recorded under
            // unsupportedCommentDrops/objectDegradations in binding-report.json" promise is broken.
            report.UnsupportedCommentDrops.AddRange(g.UnsupportedCommentDrops);
            report.UnsupportedCommentDropDetails.AddRange(g.UnsupportedCommentDropDetails);
            report.ObjectDegradations.AddRange(g.ObjectDegradations);
            // F10 Stage 20: round-trip the ObjC-prefix bridge guesses so binding-report.json carries
            // the heuristic observability channel (same projection contract as the two lists above).
            report.ObjCPrefixBridges.AddRange(g.ObjCPrefixBridges);
            // The orphan-shell set is computed once emission has settled and cannot be rederived
            // from the manifest's other sections, so it has to be carried across verbatim. The count
            // is derived from the restored list rather than round-tripped separately, which keeps
            // the two from drifting apart on a partial manifest.
            report.ClosureOrphanShellTypes.AddRange(g.ClosureOrphanShellTypes);
            report.ClosureOrphanShellTypeCount = report.ClosureOrphanShellTypes.Count;
            // Which emitted members carry a safety marker, and which of those are load-bearing.
            // Both are settled-emission facts with no manifest-side inputs to rederive them from,
            // so they cross verbatim rather than being recomputed here.
            report.DegradedMembers.AddRange(g.DegradedMembers);
            report.DegradedSurface = g.DegradedSurface;
            report.WrapperRequirement = g.WrapperRequirement;
        }

        // Proxy-suppression and wrapper-symbol-contract co-gating are no longer post-pass
        // reconciliation steps: both are decided at emission, so the manifest carries no
        // ProxyCoGating/ContractCoGating section to project. The wrapper-compile strip leg
        // (below) is the only surviving co-gating reconciliation.
        if (manifest.Wrapper is { } w)
        {
            foreach (var member in w.CSharpCoGatedMembers)
            {
                var details = member.MangledSymbol != null
                    ? $"P/Invoke removed: wrapper symbol '{member.MangledSymbol}' was stripped from compiled wrapper."
                    : "P/Invoke removed: corresponding wrapper symbol was stripped from compiled wrapper.";
                ApplyCoGated(report, member, SkipReason.MissingWrapperSymbol, details);
            }

            ReconcileMarkedSurfaceAfterCoGating(report, w.CSharpCoGatedMembers, manifest.Module);
        }

        // A1: fold the ObjC binding surface's dropped symbols into the same skip list. For a mixed
        // binding these join the Swift drops; for a pure-ObjC binding (no Generation section) this is
        // the entire skip set. Either way they roll into the single SkipTriage/ReviewCount gate below,
        // so an ObjC-heavy library's drops are no longer invisible to the release signal. Update the
        // scalar roll-ups too (not just the flat list), so the persisted report stays internally
        // consistent — SkippedItems.Count == SkippedTypes + SkippedMembers — instead of leaving the
        // ObjC drops out of SkippedMembers/SkippedTypes (and zeroed on a pure-ObjC manifest). Types
        // count against SkippedTypes; members against SkippedMembers + the per-kind roll-up, mirroring
        // the Swift path's split (ReportCollector never puts a Type in SkippedMembersByKind).
        if (manifest.ObjC is { } objc)
        {
            foreach (var item in objc.SkippedItems)
            {
                report.SkippedItems.Add(item);
                if (item.Kind == BindingItemKind.Type)
                {
                    report.SkippedTypes += 1;
                }
                else
                {
                    report.SkippedMembers += 1;
                    AdjustKind(report.SkippedMembersByKind, item.Kind, +1);
                }
            }
        }

        // Attribute causes before rolling up, on the same settled list and for the same reason: only
        // now is every row present, so a root and its cascades can be told apart.
        SkipAttributionLinker.Link(report.SkippedItems);

        // Roll the settled skip list up by actionability last — after co-gating has folded every
        // wrapper-stripped member in — so the triage reflects final reality, not the mid-pipeline guess.
        report.SkipTriage = SkipTriageBuilder.Build(report.SkippedItems);

        return report;
    }

    /// <summary>
    /// Brings the safety-marker and wrapper-requirement facts back in line with a surface the
    /// wrapper-compile strip has since shrunk.
    ///
    /// <para>Both were settled while the co-gated members were still emitted, so both describe them:
    /// a degraded row says the member carries an <c>[Obsolete]</c> the binding no longer has, and the
    /// wrapper rationale counts it among the members that reach Swift. The same projection then lists
    /// it as skipped, so leaving these alone publishes a report that contradicts itself.</para>
    ///
    /// <para>Every count is rebuilt from the surviving rows rather than decremented, so no number
    /// can drift from the list it summarizes — including the marked count the wrapper rationale
    /// quotes, which is why the rationale is restated last, once both of its inputs have settled.
    /// Prominence scores are not recomputed — they were stamped on each row during generation from
    /// session state this projection does not have, and removing rows does not change what any
    /// surviving row scored.</para>
    ///
    /// <para>Removal is bounded per identity rather than matching every row with the same name: a
    /// degraded row carries no ordinal, so two marked overloads of one name are indistinguishable
    /// here, and a blanket match would withdraw both when the strip took one. Removing exactly as
    /// many rows as were co-gated under that name keeps the surviving overload in the report.</para>
    ///
    /// <para>The two sides do not name members the same way, and matching them raw silently removes
    /// nothing. The co-gated list is read back out of the generated C#, so it carries C# identities:
    /// the emitted member name, and a containing type that is the dot-joined nesting path with no
    /// namespace. The report rows come from the emitter's own Swift declarations: a Swift member name
    /// and a module-qualified containing type. So a row is probed under both spellings — its emitted
    /// name where the recording site captured one, and its containing type with the module prefix
    /// removed — and the first candidate with budget left is the one that spends it.</para>
    /// </summary>
    private static void ReconcileMarkedSurfaceAfterCoGating(
        BindingReport report, List<CoGatedMember> coGated, string moduleName)
    {
        if (coGated.Count == 0)
            return;

        var budget = new Dictionary<(BindingItemKind, string, string), int>();
        foreach (var member in coGated)
        {
            var key = (member.Kind, member.Name, member.ContainingType ?? string.Empty);
            budget[key] = budget.GetValueOrDefault(key) + 1;
        }

        RemoveUpToBudget(
            report.DegradedMembers,
            // A degraded row already carries the emitted C# name; only its containing type needs
            // translating.
            m => CoGatedIdentityCandidates(m.Kind, m.Name, null, m.ContainingType, moduleName),
            budget);
        RemoveUpToBudget(
            report.WrappedItems,
            w => CoGatedIdentityCandidates(w.Kind, w.Name, w.EmittedName, w.ContainingType, moduleName),
            budget);

        var rebuilt = new DegradedSurfaceSummary { Total = report.DegradedMembers.Count };
        foreach (var item in report.DegradedMembers)
        {
            rebuilt.ByDiagnosticId[item.DiagnosticId] =
                rebuilt.ByDiagnosticId.GetValueOrDefault(item.DiagnosticId) + 1;
            if (item.WrapperReason is { } wrapperReason)
                rebuilt.ByWrapperReason[wrapperReason] =
                    rebuilt.ByWrapperReason.GetValueOrDefault(wrapperReason) + 1;
        }

        rebuilt.TopDegradedMembers.AddRange(report.DegradedMembers
            .Where(m => !m.IsDeprecated && m.ProminenceScore > 0)
            .Take(ReportCollector.TopDegradedMemberCount));
        report.DegradedSurface = rebuilt;

        if (report.WrapperRequirement is not { } requirement)
            return;

        // Both inputs read off the lists this method just settled, so the restated sentence and the
        // numbers beside it come from one source. The marked count is the same SB0001+SB0009 sum
        // Evaluate took, re-taken over what survived.
        WrapperRequirementEvaluator.Restate(
            requirement,
            wrappedMemberCount: report.WrappedItems
                .Count(item => item.WrapperKind != ReportCollector.ClosureParamTombstoneWrapperKind),
            unwrappedMarkedMemberCount: rebuilt.ByDiagnosticId.GetValueOrDefault("SB0001")
                + rebuilt.ByDiagnosticId.GetValueOrDefault("SB0009"));
    }

    /// <summary>
    /// Every co-gated identity a report row could be the same member as, most specific first. Only
    /// the first one with budget left is spent, so a row withdraws at most once however many
    /// candidates it offers.
    /// </summary>
    /// <remarks>
    /// Two translations, both from the report's Swift domain into the co-gated list's C# domain.
    /// <para>
    /// The name: a row that captured what was emitted for it is probed under that first, since the
    /// generated C# can only have been read under the emitted spelling; the Swift name follows for
    /// rows that captured none and for the members whose two names coincide.
    /// </para>
    /// <para>
    /// The containing type: a module-qualified Swift type name minus its module prefix IS the C#
    /// nesting path, including for nested types (<c>M.Outer.Inner</c> → <c>Outer.Inner</c>), which is
    /// exactly what the co-gated side records. Two cases the strip cannot reach, and does not
    /// pretend to: a type whose C# name was renamed away from its Swift one, and a module-scope
    /// member, whose free-function class name is chosen during emission and is not recoverable here.
    /// Both simply fail to match, which is the pre-existing behaviour for every row — never a
    /// mismatched removal, because the untranslated spelling is still offered as a candidate and a
    /// wrong one would have to collide on kind and name as well.
    /// </para>
    /// </remarks>
    private static IEnumerable<(BindingItemKind, string, string)> CoGatedIdentityCandidates(
        BindingItemKind kind, string swiftName, string? emittedName, string? containingType, string moduleName)
    {
        var containers = new List<string>(2);
        var container = containingType ?? string.Empty;
        var modulePrefix = moduleName + ".";
        if (container.StartsWith(modulePrefix, StringComparison.Ordinal))
            containers.Add(container.Substring(modulePrefix.Length));
        containers.Add(container);

        var names = new List<string>(2);
        if (!string.IsNullOrEmpty(emittedName))
            names.Add(emittedName);
        if (names.Count == 0 || !string.Equals(names[0], swiftName, StringComparison.Ordinal))
            names.Add(swiftName);

        foreach (var name in names)
        {
            foreach (var c in containers)
                yield return (kind, name, c);
        }
    }

    /// <summary>
    /// Removes from <paramref name="rows"/> at most as many entries per identity as
    /// <paramref name="budget"/> allows, consuming the budget as it goes. Each list gets the full
    /// allowance: a co-gated member is expected to appear in several, and one list's removals must
    /// not spend another's.
    /// </summary>
    private static void RemoveUpToBudget<T>(
        List<T> rows,
        Func<T, IEnumerable<(BindingItemKind, string, string)>> identities,
        Dictionary<(BindingItemKind, string, string), int> budget)
    {
        if (rows.Count == 0)
            return;

        var remaining = new Dictionary<(BindingItemKind, string, string), int>(budget);
        rows.RemoveAll(row =>
        {
            foreach (var key in identities(row))
            {
                if (remaining.GetValueOrDefault(key) <= 0)
                    continue;

                remaining[key] -= 1;
                return true;
            }

            return false;
        });
    }

    private static void ApplyCoGated(BindingReport report, CoGatedMember member, SkipReason reason, string details)
    {
        report.EmittedMembers = Math.Max(0, report.EmittedMembers - 1);
        report.SkippedMembers += 1;
        AdjustKind(report.EmittedMembersByKind, member.Kind, -1);
        AdjustKind(report.SkippedMembersByKind, member.Kind, +1);
        report.SkippedItems.Add(new SkippedItem
        {
            Kind = member.Kind,
            Name = member.Name,
            ContainingType = member.ContainingType,
            Reason = reason,
            Details = details,
            RecommendedWorkaround = WorkaroundRecommendations.GetRecommendation(reason),
        });
    }

    private static void AdjustKind(Dictionary<BindingItemKind, int> dict, BindingItemKind kind, int delta)
    {
        var current = dict.GetValueOrDefault(kind);
        var next = current + delta;
        if (next <= 0)
            dict.Remove(kind);
        else
            dict[kind] = next;
    }
}
