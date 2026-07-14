// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BindingsGeneration;

/// <summary>
/// Emits binding report artifacts (JSON + console summary).
/// </summary>
public static class ReportEmitter
{
    public static void Emit(BindingReport report, string outputDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        var reportPath = Path.Combine(outputDirectory, "binding-report.json");
        var serializerSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = new List<JsonConverter> { new StringEnumConverter() },
        };
        File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, serializerSettings));

        LogSummary(report, logger, reportPath);
    }

    /// <summary>
    /// Logs the binding generation summary for <paramref name="report"/>. Used by the
    /// manifest-driven write path, which writes the JSON itself and only needs the log.
    /// </summary>
    public static void LogSummary(BindingReport report, ILogger logger, string? reportPath = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(logger);

        var typeCoverage = GetCoverage(report.EmittedTypes, report.TotalTypes);
        var memberCoverage = GetCoverage(report.EmittedMembers, report.TotalMembers);

        logger.LogInformation("=== Binding Generation Summary ===");
        logger.LogInformation("Module: {Module}", report.ModuleName);
        logger.LogInformation("Types:      {Emitted} bound, {Skipped} skipped ({Coverage:P1} coverage)", report.EmittedTypes, report.SkippedTypes, typeCoverage);
        logger.LogInformation("Members:    {Emitted} bound, {Skipped} skipped, {Synthesized} synthesized ({Coverage:P1} coverage)",
            report.EmittedMembers, report.SkippedMembers, report.SynthesizedMembers, memberCoverage);

        // Per-kind member breakdown
        if (report.EmittedMembersByKind.Count > 0 || report.SkippedMembersByKind.Count > 0)
        {
            foreach (var kind in new[] { BindingItemKind.Method, BindingItemKind.Property, BindingItemKind.Operator, BindingItemKind.Subscript })
            {
                var emitted = report.EmittedMembersByKind.GetValueOrDefault(kind);
                var skipped = report.SkippedMembersByKind.GetValueOrDefault(kind);
                if (emitted > 0 || skipped > 0)
                {
                    var kindLabel = kind.ToString().PadRight(11);
                    logger.LogInformation("  {Kind} {Emitted} bound, {Skipped} skipped", kindLabel, emitted, skipped);
                }
            }
        }

        if (report.WrappedItems.Count > 0)
        {
            logger.LogInformation("Wrapped:    {Count} (Swift wrappers auto-generated)", report.WrappedItems.Count);
        }

        if (report.BridgedViews.Count > 0)
        {
            logger.LogInformation("SwiftUI:    {Count} views detected for bridge generation", report.BridgedViews.Count);
            if (report.BridgeSummary != null)
            {
                var bs = report.BridgeSummary;
                logger.LogInformation("  Bridge:   {Generated}/{Total} generated ({Percent:F1}%), {Template} templates, {Skipped} skipped",
                    bs.Generated, bs.TotalViews, bs.GeneratedPercent, bs.Template, bs.HintSkipped + bs.Skipped);
            }
        }

        if (report.SkippedItems.Count > 0)
        {
            logger.LogInformation("Skipped items by reason:");
            foreach (var group in report.SkippedItems.GroupBy(i => i.Reason).OrderByDescending(g => g.Count()))
            {
                var description = WorkaroundRecommendations.GetDescription(group.Key);
                if (description != null)
                    logger.LogInformation("  {Reason}: {Count} — {Description}", group.Key, group.Count(), description);
                else
                    logger.LogInformation("  {Reason}: {Count}", group.Key, group.Count());
            }
            LogTriage(report.SkipTriage, logger);
            logger.LogInformation("Skipped items are excluded from C# output but don't affect the rest of the generated API.");
            logger.LogInformation("See binding-report.json for per-item skip reasons and workaround suggestions.");
        }

        if (reportPath != null)
            logger.LogInformation("Report: {ReportPath}", reportPath);
    }

    /// <summary>
    /// Logs the actionability roll-up: how many skips are expected vs. worth a look, and the short
    /// "to review" list. This is the "80% is expected, here's the 20% to investigate" headline.
    /// No-op when the report was never projected (<paramref name="triage"/> null).
    /// </summary>
    private static void LogTriage(SkipTriageSummary? triage, ILogger logger)
    {
        if (triage == null || triage.Total == 0)
            return;

        var expected = triage.ByDisposition.GetValueOrDefault(SkipDisposition.ExpectedNonPublic.ToString())
                     + triage.ByDisposition.GetValueOrDefault(SkipDisposition.ExpectedStructural.ToString());
        var knownLimitation = triage.ByDisposition.GetValueOrDefault(SkipDisposition.KnownLimitation.ToString());
        // Rows whose typed surface was recovered via CSM projection: surfaced so the headline counts still
        // reconcile to Total (expected + known + recovered + review), and a recovered skip never reads as a loss.
        var recovered = triage.ByDisposition.GetValueOrDefault(SkipDisposition.Recovered.ToString());
        var expectedFraction = (double)expected / triage.Total;

        if (recovered > 0)
            logger.LogInformation(
                "Skip triage: {Expected} expected ({Fraction:P0}), {Known} known limitations, {Recovered} recovered via projection, {Review} to review.",
                expected, expectedFraction, knownLimitation, recovered, triage.ReviewCount);
        else
            logger.LogInformation(
                "Skip triage: {Expected} expected ({Fraction:P0}), {Known} known limitations, {Review} to review.",
                expected, expectedFraction, knownLimitation, triage.ReviewCount);

        if (triage.ReviewCount > 0)
        {
            logger.LogInformation("  To review ({Count}) — the tool cannot explain these; investigate:", triage.ReviewCount);
            foreach (var item in triage.ReviewItems)
            {
                var where = item.ContainingType != null ? $"{item.ContainingType}.{item.Name}" : item.Name;
                logger.LogInformation("    {Kind} {Where} — {Reason}", item.Kind, where, item.Reason);
            }
        }

        // Consume-degraded members are a documented KnownLimitation, so they never reach the "to review"
        // list — but a C#-authored conformer handed to one silently no-fires from Swift, and the position
        // may carry no compile signal. Call them out explicitly so a ReviewCount==0 report still names them.
        if (triage.DegradedConsumeCount > 0)
        {
            logger.LogInformation(
                "  Consume-degraded ({Count}) — a C#-authored conformer passed here silently never fires (proxy suppressed):",
                triage.DegradedConsumeCount);
            foreach (var item in triage.DegradedConsumeItems)
            {
                var where = item.ContainingType != null ? $"{item.ContainingType}.{item.Name}" : item.Name;
                logger.LogInformation("    {Kind} {Where}", item.Kind, where);
            }
        }
    }

    private static double GetCoverage(int emitted, int total) =>
        total == 0 ? 1.0 : (double)emitted / total;
}
