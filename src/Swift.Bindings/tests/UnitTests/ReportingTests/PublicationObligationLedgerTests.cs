// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the adapted publication obligation ledger: the structural obligations are always proven by
/// construction; the verifier-backed ones carry the module's verdict (proven when the verifier passed,
/// not-applicable when it did not run, unproven only when a verifier explicitly failed); and the
/// wrapper-slice obligations fall to not-applicable for a no-wrapper-surface shape. The ledger is a
/// record, so its honesty is the property under test — it never promotes an unrun verifier to proven.
/// </summary>
public class PublicationObligationLedgerTests
{
    private static PublicationEvidence Converged(bool hasWrapper = true, bool? csharp = true) => new()
    {
        HasWrapperSurface = hasWrapper,
        WrapperVerifySliceCompiledClean = hasWrapper,
        WrapperSymbolsIntegral = hasWrapper ? true : (bool?)null,
        CSharpVerified = csharp,
        AbiContractValidated = true,
        SilentTombstoneCount = 0,
    };

    private static ObligationLedgerEntry Entry(PublicationObligationLedger ledger, int number) =>
        ledger.Entries.Single(e => e.Number == number);

    [Fact]
    public void Build_ProducesAllThirteenObligationsInOrder()
    {
        var ledger = PublicationObligationLedgerBuilder.Build(Converged());
        Assert.Equal(Enumerable.Range(1, 13), ledger.Entries.Select(e => e.Number));
        Assert.All(ledger.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Verifier)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(10)]
    public void Build_StructuralObligations_AreProvenByConstruction(int number)
    {
        var ledger = PublicationObligationLedgerBuilder.Build(Converged());
        Assert.Equal(ObligationVerdict.ProvenByConstruction, Entry(ledger, number).Verdict);
    }

    [Fact]
    public void Build_Obligation4_TracksIntegrityGateVerdict_NotProvenByConstruction()
    {
        // Obligation 4's existence half is checked by the wrapper-symbol integrity gate at runtime, so
        // its verdict must track the gate — not be presented as proven by construction (which only
        // covers the owner map's uniqueness). Gate passed → proven by that verifier.
        var passed = PublicationObligationLedgerBuilder.Build(Converged() with { WrapperSymbolsIntegral = true });
        Assert.Equal(ObligationVerdict.ProvenByVerifier, Entry(passed, 4).Verdict);
        Assert.Contains("integrity gate", Entry(passed, 4).Verifier);
    }

    [Fact]
    public void Build_Obligation4_IntegrityGateFailed_IsUnproven()
    {
        // A dangling wrapper-symbol reference (the gate found a violation) must surface as Unproven and
        // fail AllDischarged — never rounded up to proven because uniqueness held by construction.
        var failed = PublicationObligationLedgerBuilder.Build(Converged() with { WrapperSymbolsIntegral = false });
        Assert.Equal(ObligationVerdict.Unproven, Entry(failed, 4).Verdict);
        Assert.False(failed.AllDischarged);
        Assert.Contains(failed.Unproven, e => e.Number == 4);
    }

    [Fact]
    public void Build_WrapperSliceObligations_DetailNamesDownstreamAuthority()
    {
        // The in-loop compile proves only the verify slice; the ledger must say so and name the
        // authoritative post-report fat build / strip gate rather than implying it proved all slices.
        var ledger = PublicationObligationLedgerBuilder.Build(Converged(hasWrapper: true));
        Assert.Contains("after this report", Entry(ledger, 12).Detail);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(12)]
    public void Build_WrapperSliceObligations_ClaimNarrowedToInLoopSlice_NotAllPromisedSlices(int number)
    {
        // Honesty: the obligation the ledger records must not claim "every/all promised slices" when
        // only the in-loop verify slice was proven at write time. The claim is narrowed to what the
        // in-loop compile actually proved; the multi-slice guarantee is the downstream gate's.
        var entry = Entry(PublicationObligationLedgerBuilder.Build(Converged(hasWrapper: true)), number);
        Assert.Contains("in-loop verify slice", entry.Obligation);
        Assert.DoesNotContain("promised slice", entry.Obligation);
    }

    [Fact]
    public void Build_Obligation3_ClaimNarrowedToNoCallingConventionViolation_NotUniversalTypedPlan()
    {
        // The ABI validator covers the source-generated [LibraryImport] P/Invokes (typed plan-backed
        // subset + a text backstop that anchors on [LibraryImport]); directly-emitted [DllImport] runtime
        // thunks are not in the validated set. The row must claim the absence of a *covered* violation,
        // name the backstop, and not assert coverage over every retained P/Invoke.
        var entry = Entry(PublicationObligationLedgerBuilder.Build(Converged()), 3);
        Assert.Contains("calling-convention violation", entry.Obligation);
        Assert.DoesNotContain("per retained P/Invoke", entry.Obligation);
        Assert.DoesNotContain("any retained P/Invoke", entry.Obligation);
        Assert.Contains("backstop", entry.Verifier);
    }

    [Fact]
    public void Build_Obligation2_LayoutClaimAdmitsDocumentedFallback_NotCompletePlan()
    {
        // A size-varying field gets an IntPtr-sized fallback; a field whose size is indeterminate or
        // whose record is unresolvable fails closed (the aggregate is skipped, not silently given a
        // typed field). The construction claim is total coverage via those documented paths — not that
        // every field carries a complete typed layout plan.
        var entry = Entry(PublicationObligationLedgerBuilder.Build(Converged()), 2);
        Assert.Contains("fallback", entry.Detail);
        Assert.Contains("fails closed", entry.Detail);
        Assert.DoesNotContain("no field is emitted without", entry.Detail ?? "");
    }

    [Fact]
    public void Build_Obligation7_VerifierNamesOnlyVtableLayout_NotANonexistentValidator()
    {
        // Obligation 7 is proven by construction from the single VtableLayout source. It must not name a
        // "protocol-plan validation" verifier — no such validator exists in production.
        var entry = Entry(PublicationObligationLedgerBuilder.Build(Converged()), 7);
        Assert.Contains("VtableLayout", entry.Verifier);
        Assert.DoesNotContain("protocol-plan", entry.Verifier);
    }

    [Fact]
    public void Build_CSharpNull_DetailStatesNoCleanInProcessVerdict_NotFalselyClaimingNoVerifierWired()
    {
        // Null covers three reachable paths: no verifier wired, verifier bypassed by no-wrapper
        // convergence, and verifier ran inconclusive. The detail must not assert a single false reason.
        var entry = Entry(PublicationObligationLedgerBuilder.Build(Converged(csharp: null)), 11);
        Assert.Contains("no clean in-process C# verdict", entry.Detail);
    }

    [Fact]
    public void Build_WithWrapperSurface_WrapperSliceObligationsProvenByVerifier()
    {
        var ledger = PublicationObligationLedgerBuilder.Build(Converged(hasWrapper: true));
        Assert.Equal(ObligationVerdict.ProvenByVerifier, Entry(ledger, 5).Verdict);
        Assert.Equal(ObligationVerdict.ProvenByVerifier, Entry(ledger, 12).Verdict);
    }

    [Fact]
    public void Build_NoWrapperSurface_WrapperObligationsNotApplicable()
    {
        var ledger = PublicationObligationLedgerBuilder.Build(Converged(hasWrapper: false));
        Assert.Equal(ObligationVerdict.NotApplicable, Entry(ledger, 4).Verdict);
        Assert.Equal(ObligationVerdict.NotApplicable, Entry(ledger, 5).Verdict);
        Assert.Equal(ObligationVerdict.NotApplicable, Entry(ledger, 12).Verdict);
        // Even with no wrapper, the module still discharges its ABI and structural obligations.
        Assert.Equal(ObligationVerdict.ProvenByVerifier, Entry(ledger, 13).Verdict);
        Assert.Equal(ObligationVerdict.ProvenByConstruction, Entry(ledger, 1).Verdict);
    }

    [Fact]
    public void Build_CSharpVerified_CompileObligationsProvenByVerifier()
    {
        var ledger = PublicationObligationLedgerBuilder.Build(Converged(csharp: true));
        Assert.Equal(ObligationVerdict.ProvenByVerifier, Entry(ledger, 9).Verdict);
        Assert.Equal(ObligationVerdict.ProvenByVerifier, Entry(ledger, 11).Verdict);
    }

    [Fact]
    public void Build_CSharpNotVerified_CompileObligationsNotApplicableWithHonestDetail()
    {
        // No C# verifier wired in this pass — the compile is the standalone gate's job, and the ledger
        // must say so rather than claim proof it does not have.
        var ledger = PublicationObligationLedgerBuilder.Build(Converged(csharp: null));
        Assert.Equal(ObligationVerdict.NotApplicable, Entry(ledger, 9).Verdict);
        Assert.Equal(ObligationVerdict.NotApplicable, Entry(ledger, 11).Verdict);
        Assert.Contains("compile-only", Entry(ledger, 11).Detail);
    }

    [Fact]
    public void Build_AbiValidated_AbiObligationsProvenByVerifier()
    {
        var ledger = PublicationObligationLedgerBuilder.Build(Converged());
        Assert.Equal(ObligationVerdict.ProvenByVerifier, Entry(ledger, 3).Verdict);
        Assert.Equal(ObligationVerdict.ProvenByVerifier, Entry(ledger, 13).Verdict);
    }

    [Fact]
    public void Build_ConvergedLoopPathEvidence_AllDischarged()
    {
        // The evidence the loop path actually passes never leaves an obligation unproven.
        Assert.True(PublicationObligationLedgerBuilder.Build(Converged(hasWrapper: true, csharp: true)).AllDischarged);
        Assert.True(PublicationObligationLedgerBuilder.Build(Converged(hasWrapper: true, csharp: null)).AllDischarged);
        Assert.True(PublicationObligationLedgerBuilder.Build(Converged(hasWrapper: false, csharp: true)).AllDischarged);
    }

    [Fact]
    public void Build_ExplicitVerifierFailure_IsUnprovenNeverSilentlyProven()
    {
        // A verifier that ran and FAILED must surface as Unproven and fail AllDischarged — the ledger
        // never rounds a failure up to proven.
        var evidence = new PublicationEvidence
        {
            HasWrapperSurface = true,
            WrapperVerifySliceCompiledClean = true,
            CSharpVerified = true,
            AbiContractValidated = false, // the ABI validator ran and found a violation
            SilentTombstoneCount = 0,
        };
        var ledger = PublicationObligationLedgerBuilder.Build(evidence);
        Assert.False(ledger.AllDischarged);
        Assert.Contains(ledger.Unproven, e => e.Number == 13);
        Assert.Contains(ledger.Unproven, e => e.Number == 3);
    }

    [Fact]
    public void Build_WrapperFailedButClaimedSurface_IsUnproven()
    {
        var evidence = Converged(hasWrapper: true) with { WrapperVerifySliceCompiledClean = false };
        var ledger = PublicationObligationLedgerBuilder.Build(evidence);
        Assert.Equal(ObligationVerdict.Unproven, Entry(ledger, 5).Verdict);
        Assert.Equal(ObligationVerdict.Unproven, Entry(ledger, 12).Verdict);
        Assert.False(ledger.AllDischarged);
    }

    [Fact]
    public void Build_SilentTombstones_SurfacedOnTypeSurfaceObligationDetail()
    {
        var ledger = PublicationObligationLedgerBuilder.Build(Converged() with { SilentTombstoneCount = 3 });
        Assert.Contains("3 type(s) shipped as silent tombstones", Entry(ledger, 1).Detail);
    }

    [Fact]
    public void Build_NoTombstones_TypeSurfaceDetailOmitsTombstoneClause()
    {
        var ledger = PublicationObligationLedgerBuilder.Build(Converged() with { SilentTombstoneCount = 0 });
        Assert.DoesNotContain("tombstone", Entry(ledger, 1).Detail ?? "");
    }
}
