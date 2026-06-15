// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
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

    [Fact]
    public void TryRecordExistentialDegradation_DedupsPerTypeAndIgnoresEmpty()
    {
        // Defect E: each distinct PAT existential that degrades to `object` is recorded once.
        // First sighting returns true (newly recorded), repeats return false, blanks are ignored —
        // so the loud SWIFTBIND023 channel fires exactly once per type regardless of emission-site count.
        var ctx = new ModuleEmissionContext();

        Assert.True(ctx.TryRecordExistentialDegradation("any AttributeKind"));
        Assert.False(ctx.TryRecordExistentialDegradation("any AttributeKind"));
        Assert.True(ctx.TryRecordExistentialDegradation("any Shape"));
        Assert.False(ctx.TryRecordExistentialDegradation(""));
        Assert.False(ctx.TryRecordExistentialDegradation(null!));

        Assert.Equal(2, ctx.DegradedExistentials.Count);
    }

    [Fact]
    public void BuildReport_DegradedExistentials_SortedAndDeduped()
    {
        var ctx = new ModuleEmissionContext();
        ctx.TryRecordExistentialDegradation("any Shape");
        ctx.TryRecordExistentialDegradation("any AttributeKind");
        ctx.TryRecordExistentialDegradation("any Shape"); // duplicate — should dedup

        var report = EmissionReportEmitter.BuildReport(ctx, "TestModule");

        Assert.Equal(new[] { "any AttributeKind", "any Shape" }, report.DegradedExistentials);
    }

    [Fact]
    public void BuildReport_EmptyContext_DegradedExistentialsEmpty()
    {
        var ctx = new ModuleEmissionContext();
        var report = EmissionReportEmitter.BuildReport(ctx, "TestModule");

        Assert.Empty(report.DegradedExistentials);
    }

    [Fact]
    public void Emit_DegradedExistentials_LogsSwiftbind023OncePerType()
    {
        // The "loud SB-diagnostic instead of silent object degradation" requirement: Emit must raise
        // exactly one SWIFTBIND023 warning per distinct degraded existential, naming the Swift type.
        var ctx = new ModuleEmissionContext();
        ctx.TryRecordExistentialDegradation("any AttributeKind");
        ctx.TryRecordExistentialDegradation("any Shape");
        var logger = new CapturingLogger();
        var tmpDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            EmissionReportEmitter.Emit(ctx, "TestModule", tmpDir, logger);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }

        var swiftbind023 = logger.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("SWIFTBIND023"))
            .ToList();
        Assert.Equal(2, swiftbind023.Count);
        Assert.Contains(swiftbind023, e => e.Message.Contains("any AttributeKind"));
        Assert.Contains(swiftbind023, e => e.Message.Contains("any Shape"));
    }

    [Fact]
    public void EmitDegradationDiagnostics_LogsSwiftbind025OncePerCommentDrop()
    {
        // Finding 53: every // Unsupported: comment-drop the report carries surfaces as exactly one
        // loud SWIFTBIND025 warning naming the dropped declaration.
        var report = new BindingReport { ModuleName = "TestModule" };
        report.UnsupportedCommentDrops.Add("Unsupported: type 'Widget' — unsupported type");
        report.UnsupportedCommentDrops.Add("Unsupported: method 'Fetch' — unsupported existential");
        var logger = new CapturingLogger();

        EmissionReportEmitter.EmitDegradationDiagnostics(report, logger);

        var sb025 = logger.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("SWIFTBIND025"))
            .ToList();
        Assert.Equal(2, sb025.Count);
        Assert.Contains(sb025, e => e.Message.Contains("type 'Widget'"));
        Assert.Contains(sb025, e => e.Message.Contains("method 'Fetch'"));
    }

    [Fact]
    public void EmitDegradationDiagnostics_LogsSwiftbind026OncePerObjectDegradation()
    {
        // Finding 53: every Swift type that degraded to bare `object` surfaces as exactly one loud
        // SWIFTBIND026 warning naming the Swift type.
        var report = new BindingReport { ModuleName = "TestModule" };
        report.ObjectDegradations.Add("any AttributeKind");
        report.ObjectDegradations.Add("any Shape");
        var logger = new CapturingLogger();

        EmissionReportEmitter.EmitDegradationDiagnostics(report, logger);

        var sb026 = logger.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("SWIFTBIND026"))
            .ToList();
        Assert.Equal(2, sb026.Count);
        Assert.Contains(sb026, e => e.Message.Contains("any AttributeKind"));
        Assert.Contains(sb026, e => e.Message.Contains("any Shape"));
    }

    [Fact]
    public void EmitDegradationDiagnostics_EmptyReport_LogsNothing()
    {
        // No degradations recorded → no SB025/SB026 noise.
        var report = new BindingReport { ModuleName = "TestModule" };
        var logger = new CapturingLogger();

        EmissionReportEmitter.EmitDegradationDiagnostics(report, logger);

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("SWIFTBIND025"));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("SWIFTBIND026"));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
