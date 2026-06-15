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
}
