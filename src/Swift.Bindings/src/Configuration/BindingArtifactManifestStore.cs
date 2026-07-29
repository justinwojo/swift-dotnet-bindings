// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BindingsGeneration;

/// <summary>
/// Atomic read/write of <see cref="BindingArtifactManifest"/> on disk. Every phase
/// of the generator pipeline reads the manifest, mutates the section it owns, then
/// writes the manifest plus a rederived <c>binding-report.json</c>. Writes go through
/// <see cref="AtomicArtifactWriter"/>, so a crash mid-write leaves the prior valid manifest
/// in place and concurrent invocations against one output directory do not fault each other.
/// </summary>
public static class BindingArtifactManifestStore
{
    public const string ManifestFileName = "binding-artifact-manifest.json";
    public const string ReportFileName = "binding-report.json";

    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        Formatting = Formatting.Indented,
        Converters = new List<JsonConverter> { new StringEnumConverter() },
        NullValueHandling = NullValueHandling.Include,
    };

    /// <summary>
    /// Returns the generator's <see cref="AssemblyInformationalVersionAttribute"/> value
    /// (e.g. <c>"1.0.0+&lt;sha&gt;"</c>) or null when unavailable. Stamped into the manifest
    /// so a stale on-disk artifact can be tied back to the build that produced it.
    /// </summary>
    public static string? GetGeneratorVersion()
        => typeof(BindingArtifactManifestStore).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>
    /// Reads the manifest from <paramref name="outputDirectory"/>. Returns null when
    /// no manifest file exists. Throws <see cref="InvalidDataException"/> when the
    /// file exists but is unparseable — callers decide whether that is recoverable.
    /// </summary>
    public static BindingArtifactManifest? TryRead(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var path = Path.Combine(outputDirectory, ManifestFileName);
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        try
        {
            return JsonConvert.DeserializeObject<BindingArtifactManifest>(json, SerializerSettings)
                ?? throw new InvalidDataException($"Manifest at '{path}' deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Manifest at '{path}' is corrupt: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reads the existing manifest, applies <paramref name="mutator"/>, and writes
    /// the result atomically (manifest first, then derived report). When no manifest
    /// exists, the missing-state policy is decided by inspecting whether
    /// <c>binding-report.json</c> is on disk: a stray report without a manifest is
    /// corruption (fail), neither file present is a clean standalone-CLI use case
    /// (warn + write partial).
    /// </summary>
    public static BindingArtifactManifest ReadModifyWrite(
        string outputDirectory,
        string moduleName,
        Action<BindingArtifactManifest> mutator,
        ILogger logger,
        string? partialReasonWhenNew = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentNullException.ThrowIfNull(mutator);
        ArgumentNullException.ThrowIfNull(logger);

        var manifest = TryRead(outputDirectory);
        if (manifest == null && File.Exists(Path.Combine(outputDirectory, ReportFileName)))
        {
            // Re-read before calling it corruption. Write lands the manifest first and the report
            // second, so a peer process writing this directory concurrently can put a report on
            // disk in the window between the read above and this existence check — by then its
            // manifest is already there too. Only a report that survives a second look with still
            // no manifest is a genuinely orphaned artifact.
            manifest = TryRead(outputDirectory);
            if (manifest == null)
            {
                throw new InvalidDataException(
                    $"Output directory '{outputDirectory}' has '{ReportFileName}' but no '{ManifestFileName}'. " +
                    "This indicates a corrupt or out-of-date generation artifact. Re-run binding generation.");
            }
        }

        if (manifest == null)
        {
            logger.LogWarning(
                "No '{Manifest}' found in '{Dir}'. Treating as standalone-CLI invocation; the resulting manifest will be marked Partial.",
                ManifestFileName, outputDirectory);

            manifest = new BindingArtifactManifest
            {
                Module = moduleName,
                GeneratorVersion = GetGeneratorVersion(),
                Status = ManifestStatus.Partial,
                PartialReason = partialReasonWhenNew
                    ?? "No prior generation manifest found in this output directory.",
            };
        }
        else if (!string.Equals(manifest.Module, moduleName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Manifest at '{outputDirectory}' is for module '{manifest.Module}' but the current operation targets '{moduleName}'. " +
                "This output directory was produced by a different module — re-running into it would conflate artifacts. " +
                "Use a separate output directory or delete the stale manifest first.");
        }

        mutator(manifest);
        Write(manifest, outputDirectory, logger);
        return manifest;
    }

    /// <summary>
    /// Writes the manifest atomically, then rederives and writes <c>binding-report.json</c>
    /// atomically. The manifest is the source of truth — if report projection fails,
    /// the manifest is already on disk and the prior valid <c>binding-report.json</c>
    /// (if any) is preserved.
    /// </summary>
    public static void Write(BindingArtifactManifest manifest, string outputDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        Directory.CreateDirectory(outputDirectory);

        manifest.Status = manifest.Generation != null
            ? ManifestStatus.Complete
            : ManifestStatus.Partial;
        if (manifest.Status == ManifestStatus.Complete)
            manifest.PartialReason = null;
        else
            manifest.PartialReason ??= "Generation phase has not produced a section in this output directory.";

        var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        var manifestJson = JsonConvert.SerializeObject(manifest, SerializerSettings);
        AtomicArtifactWriter.Write(manifestPath, manifestJson);

        var report = BindingReportProjection.Project(manifest);
        var reportPath = Path.Combine(outputDirectory, ReportFileName);
        var reportJson = JsonConvert.SerializeObject(report, SerializerSettings);
        AtomicArtifactWriter.Write(reportPath, reportJson);
    }
}
