// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

// ============================================================
// ABI Coverage Grid — report + gate (--abi-grid)
//
// A post-run reporting layer over the EXISTING test-results JSONL: it joins the
// abi-grid-manifest.json cells to the run's results + the static TestClasses.g.txt
// inventory, renders a green/red/by-design-gray grid, writes BindingTests/output/abi-grid.json,
// and returns a gate verdict. The fixtures themselves are ordinary tests in the normal run —
// there is no separate execution path.
//
// Phase 1 grades sim+device together: each platform run stashes its JSONL under a runtime key
// (StashAbiGridResults), and one merged grid (RunMergedAbiGridReport) is rendered + gated after
// the platform loop, grading each cell against its runtime's own results so a cell is green only
// when it passes on every declared+exercised runtime.
// ============================================================

partial class Build
{
    [Parameter("Emit the ABI coverage grid report + gate after the platform run(s), merging sim+device")]
    readonly bool AbiGrid;

    AbsolutePath AbiGridManifestPath => BindingTestsDir / "abi-grid-manifest.json";
    AbsolutePath AbiGridArtifactPath => BtOutputDir / "abi-grid.json";

    /// <summary>
    /// Per-runtime JSONL results accumulated across the platform runs of a single
    /// <c>nuke binding-tests</c> invocation. Keyed by grid runtime ("sim" / "device"). The merged
    /// grid (<see cref="RunMergedAbiGridReport"/>) grades each cell against its runtime's own
    /// results, so a cell is green only when it passes on every declared+exercised runtime (§7).
    /// </summary>
    readonly Dictionary<string, JsonlTestResults> _abiGridResultsByRuntime = new(StringComparer.Ordinal);

    /// <summary>
    /// Maps a runtime-test platform label to its ABI-grid runtime key, or null when the platform
    /// is not one the grid grades. The grid's two declared runtimes are the iOS Simulator (Mono
    /// JIT) and a physical iOS device (NativeAOT); macOS/Catalyst/tvOS are intentionally not grid
    /// runtimes (the manifest declares only sim/device), so their results don't feed the grid.
    /// </summary>
    static string? AbiGridRuntimeKey(string platform) => platform switch
    {
        "Simulator" => "sim",
        "Device/NativeAOT" => "device",
        _ => null,
    };

    /// <summary>
    /// Records a completed platform run's results for the merged grid. No-op when --abi-grid is
    /// off or the platform is not a grid runtime. A platform that ran but produced no JSONL is
    /// stashed as an empty result set (and counts as exercised) so every mapped cell grades
    /// 'missing' — a full run with zero evidence must fail coverage, not silently pass.
    /// </summary>
    void StashAbiGridResults(string platform, JsonlTestResults? jsonlResults)
    {
        if (!AbiGrid) return;
        var rt = AbiGridRuntimeKey(platform);
        if (rt == null) return;
        _abiGridResultsByRuntime[rt] = jsonlResults ?? new JsonlTestResults();
    }

    /// <summary>
    /// Renders + writes the merged ABI grid from every runtime stashed this invocation and returns
    /// the report (including the gate verdict) WITHOUT throwing — the caller enforces the gate after
    /// the platform loop so a test-failure exception (which means the build already fails) isn't
    /// masked. The report always renders + writes its artifact even when cells are red, so a failing
    /// grid is visible. Manifest integrity (rename-rot, malformed manifest) is enforced on EVERY run
    /// — it reads only the static inventory — so even a degenerate run with no JSONL still grades.
    /// When no grid runtime ran at all (e.g. a --macos-only --abi-grid run) the grade still happens
    /// over the empty result set: integrity blocks, and every cell grades not-run (never gated).
    /// </summary>
    AbiGridReport RunMergedAbiGridReport()
    {
        // --abi-grid was explicitly requested: a missing/broken manifest is a hard error,
        // not a silently-absent grid.
        var manifest = AbiGridManifest.Load(AbiGridManifestPath);

        var inventoryPath = BindingTestsDir / "RuntimeTestsApp" / "TestClasses.g.txt";
        var staticInventory = TestClassInventory.Load(inventoryPath);

        // Partial when a subset of cells/classes could not have run: a class filter, a smoke
        // run (extra classes compiled in / pass-count not comparable), or a skip-regen run
        // (the inventory + bindings may be stale). On a partial run the coverage gate only
        // reports; rename-rot stays enforced (it is independent of which tests ran).
        var partialReasons = new List<string>();
        if (!string.IsNullOrEmpty(ClassFilter)) partialReasons.Add($"--class-filter {ClassFilter}");
        // EffectiveSkipRegen folds in --skip-build (which implies skip-regen, matching the shell
        // script): either reuses a possibly-stale TestClasses.g.txt + JSONL, so the coverage gate
        // can't speak for cells that may not match a fresh emission. Rename-rot stays enforced.
        if (EffectiveSkipRegen)
            partialReasons.Add(SkipBuild && !SkipRegen
                ? "--skip-build (implies skip-regen; possible staleness)"
                : "--skip-regen (possible staleness)");
        var smokeFlags = GetActiveSmokeFlags();
        if (smokeFlags.Count > 0) partialReasons.Add($"smoke flags: {string.Join(",", smokeFlags.Select(f => f.FlagName))}");
        var partial = partialReasons.Count > 0;

        // Runtimes that actually ran this invocation, in stable sim-before-device order. A cell's
        // declared runtime that was NOT exercised grades 'not-run' (reported, never gated).
        var runtimesExercised = new[] { "sim", "device" }
            .Where(_abiGridResultsByRuntime.ContainsKey)
            .ToArray();

        var report = AbiGridReporter.Generate(
            manifest, staticInventory, _abiGridResultsByRuntime, runtimesExercised,
            partial, string.Join("; ", partialReasons));

        // Write the JSON artifact (always).
        Directory.CreateDirectory(BtOutputDir);
        File.WriteAllText(AbiGridArtifactPath, report.Json);

        // Render the human table + rollup.
        var runtimeLabel = runtimesExercised.Length > 0 ? string.Join("+", runtimesExercised) : "none";
        Log.Information("");
        Log.Information("=== ABI COVERAGE GRID ({Runtimes}) ===", runtimeLabel);
        foreach (var row in report.Table.Split('\n'))
            Log.Information("{Row}", row.TrimEnd());
        Log.Information("");
        foreach (var row in report.Rollup.Split('\n'))
            Log.Information("{Row}", row.TrimEnd());
        Log.Information("Artifact: {Path}", AbiGridArtifactPath);

        if (report.GatePassed)
        {
            // On a partial run coverage was deliberately not graded (some cells didn't run), so
            // don't print a bare "PASS" — only the integrity (rename-rot) dimension is clean here.
            if (partial)
                Log.Information("ABI grid gate: rename-rot clean; coverage NOT EVALUATED (partial run).");
            else
                Log.Information("ABI grid gate: PASS");
        }
        else
        {
            // Manifest-integrity issues (rename-rot, malformed manifest) block on EVERY run.
            if (report.IntegrityErrors.Count > 0)
            {
                Log.Error("ABI grid gate: FAIL — {Count} manifest-integrity issue(s) (enforced on all runs)",
                    report.IntegrityErrors.Count);
                foreach (var err in report.IntegrityErrors)
                    Log.Error("  - {Error}", err);
            }

            // Coverage issues block only on a full run; on a partial run they are report-only.
            if (report.CoverageErrors.Count > 0)
            {
                if (partial)
                {
                    Log.Warning("ABI grid gate: {Count} coverage issue(s) — not enforced (partial run)",
                        report.CoverageErrors.Count);
                    foreach (var err in report.CoverageErrors)
                        Log.Warning("  - {Error}", err);
                }
                else
                {
                    Log.Error("ABI grid gate: FAIL — {Count} coverage issue(s)", report.CoverageErrors.Count);
                    foreach (var err in report.CoverageErrors)
                        Log.Error("  - {Error}", err);
                }
            }
        }

        return report;
    }
}
