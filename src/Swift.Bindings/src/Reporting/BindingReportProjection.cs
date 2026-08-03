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

            ReconcileMarkedSurfaceAfterCoGating(report, w.CSharpCoGatedMembers);
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
    /// </summary>
    private static void ReconcileMarkedSurfaceAfterCoGating(
        BindingReport report, List<CoGatedMember> coGated)
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
            m => (m.Kind, m.Name, m.ContainingType ?? string.Empty),
            budget);
        RemoveUpToBudget(
            report.WrappedItems,
            w => (w.Kind, w.Name, w.ContainingType ?? string.Empty),
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
    /// Removes from <paramref name="rows"/> at most as many entries per identity as
    /// <paramref name="budget"/> allows, consuming the budget as it goes. Each list gets the full
    /// allowance: a co-gated member is expected to appear in several, and one list's removals must
    /// not spend another's.
    /// </summary>
    private static void RemoveUpToBudget<T>(
        List<T> rows,
        Func<T, (BindingItemKind, string, string)> identity,
        Dictionary<(BindingItemKind, string, string), int> budget)
    {
        if (rows.Count == 0)
            return;

        var remaining = new Dictionary<(BindingItemKind, string, string), int>(budget);
        rows.RemoveAll(row =>
        {
            var key = identity(row);
            if (remaining.GetValueOrDefault(key) <= 0)
                return false;

            remaining[key] -= 1;
            return true;
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
