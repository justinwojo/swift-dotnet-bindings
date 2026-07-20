// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

using BindingsGeneration.Diagnostics;

namespace BindingsGeneration;

/// <summary>What the strip-to-withdrawal classifier decided for one stripped wrapper symbol.</summary>
public enum StripWithdrawalDisposition
{
    /// <summary>The symbol's owner is a droppable leaf/accessor — it can become an iteration-0 denylist
    /// withdrawal, seeded exactly as the verify-recover loop withdraws a leaf.</summary>
    Withdraw,

    /// <summary>The symbol cannot be soundly turned into a leaf withdrawal — no recorded owner, or an
    /// owner that fans in (a shared helper, a conformance, a reverse vtable, the module). Fail closed:
    /// a coarse or unattributable withdrawal requires graph closure that does not exist yet.</summary>
    FailClosed,
}

/// <summary>The classification of one stripped symbol: its disposition, the owning unit (when resolved),
/// and a one-line reason.</summary>
public readonly record struct StripWithdrawalClassification(
    string Symbol,
    StripWithdrawalDisposition Disposition,
    RecoveryUnitId? Unit,
    string Reason);

/// <summary>
/// Classifies each block the <c>SwiftWrapperPostProcessor</c> strips into either an iteration-0 denylist
/// withdrawal or a fail-closed refusal, so the post-processor's silent text edits can — in principle —
/// become recorded withdrawals through the same channel the verify-recover loop uses, rather than a
/// blind text scrub the report never sees.
/// </summary>
/// <remarks>
/// <para>
/// This is a PURE classifier. It does not seed a denylist, mutate a render, or retire the reconciler:
/// the production loop path keeps its fail-closed posture on any residual strip (SWIFTBIND115), and this
/// classifier is exercised by fixtures only until a nonzero-strip canary proves the conversion is sound.
/// It exists so that conversion, when enabled, resolves each symbol through the ONE ownership map the
/// emission side stamps (<c>ModuleEmissionContext.TryGetWrapperSymbolOwner</c>) and the ONE classification
/// the loop uses (<see cref="RecoveryUnitClassifier.ClassifyArtifact"/>) — never a private re-derivation.
/// </para>
/// <para>
/// The disposition rules mirror what the controller will actually accept, gated on the SAME wave-1
/// scope predicate the loop uses (<see cref="WrapperRecoveryController.IsLeafRecoverable"/>): only a
/// LeafApi or AccessorGroup owner becomes a withdrawal. A symbol with no recorded owner (the
/// post-processor's symbol-less patterns — EveryProtocol conformance, plain extensions, <c>_SBW_</c>
/// dispatch protocols — or an owner the emission side never threaded), any coarser-scope owner
/// (type surface / managed conformance / module), and the conservative unclassified sink all fail
/// closed, exactly as <c>WrapperRecoveryController</c> fails a coarse-scope culprit closed. A symbol
/// maps to at most one owner (the ownership map is single-valued), so there is no multi-owner arm.
/// </para>
/// </remarks>
public static class StripToWithdrawalClassifier
{
    /// <summary>
    /// Classifies one stripped symbol against its already-resolved owner (null when the ownership map has
    /// no entry — a symbol-less or unthreaded strip). Pure: the caller resolves the owner.
    /// </summary>
    public static StripWithdrawalClassification Classify(string strippedSymbol, ArtifactId? owner)
    {
        if (owner is not { } artifact)
        {
            return new StripWithdrawalClassification(
                strippedSymbol, StripWithdrawalDisposition.FailClosed, Unit: null,
                "no recorded wrapper owner (a symbol-less strip, or an owner the emission side never " +
                "threaded); a withdrawal cannot be attributed, so fail closed");
        }

        var (unit, droppable) = RecoveryUnitClassifier.ClassifyArtifact(artifact);

        // Mirror the wave-1 controller's scope gate EXACTLY (WrapperRecoveryController.IsLeafRecoverable):
        // it withdraws only leaf and accessor-group culprits — the two scopes whose removal is provably
        // ABI-neutral. A type surface, a managed conformance, or the module is droppable-alone in the
        // classifier's layout sense yet still needs the dependency closure a populated recovery graph
        // supplies; until that graph is wired the loop fails such a culprit closed, and so must this.
        if (!WrapperRecoveryController.IsLeafRecoverable(unit.Scope))
        {
            return new StripWithdrawalClassification(
                strippedSymbol, StripWithdrawalDisposition.FailClosed, unit,
                $"owning unit is {unit.Scope} scope, coarser than the leaf/accessor-group scopes the " +
                "verify-recover loop withdraws at this wave; a coarser withdrawal needs graph closure, " +
                "so fail closed");
        }

        // A leaf-scoped but layout-contributing owner is the conservative unclassified sink — a role/
        // declaration pairing the generator does not model. It escalates to its parent rather than being
        // withdrawn on a guess, so fail closed here too.
        if (!droppable)
        {
            return new StripWithdrawalClassification(
                strippedSymbol, StripWithdrawalDisposition.FailClosed, unit,
                "owning unit is the conservative unclassified sink (contributes to its parent's layout) " +
                "and is not droppable alone; fail closed rather than withdraw on a guess");
        }

        return new StripWithdrawalClassification(
            strippedSymbol, StripWithdrawalDisposition.Withdraw, unit,
            $"owned droppable {unit.Scope} — convertible to an iteration-0 withdrawal");
    }

    /// <summary>
    /// Classifies every stripped symbol, resolving each owner through the emission context's ownership
    /// map. The order follows enumeration of <paramref name="strippedSymbols"/>.
    /// </summary>
    public static IReadOnlyList<StripWithdrawalClassification> ClassifyAll(
        IReadOnlySet<string> strippedSymbols, ModuleEmissionContext context)
    {
        System.ArgumentNullException.ThrowIfNull(strippedSymbols);
        System.ArgumentNullException.ThrowIfNull(context);

        var results = new List<StripWithdrawalClassification>(strippedSymbols.Count);
        foreach (var symbol in strippedSymbols)
        {
            ArtifactId? owner = context.TryGetWrapperSymbolOwner(symbol, out var artifact) ? artifact : null;
            results.Add(Classify(symbol, owner));
        }
        return results;
    }
}
