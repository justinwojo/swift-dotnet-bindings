// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Joins the ABI grid manifest to a test run and produces the grid report + gate verdict.
///
/// Inputs are all already on hand at report time: the manifest (source of truth), the merged
/// JSONL of a run (with crash-recovery-synthesized "crash" entries already folded in), the
/// static TestClasses.g.txt inventory (every class.method the source generator knows about,
/// independent of which classes actually ran), and which runtimes were exercised this session.
///
/// Two distinct checks:
///  - Rename-rot (always enforced): every (class, method) a cell cites must exist in the static
///    inventory. A cite that doesn't is a broken manifest — independent of which tests ran, so
///    it is safe and correct to enforce on partial runs too.
///  - Coverage (full runs only): every expect-green cell must be green ("pass" on every declared
///    runtime that was actually exercised). On a partial run (--class-filter / --skip-regen /
///    smoke) the coverage gate only reports; it does not block.
///
/// supported-low-priority reds are reported, never gated. by-design-gray cells are never gated.
/// A declared runtime that wasn't exercised this session is "not-run" — reported, not gated.
/// </summary>
public static class AbiGridReporter
{
    // Per-(cell,runtime) status strings.
    public const string Green = "green";   // every mapped method passed
    public const string Red = "red";       // a mapped method failed or crashed
    public const string Skip = "skip";     // a mapped method was skipped (not actually exercised)
    public const string Missing = "missing"; // a mapped method produced no JSONL entry this run
    public const string NotRun = "not-run"; // this runtime was not exercised this session
    public const string Gray = "gray";     // by-design unsupported (no run expected)

    public static AbiGridReport Generate(
        AbiGridManifest manifest,
        TestClassInventory staticInventory,
        IReadOnlyDictionary<string, JsonlTestResults> resultsByRuntime,
        IReadOnlyList<string> runtimesExercised,
        bool partial,
        string partialReason)
    {
        var report = new AbiGridReport
        {
            Partial = partial,
            PartialReason = partial ? partialReason : null,
            RuntimesExercised = runtimesExercised.ToList(),
        };

        // Manifest-authoring validation (bad disposition, gray w/o reason, dup id, ...).
        // Static integrity — independent of which tests ran, so it blocks on partial runs too.
        report.IntegrityErrors.AddRange(manifest.Validate());

        // Per-runtime fast lookup of run entries by "Class.Method". Each runtime is graded
        // against ITS OWN results (sim against the sim JSONL, device against the device JSONL) —
        // a cell is green only when it passes on every declared+exercised runtime (design §7).
        // Merging into one index would let a sim pass mask a device crash (or vice versa).
        var runIndexByRuntime = new Dictionary<string, Dictionary<string, JsonlTestResults.TestEntry>>(StringComparer.Ordinal);
        foreach (var (rt, results) in resultsByRuntime)
        {
            var idx = new Dictionary<string, JsonlTestResults.TestEntry>(StringComparer.Ordinal);
            foreach (var t in results.Tests)
                idx[$"{t.ClassName}.{t.TestName}"] = t; // last wins (already crash-merged per runtime)
            runIndexByRuntime[rt] = idx;
        }

        var emptyIndex = new Dictionary<string, JsonlTestResults.TestEntry>(StringComparer.Ordinal);
        var exercised = new HashSet<string>(runtimesExercised, StringComparer.Ordinal);

        foreach (var cell in manifest.Cells)
        {
            var cellResult = new AbiCellResult
            {
                Id = cell.Id,
                Disposition = cell.Disposition,
                Reason = cell.Reason,
                RuntimeNote = cell.RuntimeNote,
                Mapping = cell.Mapping.Select(m => $"{m.Class}.{m.Method}").ToList(),
            };

            // --- Rename-rot: every cited method must exist in the static inventory. ---
            foreach (var map in cell.Mapping)
            {
                var known = staticInventory.GetMethods(map.Class).Contains(map.Method);
                if (!known)
                {
                    var msg = $"Cell '{cell.Id}' cites {map.Class}.{map.Method}, which is absent " +
                              "from the test inventory (TestClasses.g.txt) — rename-rot or typo.";
                    cellResult.Notes.Add(msg);
                    report.IntegrityErrors.Add(msg);
                }
            }

            // --- Per-runtime status. ---
            foreach (var rt in cell.Runtimes)
            {
                string status;
                if (cell.Disposition == AbiGridManifest.ByDesignGray)
                    status = Gray;
                else if (!exercised.Contains(rt))
                    status = NotRun;
                else
                    status = CombineMappedStatus(
                        cell, runIndexByRuntime.TryGetValue(rt, out var rtIndex) ? rtIndex : emptyIndex);

                cellResult.RuntimeStatus[rt] = status;
            }

            cellResult.Overall = ComputeOverall(cell, cellResult);

            // --- Coverage gate (expect-green, full runs only). ---
            if (!partial && cell.Disposition == AbiGridManifest.ExpectGreen)
            {
                foreach (var rt in cell.Runtimes)
                {
                    if (!exercised.Contains(rt)) continue; // not-run is not gated
                    var st = cellResult.RuntimeStatus[rt];
                    if (st != Green)
                        report.CoverageErrors.Add(
                            $"expect-green cell '{cell.Id}' is '{st}' on {rt} " +
                            $"(mapped: {string.Join(", ", cellResult.Mapping)}).");
                }
            }

            report.Cells.Add(cellResult);
        }

        report.Json = SerializeArtifact(report);
        report.Table = RenderTable(report);
        report.Rollup = RenderRollup(report);
        return report;
    }

    /// <summary>
    /// Combines the JSONL statuses of all methods a cell maps to into one runtime status.
    /// A crash or fail dominates (red); else a missing entry, else a skip, else green.
    /// </summary>
    private static string CombineMappedStatus(
        AbiGridCell cell,
        IReadOnlyDictionary<string, JsonlTestResults.TestEntry> runIndex)
    {
        if (cell.Mapping.Count == 0)
            return Missing; // no fixture mapped — cannot be green

        bool anyMissing = false, anySkip = false;
        foreach (var map in cell.Mapping)
        {
            if (!runIndex.TryGetValue($"{map.Class}.{map.Method}", out var entry))
            {
                anyMissing = true;
                continue;
            }
            switch (entry.Status)
            {
                case "pass": break;
                case "fail":
                case "crash":
                    return Red; // a single red mapped method reds the cell
                case "skip":
                    anySkip = true;
                    break;
                default:
                    anyMissing = true; // unknown status — treat as not-credibly-green
                    break;
            }
        }

        if (anyMissing) return Missing;
        if (anySkip) return Skip;
        return Green;
    }

    /// <summary>Overall cell status across its declared runtimes.</summary>
    private static string ComputeOverall(AbiGridCell cell, AbiCellResult result)
    {
        if (cell.Disposition == AbiGridManifest.ByDesignGray)
            return Gray;

        var statuses = result.RuntimeStatus.Values.ToList();
        if (statuses.Contains(Red)) return Red;
        if (statuses.Contains(Missing)) return Missing;
        if (statuses.Contains(Skip)) return Skip;

        var exercisedStatuses = statuses.Where(s => s != NotRun).ToList();
        if (exercisedStatuses.Count == 0) return NotRun; // every declared runtime un-exercised
        return exercisedStatuses.All(s => s == Green) ? Green : Missing;
    }

    private static string SerializeArtifact(AbiGridReport report)
    {
        // Ordered runtime columns for stable output (sim before device, then any others).
        var runtimes = report.Cells
            .SelectMany(c => c.RuntimeStatus.Keys)
            .Distinct()
            .OrderBy(RuntimeSortKey).ThenBy(r => r, StringComparer.Ordinal)
            .ToList();

        var dto = new
        {
            schemaVersion = 1,
            partial = report.Partial,
            partialReason = report.PartialReason,
            runtimesExercised = report.RuntimesExercised,
            gatePassed = report.GatePassed,
            gateErrors = report.GateErrors,
            integrityErrors = report.IntegrityErrors,
            coverageErrors = report.CoverageErrors,
            rollup = new
            {
                expectGreen = report.CountByDisposition(AbiGridManifest.ExpectGreen),
                expectGreenGreen = report.CountGreen(AbiGridManifest.ExpectGreen),
                supportedLowPriority = report.CountByDisposition(AbiGridManifest.SupportedLowPriority),
                supportedLowPriorityGreen = report.CountGreen(AbiGridManifest.SupportedLowPriority),
                byDesignGray = report.CountByDisposition(AbiGridManifest.ByDesignGray),
            },
            columns = runtimes,
            cells = report.Cells.Select(c => new
            {
                id = c.Id,
                disposition = c.Disposition,
                overall = c.Overall,
                runtimes = runtimes.ToDictionary(
                    r => r,
                    r => c.RuntimeStatus.TryGetValue(r, out var s) ? s : null),
                mapping = c.Mapping,
                reason = c.Reason,
                runtimeNote = c.RuntimeNote,
                notes = c.Notes,
            }),
        };

        return JsonSerializer.Serialize(dto, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
    }

    private static string RenderTable(AbiGridReport report)
    {
        var runtimes = report.Cells
            .SelectMany(c => c.RuntimeStatus.Keys)
            .Distinct()
            .OrderBy(RuntimeSortKey).ThenBy(r => r, StringComparer.Ordinal)
            .ToList();

        var idWidth = Math.Max(4, report.Cells.Select(c => c.Id.Length).DefaultIfEmpty(4).Max());
        var dispWidth = Math.Max(11, report.Cells.Select(c => c.Disposition.Length).DefaultIfEmpty(11).Max());
        var colWidth = Math.Max(7, runtimes.Select(r => r.Length).DefaultIfEmpty(7).Max());

        var sb = new StringBuilder();
        var header = "Cell".PadRight(idWidth) + "  " + "Disposition".PadRight(dispWidth);
        foreach (var r in runtimes) header += "  " + r.PadRight(colWidth);
        sb.AppendLine(header);
        sb.AppendLine(new string('-', header.Length));

        foreach (var c in report.Cells)
        {
            var line = c.Id.PadRight(idWidth) + "  " + c.Disposition.PadRight(dispWidth);
            foreach (var r in runtimes)
            {
                var s = c.RuntimeStatus.TryGetValue(r, out var v) ? Glyph(v) : "·";
                line += "  " + s.PadRight(colWidth);
            }
            sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderRollup(AbiGridReport report)
    {
        var eg = report.CountByDisposition(AbiGridManifest.ExpectGreen);
        var egGreen = report.CountGreen(AbiGridManifest.ExpectGreen);
        var slp = report.CountByDisposition(AbiGridManifest.SupportedLowPriority);
        var slpGreen = report.CountGreen(AbiGridManifest.SupportedLowPriority);
        var gray = report.CountByDisposition(AbiGridManifest.ByDesignGray);

        var pct = eg == 0 ? 100.0 : 100.0 * egGreen / eg;
        var runtimeList = report.RuntimesExercised.Count > 0
            ? string.Join("+", report.RuntimesExercised)
            : "none";

        var sb = new StringBuilder();
        sb.AppendLine($"expect-green:           {egGreen}/{eg} green ({pct:0.#}%) on {runtimeList}");
        sb.AppendLine($"supported-low-priority: {slpGreen}/{slp} green (reported, not gated)");
        sb.AppendLine($"by-design-gray:         {gray} (out of scope)");
        if (report.Partial)
            sb.AppendLine($"PARTIAL run — coverage gate not enforced ({report.PartialReason}). Rename-rot still enforced.");
        return sb.ToString().TrimEnd();
    }

    private static string Glyph(string status) => status switch
    {
        Green => "green",
        Red => "RED",
        Skip => "skip",
        Missing => "MISSING",
        NotRun => "not-run",
        Gray => "gray",
        _ => status,
    };

    private static int RuntimeSortKey(string r) => r switch
    {
        "sim" => 0,
        "device" => 1,
        _ => 2,
    };
}

/// <summary>The computed grid report + gate verdict for one run.</summary>
public class AbiGridReport
{
    /// <summary>
    /// Static manifest-integrity failures: a malformed manifest (bad disposition, dup id,
    /// gray-without-reason, ...) or rename-rot (a cell citing a method absent from the static
    /// inventory). These are independent of which tests ran, so they block on EVERY run —
    /// including a fast partial (--skip-regen / --class-filter) inner-loop run.
    /// </summary>
    public List<string> IntegrityErrors { get; } = new();

    /// <summary>
    /// Coverage failures: an expect-green cell that did not land green on an exercised runtime.
    /// Run-dependent, so the build only BLOCKS on these for a full run; a partial run reports
    /// them but does not fail (the subset that ran can't speak for the cells that didn't).
    /// </summary>
    public List<string> CoverageErrors { get; } = new();

    public bool Partial { get; set; }
    public string? PartialReason { get; set; }
    public List<string> RuntimesExercised { get; set; } = new();
    public List<AbiCellResult> Cells { get; } = new();

    public string Json { get; set; } = "";
    public string Table { get; set; } = "";
    public string Rollup { get; set; } = "";

    /// <summary>All gate errors, integrity first — for the artifact + diagnostics.</summary>
    public IReadOnlyList<string> GateErrors =>
        IntegrityErrors.Concat(CoverageErrors).ToList();

    /// <summary>True when the grid is fully clean (nothing to report at any severity).</summary>
    public bool GatePassed => IntegrityErrors.Count == 0 && CoverageErrors.Count == 0;

    /// <summary>
    /// Whether the build should FAIL given this run's partial-ness: integrity errors always
    /// block; coverage errors block only on a full run.
    /// </summary>
    public bool IsBlocking(bool partial) =>
        IntegrityErrors.Count > 0 || (!partial && CoverageErrors.Count > 0);

    /// <summary>The errors that actually block, given partial-ness — for the failure message.</summary>
    public IReadOnlyList<string> BlockingErrors(bool partial) =>
        partial ? IntegrityErrors : GateErrors;

    public string BlockingFailureSummary(bool partial)
    {
        var errs = BlockingErrors(partial);
        return errs.Count == 0 ? "" : string.Join("; ", errs.Take(5)) +
            (errs.Count > 5 ? $" (+{errs.Count - 5} more)" : "");
    }

    public int CountByDisposition(string disposition) =>
        Cells.Count(c => c.Disposition == disposition);

    public int CountGreen(string disposition) =>
        Cells.Count(c => c.Disposition == disposition && c.Overall == AbiGridReporter.Green);
}

/// <summary>Per-cell computed result.</summary>
public class AbiCellResult
{
    public string Id { get; set; } = "";
    public string Disposition { get; set; } = "";
    public string? Reason { get; set; }
    public string? RuntimeNote { get; set; }
    public List<string> Mapping { get; set; } = new();
    public Dictionary<string, string> RuntimeStatus { get; } = new(StringComparer.Ordinal);
    public string Overall { get; set; } = "";
    public List<string> Notes { get; } = new();
}
