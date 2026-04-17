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
                "Runtime-owned canonical types; see apple-swift-types-architecture.md.");
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

        var manifest = builder.Build(new ManifestOptions
        {
            SdkTrainMajor = sdkTrainMajor,
            SdkTrainLabel = sdkTrainLabel,
            Platforms = platforms,
            GeneratedBy = generatedBy ?? "Swift.Bindings --emit-apple-types-manifest",
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        });

        AppleTypesManifestSerializer.WriteTo(manifest, outputPath);
        logger.LogInformation(
            "Wrote manifest to '{Path}' with {TypeCount} types across {ModuleCount} modules (matched {MatchedCount} of filter).",
            outputPath,
            manifest.Modules.Values.Sum(m => m.Types.Count),
            manifest.Modules.Count,
            builder.MatchedIdentities.Count);
        return 0;
    }
}
