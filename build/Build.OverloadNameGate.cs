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
// Fail-closed by construction. There is no `--permissive` arm and no flag to skip it: unlike the
// ratchets around it this gate encodes a policy, not a baseline, so there is nothing to reseed and
// no legitimate local state in which a numeric overload name is acceptable.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

partial class Build
{
    /// <summary>
    /// Asserts that no overload-disambiguation decision in the freshly-generated bindings assigned a
    /// bare numeric suffix. Invoked from the --compile-only path after the ingestion-kitchen gate.
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

        var records = new List<OverloadRenameRecord>();
        foreach (var path in reports)
        {
            OverloadRenameReport? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<OverloadRenameReport>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                throw new Exception($"Overload-name gate: {path} is not readable JSON: {ex.Message}");
            }
            if (parsed?.OverloadRenames is { } list)
                records.AddRange(list);
        }

        // Positive control. Every fixture corpus this gate runs over contains colliding overloads, so
        // an empty ledger means the records stopped being written (a manifest round-trip that dropped
        // the section, a resolver that stopped recording), not that the surface got cleaner. Without
        // this the gate would pass vacuously in exactly the situation it exists to catch.
        if (records.Count == 0)
            throw new Exception(
                "Overload-name gate: the generated bindings recorded ZERO overload-disambiguation " +
                "decisions. The BindingTests corpus contains colliding overloads, so an empty ledger " +
                "means the resolver's records are no longer reaching binding-report.json — the gate " +
                "cannot verify anything. Check ReportCollector.RecordOverloadRenamed and the " +
                "GenerationSection.OverloadRenames round-trip.");

        var numeric = records.Where(IsNumericAssignment).ToList();
        foreach (var r in numeric)
        {
            Log.Error("  ✗ {Declaring}.{Emitted} — numeric suffix over natural name '{Natural}' ({Swift})",
                r.DeclaringName, r.EmittedName, r.NaturalName, r.SwiftSignature);
        }

        if (numeric.Count > 0)
            throw new Exception(
                $"Overload-name gate: {numeric.Count} public member(s) carry a resolver-assigned numeric " +
                "suffix. Overload names must come from Swift argument labels or parameter types; a family " +
                "neither can separate is refused with a report entry, never numbered.");

        var byScheme = records
            .GroupBy(r => r.Scheme ?? "Unknown", StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        Log.Information("  ✓ {Count} overload name(s) assigned, none numeric ({Breakdown})",
            records.Count,
            string.Join(", ", byScheme.Select(g => $"{g.Key}: {g.Count()}")));
    }

    /// <summary>
    /// A record is a numeric assignment when the emitted name is the natural name followed only by
    /// digits. Both names come from the same record, so a name that merely ENDS in a digit —
    /// <c>Vector3</c>, <c>Utf8</c>, <c>Sha256</c> — cannot trip this: its natural name ends in the
    /// same digits and the two are equal.
    /// </summary>
    static bool IsNumericAssignment(OverloadRenameRecord record)
    {
        var natural = record.NaturalName;
        var emitted = record.EmittedName;
        if (string.IsNullOrEmpty(natural) || string.IsNullOrEmpty(emitted))
            return false;
        if (emitted.Length <= natural.Length || !emitted.StartsWith(natural, StringComparison.Ordinal))
            return false;
        return emitted.Skip(natural.Length).All(char.IsAsciiDigit);
    }

    /// <summary>Just enough of <c>binding-report.json</c> to read the resolver's decision records.</summary>
    sealed class OverloadRenameReport
    {
        [JsonPropertyName("OverloadRenames")]
        public List<OverloadRenameRecord>? OverloadRenames { get; set; }
    }

    /// <summary>Mirror of the generator's <c>OverloadRenameItem</c>.</summary>
    sealed class OverloadRenameRecord
    {
        public string? DeclaringName { get; set; }
        public string? SwiftSignature { get; set; }
        public string? NaturalName { get; set; }
        public string? EmittedName { get; set; }
        public string? Scheme { get; set; }
    }
}
