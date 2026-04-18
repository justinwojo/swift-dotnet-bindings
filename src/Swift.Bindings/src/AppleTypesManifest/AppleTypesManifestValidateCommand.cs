// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BindingsGeneration.AppleTypesManifest;

// Entry point for the `--validate-apple-types-manifest` CLI mode (Phase 2 / M10).
// Loads the manifest, runs AppleTypesManifestValidator over every entry, and either
// prints a one-line summary per entry (read-only) or writes back the probed VWT
// fields and serializes the updated manifest in place (`--apple-types-manifest-write-back`).
//
// Exit codes:
//   0 — every probed entry passed (no drift, no missing symbols on the host platform).
//       Skipped entries (not advertised on the host) do not fail the run.
//   1 — at least one entry failed (drift, missing symbol, library load failure,
//       or returned-null accessor).
public static class AppleTypesManifestValidateCommand
{
    public static int Run(
        string manifestPath,
        bool writeBack,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            logger.LogError("--validate-apple-types-manifest requires --apple-types-manifest <path>.");
            return 1;
        }
        if (!File.Exists(manifestPath))
        {
            logger.LogError("Manifest file not found: '{Path}'.", manifestPath);
            return 1;
        }

        Manifest manifest;
        try
        {
            var manifestJson = File.ReadAllText(manifestPath);
            manifest = JsonConvert.DeserializeObject<Manifest>(manifestJson)
                ?? throw new InvalidOperationException("Deserialization returned null.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load manifest '{Path}': {Message}", manifestPath, ex.Message);
            return 1;
        }

        var results = AppleTypesManifestValidator.Validate(manifest, writeBack, logger);

        var probed   = 0;
        var matches  = 0;
        var skipped  = 0;
        var failures = 0;

        foreach (var r in results)
        {
            switch (r.Outcome)
            {
                case AppleTypesManifestValidator.ValidationOutcome.Probed:
                    probed++;
                    logger.LogInformation(
                        "PROBED  {Identity}: size={Size} align={Align} stride={Stride} nonPOD={NonPOD} ({Detail})",
                        r.SwiftIdentity, r.ProbedSize, r.ProbedAlignment, r.ProbedStride, r.ProbedIsNonPOD,
                        r.Detail ?? "first probe");
                    break;
                case AppleTypesManifestValidator.ValidationOutcome.ProbedMatchesManifest:
                    matches++;
                    logger.LogInformation(
                        "MATCH   {Identity}: size={Size} align={Align} stride={Stride} nonPOD={NonPOD}",
                        r.SwiftIdentity, r.ProbedSize, r.ProbedAlignment, r.ProbedStride, r.ProbedIsNonPOD);
                    break;
                case AppleTypesManifestValidator.ValidationOutcome.SkippedUnavailableOnHost:
                    skipped++;
                    logger.LogInformation(
                        "SKIP    {Identity}: {Detail}",
                        r.SwiftIdentity, r.Detail ?? "unavailable on host");
                    break;
                default:
                    failures++;
                    logger.LogError(
                        "FAIL    {Identity} ({Outcome}): {Detail}",
                        r.SwiftIdentity, r.Outcome, r.Detail ?? "(no detail)");
                    break;
            }
        }

        logger.LogInformation(
            "Apple types manifest validation: {Total} entries — probed={Probed}, match={Match}, skip={Skip}, fail={Fail}",
            results.Count, probed, matches, skipped, failures);

        if (writeBack && failures == 0 && (probed > 0 || matches > 0))
        {
            // Temp-file + atomic rename: guards against a crash mid-serialization leaving
            // the repo's manifest.json truncated or half-written. Mirrors the pattern in
            // AppleTypesManifestCommand.Run so both write paths give consumers reading
            // manifest.json through git / file-system watches a complete file.
            var tempPath = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                AppleTypesManifestSerializer.WriteTo(manifest, tempPath);
                File.Move(tempPath, manifestPath, overwrite: true);
                logger.LogInformation("Wrote updated manifest to '{Path}'.", manifestPath);
            }
            catch (Exception ex)
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* best effort */ }
                }
                logger.LogError(ex, "Failed to write back manifest '{Path}': {Message}", manifestPath, ex.Message);
                return 1;
            }
        }

        return failures == 0 ? 0 : 1;
    }
}
