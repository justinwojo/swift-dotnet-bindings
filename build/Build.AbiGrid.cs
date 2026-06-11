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
// there is no separate execution path. Phase 0 is sim-only proof-of-plumbing.
//
// See src/docs/Design/abi-coverage-grid.md.
// ============================================================

partial class Build
{
    [Parameter("Emit the ABI coverage grid report + gate after the platform run (sim-only in Phase 0)")]
    readonly bool AbiGrid;

    AbsolutePath AbiGridManifestPath => BindingTestsDir / "abi-grid-manifest.json";
    AbsolutePath AbiGridArtifactPath => BtOutputDir / "abi-grid.json";

    /// <summary>
    /// Runs the ABI grid report for a completed platform run. Renders the grid table + rollup,
    /// writes the JSON artifact, and returns the report (including the gate verdict) so the
    /// caller can enforce the gate after the normal runtime verdict. Returns null ONLY when the
    /// grid does not apply to this run (non-sim platform in Phase 0). A run that produced no
    /// results still grades — manifest integrity is enforced regardless, and the absent results
    /// grade every mapped cell as 'missing' (which, on a full run, fails coverage).
    ///
    /// The report ALWAYS renders + writes its artifact before returning — even when cells are
    /// red — so a failing grid is visible, not swallowed by the surrounding runtime verdict.
    /// </summary>
    AbiGridReport? RunAbiGridReport(string platform, JsonlTestResults? jsonlResults)
    {
        // Phase 0 is sim-only. The full sim+device grid is a later phase.
        if (!string.Equals(platform, "Simulator", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("--abi-grid is sim-only in Phase 0; skipping grid for platform '{Platform}'.", platform);
            return null;
        }

        // Manifest integrity (rename-rot, malformed manifest) is enforced on EVERY run — it reads
        // only the static inventory, not the run results. So even a degenerate run that produced no
        // JSONL (aggregated.Tests.Count == 0 -> null) still loads + validates the manifest; the
        // run simply has no results, so every mapped cell grades 'missing' (and coverage, on a full
        // run, fails — a Success verdict with zero test evidence cannot prove the expect-green cells).
        if (jsonlResults == null)
        {
            // Manifest integrity still grades (it reads only the static inventory). Coverage is
            // NOT skipped: with no results every mapped cell grades 'missing', so a full run fails
            // coverage — a verdict with zero test evidence cannot prove the expect-green cells.
            // (On a partial run, coverage is report-only either way.)
            Log.Warning("--abi-grid: no test-results JSONL for this run; manifest integrity is still " +
                        "enforced and every mapped cell will grade 'missing' (a full run fails coverage).");
            jsonlResults = new JsonlTestResults();
        }

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

        var runtimesExercised = new[] { "sim" };
        var report = AbiGridReporter.Generate(
            manifest, staticInventory, jsonlResults, runtimesExercised,
            partial, string.Join("; ", partialReasons));

        // Write the JSON artifact (always).
        Directory.CreateDirectory(BtOutputDir);
        File.WriteAllText(AbiGridArtifactPath, report.Json);

        // Render the human table + rollup.
        Log.Information("");
        Log.Information("=== ABI COVERAGE GRID ({Platform}) ===", platform);
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
