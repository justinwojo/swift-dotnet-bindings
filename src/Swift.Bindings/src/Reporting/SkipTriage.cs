// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Actionability roll-up of a binding report's flat skip list. Answers "how much of what was skipped
/// is expected, and what is the short list a human should look at" without re-scanning every
/// per-item reason. Serialized into <c>binding-report.json</c> under <c>SkipTriage</c> and surfaced as
/// a one-line console headline.
/// </summary>
public sealed class SkipTriageSummary
{
    /// <summary>Total skipped items — equals the length of <see cref="BindingReport.SkippedItems"/>.</summary>
    public int Total { get; set; }

    /// <summary>
    /// Count per <see cref="SkipDisposition"/>, keyed by the disposition's name and inserted in
    /// disposition order (nothing-to-do → review) for stable, readable output.
    /// </summary>
    public Dictionary<string, int> ByDisposition { get; } = new();

    /// <summary>Count per <see cref="SkipReason"/>, keyed by reason name, most-frequent first.</summary>
    public Dictionary<string, int> ByReason { get; } = new();

    /// <summary>
    /// Skips that removed something a consumer could theoretically have seen — every item whose
    /// disposition is neither <see cref="SkipDisposition.ExpectedNonPublic"/> (never public) nor
    /// <see cref="SkipDisposition.Recovered"/> (typed surface recovered via CSM projection). Not all
    /// are generator bugs; this is the consumer-visible surface area, expected or not.
    /// </summary>
    public int PublicSurfaceLost { get; set; }

    /// <summary>Convenience mirror of <c>ByDisposition["Review"]</c> — the size of the "look at this" set.</summary>
    public int ReviewCount { get; set; }

    /// <summary>
    /// The <see cref="SkipDisposition.Review"/> items themselves, inlined so the report carries the
    /// short actionable list directly. Ordered as they appear in <see cref="BindingReport.SkippedItems"/>.
    /// </summary>
    public List<SkipTriageItem> ReviewItems { get; } = new();
}

/// <summary>
/// A lean projection of a <see cref="SkippedItem"/> for the <see cref="SkipTriageSummary.ReviewItems"/>
/// list — just the columns needed to locate and understand a skip worth investigating.
/// </summary>
public sealed class SkipTriageItem
{
    public required BindingItemKind Kind { get; init; }
    public required string Name { get; init; }
    public string? ContainingType { get; init; }
    public required SkipReason Reason { get; init; }
    public string? Details { get; init; }
}

/// <summary>
/// Builds a <see cref="SkipTriageSummary"/> from a settled skip list. Pure function of its input — the
/// authoritative call site is <see cref="BindingReportProjection.Project"/>, after co-gating has moved
/// every wrapper-stripped member into the skip list, so the roll-up reflects final reality.
/// </summary>
public static class SkipTriageBuilder
{
    public static SkipTriageSummary Build(IReadOnlyList<SkippedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var byDisposition = new Dictionary<SkipDisposition, int>();
        var byReason = new Dictionary<SkipReason, int>();
        var summary = new SkipTriageSummary { Total = items.Count };

        foreach (var item in items)
        {
            var disposition = SkipDispositionClassifier.Classify(item);
            byDisposition[disposition] = byDisposition.GetValueOrDefault(disposition) + 1;
            byReason[item.Reason] = byReason.GetValueOrDefault(item.Reason) + 1;

            if (disposition == SkipDisposition.Review)
            {
                summary.ReviewItems.Add(new SkipTriageItem
                {
                    Kind = item.Kind,
                    Name = item.Name,
                    ContainingType = item.ContainingType,
                    Reason = item.Reason,
                    Details = item.Details,
                });
            }
        }

        // ByDisposition in enum order (nothing-to-do → review) for stable output.
        foreach (SkipDisposition disposition in Enum.GetValues<SkipDisposition>())
        {
            if (byDisposition.TryGetValue(disposition, out var count))
                summary.ByDisposition[disposition.ToString()] = count;
        }

        // ByReason most-frequent first, ties broken by name — matches the console "by reason" ordering.
        foreach (var pair in byReason.OrderByDescending(p => p.Value).ThenBy(p => p.Key.ToString(), StringComparer.Ordinal))
            summary.ByReason[pair.Key.ToString()] = pair.Value;

        summary.ReviewCount = byDisposition.GetValueOrDefault(SkipDisposition.Review);
        // Public surface a consumer could theoretically have seen but didn't get: everything except
        // never-public members AND CSM-recovered rows (whose typed surface IS callable — the skip is
        // only the open-generic base member being accounted for).
        summary.PublicSurfaceLost = summary.Total
            - byDisposition.GetValueOrDefault(SkipDisposition.ExpectedNonPublic)
            - byDisposition.GetValueOrDefault(SkipDisposition.Recovered);
        return summary;
    }
}
