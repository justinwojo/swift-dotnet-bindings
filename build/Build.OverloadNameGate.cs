// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.OverloadNameGate.cs — "no bare numeric suffix on the public surface" ship gate for
// `nuke binding-tests --compile-only`.
//
// Colliding overloads used to be numbered in declaration order (`Configure`, `Configure2`,
// `Configure3`). Two things were wrong with that: the suffix tells a consumer nothing at a call
// site, and the rank shifts when upstream inserts an overload earlier in the file, silently
// renaming API someone already compiled against. Names are now derived from the member's own Swift
// argument labels or parameter types, and a family that neither can separate is refused outright
// rather than numbered. This gate is what keeps that true.
//
// It reads the resolver's OWN decision records — `OverloadRenames` in `binding-report.json`, each
// carrying the natural name AND the assigned one — not the emitted identifiers. That distinction is
// the whole point: no check over identifiers can tell a resolver-assigned `Configure2` from a Swift
// author's own `vector3`, but a record whose assigned name is its natural name plus digits is
// unambiguously the former.
//
// The claim is about the OVERLOAD lane, and one other lane deliberately numbers members: two
// sibling Swift names differing only by case (`url` alongside `URL`) project onto one C# identifier
// and carry no labels or parameter types to derive a name from, so the later declaration takes a
// numeric suffix. Those decisions travel in their own `CaseOnlyRenames` channel and are REPORTED
// here rather than failed — the naming policy for that arm is a separate question, and a gate that
// silently omitted them would leave numeric public names nothing accounts for.
//
// Fail-closed by construction. There is no `--permissive` arm and no flag to skip it: unlike the
// ratchets around it this gate encodes a policy, not a baseline, so there is nothing to reseed and
// no legitimate local state in which a numeric OVERLOAD name is acceptable.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

partial class Build
{
    /// <summary>
    /// Asserts that no overload-disambiguation decision in the freshly-generated bindings assigned a
    /// bare numeric suffix, and reports the case-only lane's deliberate numeric assignments as their
    /// own non-failing category. Invoked from the --compile-only path after the ingestion-kitchen gate.
    /// </summary>
    void RunOverloadNameGate()
    {
        Log.Information("=========================================");
        Log.Information(" Overload-name gate (no numeric suffixes)");
        Log.Information("=========================================");

        var reports = Directory.Exists(BtOutputDir)
            ? Directory.EnumerateFiles(BtOutputDir, "binding-report.json", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList()
            : new List<string>();

        if (reports.Count == 0)
            throw new Exception(
                $"Overload-name gate: no `binding-report.json` found under {BtOutputDir}. " +
                "Run `nuke binding-tests --compile-only` (regenerates) first.");

        var overloadRenames = new List<OverloadRenameRecord>();
        var caseOnlyRenames = new List<CaseOnlyRenameRecord>();
        foreach (var path in reports)
        {
            OverloadNameLedgerDocument parsed;
            try
            {
                parsed = OverloadNameLedger.Parse(File.ReadAllText(path));
            }
            catch (JsonException ex)
            {
                throw new Exception($"Overload-name gate: {path} is not readable JSON: {ex.Message}");
            }
            if (parsed.OverloadRenames is { } overloads)
                overloadRenames.AddRange(overloads);
            if (parsed.CaseOnlyRenames is { } caseOnly)
                caseOnlyRenames.AddRange(caseOnly);
        }

        var verdict = OverloadNameLedger.Evaluate(overloadRenames, caseOnlyRenames);

        foreach (var r in verdict.NumericOverloadAssignments)
        {
            Log.Error("  ✗ {Declaring}.{Emitted} — numeric suffix over natural name '{Natural}' ({Swift})",
                r.DeclaringName, r.EmittedName, r.NaturalName, r.SwiftSignature);
        }

        if (!verdict.Passed)
            throw new Exception("Overload-name gate: " + string.Join(" | ", verdict.Failures));

        Log.Information("  ✓ {Count} overload name(s) assigned, none numeric ({Breakdown})",
            overloadRenames.Count,
            verdict.OverloadSchemeBreakdown);

        // Reported, not asserted. The case-only arm's numeric scheme is deliberate; whether it
        // SHOULD be numeric is a naming-policy question this gate does not decide. What it does do
        // is make the assignments countable and attributable instead of leaving them to be
        // discovered by reading generated C#.
        Log.Information("  · {Count} case-only member rename(s), {Numeric} numeric ({Breakdown})",
            verdict.CaseOnlyAssignments.Count,
            verdict.NumericCaseOnlyAssignments.Count,
            verdict.CaseOnlySchemeBreakdown);
        foreach (var r in verdict.NumericCaseOnlyAssignments)
        {
            Log.Information("      {Declaring}.{Emitted} — Swift '{Swift}' over natural name '{Natural}'",
                r.DeclaringName, r.EmittedName, r.SwiftName, r.NaturalName);
        }
    }
}
