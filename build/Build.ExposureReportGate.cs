// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Serilog;

partial class Build
{
    /// <summary>
    /// Re-reads the settled generated files independently of the generator's collector and checks
    /// that binding-report.json contains the exact exposure relation and a current completeness
    /// receipt. This invariant has no permissive mode or count baseline.
    /// </summary>
    void RunExposureReportGate()
    {
        Log.Information("=============================================");
        Log.Information(" Direct SwiftSelf exposure report truth gate");
        Log.Information("=============================================");

        if (!Directory.Exists(BtOutputDir))
            throw new Exception($"Exposure report gate: output directory is absent: {BtOutputDir}");

        var apiManifests = Directory.EnumerateFiles(
                BtOutputDir, "*.api-manifest.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (apiManifests.Count == 0)
            throw new Exception("Exposure report gate: expected generated-module inventory is absent (no *.api-manifest.json).");

        var expectedModules = apiManifests.Select(ReadApiManifestModule)
            .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (!expectedModules.Contains(ModuleName))
            throw new Exception($"Exposure report gate: expected module inventory does not contain {ModuleName}.");

        var reportPath = Path.Combine(BtOutputDir, "binding-report.json");
        if (!File.Exists(reportPath))
            throw new Exception($"Exposure report gate: expected report is absent: {reportPath}");

        var allGeneratedFiles = Directory.EnumerateFiles(BtOutputDir, "*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal).ToList();
        foreach (var module in expectedModules)
        {
            if (!allGeneratedFiles.Any(path => BelongsToModule(path, module)))
                throw new Exception($"Exposure report gate: expected module '{module}' has no generated C# inventory.");
        }

        // One binding-report.json belongs to the primary generation. Dependency bindings are
        // generated in a separate output directory and moved beside it afterward for compile-check;
        // including those files would compare a two-module directory with a one-module receipt.
        var generatedFiles = allGeneratedFiles.Where(path => BelongsToModule(path, ModuleName)).ToList();
        if (generatedFiles.Count == 0)
            throw new Exception("Exposure report gate: generated C# inventory is empty.");

        var scan = BindingsGeneration.GeneratedSwiftCallReader.Scan(generatedFiles, BtOutputDir);
        var errors = ExposureReportLedger.Evaluate(File.ReadAllText(reportPath), scan, ModuleName);
        foreach (var error in errors)
            Log.Error("  ✗ {Error}", error);
        if (errors.Count != 0)
            throw new Exception($"Exposure report gate failed with {errors.Count} reporting inconsistency(s).");

        Log.Information(
            "  ✓ {Rows} direct SwiftSelf row(s), {Files} final C# file(s), receipt/hash exact; named positive, resolved-negative, and function-pointer fixtures attributed",
            scan.Observations.Count,
            scan.ScannedFileCount);
    }

    private static string ReadApiManifestModule(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("module", out var module)
            || module.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(module.GetString()))
        {
            throw new Exception($"Exposure report gate: API manifest has no module identity: {path}");
        }
        return module.GetString()!;
    }

    private static bool BelongsToModule(string path, string module)
    {
        var file = Path.GetFileName(path);
        return string.Equals(file, module + ".cs", StringComparison.Ordinal)
            || file.StartsWith(module + ".", StringComparison.Ordinal);
    }
}
