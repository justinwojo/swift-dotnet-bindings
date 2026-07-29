// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Fills in the attribution fields on a settled skip list: which rows are root causes, which are
/// consequences of another row, and who owns each.
/// </summary>
/// <remarks>
/// <para>
/// This runs at projection time as a pure pass over the finished rows, rather than being threaded
/// through the ~100 producer call sites. The rows are only complete once the whole pipeline has run —
/// <see cref="SkipReason.MissingWrapperSymbol"/> rows do not exist until after the Swift wrapper is
/// built — so attribution has to happen after the fact regardless.
/// </para>
/// <para>
/// <b>It annotates; it does not invent.</b> Two cascade families are derivable from the rows as they
/// exist, and both are found the same structural way — by parsing <see cref="SkippedItem.DeclId"/> and
/// locating the nearest enclosing declaration that also has a row. <see cref="SkipReason.AncestorSkipped"/>
/// is a type whose ancestor was skipped; <see cref="SkipReason.ParentTypeSuppressed"/> is a member
/// whose declaring type was suppressed as a whole, which by construction means the enclosing type has
/// a row of its own and is the entire reason the member has one. Everything else stays a root. In
/// particular, a member row is <em>not</em> treated as a cascade of its containing type merely because
/// both were skipped: a member gate that fires on its own names its own cause, and where a type row
/// and a member row co-occur incidentally — a suppressed proxy is recorded as a synthetic type row
/// under the protocol — the containing type is not the cause. Suppressed-proxy declines name their
/// cause only in prose today, so they stay roots at low confidence rather than being linked on a
/// string match.
/// </para>
/// </remarks>
public static class SkipAttributionLinker
{
    /// <summary>
    /// A row indexed by both the declaration it names and the recovery unit that declaration maps to.
    /// Carrying the unit id alongside the row is what keeps the two indexes in agreement: the id a
    /// cascade edge is written with is the same string a root lookup is keyed on.
    /// </summary>
    private readonly record struct Row(SkippedItem Item, DeclId Decl, string UnitId);

    /// <summary>
    /// Annotates every row in place. Idempotent — running twice produces the same result, so a report
    /// that is projected more than once does not accumulate contradictory attributions.
    /// </summary>
    public static void Link(IReadOnlyList<SkippedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            return;

        // Index the rows that can be a cause: those with a parseable declaration identity. Both maps
        // are built from the same Row so a unit id can never be written by one surface and looked up
        // against another — the accessor normalization in UnitIdOf used to make exactly that mistake.
        var byDecl = new Dictionary<string, Row>(StringComparer.Ordinal);
        var byUnit = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.DeclId is not { Length: > 0 } canonical)
                continue;
            if (!DeclId.TryParse(canonical, out var decl))
                continue;

            var row = new Row(item, decl, UnitIdOf(item, decl));
            // First row wins: a declaration with two rows is already ambiguous, and picking the later
            // one would make the result depend on emission order.
            byDecl.TryAdd(canonical, row);
            // A getter and a setter normalize onto one accessor-group unit, so this map is genuinely
            // many-to-one and first-wins is the same tie-break by a different name.
            byUnit.TryAdd(row.UnitId, row);
        }

        foreach (var item in items)
        {
            var parent = ResolveCascadeParent(item, byDecl);
            item.CascadeFrom = parent?.UnitId;
        }

        foreach (var item in items)
            Attribute(item, items, byDecl, byUnit);
    }

    /// <summary>
    /// Whether a row represents an actual loss of surface.
    /// </summary>
    /// <remarks>
    /// Two shapes are recorded as skips without anything being lost. A row whose
    /// <see cref="SkippedItem.RecoveredBy"/> is populated was closed by another mechanism, and an
    /// <see cref="SkipReason.OwnedByAppleSupplement"/> row is deliberately left to the Apple supplement
    /// package — the surface exists either way, so counting them as degradations would report
    /// already-provided API as missing.
    /// </remarks>
    public static bool IsLoss(SkippedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.RecoveredBy is not { Count: > 0 }
            && item.Reason != SkipReason.OwnedByAppleSupplement;
    }

    private static Row? ResolveCascadeParent(SkippedItem item, Dictionary<string, Row> byDecl)
    {
        // The two structurally derivable families. Every other reason describes the row's own failure.
        // A parent-suppressed member exists only because its declaring type has a row, so the same
        // nearest-enclosing search that resolves an ancestor-skipped type resolves it — and resolving
        // it is what lets the member inherit the owner and stage of the decision that caused it,
        // instead of standing as its own unowned root.
        if (item.Reason is not (SkipReason.AncestorSkipped or SkipReason.ParentTypeSuppressed))
            return null;
        if (item.DeclId is not { Length: > 0 } canonical || !DeclId.TryParse(canonical, out var decl))
            return null;

        // Nearest enclosing declaration with a row of its own. Nearest, not any, so a three-deep nest
        // links child -> parent -> grandparent rather than collapsing both onto the outermost.
        Row? best = null;
        var bestDepth = -1;
        foreach (var (_, candidate) in byDecl)
        {
            if (ReferenceEquals(candidate.Item, item))
                continue;
            // Only a type can enclose a declaration. Textual paths alone would let a skipped method
            // whose own path happens to prefix the row's be named as its ancestor.
            if (candidate.Item.Kind != BindingItemKind.Type)
                continue;
            if (!candidate.Decl.Encloses(decl))
                continue;

            var depth = candidate.Decl.QualifiedPath.Length;
            // Ties are broken on the canonical id rather than left to dictionary order, so the same
            // input always attributes to the same ancestor.
            if (depth > bestDepth
                || (depth == bestDepth && best is { } incumbent
                    && string.CompareOrdinal(candidate.Decl.Canonical, incumbent.Decl.Canonical) < 0))
            {
                bestDepth = depth;
                best = candidate;
            }
        }

        return best;
    }

    private static void Attribute(
        SkippedItem item,
        IReadOnlyList<SkippedItem> items,
        Dictionary<string, Row> byDecl,
        Dictionary<string, Row> byUnit)
    {
        var root = ResolveRoot(item, items, byUnit);
        var isCascade = !ReferenceEquals(root, item);

        var attribution = SkipCauseClassifier.Classify(root.Reason, root.Details);
        if (isCascade)
        {
            // Second-hand: the owner and stage are the root's, but the row itself is evidence of a
            // consequence, not of the cause, so it never claims more certainty than Medium.
            attribution = SkipAttribution.Of(
                attribution.Owner,
                attribution.Stage,
                attribution.Confidence == AttributionConfidence.High
                    ? AttributionConfidence.Medium
                    : attribution.Confidence);
        }

        item.CauseOwner = attribution.Owner;
        item.RecoveryStage = attribution.Stage;
        item.Confidence = attribution.Confidence;
        item.RootCauseId = RootIdOf(root, byDecl);
    }

    private static SkippedItem ResolveRoot(
        SkippedItem item,
        IReadOnlyList<SkippedItem> items,
        Dictionary<string, Row> byUnit)
    {
        var current = item;
        // SkippedItem is a plain class, so the default comparer is reference identity — which is what
        // this guard wants: two rows with identical content are still two distinct rows.
        var guard = new HashSet<SkippedItem>();

        // Bounded by the row count even if the data were cyclic, which the guard also rules out.
        for (var hops = 0; hops <= items.Count; hops++)
        {
            if (current.CascadeFrom is not { Length: > 0 } parentUnit)
                return current;
            if (!guard.Add(current))
                return current;

            if (!byUnit.TryGetValue(parentUnit, out var parent))
                return current;

            current = parent.Item;
        }

        return current;
    }

    private static string? RootIdOf(SkippedItem root, Dictionary<string, Row> byDecl)
    {
        if (root.DeclId is not { Length: > 0 } canonical)
            return null;
        return byDecl.TryGetValue(canonical, out var found) ? found.UnitId : null;
    }

    /// <summary>
    /// The recovery unit a skip row names. Scope follows the kind of thing that was lost, which is
    /// what makes a root-cause id joinable with the recovery graph.
    /// </summary>
    private static string UnitIdOf(SkippedItem item, DeclId decl) => item.Kind switch
    {
        BindingItemKind.Type => RecoveryUnitId.Create(decl, RecoveryScope.TypeSurface).Canonical,
        BindingItemKind.Property or BindingItemKind.Subscript =>
            RecoveryUnitId.ForAccessorGroup(decl).Canonical,
        _ => RecoveryUnitId.Create(decl, RecoveryScope.LeafApi).Canonical,
    };
}
