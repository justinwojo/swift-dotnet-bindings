// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using BindingsGeneration.Diagnostics;

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BindingsGeneration;

/// <summary>
/// Writes the <see cref="BindingFailureReport"/> to disk and prints a short console summary. Both are
/// best-effort and total: the whole point of this type is that a generation failure leaves durable
/// evidence, so a fault in the reporter itself must never mask — or replace — the original failure.
/// A write error is warned and swallowed; the caller's exit code is untouched.
/// </summary>
public static class BindingFailureReporting
{
    /// <summary>The report's on-disk name, alongside <c>binding-report.json</c> in the output directory.</summary>
    public const string FileName = "binding-failure-report.json";

    /// <summary>How many error diagnostics the console summary prints before eliding the rest.</summary>
    private const int MaxConsoleDiagnostics = 5;

    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        Formatting = Formatting.Indented,
        Converters = new List<JsonConverter> { new StringEnumConverter() },
        NullValueHandling = NullValueHandling.Include,
    };

    /// <summary>
    /// Writes the report (best-effort) and prints the console summary. This is the single call a failing
    /// exit path makes. It must run BEFORE any <c>ReportCollector.Reset()</c> so the attribution the
    /// report projects is still live, and it never throws — a reporter fault leaves the original failure
    /// exactly as it was.
    /// </summary>
    public static void Emit(BindingFailureReport report, string outputDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(logger);

        var writtenPath = TryWrite(report, outputDirectory, logger);
        try
        {
            WriteConsoleSummary(report, writtenPath, logger);
        }
        catch (Exception ex)
        {
            // The console summary is a convenience over the durable on-disk report; a fault printing it
            // must never escape and mask the original generation failure. The file already stands.
            logger.LogWarning("Could not print the failure summary: {Message}.", ex.Message);
        }
    }

    /// <summary>
    /// Best-effort removes a stale <c>binding-failure-report.json</c> left in the output directory by an
    /// earlier failed run, so a subsequent successful generation into the same directory does not leave
    /// false failure evidence beside its <c>binding-report.json</c>. Never throws; a removal fault is
    /// warned and the success stands.
    /// </summary>
    public static void RemoveStaleReport(string outputDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        // Fully total by design: this runs on the SUCCESS path, where a throw would escape into the
        // outer catch and flip a successful generation into a failure exit. Stale-report cleanup is pure
        // best-effort — no fault removing it may ever fail a generation that otherwise succeeded — so it
        // swallows any exception (matching the fatal-emit shield), not just the File.Delete throw set.
        try
        {
            var path = Path.Combine(outputDirectory, FileName);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Could not remove a stale '{FileName}' from '{Dir}': {Message}.",
                FileName, outputDirectory, ex.Message);
        }
    }

    /// <summary>
    /// Serializes the report and writes it atomically next to <c>binding-report.json</c>. Returns the
    /// path written, or null on any IO/serialization error (warned, never thrown).
    /// </summary>
    public static string? TryWrite(BindingFailureReport report, string outputDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, FileName);
            var json = JsonConvert.SerializeObject(report, SerializerSettings);
            AtomicArtifactWriter.Write(path, json);
            return path;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            logger.LogWarning(
                "Could not write '{FileName}' to '{Dir}': {Message}. The original generation failure stands.",
                FileName, outputDirectory, ex.Message);
            return null;
        }
    }

    // Prints the first few error diagnostics — the ones a human actually needs — plus a pointer to the
    // full report, rather than the whole diagnostic dump. Logged at Error so it surfaces alongside the
    // failure regardless of verbosity.
    private static void WriteConsoleSummary(BindingFailureReport report, string? writtenPath, ILogger logger)
    {
        var errors = report.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        foreach (var diagnostic in errors.Take(MaxConsoleDiagnostics))
        {
            var where = diagnostic.Span is { } span && !string.IsNullOrEmpty(span.File)
                ? $"{Path.GetFileName(span.File)}:{span.Line}:{span.Column}: "
                : string.Empty;
            logger.LogError("  {Plane}: {Where}{Message}", diagnostic.Plane, where, diagnostic.Message);
        }

        var elided = errors.Count - Math.Min(errors.Count, MaxConsoleDiagnostics);
        if (elided > 0)
            logger.LogError("  … and {More} more error diagnostic(s) in the report.", elided);

        if (writtenPath is not null)
            logger.LogError("Structured failure evidence written to '{Path}'.", writtenPath);
    }
}
