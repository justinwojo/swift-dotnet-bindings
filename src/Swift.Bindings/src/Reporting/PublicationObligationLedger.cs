// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BindingsGeneration;

/// <summary>How a publication obligation was discharged for one module.</summary>
public enum ObligationVerdict
{
    /// <summary>Discharged by a structural invariant of the generator: the obligation cannot be false if
    /// output was produced at all, because the code that would violate it does not exist. Recorded, not
    /// re-checked — but named so the invariant is auditable rather than implicit.</summary>
    ProvenByConstruction,

    /// <summary>Discharged by a runtime verifier that actually ran and passed for this module (the
    /// wrapper compile, the C# compile leg, the ABI-contract validator, the native-symbol probe).</summary>
    ProvenByVerifier,

    /// <summary>The obligation does not apply to this module — e.g. the wrapper-slice obligations when
    /// the module has no wrapper surface, or the reverse-conformance obligation when it advertises
    /// none.</summary>
    NotApplicable,

    /// <summary>A verifier that should have discharged this obligation did not run, or its verdict was
    /// not available at publication time. Recorded honestly: an unproven obligation is never silently
    /// promoted to proven, and its presence is what fails the ledger's <see
    /// cref="PublicationObligationLedger.AllDischarged"/>.</summary>
    Unproven,
}

/// <summary>
/// One row of the adapted proof-obligation ledger: a numbered obligation, the verifier that now
/// discharges it, the verdict for this module, and an optional detail line.
/// </summary>
public sealed record ObligationLedgerEntry
{
    /// <summary>The obligation number, following the sol-report numbering (1–13) so the ledger reads
    /// against the design's obligation table.</summary>
    public required int Number { get; init; }

    /// <summary>What must hold before the binding may be published.</summary>
    public required string Obligation { get; init; }

    /// <summary>The verifier (or structural invariant) that discharges it.</summary>
    public required string Verifier { get; init; }

    /// <summary>The verdict for this module.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public required ObligationVerdict Verdict { get; init; }

    /// <summary>An optional one-line elaboration (a count, a reason, the invariant named).</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// The adapted proof-obligation ledger recorded alongside a published (or failed) binding. It is an
/// honest RECORD, not a second gate: the obligations are discharged where they are discharged — the
/// wrapper-symbol integrity gate, the ABI-contract validator, the C# compile leg, the usable-surface
/// gate — and this ledger states, per obligation, which verifier discharged it and with what verdict.
/// </summary>
/// <remarks>
/// <para>
/// This session deliberately does NOT claim whole-publication atomicity. The in-tree atomic-promote
/// precedent (<c>SwiftWrapperCompiler.PromoteStagedXcframework</c>) stages then renames the wrapper
/// xcframework alone; wrapping the entire multi-artifact publication in one atomic promote is a
/// larger change routed to a later session. The ledger's role here is to make each obligation's
/// discharge auditable — a construction-time invariant is marked as such rather than presented as a
/// runtime proof, and a verifier-backed obligation carries the actual verdict — so the report never
/// implies more was proven than was.
/// </para>
/// <para>
/// Obligations backed by a structural invariant carry <see cref="ObligationVerdict.ProvenByConstruction"/>;
/// those with a live verifier in the publication path carry the verifier's verdict. An obligation with
/// no available verdict is <see cref="ObligationVerdict.Unproven"/> — surfaced, never assumed.
/// </para>
/// </remarks>
public sealed record PublicationObligationLedger
{
    /// <summary>The ledger rows, in obligation-number order.</summary>
    public required IReadOnlyList<ObligationLedgerEntry> Entries { get; init; }

    /// <summary>True when no obligation is left unproven — every row is proven (by construction or by a
    /// verifier) or is not applicable to this module.</summary>
    public bool AllDischarged => Entries.All(e => e.Verdict != ObligationVerdict.Unproven);

    /// <summary>The obligations left unproven, if any — the exact set a promotion decision must weigh.</summary>
    public IReadOnlyList<ObligationLedgerEntry> Unproven =>
        Entries.Where(e => e.Verdict == ObligationVerdict.Unproven).ToList();
}

/// <summary>
/// The signals available at publication time that decide the verdict of each verifier-backed
/// obligation. Structural-invariant obligations do not read this — they are proven by construction.
/// </summary>
public readonly record struct PublicationEvidence
{
    /// <summary>False when the loop converged on the no-wrapper-surface signal — the module emitted no
    /// wrapper at all, so every wrapper-slice obligation is not applicable.</summary>
    public bool HasWrapperSurface { get; init; }

    /// <summary>True when the in-loop verify slice recorded a clean wrapper compile (obligations 5, 12).
    /// Scoped to the slice the verify-recover loop actually compiled — NOT all promised production slices,
    /// which the post-report fat build finalizes. Ignored when <see cref="HasWrapperSurface"/> is false.</summary>
    public bool WrapperVerifySliceCompiledClean { get; init; }

    /// <summary>True when the wrapper-symbol integrity gate reconciled every emitted wrapper-symbol
    /// P/Invoke reference against an emitted wrapper definition — the runtime verifier that discharges
    /// obligation 4's existence half (obligation 4). Null when the module has no wrapper surface, where
    /// there is no wrapper-targeting P/Invoke to reconcile.</summary>
    public bool? WrapperSymbolsIntegral { get; init; }

    /// <summary>True when the direct-native P/Invoke symbols were probed against the exported symbol
    /// tables and matched (obligation 6). Null when no direct-native probe was performed.</summary>
    public bool? DirectNativeSymbolsResolved { get; init; }

    /// <summary>True when the emitted C# verified and compiled against the settled reference set
    /// (obligations 9, 11). Null when the C# verify leg did not run in this invocation.</summary>
    public bool? CSharpVerified { get; init; }

    /// <summary>True when the typed ABI call-plan / contract validator found no violation (obligations
    /// 3, 13). Null when validation did not run.</summary>
    public bool? AbiContractValidated { get; init; }

    /// <summary>True when the module advertises at least one reverse (managed) conformance whose
    /// capability recompute completed (obligation 8). Null when it advertises none.</summary>
    public bool? ReverseConformancesComplete { get; init; }

    /// <summary>The count of types that shipped as silent tombstones — recorded on the type-surface
    /// obligation's detail so a degenerate shape is visible in the ledger.</summary>
    public int SilentTombstoneCount { get; init; }
}

/// <summary>
/// Builds the adapted obligation ledger from the evidence available at publication time. The
/// obligation set and each obligation's discharging verifier are the design's table; the verdicts are
/// this module's.
/// </summary>
public static class PublicationObligationLedgerBuilder
{
    /// <summary>Maps a nullable verifier signal to a verdict: ran-and-passed → proven; ran-and-failed →
    /// unproven; did-not-run → unproven (never silently proven).</summary>
    private static ObligationVerdict FromVerifier(bool? passed) =>
        passed is true ? ObligationVerdict.ProvenByVerifier : ObligationVerdict.Unproven;

    /// <summary>Maps a required (non-null) verifier signal to a verdict.</summary>
    private static ObligationVerdict FromVerifier(bool passed) =>
        passed ? ObligationVerdict.ProvenByVerifier : ObligationVerdict.Unproven;

    /// <summary>Maps a nullable signal that is <see cref="ObligationVerdict.NotApplicable"/> when null,
    /// otherwise the pass/fail verdict.</summary>
    private static ObligationVerdict FromOptionalVerifier(bool? signal) =>
        signal is null ? ObligationVerdict.NotApplicable : FromVerifier(signal.Value);

    /// <summary>The honest detail for the two C#-compile obligations (9, 11).</summary>
    private static string? CSharpDetail(bool? csharpVerified) => csharpVerified switch
    {
        true => "the verify-recover loop reached a joint wrapper+C# fixed point, so the emitted C# "
            + "verified and compiled under the settled member set",
        false => null,
        null => "no clean in-process C# verdict was obtained (no verifier wired, or convergence bypassed "
            + "it, or it ran inconclusive); the emitted C# is gated by the standalone compile-only leg "
            + "(the CI compile gate)",
    };

    /// <summary>Builds the ledger.</summary>
    public static PublicationObligationLedger Build(PublicationEvidence evidence)
    {
        // The wrapper-slice obligations (5, 12) are not applicable to a module with no wrapper surface;
        // otherwise they carry the compile verdict.
        var wrapperSliceVerdict = evidence.HasWrapperSurface
            ? FromVerifier(evidence.WrapperVerifySliceCompiledClean)
            : ObligationVerdict.NotApplicable;
        // Detail must track the compile signal, not merely whether a surface exists: a success claim
        // paired with an Unproven verdict (compile failed) would be a false statement written to the
        // report before the module fails closed.
        var wrapperSliceDetail = !evidence.HasWrapperSurface
            ? "module emitted no wrapper surface — no wrapper slice to prove"
            : evidence.WrapperVerifySliceCompiledClean
                ? "the in-loop verify-recover wrapper compile accepted the slice it verified; the "
                    + "authoritative multi-slice fat build and the residual-strip gate run after this "
                    + "report and are the final slice/strip authority (this session does not fold them "
                    + "back into the ledger — whole-publication atomicity is deferred)"
                : "the in-loop verify-recover wrapper compile did not accept the slice it verified; the "
                    + "module fails closed before publication";

        var entries = new List<ObligationLedgerEntry>
        {
            new()
            {
                Number = 1, Obligation = "a representation category for every retained type",
                Verifier = "representation-category assignment (Unknown pruned)",
                Verdict = ObligationVerdict.ProvenByConstruction,
                // The category assignment is the genuine construction invariant: an Unknown/unclassified
                // type is pruned, never emitted. Concrete layout is NOT claimed complete here — it is
                // resolved where known and falls back to a documented sizing where unresolved (ob. 2).
                Detail = "a retained type is assigned a representation category by construction; an "
                    + "Unknown/unclassified type is pruned rather than emitted",
            },
            new()
            {
                Number = 2, Obligation = "total stored-field layout coverage for retained aggregates",
                Verifier = "frozen-struct layout path (IntPtr-sized fallback; unresolvable fields fail closed)",
                Verdict = ObligationVerdict.ProvenByConstruction,
                // Honest scope: every retained stored field routes through the layout path and gets a
                // concrete layout kind — a concrete inline size where known, an IntPtr-sized backing where
                // the size varies (Optional / reference-managed fields), or a typed C# field for a resolved
                // plain field. A field whose size is indeterminate, or whose type record is unresolvable,
                // does NOT get a silent typed-field fallback: the aggregate is skipped before emission
                // (fails closed), so a retained aggregate never ships a mis-sized field. This is total
                // COVERAGE by construction, not a guarantee that every concrete size is known.
                Detail = "every retained stored field routes through the frozen-struct layout path, which "
                    + "resolves concrete inline size where known, applies an IntPtr-sized fallback where "
                    + "the size varies (Optional / reference-managed fields), and emits a typed C# field "
                    + "for a resolved plain field; a field whose size is indeterminate or whose record is "
                    + "unresolvable fails closed — the aggregate is skipped before emission rather than "
                    + "shipping a mis-sized field",
            },
            new()
            {
                Number = 3, Obligation = "no calling-convention violation in any validator-covered P/Invoke",
                Verifier = "ABI-contract validation (typed plan-backed subset + [LibraryImport] text backstop)",
                Verdict = FromOptionalVerifier(evidence.AbiContractValidated),
                // Honest scope: the validator covers the source-generated [LibraryImport] P/Invokes —
                // the typed plan-backed subset plus a text backstop over the rest (the backstop's text
                // scan anchors on [LibraryImport]). Directly-emitted [DllImport] runtime P/Invokes
                // (closure / async completion thunks) are construction-pinned to Cdecl and are NOT part
                // of the validated set, so a clean verdict proves the absence of a covered violation, not
                // a universal one over every retained P/Invoke.
                Detail = evidence.AbiContractValidated is true
                    ? "the ABI-contract validator ran during the converged render and raised no "
                        + "calling-convention violation on the covered [LibraryImport] P/Invokes (typed "
                        + "plan-backed subset + text backstop); a violation fails the module closed "
                        + "before publication"
                    : null,
            },
            new()
            {
                Number = 4, Obligation = "each wrapper-targeting P/Invoke has exactly one retained wrapper definition",
                Verifier = "wrapper-symbol integrity gate (single-valued owner map)",
                // "Exactly one" is two properties: uniqueness (at most one) is proven by construction —
                // the owner map is single-valued, so a second definition cannot be recorded — but
                // existence (at least one) is a runtime property the integrity gate checks by
                // reconciling references against emitted definitions. The gate's verdict, not
                // construction, is therefore the honest discharge.
                Verdict = evidence.HasWrapperSurface
                    ? FromVerifier(evidence.WrapperSymbolsIntegral == true)
                    : ObligationVerdict.NotApplicable,
                // Detail must track the gate verdict, not merely whether a surface exists. A failed gate
                // gives an Unproven verdict AND the report is written to disk before the fail-closed
                // return, so success prose here would be a false statement on disk.
                Detail = !evidence.HasWrapperSurface
                    ? "no wrapper surface — no wrapper-targeting P/Invoke to bind"
                    : evidence.WrapperSymbolsIntegral == true
                        ? "the wrapper-symbol integrity gate reconciled every emitted P/Invoke reference "
                            + "against an emitted wrapper definition (existence); the owner map is "
                            + "single-valued by construction (uniqueness)"
                        : "the wrapper-symbol integrity gate found an emitted P/Invoke reference with no "
                            + "emitted wrapper definition (or the gate verdict was unavailable); the module "
                            + "fails closed before publication",
            },
            new()
            {
                // Narrowed to what the ledger can honestly assert at write time: the in-loop verify slice.
                // The "every promised slice" guarantee is finalized by the post-report fat build / strip
                // gate (named in the detail) — this session does not fold that verdict back into the
                // ledger (whole-publication atomicity is deferred), so the row claims only what it proved.
                Number = 5, Obligation = "every retained wrapper definition present in the in-loop verify slice",
                Verifier = "slice-aware wrapper compile results (in-loop verify slice)",
                Verdict = wrapperSliceVerdict, Detail = wrapperSliceDetail,
            },
            new()
            {
                Number = 6, Obligation = "direct-native P/Invokes target exported symbols per slice",
                Verifier = "native-symbol probe (per slice)", Verdict = FromOptionalVerifier(evidence.DirectNativeSymbolsResolved),
                Detail = evidence.DirectNativeSymbolsResolved is null
                    ? "no direct-native symbol probe was performed in this generation pass; the per-slice "
                        + "probe runs in the SDK consumer-targets path"
                    : null,
            },
            new()
            {
                Number = 7, Obligation = "vtable pairs derive from one canonical layout",
                Verifier = "VtableLayout (single source)",
                Verdict = ObligationVerdict.ProvenByConstruction,
                Detail = "every reverse-dispatch walk renders one VtableLayout; a second layout source "
                    + "does not exist",
            },
            new()
            {
                Number = 8, Obligation = "advertised reverse conformances complete",
                Verifier = "conformance capability recompute", Verdict = FromOptionalVerifier(evidence.ReverseConformancesComplete),
                Detail = evidence.ReverseConformancesComplete is null
                    ? "reverse-conformance completeness degrades-and-records at emission (see the emission "
                        + "report's degradedReverseDispatchReceivers) rather than gating in this ledger"
                    : null,
            },
            new()
            {
                Number = 9, Obligation = "C# interface implementations complete under the settled member set",
                Verifier = "C# verifier", Verdict = FromOptionalVerifier(evidence.CSharpVerified),
                Detail = CSharpDetail(evidence.CSharpVerified),
            },
            new()
            {
                Number = 10, Obligation = "retained dependency edges point at retained or external artifacts",
                // The production driver withdraws only self-contained leaf/accessor units and fails any
                // coarser (dependent-carrying) withdrawal closed — the recovery-graph escalation closure
                // that would authorize wider withdrawals is not wired into it. So the discharge is the
                // leaf/accessor self-containment invariant, not the graph closure: naming the unwired
                // graph here would claim a verifier that never runs in production.
                Verifier = "leaf/accessor withdrawals are self-contained (coarse withdrawal fails closed)",
                Verdict = ObligationVerdict.ProvenByConstruction,
                Detail = "production withdraws only self-contained leaf/accessor units — a leaf has no "
                    + "retained dependent to strand, and any coarser culprit fails the module closed "
                    + "(RequiresGraphClosure) rather than withdrawing — so no retained unit is left "
                    + "pointing at a withdrawn artifact by construction",
            },
            new()
            {
                Number = 11, Obligation = "C# compiles with the actual reference set",
                Verifier = "publication-gate C# compile leg", Verdict = FromOptionalVerifier(evidence.CSharpVerified),
                Detail = CSharpDetail(evidence.CSharpVerified),
            },
            new()
            {
                // Narrowed to the in-loop verify slice for the same reason as obligation 5: the authoritative
                // all-slices compile/link is the post-report fat build's job, deferred from this ledger.
                Number = 12, Obligation = "Swift wrappers compile/link for the in-loop verify slice",
                Verifier = "slice-aware wrapper compile results (in-loop verify slice)",
                Verdict = wrapperSliceVerdict, Detail = wrapperSliceDetail,
            },
            new()
            {
                Number = 13, Obligation = "no ABI-contract violation among validator-covered calls",
                Verifier = "ABI-contract validator (finite rule set; [LibraryImport]-covered)",
                Verdict = FromOptionalVerifier(evidence.AbiContractValidated),
                // Narrowed like obligation 3: the checker is a finite rule set over the [LibraryImport]
                // surface and treats unrecognized carriers as compatible (precision over recall), so a
                // clean verdict is "no covered violation", not universal ABI soundness.
                Detail = evidence.AbiContractValidated is true
                    ? "the converged render raised no AbiContractViolationException on the validator's "
                        + "covered calls — a finite rule set over the [LibraryImport] surface that treats "
                        + "unrecognized carriers as compatible (a violation would fail the module closed "
                        + "before publication)"
                    : null,
            },
        };

        if (evidence.SilentTombstoneCount > 0)
        {
            // Surface the degenerate-shape count on the type-surface obligation so a tombstone-heavy
            // ship is visible in the ledger rather than only in the tombstone rows.
            var typeSurface = entries[0];
            entries[0] = typeSurface with
            {
                Detail = $"{typeSurface.Detail}; {evidence.SilentTombstoneCount} type(s) shipped as silent "
                    + "tombstones (opaque, zero usable members)",
            };
        }

        return new PublicationObligationLedger { Entries = entries };
    }
}
