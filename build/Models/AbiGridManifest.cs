// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The ABI Coverage Grid manifest — the single source of truth for thin-corner runtime
/// coverage cells. Each cell declares a stable
/// dotted id, a disposition, the runtimes it runs on, and a name-based mapping to the
/// existing test(s) that cover it. The manifest is hand-maintained; the report layer joins
/// these names against the existing test-results JSONL + TestClasses.g.txt inventory, so no
/// test-infra change is required (the [AbiCell] attribute is a deferred v2 escalation).
/// </summary>
public class AbiGridManifest
{
    public int SchemaVersion { get; set; }
    public string? Description { get; set; }
    public List<AbiGridCell> Cells { get; set; } = new();

    /// <summary>
    /// The manifest schema version this build understands. Bump in lockstep with the manifest's
    /// own <c>schemaVersion</c> whenever the cell shape changes — an unsupported value is a hard
    /// integrity error (Validate), not a silent partial deserialize against a newer/older shape.
    /// </summary>
    public const int ExpectedSchemaVersion = 1;

    /// <summary>The disposition values understood by the report/gate.</summary>
    public const string ExpectGreen = "expect-green";
    public const string SupportedLowPriority = "supported-low-priority";
    public const string ByDesignGray = "by-design-gray";

    private static readonly HashSet<string> KnownDispositions = new(StringComparer.Ordinal)
    {
        ExpectGreen, SupportedLowPriority, ByDesignGray,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads and deserializes the manifest from disk. Throws if the file is missing or
    /// malformed — a present-but-broken manifest is a hard error (the grid cannot run
    /// without its source of truth), not a silently-empty grid.
    /// </summary>
    public static AbiGridManifest Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"ABI grid manifest not found: {filePath}");

        var json = File.ReadAllText(filePath);
        AbiGridManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AbiGridManifest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new Exception($"ABI grid manifest is not valid JSON ({filePath}): {ex.Message}", ex);
        }

        if (manifest == null)
            throw new Exception($"ABI grid manifest deserialized to null: {filePath}");

        // Normalize: default runtimes to [sim, device] when omitted (the design default).
        foreach (var cell in manifest.Cells)
        {
            if (cell.Runtimes == null || cell.Runtimes.Count == 0)
                cell.Runtimes = new List<string> { "sim", "device" };
            cell.Mapping ??= new List<AbiCellMapping>();
        }

        return manifest;
    }

    /// <summary>
    /// Validates the manifest for internal consistency (independent of any test run).
    /// Returns a list of human-readable error strings; empty means valid. These are
    /// manifest-authoring errors (bad disposition, gray without a reason, expect-green
    /// without a fixture, duplicate ids) — distinct from the run-time rename-rot / coverage
    /// checks the reporter performs against the JSONL + inventory.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        // Schema-version handshake: a manifest authored against a different cell shape must not
        // be silently partial-deserialized. Bump ExpectedSchemaVersion + the manifest together.
        if (SchemaVersion != ExpectedSchemaVersion)
            errors.Add($"Manifest schemaVersion {SchemaVersion} is unsupported " +
                       $"(this build expects {ExpectedSchemaVersion}). Update the build and the manifest in lockstep.");

        foreach (var cell in Cells)
        {
            if (string.IsNullOrWhiteSpace(cell.Id))
            {
                errors.Add("A cell has an empty 'id'.");
                continue;
            }

            if (!seenIds.Add(cell.Id))
                errors.Add($"Duplicate cell id '{cell.Id}'.");

            if (string.IsNullOrWhiteSpace(cell.Disposition) || !KnownDispositions.Contains(cell.Disposition))
                errors.Add($"Cell '{cell.Id}' has unknown disposition '{cell.Disposition}'. " +
                           $"Expected one of: {string.Join(", ", KnownDispositions)}.");

            // by-design-gray must cite a reason (roadmap Not Worth Addressing / Out of Scope).
            if (cell.Disposition == ByDesignGray && string.IsNullOrWhiteSpace(cell.Reason))
                errors.Add($"Cell '{cell.Id}' is by-design-gray but has no 'reason'. " +
                           "Gray cells must cite a roadmap 'Not Worth Addressing' / 'Explicitly Out of Scope' entry.");

            // expect-green requires at least one covering fixture.
            if (cell.Disposition == ExpectGreen && (cell.Mapping == null || cell.Mapping.Count == 0))
                errors.Add($"Cell '{cell.Id}' is expect-green but maps to no test. " +
                           "An expect-green cell requires >=1 covering fixture.");

            // Any cell that declares a mapping must declare it completely.
            foreach (var map in cell.Mapping ?? new List<AbiCellMapping>())
            {
                if (string.IsNullOrWhiteSpace(map.Class) || string.IsNullOrWhiteSpace(map.Method))
                    errors.Add($"Cell '{cell.Id}' has a mapping entry missing 'class' or 'method'.");
            }

            if (cell.Runtimes == null || cell.Runtimes.Count == 0)
                errors.Add($"Cell '{cell.Id}' declares no runtimes.");
        }

        return errors;
    }
}

/// <summary>A single grid cell: one point in the feature-interaction space.</summary>
public class AbiGridCell
{
    /// <summary>Stable dotted id, e.g. "tuple.ret.triple.primitive".</summary>
    public string Id { get; set; } = "";

    /// <summary>One of expect-green / supported-low-priority / by-design-gray.</summary>
    public string Disposition { get; set; } = "";

    /// <summary>Required for by-design-gray: cite the roadmap out-of-scope entry.</summary>
    public string? Reason { get; set; }

    /// <summary>Runtimes this cell runs on. Defaults to [sim, device] when omitted.</summary>
    public List<string> Runtimes { get; set; } = new();

    /// <summary>Optional justification when a cell is intentionally one-runtime.</summary>
    public string? RuntimeNote { get; set; }

    /// <summary>The (class, method) test(s) covering this cell. 1:N allowed.</summary>
    public List<AbiCellMapping> Mapping { get; set; } = new();
}

/// <summary>A name-based pointer to one covering test method.</summary>
public class AbiCellMapping
{
    public string Class { get; set; } = "";
    public string Method { get; set; } = "";
}
