// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration.AppleTypesManifest;

// Entry point for the `--emit-apple-types-manifest` CLI mode. Orchestrates the three
// pieces: filter load, per-ABI-JSON ingest, serialize. Returns a process exit code.
public static class AppleTypesManifestCommand
{
    public static int Run(
        IEnumerable<string> abiJsonPaths,
        string? includeTypesPath,
        string outputPath,
        int sdkTrainMajor,
        string? sdkTrainLabel,
        Availability? platforms,
        string? generatedBy,
        bool allowPartial,
        ILogger logger)
    {
        var paths = abiJsonPaths?.ToList() ?? new List<string>();
        if (paths.Count == 0)
        {
            logger.LogError("--emit-apple-types-manifest requires at least one --apple-abi-json <path>.");
            return 1;
        }
        foreach (var p in paths)
        {
            if (!File.Exists(p))
            {
                logger.LogError("ABI JSON input not found: '{Path}'.", p);
                return 1;
            }
        }

        IncludeFilter filter;
        if (!string.IsNullOrWhiteSpace(includeTypesPath))
        {
            if (!File.Exists(includeTypesPath))
            {
                logger.LogError("Include-types file not found: '{Path}'.", includeTypesPath);
                return 1;
            }
            filter = IncludeFilter.FromFile(includeTypesPath!);
        }
        else
        {
            logger.LogError("--emit-apple-types-manifest requires --apple-include-types <path>. " +
                "The filter is positive-list only so the supplement cannot accidentally shadow " +
                "Runtime-owned canonical types; see src/docs/Design/apple-swift-types-architecture.md.");
            return 1;
        }

        var builder = new AppleTypesManifestBuilder(filter, logger);
        foreach (var p in paths)
        {
            try
            {
                builder.IngestAbiJson(p);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to ingest ABI JSON '{Path}': {Message}", p, ex.Message);
                return 1;
            }
        }

        // Coverage gate: every identity in include-types.json must be matched in the
        // ingested ABI dumps. An unmatched identity means the type was renamed,
        // dropped, or the include-types entry has a typo — all of which produce a
        // ship manifest missing the expected entry. Without this gate the regression
        // only surfaces when a consumer crashes at runtime. `--allow-partial` exists
        // for dev workflows where one platform's SDK isn't installed; it must never
        // be passed by regenerate.sh's default path.
        var matched = new HashSet<string>(builder.MatchedIdentities, StringComparer.Ordinal);
        var unmatched = filter.RequestedIdentities
            .Where(id => !matched.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        if (unmatched.Count > 0)
        {
            var list = string.Join("\n  - ", unmatched);
            if (allowPartial)
            {
                logger.LogWarning(
                    "{Count} requested Swift identit{Suffix} not matched in ABI dumps " +
                    "(--allow-partial-apple-types-manifest set):\n  - {List}",
                    unmatched.Count,
                    unmatched.Count == 1 ? "y was" : "ies were",
                    list);
            }
            else
            {
                logger.LogError(
                    "{Count} requested Swift identit{Suffix} not matched in ABI dumps. " +
                    "A manifest missing requested types ships broken bindings silently — " +
                    "fix include-types.json or the ABI input, or pass " +
                    "--allow-partial-apple-types-manifest for dev workflows only:\n  - {List}",
                    unmatched.Count,
                    unmatched.Count == 1 ? "y was" : "ies were",
                    list);
                return 1;
            }
        }

        var manifest = builder.Build(new ManifestOptions
        {
            SdkTrainMajor = sdkTrainMajor,
            SdkTrainLabel = sdkTrainLabel,
            Platforms = platforms,
            GeneratedBy = generatedBy ?? "Swift.Bindings --emit-apple-types-manifest",
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        });

        // Temp-file + atomic rename: guards against a crash mid-serialization leaving
        // the repo's manifest.json truncated or half-written. Consumers reading
        // manifest.json through git / file-system watches always see a complete file.
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);
        var tempPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            AppleTypesManifestSerializer.WriteTo(manifest, tempPath);
            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
            throw;
        }

        logger.LogInformation(
            "Wrote manifest to '{Path}' with {TypeCount} types across {ModuleCount} modules (matched {MatchedCount} of {RequestedCount} requested).",
            outputPath,
            manifest.Modules.Values.Sum(m => m.Types.Count),
            manifest.Modules.Count,
            builder.MatchedIdentities.Count,
            filter.RequestedIdentities.Count);
        return 0;
    }
}
