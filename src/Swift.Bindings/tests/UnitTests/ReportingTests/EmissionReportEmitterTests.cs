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
        Assert.Empty(report.SilentTombstones);
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

    [Fact]
    public void BuildReport_SilentTombstones_SortedAndDeduped()
    {
        var ctx = new ModuleEmissionContext();
        ctx.AddSilentTombstone("WeatherKit.Forecast");
        ctx.AddSilentTombstone("StoreKit.VerificationResult");
        ctx.AddSilentTombstone("WeatherKit.Forecast"); // duplicate — should dedup

        var report = EmissionReportEmitter.BuildReport(ctx, "TestModule");

        Assert.Equal(2, report.SilentTombstones.Count);
        Assert.Equal("StoreKit.VerificationResult", report.SilentTombstones[0]);
        Assert.Equal("WeatherKit.Forecast", report.SilentTombstones[1]);
    }

    [Fact]
    public void BuildReport_SilentTombstones_EmptyInputIgnored()
    {
        var ctx = new ModuleEmissionContext();
        ctx.AddSilentTombstone("");
        ctx.AddSilentTombstone(null!);

        var report = EmissionReportEmitter.BuildReport(ctx, "TestModule");

        Assert.Empty(report.SilentTombstones);
    }

    [Fact]
    public void IsSilentTombstone_ReturnsTrueForRegisteredType()
    {
        var ctx = new ModuleEmissionContext();
        ctx.AddSilentTombstone("WeatherKit.Forecast");

        Assert.True(ctx.IsSilentTombstone("WeatherKit.Forecast"));
        Assert.False(ctx.IsSilentTombstone("WeatherKit.Weather"));
        Assert.False(ctx.IsSilentTombstone(""));
    }

    [Fact]
    public void AssertSilentTombstoneInvariant_AllRegisteredAlsoEmitted_DoesNotThrow()
    {
        var ctx = new ModuleEmissionContext();
        ctx.AddSilentTombstone("MusicKit.MusicAttributeProperty");
        ctx.AddSilentTombstone("MusicKit.MusicRelationshipProperty");
        ctx.AddEmittedOpaqueType("MusicKit.MusicAttributeProperty");
        ctx.AddEmittedOpaqueType("MusicKit.MusicRelationshipProperty");
        ctx.AddEmittedOpaqueType("MusicKit.SomeOtherOpaqueType"); // emitted-but-not-tombstoned is fine

        EmissionReportEmitter.AssertSilentTombstoneInvariant(ctx, "MusicKit");
    }

    [Fact]
    public void AssertSilentTombstoneInvariant_RegisteredButNotEmitted_Throws()
    {
        var ctx = new ModuleEmissionContext();
        ctx.AddSilentTombstone("MusicKit.MusicAttributeProperty");
        ctx.AddSilentTombstone("MusicKit.RegisteredButNotEmitted");
        ctx.AddEmittedOpaqueType("MusicKit.MusicAttributeProperty");

        var ex = Assert.Throws<InvalidOperationException>(
            () => EmissionReportEmitter.AssertSilentTombstoneInvariant(ctx, "MusicKit"));
        Assert.Contains("MusicKit.RegisteredButNotEmitted", ex.Message);
        Assert.Contains("Silent tombstone invariant violated", ex.Message);
        Assert.Contains("module 'MusicKit'", ex.Message);
    }

    [Fact]
    public void AssertSilentTombstoneInvariant_EmptyContext_DoesNotThrow()
    {
        var ctx = new ModuleEmissionContext();
        EmissionReportEmitter.AssertSilentTombstoneInvariant(ctx, "TestModule");
    }
}
