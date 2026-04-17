// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BindingsGeneration.AppleTypesManifest;

// Entry point for the `--emit-apple-types-cs` CLI mode. Consumes the manifest produced
// by `--emit-apple-types-manifest` + an optional sequential-layout whitelist and writes
// C# source into the output directory. The SwiftBindings.Apple MSBuild project invokes
// this as a BeforeCompile step so the emitted sources live in `obj/` and never appear
// in git.
public static class AppleTypesCsCommand
{
    public static int Run(
        string manifestPath,
        string? whitelistPath,
        string outputDir,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            logger.LogError("--emit-apple-types-cs requires --apple-types-manifest <path>.");
            return 1;
        }
        if (!File.Exists(manifestPath))
        {
            logger.LogError("Manifest file not found: '{Path}'.", manifestPath);
            return 1;
        }
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            logger.LogError("--emit-apple-types-cs requires -o <output-dir>.");
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

        SequentialLayoutWhitelist whitelist;
        if (!string.IsNullOrWhiteSpace(whitelistPath))
        {
            if (!File.Exists(whitelistPath))
            {
                logger.LogError("Sequential-layout whitelist file not found: '{Path}'.", whitelistPath);
                return 1;
            }
            try
            {
                whitelist = SequentialLayoutWhitelist.Load(whitelistPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to parse whitelist '{Path}': {Message}", whitelistPath, ex.Message);
                return 1;
            }
        }
        else
        {
            whitelist = SequentialLayoutWhitelist.Empty();
        }

        // Fresh-emit policy: clear the output directory so a manifest entry that has been
        // removed does not leave a stale .cs file behind. Scoped to the emitter's output
        // dir only — never a source-tree directory — so this is safe to do eagerly.
        if (Directory.Exists(outputDir))
        {
            foreach (var file in Directory.EnumerateFiles(outputDir, "*.cs", SearchOption.AllDirectories))
                File.Delete(file);
        }

        var emitter = new AppleTypesCsEmitter(whitelist, logger);
        emitter.Emit(manifest, outputDir);

        var emittedCount = emitter.EmittedFiles.Count;
        var skippedCount = emitter.SkippedEntries.Count;
        logger.LogInformation(
            "Emitted {EmittedCount} C# files to '{OutputDir}'; skipped {SkippedCount} entries.",
            emittedCount, outputDir, skippedCount);

        // Hard gate failures (whitelist opt-in with missing validation) land in SkippedEntries
        // but should NOT fail the command today, because the baseline manifest has size=null
        // everywhere. If a later session probes sizes and adds a real whitelist entry, a
        // refused emission is a configuration bug and should fail — but that's enforced via
        // the baseline zero-regression policy at the `nuke test` level, not here.
        return 0;
    }
}
