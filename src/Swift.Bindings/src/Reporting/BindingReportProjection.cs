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
        }

        if (manifest.ProxyCoGating is { } pc)
        {
            foreach (var member in pc.CoGatedMethods)
            {
                ApplyCoGated(
                    report,
                    member,
                    SkipReason.SuppressedProxyMethodBody,
                    "Method body removed because the suppressed proxy class it constructed was unreachable.");
            }
        }

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
