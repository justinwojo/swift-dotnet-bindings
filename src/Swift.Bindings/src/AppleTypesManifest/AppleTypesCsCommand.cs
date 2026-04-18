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
        var structuralSkipCount = emitter.StructuralSkips.Count;
        var refusedCount = emitter.RefusedWhitelistEntries.Count;
        logger.LogInformation(
            "Emitted {EmittedCount} C# files to '{OutputDir}'; skipped {SkippedCount} benign entries; " +
            "{StructuralSkipCount} structural skips; refused {RefusedCount} whitelist opt-ins.",
            emittedCount, outputDir, skippedCount, structuralSkipCount, refusedCount);

        // Fail-closed: structural skips mean the manifest is malformed (blank metadata
        // accessor, missing accessor on a non-Runtime-owned type, …) and a type was
        // silently dropped. Benign skips (Runtime-owned canonicals) do NOT count — those
        // are expected per TypeOwnerRegistry and the type lives in Swift.Runtime instead.
        if (structuralSkipCount > 0)
        {
            foreach (var skip in emitter.StructuralSkips)
            {
                logger.LogError(
                    "Structural skip: '{Identity}' — {Reason}",
                    skip.SwiftIdentity, skip.Reason);
            }
            logger.LogError(
                "Manifest contains {StructuralSkipCount} structural skip(s). Fix the manifest " +
                "(regenerate via apple-types-manifest or repair malformed entries) before emitting.",
                structuralSkipCount);
            return 1;
        }

        // Fail-closed: any refused whitelist opt-in (missing or incomplete evidence, or a
        // structural gate miss) means the manifest is shipping a broken layout claim. The
        // type is still emitted via the VWT-opaque fallback so consumers don't lose it,
        // but the build must fail so the regression cannot slip into a release manifest.
        if (refusedCount > 0)
        {
            foreach (var refused in emitter.RefusedWhitelistEntries)
            {
                logger.LogError(
                    "Refused sequential-layout opt-in: '{Identity}' — {Reason}",
                    refused.SwiftIdentity, refused.Reason);
            }
            logger.LogError(
                "Manifest contains {RefusedCount} whitelist opt-in(s) that failed the " +
                "evidence/structural gate. Fix the manifest (add/correct " +
                "sequential_layout_evidence) or drop the whitelist entry.",
                refusedCount);
            return 1;
        }

        return 0;
    }
}
