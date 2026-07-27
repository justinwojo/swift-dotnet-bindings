// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;

namespace BindingsGeneration;

/// <summary>
/// The set of module-qualified type names that emission WITHDREW, for the planes that run after the
/// emission attempt has been disposed and therefore cannot read the ambient poison list.
/// </summary>
/// <remarks>
/// <para>
/// Two such planes exist — the serialized module database and the Swift type-ownership manifest —
/// and they must read the SAME set. When they disagree, the disagreement is silent and it is the
/// ownership manifest that does the damage: a withdrawn type still claiming its
/// <c>objcRuntimeName</c> makes the ObjC companion dedup its own declaration away against a Swift
/// declaration that was never emitted, so the type is lost on BOTH planes. Deriving both from one
/// function is what makes them agree by construction rather than by review.
/// </para>
/// <para>
/// The completed report is the path-independent oracle: Gate 0 in the type-body walk is the single
/// choke point for every whole-type refusal — an ingestion quarantine, a containment denial, or a
/// verify-recover withdrawal — and records each as a type-scope <see cref="SkipReason.EmitterFault"/>
/// skip. Narrowing either consumer to the ingestion withdrawals alone would leave the other two
/// causes unhandled. Skips with OTHER reasons are deliberately excluded: an Apple-supplement-owned
/// type and a SwiftUI View are declared elsewhere (the supplement package, the generated bridge) and
/// remain resolvable identities that must keep being advertised.
/// </para>
/// </remarks>
internal static class PostEmissionWithdrawalSet
{
    /// <summary>
    /// Builds the withdrawal set from the completed report, unioned with the ingestion closure's own
    /// withdrawn names, and closed over nested descendants of every withdrawn type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ingestion names are unioned in rather than assumed subsumed by the report: they are keyed
    /// on <c>SwiftTypeName.ModuleQualifiedName</c> while the report keys on
    /// <c>DeclId.QualifiedPath</c>. The two agree today, so the union is normally a no-op — but it
    /// costs nothing and keeps the result correct if a future report shape stops carrying a
    /// <c>DeclId</c> for some withdrawal, which would otherwise silently un-withdraw the type.
    /// </para>
    /// <para>
    /// Descendant closure is what makes the set safe for BOTH consumers, and it belongs here rather
    /// than in either of them. A whole-type refusal is recorded against the OUTER type only, but
    /// withdrawing an outer type withdraws its nested types with it — they have no declaration
    /// without their container. The ownership manifest walks nested types recursively and the module
    /// database holds a flat record per nested type, so an exact-name-only set leaves both planes
    /// advertising <c>M.Outer.Inner</c> after <c>M.Outer</c> was withdrawn. Closure is computed by
    /// walking the real type tree and adding each descendant's own qualified name, NOT by string
    /// prefix matching — a prefix test would also swallow the unrelated sibling
    /// <c>M.OuterOther</c>.
    /// </para>
    /// </remarks>
    /// <param name="report">The completed emission report, or null.</param>
    /// <param name="ingestionWithdrawnTypeNames">Names the ingestion closure withdrew, or null.</param>
    /// <param name="moduleDecl">
    /// The module whose type tree supplies the nested descendants. When null the set carries only
    /// the exact recorded names — correct but not closed, so callers with a module in hand must
    /// pass it.
    /// </param>
    public static HashSet<string> Build(
        BindingReport? report,
        IEnumerable<string>? ingestionWithdrawnTypeNames,
        ModuleDecl? moduleDecl = null)
    {
        var withdrawn = new HashSet<string>(StringComparer.Ordinal);

        if (report != null)
        {
            foreach (var item in report.SkippedItems)
            {
                if (item.Kind != BindingItemKind.Type || item.Reason != SkipReason.EmitterFault || item.DeclId == null)
                    continue;
                if (!DeclId.TryParse(item.DeclId, out var declId))
                    continue;
                var name = declId.QualifiedPath;
                if (!string.IsNullOrEmpty(name))
                    withdrawn.Add(name);
            }
        }

        if (ingestionWithdrawnTypeNames != null)
        {
            foreach (var name in ingestionWithdrawnTypeNames)
            {
                if (!string.IsNullOrEmpty(name))
                    withdrawn.Add(name);
            }
        }

        if (moduleDecl != null)
            AddWithdrawnDescendants(moduleDecl.Types, withdrawn, ancestorWithdrawn: false);

        return withdrawn;
    }

    /// <summary>
    /// Adds every nested descendant of an already-withdrawn type to the set, by walking the type
    /// tree and carrying the withdrawn state down. Names are added verbatim from each descendant's
    /// own <c>SwiftTypeName</c>, so only genuine containment cascades.
    /// </summary>
    private static void AddWithdrawnDescendants(
        IReadOnlyList<TypeDecl> types, HashSet<string> withdrawn, bool ancestorWithdrawn)
    {
        foreach (var type in types)
        {
            var name = type.SwiftTypeName?.ModuleQualifiedName;
            var selfWithdrawn = ancestorWithdrawn
                || (!string.IsNullOrEmpty(name) && withdrawn.Contains(name!));

            if (selfWithdrawn && !string.IsNullOrEmpty(name))
                withdrawn.Add(name!);

            AddWithdrawnDescendants(type.Types, withdrawn, selfWithdrawn);
        }
    }
}
