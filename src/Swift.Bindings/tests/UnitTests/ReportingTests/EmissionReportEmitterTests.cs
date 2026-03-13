// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests that EmissionReportEmitter produces correct report structure from ModuleEmissionContext.
/// </summary>
public class EmissionReportEmitterTests
{
    [Fact]
    public void BuildReport_EmptyContext_ProducesEmptyReport()
    {
        var ctx = new ModuleEmissionContext();
        var report = EmissionReportEmitter.BuildReport(ctx, "TestModule");

        Assert.Equal("TestModule", report.Module);
        Assert.Empty(report.WrapperStrategyCounts);
        Assert.Empty(report.SkipReasons);
        Assert.Equal(0, report.ConformanceDecisions.EmittedInSource);
        Assert.Equal(0, report.ConformanceDecisions.SkippedAtEmission);
    }

    [Fact]
    public void BuildReport_WrapperStrategyCounts_Aggregated()
    {
        var ctx = new ModuleEmissionContext();
        ctx.IncrementWrapperStrategy("CdeclMethod");
        ctx.IncrementWrapperStrategy("CdeclMethod");
        ctx.IncrementWrapperStrategy("CdeclProperty");

        var report = EmissionReportEmitter.BuildReport(ctx, "TestModule");

        Assert.Equal(2, report.WrapperStrategyCounts["CdeclMethod"]);
        Assert.Equal(1, report.WrapperStrategyCounts["CdeclProperty"]);
    }

    [Fact]
    public void BuildReport_SkipReasons_Aggregated()
    {
        var ctx = new ModuleEmissionContext();
        ctx.IncrementWrapperSkipReason("methodLevelGenerics");
        ctx.IncrementWrapperSkipReason("methodLevelGenerics");
        ctx.IncrementWrapperSkipReason("inoutParams");

        var report = EmissionReportEmitter.BuildReport(ctx, "TestModule");

        Assert.Equal(2, report.SkipReasons["methodLevelGenerics"]);
        Assert.Equal(1, report.SkipReasons["inoutParams"]);
    }

    [Fact]
    public void BuildReport_ConformanceDecisions_Counted()
    {
        var ctx = new ModuleEmissionContext();
        ctx.RecordConformanceDecision("ProtoA", true, null);
        ctx.RecordConformanceDecision("ProtoB", true, null);
        ctx.RecordConformanceDecision("ProtoC", false, "HasSelfRequirement");

        var report = EmissionReportEmitter.BuildReport(ctx, "TestModule");

        Assert.Equal(2, report.ConformanceDecisions.EmittedInSource);
        Assert.Equal(1, report.ConformanceDecisions.SkippedAtEmission);
        Assert.Contains("Pattern 1", report.ConformanceDecisions.Note);
    }

    [Fact]
    public void BuildReport_ConformanceDecisions_NoteIsPresent()
    {
        var ctx = new ModuleEmissionContext();
        var report = EmissionReportEmitter.BuildReport(ctx, "TestModule");

        Assert.NotNull(report.ConformanceDecisions.Note);
        Assert.Contains("EveryProtocol", report.ConformanceDecisions.Note);
    }
}
