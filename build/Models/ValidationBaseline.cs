// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nuke.Common.IO;

/// <summary>
/// Typed model for .validation-baseline.json.
/// Tracks compile gate results per library for regression detection.
/// </summary>
public record ValidationBaseline
{
    [JsonPropertyName("git_sha")] public string GitSha { get; init; } = "";

    [JsonPropertyName("compile_gate")]
    public CompileGate Gate { get; init; } = new();

    [JsonPropertyName("skip_metrics")]
    public SkipMetricsBaseline SkipMetrics { get; init; } = new();

    public record CompileGate
    {
        [JsonPropertyName("libraries")]
        public IDictionary<string, LibraryResult> Libraries { get; init; }
            = new Dictionary<string, LibraryResult>();
    }

    public record LibraryResult
    {
        [JsonPropertyName("compile")] public string Compile { get; init; } = "unknown";
        [JsonPropertyName("errors")] public int Errors { get; init; }
        [JsonPropertyName("lines")] public int Lines { get; init; }
        [JsonPropertyName("dep_compile")] public string DepCompile { get; init; } = "none";
        [JsonPropertyName("swift_compile")] public string SwiftCompile { get; init; } = "unknown";
    }

    public record SkipMetricsBaseline
    {
        [JsonPropertyName("total_emitted_members")] public int TotalEmittedMembers { get; init; }
        [JsonPropertyName("total_skipped_members")] public int TotalSkippedMembers { get; init; }
        [JsonPropertyName("skip_rate_pct")] public double SkipRatePct { get; init; }
        [JsonPropertyName("skip_reasons")]
        public IDictionary<string, int> SkipReasons { get; init; }
            = new Dictionary<string, int>();
    }

    public static ValidationBaseline Load(AbsolutePath path)
        => File.Exists(path)
            ? JsonSerializer.Deserialize<ValidationBaseline>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
            : new();

    public void Save(AbsolutePath path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>
    /// Compares current results against baseline, returns regressions and improvements.
    /// A library "passes" if it compiles standalone (ok/known_errors) OR via dep gate (dep_compile=ok).
    /// Matches the bash regression detector in validate-libraries.sh Phase 5.
    /// </summary>
    public (IReadOnlyList<string> Regressions, IReadOnlyList<string> Improvements,
            IReadOnlyList<string> Drift) Compare(
        IDictionary<string, LibraryResult> currentResults, bool isFullRun)
    {
        var regressions = new List<string>();
        var improvements = new List<string>();
        var drift = new List<string>();

        foreach (var (name, prev) in Gate.Libraries)
        {
            if (!currentResults.TryGetValue(name, out var curr))
            {
                if (isFullRun)
                    regressions.Add($"{name}: {prev.Compile}(present) -> MISSING");
                continue;
            }

            bool prevOk = prev.Compile is "ok" or "known_errors" || prev.DepCompile == "ok";
            bool currOk = curr.Compile is "ok" or "known_errors" || curr.DepCompile == "ok";

            if (prevOk && !currOk)
                regressions.Add($"{name}: {prev.Compile}({prev.Errors}) -> {curr.Compile}({curr.Errors})");
            else if (!prevOk && currOk)
                improvements.Add($"{name}: {prev.Compile}({prev.Errors}) -> {curr.Compile}({curr.Errors})");
            else if (prevOk && currOk && prev.Errors == 0 && curr.Errors > 0)
                regressions.Add($"{name}: ok(0) -> {curr.Compile}({curr.Errors})");
            else if (prevOk && currOk && prev.Errors > 0 && curr.Errors == 0)
                improvements.Add($"{name}: {prev.Compile}({prev.Errors}) -> ok(0)");

            // Swift wrapper regression
            if (prev.SwiftCompile == "ok" && curr.SwiftCompile == "fail")
                regressions.Add($"{name}: swift:ok -> swift:fail");
            else if (prev.SwiftCompile == "fail" && curr.SwiftCompile == "ok")
                improvements.Add($"{name}: swift:fail -> swift:ok");

            // Line drift (>10% change)
            if (prev.Lines > 0)
            {
                double pct = Math.Abs(curr.Lines - prev.Lines) / (double)prev.Lines * 100;
                if (pct > 10)
                    drift.Add($"{name}: {prev.Lines} -> {curr.Lines} ({pct:F0}%)");
            }
        }

        return (regressions, improvements, drift);
    }
}
