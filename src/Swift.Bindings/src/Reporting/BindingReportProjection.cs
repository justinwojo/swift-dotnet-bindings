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
