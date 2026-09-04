// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nuke.Common.IO;

/// <summary>
/// Typed model for build/baselines/validation-baseline.json.
/// Tracks compile gate results per library for regression detection.
/// </summary>
public record ValidationBaseline
{
    [JsonPropertyName("git_sha")] public string GitSha { get; init; } = "";

    [JsonPropertyName("compile_gate")]
    public CompileGate Gate { get; init; } = new();

    [JsonPropertyName("skip_metrics")]
    public SkipMetricsBaseline SkipMetrics { get; init; } = new();

    [JsonPropertyName("runtime_tests")]
    public RuntimeTestsBaseline? RuntimeTests { get; init; }

    [JsonPropertyName("unit_tests")]
    public UnitTestsBaseline? UnitTests { get; init; }

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

    public record RuntimeTestsBaseline
    {
        [JsonPropertyName("simulator")]
        public RuntimeTestsPlatformCounts? Simulator { get; init; }

        [JsonPropertyName("device")]
        public RuntimeTestsPlatformCounts? Device { get; init; }

        // Physical-device Mono full-AOT cell (the .NET-for-iOS default runtime, built by
        // `nuke binding-tests --device --mono-aot`). Tracked separately from the NativeAOT
        // "device" key because the two lanes have different skip sets — the runtime-detected
        // Mono skips apply here and the NativeAOT-Release-shaped ones do not — so a shared
        // floor would either under-gate one lane or false-regress the other.
        [JsonPropertyName("device_monoaot")]
        public RuntimeTestsPlatformCounts? DeviceMonoAot { get; init; }

        [JsonPropertyName("macos")]
        public RuntimeTestsPlatformCounts? MacOS { get; init; }

        // Intel/x86_64 macOS-workload cell (run under Rosetta). Tracked separately
        // from the arm64 "macos" key so the auto-update on a green run never
        // overwrites the arm64 floor with the x64 count (or vice versa).
        [JsonPropertyName("macos_x64")]
        public RuntimeTestsPlatformCounts? MacOSX64 { get; init; }

        [JsonPropertyName("maccatalyst")]
        public RuntimeTestsPlatformCounts? MacCatalyst { get; init; }

        [JsonPropertyName("maccatalyst_x64")]
        public RuntimeTestsPlatformCounts? MacCatalystX64 { get; init; }

        [JsonPropertyName("tvos_simulator")]
        public RuntimeTestsPlatformCounts? TvOSSimulator { get; init; }
    }

    public record RuntimeTestsPlatformCounts
    {
        [JsonPropertyName("pass")] public int Pass { get; init; }
        [JsonPropertyName("fail")] public int Fail { get; init; }
        [JsonPropertyName("skip")] public int Skip { get; init; }
        [JsonPropertyName("crash")] public int Crash { get; init; }
    }

    /// <summary>
    /// Pass-count floor for the unit-test layer (Finding 28's "unit tests have no baseline at all").
    /// Keyed on <c>Swift.Bindings.Unit.Tests</c> ONLY: <c>Swift.Analyzers.Tests</c> is deliberately
    /// excluded because its reference-pack download fails in the sandbox (environmental — see memory
    /// <c>feedback_analyzer_tests_need_refpack</c>), so flooring it would red the gate spuriously.
    /// </summary>
    public record UnitTestsBaseline
    {
        [JsonPropertyName("swift_bindings_unit_pass_floor")]
        public int SwiftBindingsUnitPassFloor { get; init; }
    }

    public record SkipMetricsBaseline
    {
        [JsonPropertyName("total_emitted_members")] public int TotalEmittedMembers { get; init; }
        [JsonPropertyName("total_skipped_members")] public int TotalSkippedMembers { get; init; }
        [JsonPropertyName("skip_rate_pct")] public double SkipRatePct { get; init; }
        [JsonPropertyName("skip_reasons")]
        public IDictionary<string, int> SkipReasons { get; init; }
            = new Dictionary<string, int>();

        /// <summary>
        /// Aggregate per-sub-cause counts from <c>SwiftWrapperPostProcessor</c> across all
        /// validated libraries. Lets us track whether the <c>Pattern2InternalTypeReach</c>
        /// emission gate is taking the load expected of it: the <c>InternalType</c> bucket
        /// should drop to a small, documented residue once the gate ships.
        /// </summary>
        [JsonPropertyName("post_processor_sub_causes")]
        public IDictionary<string, int> PostProcessorSubCauses { get; init; }
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
    /// Matches the bash regression detector in validate-libraries.sh.
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
