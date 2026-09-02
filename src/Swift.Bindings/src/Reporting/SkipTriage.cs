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

    /// <summary>
    /// Count of CONSUME-degraded reverse-dispatch members — a setter/parameter/enum-payload position that
    /// accepts an existential whose <c>{Protocol}Proxy</c> was suppressed, so a C#-authored conformer passed
    /// there silently never fires from Swift. These roll up under <see cref="SkipDisposition.KnownLimitation"/>
    /// (they are attributed, not a generator bug, so reclassifying them to <see cref="SkipDisposition.Review"/>
    /// would be dishonest and would perturb the ReviewCount==0 invariant). But a degrade that is invisible in
    /// source (a dropped C# wrap-fallback lambda) has no compile signal at every position — so it is surfaced
    /// here as an additive callout, visible even when <see cref="ReviewCount"/> is zero, without touching
    /// <see cref="ByDisposition"/> or <see cref="ReviewCount"/>.
    /// </summary>
    public int DegradedConsumeCount { get; set; }

    /// <summary>
    /// The CONSUME-degraded items themselves, inlined so a clean (ReviewCount==0) report still names the
    /// silent arms a consumer needs to know about. Ordered as they appear in
    /// <see cref="BindingReport.SkippedItems"/>.
    /// </summary>
    public List<SkipTriageItem> DegradedConsumeItems { get; } = new();

    /// <summary>
    /// Count of rows that describe a member the generator DID emit, degraded on one call path rather
    /// than absent (see <see cref="SkipDispositionClassifier.IsDeclaredButDegraded"/>). They are part
    /// of <see cref="Total"/> and <see cref="ByReason"/> — that is the point, they are meant to be
    /// countable — but deliberately excluded from <see cref="PublicSurfaceLost"/>, because the
    /// declaration a consumer sees is still there. This count is what makes that subtraction visible
    /// instead of an unexplained gap between the two figures.
    /// </summary>
    public int DeclaredButDegradedCount { get; set; }

    /// <summary>
    /// Every reason name <see cref="SkipDispositionClassifier.IsDeclaredButDegraded"/> covers — the
    /// whole predicate, not just the reasons this run happened to hit, so a consumer sees the same
    /// contract on a corpus that trips none of them. Published because the predicate has an
    /// out-of-process consumer: the BindingTests coverage ratchet measures LOST surface per feature,
    /// and a row here names a member that WAS emitted. Reading the set beats re-declaring it — a
    /// second copy agrees with this one only until the next reason is added, and the failure is
    /// silent (a working member reported as a coverage loss).
    /// </summary>
    public List<string> DeclaredButDegradedReasons { get; } = new();
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
        var declaredButDegraded = 0;

        summary.DeclaredButDegradedReasons.AddRange(
            Enum.GetValues<SkipReason>()
                .Where(SkipDispositionClassifier.IsDeclaredButDegraded)
                .Select(reason => reason.ToString())
                .OrderBy(name => name, StringComparer.Ordinal));

        foreach (var item in items)
        {
            var disposition = SkipDispositionClassifier.Classify(item);
            byDisposition[disposition] = byDisposition.GetValueOrDefault(disposition) + 1;
            byReason[item.Reason] = byReason.GetValueOrDefault(item.Reason) + 1;

            // Counted only when no other exclusion already covers this row, so the three subtractions
            // from PublicSurfaceLost below stay disjoint and cannot drive the figure negative.
            if (SkipDispositionClassifier.IsDeclaredButDegraded(item.Reason) &&
                disposition is not (SkipDisposition.ExpectedNonPublic or SkipDisposition.Recovered))
            {
                declaredButDegraded++;
            }

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

            // Additive CONSUME-degrade callout — surfaced regardless of disposition (the row stays
            // KnownLimitation). Matched on the greppable site token stamped by SuppressedProxyReporting so
            // the produce-throw / receiver-failfast siblings, which carry their own signal (SB0006 compile
            // error / fail-fast body), are NOT swept in here.
            if (item.Reason == SkipReason.SuppressedProxyMemberDegraded && item.Details is { } degradeDetails &&
                degradeDetails.Contains(
                    SuppressedProxyReporting.Token(SuppressedProxyReporting.Site.ConsumeDegraded),
                    StringComparison.Ordinal))
            {
                summary.DegradedConsumeItems.Add(new SkipTriageItem
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
        summary.DegradedConsumeCount = summary.DegradedConsumeItems.Count;
        summary.DeclaredButDegradedCount = declaredButDegraded;
        // Public surface a consumer could theoretically have seen but didn't get: everything except
        // never-public members, CSM-recovered rows (whose typed surface IS callable — the skip is
        // only the open-generic base member being accounted for), and declared-but-degraded rows
        // (whose C# declaration was written — only one call path through it is limited).
        summary.PublicSurfaceLost = summary.Total
            - byDisposition.GetValueOrDefault(SkipDisposition.ExpectedNonPublic)
            - byDisposition.GetValueOrDefault(SkipDisposition.Recovered)
            - declaredButDegraded;
        return summary;
    }
}
