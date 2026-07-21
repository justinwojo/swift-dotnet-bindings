// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Finding 50: behavior of the ambient <see cref="InputResolutionReport"/> collector — the
/// chronological record of input-edge decisions (slice/arch/artifact selection, dependency loads)
/// that drives both the artifact manifest's input-resolution section and the
/// <c>--strict-inputs</c> fail-closed gate.
/// </summary>
public class InputResolutionReportTests
{
    [Fact]
    public void Reset_ClearsAllDecisions()
    {
        InputResolutionReport.Reset();
        InputResolutionReport.RecordInfo(InputResolutionCategory.SliceSelection, "found");
        InputResolutionReport.RecordDegradation(InputResolutionCategory.Tbd, "ambiguous");
        Assert.NotEmpty(InputResolutionReport.Decisions);

        InputResolutionReport.Reset();

        Assert.Empty(InputResolutionReport.Decisions);
        Assert.False(InputResolutionReport.HasDegradations);
    }

    [Fact]
    public void RecordInfo_DoesNotTripHasDegradations()
    {
        InputResolutionReport.Reset();
        InputResolutionReport.RecordInfo(InputResolutionCategory.SwiftInterface, "found interface");
        InputResolutionReport.RecordInfo(InputResolutionCategory.AbiJson, "arch-specific abi");

        Assert.False(InputResolutionReport.HasDegradations);
        Assert.Equal(2, InputResolutionReport.Decisions.Count);
        Assert.All(InputResolutionReport.Decisions,
            d => Assert.Equal(InputResolutionSeverity.Info, d.Severity));
    }

    [Fact]
    public void RecordDegradation_TripsHasDegradations()
    {
        InputResolutionReport.Reset();
        InputResolutionReport.RecordInfo(InputResolutionCategory.SliceSelection, "preferred slice");
        InputResolutionReport.RecordDegradation(
            InputResolutionCategory.SliceSelection, "device slice absent; fell back to simulator");

        Assert.True(InputResolutionReport.HasDegradations);
    }

    [Fact]
    public void Decisions_PreserveChronologicalOrder()
    {
        InputResolutionReport.Reset();
        InputResolutionReport.RecordInfo(InputResolutionCategory.SliceSelection, "first");
        InputResolutionReport.RecordDegradation(InputResolutionCategory.SwiftInterface, "second");
        InputResolutionReport.RecordInfo(InputResolutionCategory.AbiJson, "third");

        var decisions = InputResolutionReport.Decisions;
        Assert.Equal(3, decisions.Count);
        Assert.Equal("first", decisions[0].Detail);
        Assert.Equal(InputResolutionCategory.SliceSelection, decisions[0].Category);
        Assert.Equal("second", decisions[1].Detail);
        Assert.Equal(InputResolutionSeverity.Degradation, decisions[1].Severity);
        Assert.Equal("third", decisions[2].Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Record_EmptyOrNullDetail_IsIgnored(string? detail)
    {
        InputResolutionReport.Reset();
        InputResolutionReport.RecordInfo(InputResolutionCategory.Tbd, detail!);
        InputResolutionReport.RecordDegradation(InputResolutionCategory.Tbd, detail!);

        Assert.Empty(InputResolutionReport.Decisions);
        Assert.False(InputResolutionReport.HasDegradations);
    }

    [Fact]
    public void Decisions_IsSnapshot_NotLiveView()
    {
        InputResolutionReport.Reset();
        InputResolutionReport.RecordInfo(InputResolutionCategory.SliceSelection, "first");

        var snapshot = InputResolutionReport.Decisions;
        InputResolutionReport.RecordDegradation(InputResolutionCategory.Tbd, "later");

        // The earlier snapshot must not observe the subsequently-recorded decision.
        Assert.Single(snapshot);
        Assert.Equal(2, InputResolutionReport.Decisions.Count);
    }

    private static IngestionLedgerEntry LedgerEntry(string symbol, IngestionStatus status) =>
        new(
            Input: new IngestionInputIdentity("Demo", "Struct", symbol),
            Parent: null,
            Plane: IngestionPlane.Ingest,
            Cause: IngestionCause.MalformedTypeRecord,
            Referenced: null,
            Disposition: status == IngestionStatus.Quarantined
                ? IngestionDisposition.QuarantineType
                : IngestionDisposition.ReportOnly,
            ClosureEvidence: "closure proven complete",
            Status: status);

    [Fact]
    public void EscalateQuarantinesToFatal_RewritesQuarantinedToFatal()
    {
        InputResolutionReport.Reset();
        InputResolutionReport.RecordLedgerEntry(LedgerEntry("QuarantinedRoot", IngestionStatus.Quarantined));
        InputResolutionReport.RecordLedgerEntry(LedgerEntry("QuarantinedDependent", IngestionStatus.Quarantined));
        Assert.True(InputResolutionReport.HasQuarantines);

        InputResolutionReport.EscalateQuarantinesToFatal("SWIFTBIND120: closure unprovable");

        // No entry may still read Quarantined — a failing run never reports a tombstoned-but-shipped
        // withdrawal.
        Assert.False(InputResolutionReport.HasQuarantines);
        Assert.All(InputResolutionReport.Ledger, e => Assert.Equal(IngestionStatus.Fatal, e.Status));
        Assert.All(InputResolutionReport.Ledger,
            e => Assert.Equal(IngestionDisposition.ReportOnlyFatal, e.Disposition));
        // The escalation reason is appended to each entry's evidence so the ledger stays auditable.
        Assert.All(InputResolutionReport.Ledger,
            e => Assert.Contains("SWIFTBIND120: closure unprovable", e.ClosureEvidence));
    }

    [Fact]
    public void EscalateQuarantinesToFatal_LeavesNonQuarantinedEntriesUntouched()
    {
        InputResolutionReport.Reset();
        InputResolutionReport.RecordLedgerEntry(LedgerEntry("Dropped", IngestionStatus.Dropped));
        InputResolutionReport.RecordLedgerEntry(LedgerEntry("Quarantined", IngestionStatus.Quarantined));

        InputResolutionReport.EscalateQuarantinesToFatal("reason");

        var ledger = InputResolutionReport.Ledger;
        Assert.Equal(IngestionStatus.Dropped, ledger[0].Status);
        Assert.Equal("closure proven complete", ledger[0].ClosureEvidence); // unchanged
        Assert.Equal(IngestionStatus.Fatal, ledger[1].Status);
    }

    [Fact]
    public void EscalateQuarantinesToFatal_EmptyLedger_IsNoOp()
    {
        InputResolutionReport.Reset();
        InputResolutionReport.EscalateQuarantinesToFatal("reason");
        Assert.Empty(InputResolutionReport.Ledger);
    }
}
