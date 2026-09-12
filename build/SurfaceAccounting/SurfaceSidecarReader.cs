// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

public sealed record SurfaceApiManifestRow(
    string Module,
    string Signature,
    string Symbol,
    SurfaceFileReference Evidence);

public sealed record SurfaceSkippedRow(
    string Kind,
    string Name,
    string? ContainingType,
    string Reason,
    string Details,
    string? DeclId,
    string? RootCauseId,
    string? CascadeFrom,
    string? Confidence,
    SurfaceFileReference Evidence);

public sealed record SurfaceSidecarResult(
    string Module,
    IReadOnlyList<SurfaceApiManifestRow> ManifestRows,
    IReadOnlyList<SurfaceSkippedRow> SkippedRows,
    IReadOnlyList<(string Display, SurfaceFileReference Evidence)> WithdrawnUnits,
    IReadOnlyDictionary<string, int?> Counters,
    IReadOnlyList<string> Errors,
    int ApiManifestFileCount);

public static class SurfaceSidecarReader
{
    public static SurfaceSidecarResult Read(string targetDirectory)
    {
        var reportPath = Path.Combine(targetDirectory, "binding-report.json");
        var emissionPath = Path.Combine(targetDirectory, "binding-emission-report.json");
        var artifactPath = Path.Combine(targetDirectory, "binding-artifact-manifest.json");
        var errors = new List<string>();
        var module = "";
        var skipped = new List<SurfaceSkippedRow>();
        var counters = new Dictionary<string, int?>(StringComparer.Ordinal);

        if (File.Exists(reportPath))
        {
            using var report = SurfaceCanonicalJson.ParseStrictFile(reportPath);
            var root = report.RootElement;
            module = RequiredString(root, "ModuleName", reportPath);
            foreach (var property in new[] { "TotalTypes", "EmittedTypes", "SkippedTypes", "TotalMembers", "EmittedMembers", "SkippedMembers", "SynthesizedMembers" })
                counters[property] = OptionalInt(root, property);
            if (!root.TryGetProperty("SkippedItems", out var items))
                throw new InvalidDataException($"{reportPath}: missing SkippedItems array.");
            if (items.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{reportPath}: SkippedItems is not an array.");
            var index = 0;
            foreach (var item in items.EnumerateArray())
            {
                skipped.Add(new SurfaceSkippedRow(
                    RequiredString(item, "Kind", reportPath),
                    RequiredString(item, "Name", reportPath),
                    OptionalString(item, "ContainingType"),
                    RequiredString(item, "Reason", reportPath),
                    OptionalString(item, "Details") ?? "",
                    OptionalString(item, "DeclId"),
                    OptionalString(item, "RootCauseId"),
                    OptionalString(item, "CascadeFrom"),
                    OptionalString(item, "Confidence"),
                    JsonReference(reportPath, targetDirectory, $"/SkippedItems/{index}")));
                index++;
            }
        }
        else
        {
            errors.Add("missing binding-report.json");
        }

        var withdrawn = new List<(string Display, SurfaceFileReference Evidence)>();
        if (File.Exists(emissionPath))
        {
            using var emission = SurfaceCanonicalJson.ParseStrictFile(emissionPath);
            var root = emission.RootElement;
            var emissionModule = RequiredString(root, "module", emissionPath);
            if (module.Length == 0) module = emissionModule;
            else if (!string.Equals(module, emissionModule, StringComparison.Ordinal))
                errors.Add($"module mismatch: binding-report '{module}', emission-report '{emissionModule}'");
            if (!root.TryGetProperty("withdrawnUnits", out var units))
                throw new InvalidDataException($"{emissionPath}: missing withdrawnUnits array.");
            if (units.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{emissionPath}: withdrawnUnits is not an array.");
            var index = 0;
            foreach (var unit in units.EnumerateArray())
            {
                if (unit.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException($"{emissionPath}: withdrawnUnits[{index}] is not a string.");
                withdrawn.Add((unit.GetString()!, JsonReference(
                    emissionPath,
                    targetDirectory,
                    $"/withdrawnUnits/{index}")));
                index++;
            }
        }
        else
        {
            errors.Add("missing binding-emission-report.json");
        }

        if (File.Exists(artifactPath))
        {
            using var artifact = SurfaceCanonicalJson.ParseStrictFile(artifactPath);
            var root = artifact.RootElement;
            var schema = RequiredInt(root, "SchemaVersion", artifactPath);
            if (schema != 3) errors.Add($"unsupported binding-artifact-manifest schema {schema}");
            var status = RequiredString(root, "Status", artifactPath);
            if (!string.Equals(status, "Complete", StringComparison.Ordinal))
                errors.Add($"binding-artifact-manifest status is '{status}'");
            var artifactModule = RequiredString(root, "Module", artifactPath);
            if (module.Length == 0) module = artifactModule;
            else if (!string.Equals(module, artifactModule, StringComparison.Ordinal))
                errors.Add($"module mismatch: report '{module}', artifact '{artifactModule}'");
        }
        else
        {
            errors.Add("missing binding-artifact-manifest.json");
        }

        var manifestRows = ReadApiManifests(targetDirectory, module, errors, out var manifestFileCount);
        return new SurfaceSidecarResult(module, manifestRows, skipped, withdrawn, counters, errors, manifestFileCount);
    }

    public static IReadOnlyList<SurfaceWithdrawalObservation> ReconcileWithdrawals(
        string captureId,
        SurfaceTargetKey targetKey,
        SurfaceSidecarResult sidecars)
    {
        var candidates = sidecars.SkippedRows
            .Where(s => string.Equals(s.Reason, "EmitterFault", StringComparison.Ordinal)
                && s.RootCauseId is not null
                && s.CascadeFrom is null
                && s.Details.Contains("Withdrawn by", StringComparison.OrdinalIgnoreCase))
            .Select(s => new RootCandidate(s, RecoveryDisplay(s.RootCauseId!), RecoveryScope(s.RootCauseId!)))
            .GroupBy(c => c.Display, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => new Queue<RootCandidate>(g.OrderBy(c => c.Skip.RootCauseId, StringComparer.Ordinal)),
                StringComparer.Ordinal);
        var occurrenceCounts = sidecars.WithdrawnUnits
            .GroupBy(w => w.Display, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var candidateCounts = candidates.ToDictionary(p => p.Key, p => p.Value.Count, StringComparer.Ordinal);
        var distinctCandidateCounts = sidecars.SkippedRows
            .Where(s => string.Equals(s.Reason, "EmitterFault", StringComparison.Ordinal)
                && s.RootCauseId is not null
                && s.CascadeFrom is null
                && s.Details.Contains("Withdrawn by", StringComparison.OrdinalIgnoreCase))
            .Select(s => new { Display = RecoveryDisplay(s.RootCauseId!), s.RootCauseId })
            .GroupBy(c => c.Display, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.RootCauseId).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);
        var rows = new List<SurfaceWithdrawalObservation>();

        foreach (var (display, evidence) in sidecars.WithdrawnUnits)
        {
            candidates.TryGetValue(display, out var queue);
            var exactMultiplicity = queue is not null
                && candidateCounts[display] == occurrenceCounts[display]
                && distinctCandidateCounts[display] == candidateCounts[display];
            var candidate = queue is { Count: > 0 } ? queue.Dequeue() : null;
            var canonical = exactMultiplicity ? candidate?.Skip.RootCauseId : null;
            var scope = candidate?.Scope ?? DisplayScope(display);
            var observationId = SurfaceCanonicalJson.Hash(new
            {
                captureId,
                targetKey,
                canonicalRecoveryId = canonical,
                occurrence = evidence.JsonPointer,
                display,
            });
            rows.Add(new SurfaceWithdrawalObservation
            {
                Schema = SurfaceAccountingSchema.Artifact,
                ObservationId = observationId,
                CaptureId = captureId,
                TargetKey = targetKey,
                CanonicalRecoveryId = canonical,
                DeclId = exactMultiplicity ? candidate?.Skip.DeclId : null,
                Scope = scope,
                RawDisplay = display,
                OccurrenceReference = evidence,
                ReportReferences = candidate is null ? [] : [candidate.Skip.Evidence],
                ResolutionStatus = exactMultiplicity ? "resolved-by-multiset" : "unresolved-multiplicity",
            });
        }
        return rows;
    }

    public static string RecoveryDisplay(string canonicalRecoveryId)
    {
        var bang = FindLastUnescaped(canonicalRecoveryId, '!');
        var decl = bang < 0 ? canonicalRecoveryId : canonicalRecoveryId[..bang];
        var scope = bang < 0 ? "unknown" : canonicalRecoveryId[(bang + 1)..];
        var fields = SplitEscaped(decl, '|');
        if (fields.Count < 4) return canonicalRecoveryId;
        var pieces = new[] { fields[0], fields[1], fields[3] }
            .Where(p => !string.IsNullOrEmpty(p));
        return $"{string.Join('.', pieces)} ({scope})";
    }

    private static IReadOnlyList<SurfaceApiManifestRow> ReadApiManifests(
        string targetDirectory,
        string expectedModule,
        List<string> errors,
        out int manifestFileCount)
    {
        var rows = new List<SurfaceApiManifestRow>();
        var paths = Directory.EnumerateFiles(targetDirectory, "*.api-manifest.json", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        manifestFileCount = paths.Count;
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            using var document = SurfaceCanonicalJson.ParseStrictFile(path);
            var root = document.RootElement;
            var schema = RequiredInt(root, "schema_version", path);
            if (schema != 1) throw new InvalidDataException($"{path}: unsupported API manifest schema {schema}.");
            var module = RequiredString(root, "module", path);
            if (expectedModule.Length > 0 && !string.Equals(expectedModule, module, StringComparison.Ordinal))
                errors.Add($"API manifest module '{module}' does not match report module '{expectedModule}'");
            if (!root.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{path}: missing members array.");
            var index = 0;
            foreach (var member in members.EnumerateArray())
            {
                var signature = RequiredString(member, "signature", path);
                if (!signatures.Add(signature))
                    throw new InvalidDataException($"{path}: duplicate API manifest signature '{signature}'.");
                rows.Add(new SurfaceApiManifestRow(
                    module,
                    signature,
                    RequiredString(member, "symbol", path),
                    JsonReference(path, targetDirectory, $"/members/{index}")));
                index++;
            }
        }
        return rows;
    }

    private static IReadOnlyList<string> SplitEscaped(string value, char separator)
    {
        var fields = new List<string>();
        var start = 0;
        var escaped = false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (escaped) { escaped = false; continue; }
            if (ch == '\\') { escaped = true; continue; }
            if (ch != separator) continue;
            fields.Add(value[start..i].Replace($"\\{separator}", separator.ToString(), StringComparison.Ordinal));
            start = i + 1;
        }
        fields.Add(value[start..].Replace($"\\{separator}", separator.ToString(), StringComparison.Ordinal));
        return fields;
    }

    private static int FindLastUnescaped(string value, char character)
    {
        for (var i = value.Length - 1; i >= 0; i--)
        {
            if (value[i] != character) continue;
            var slashes = 0;
            for (var j = i - 1; j >= 0 && value[j] == '\\'; j--) slashes++;
            if (slashes % 2 == 0) return i;
        }
        return -1;
    }

    private static string RecoveryScope(string canonicalRecoveryId)
    {
        var bang = FindLastUnescaped(canonicalRecoveryId, '!');
        return bang < 0 ? "unknown" : canonicalRecoveryId[(bang + 1)..];
    }

    private static string DisplayScope(string display)
    {
        var open = display.LastIndexOf(" (", StringComparison.Ordinal);
        return open < 0 || !display.EndsWith(')') ? "unknown" : display[(open + 2)..^1];
    }

    private static SurfaceFileReference JsonReference(string path, string root, string pointer)
        => new(
            SurfaceCanonicalJson.Sha256File(path),
            Path.GetRelativePath(root, path).Replace('\\', '/'),
            JsonPointer: pointer);

    private static string RequiredString(JsonElement element, string property, string path)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{path}: missing string property '{property}'.");
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int RequiredInt(JsonElement element, string property, string path)
    {
        var value = OptionalInt(element, property);
        return value ?? throw new InvalidDataException($"{path}: missing integer property '{property}'.");
    }

    private static int? OptionalInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private sealed record RootCandidate(SurfaceSkippedRow Skip, string Display, string Scope);
}
