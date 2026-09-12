// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using Nuke.Common;
using Serilog;

partial class Build
{
    private const string SurfaceFrozenManifestSha256 = "4b89d88dcdddf6208f930db770e37f7f09da78d8d6599fc95f4a1235a870fa46";
    private const int SurfaceFrozenLibraryCount = 66;
    private const int SurfaceFrozenModuleCount = 110;
    private const int SurfaceFrozenTargetCount = 132;

    [Parameter("Path to a surface-accounting-request/1 JSON file containing frozen capture metadata and the explicit target roster")]
    readonly string? SurfaceRequest;

    [Parameter("New output directory for immutable surface-accounting/1 artifacts")]
    readonly string? SurfaceOutput;

    Target SurfaceAccounting => _ => _
        // Ordering only: keep standalone sinks totally ordered for Nuke --strict.
        .After(ReleaseGatesAttest)
        .Executes(() =>
        {
            if (string.IsNullOrWhiteSpace(SurfaceRequest))
                throw new ArgumentException("--surface-request is required.");
            if (string.IsNullOrWhiteSpace(SurfaceOutput))
                throw new ArgumentException("--surface-output is required.");

            var requestPath = Path.GetFullPath(SurfaceRequest);
            var requestDirectory = Path.GetDirectoryName(requestPath)!;
            var request = SurfaceCanonicalJson.DeserializeStrict<SurfaceAccountingRequest>(requestPath);
            request = ResolvePaths(request, requestDirectory);
            ValidateFrozenRoster(request);
            var result = SurfaceAccountingEngine.Analyze(request);
            var schemaPath = RootDirectory / "build" / "SurfaceAccounting" / "schema.surface-accounting-1.json";
            SurfaceAccountingWriter.Write(result, Path.GetFullPath(SurfaceOutput), schemaPath);

            var summary = result.Comparison.Summary;
            Log.Information(
                "Surface accounting wrote {Output}: {Comparable}/{Required} comparable target(s), " +
                "{Removed} removal(s), {Added} addition(s), {Shape} shape/accessor change(s), {Unresolved} unresolved change(s).",
                Path.GetFullPath(SurfaceOutput),
                summary.ComparableTargets,
                summary.RequiredTargets,
                summary.PublicRemovals,
                summary.PublicAdditions,
                summary.ShapeChanges + summary.AccessorLosses,
                summary.UnresolvedChanges);
            if (!summary.Complete)
                throw new Exception(
                    "Surface-accounting comparison is incomplete. Retained artifacts name the missing or unresolved evidence.");
        });

    private static SurfaceAccountingRequest ResolvePaths(SurfaceAccountingRequest request, string requestDirectory)
        => request with
        {
            ManifestPath = ResolvePath(requestDirectory, request.ManifestPath),
            InputLockPath = ResolvePath(requestDirectory, request.InputLockPath),
            Targets = request.Targets.Select(target => target with
            {
                OldDirectory = ResolvePath(requestDirectory, target.OldDirectory),
                TipDirectory = ResolvePath(requestDirectory, target.TipDirectory),
            }).ToList(),
        };

    private static string ResolvePath(string root, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));

    private static void ValidateFrozenRoster(SurfaceAccountingRequest request)
    {
        if (!string.Equals(request.ManifestSha256, SurfaceFrozenManifestSha256, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"P1 requires frozen manifest {SurfaceFrozenManifestSha256}; request names {request.ManifestSha256}.");
        var manifest = ValidationManifest.Load(request.ManifestPath);
        var expanded = manifest.ExpandTargets().OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var moduleCount = manifest.Libraries.SelectMany(l => l.Products).Select(p => p.Framework)
            .Distinct(StringComparer.Ordinal).Count();
        if (manifest.Libraries.Count != SurfaceFrozenLibraryCount
            || moduleCount != SurfaceFrozenModuleCount
            || expanded.Count != SurfaceFrozenTargetCount)
            throw new InvalidDataException(
                $"P1 frozen corpus mismatch: expected {SurfaceFrozenLibraryCount} libraries, " +
                $"{SurfaceFrozenModuleCount} modules and {SurfaceFrozenTargetCount} targets; found " +
                $"{manifest.Libraries.Count}, {moduleCount} and {expanded.Count}.");
        if (request.ExpectedTargetCount != SurfaceFrozenTargetCount)
            throw new InvalidDataException(
                $"P1 request must contain all {SurfaceFrozenTargetCount} targets, not {request.ExpectedTargetCount}.");

        var requested = request.Targets.ToDictionary(t => t.Key.TargetName, StringComparer.Ordinal);
        foreach (var expected in expanded)
        {
            if (!requested.TryGetValue(expected.Name, out var actual))
                throw new InvalidDataException($"P1 request is missing target '{expected.Name}'.");
            if (!string.Equals(actual.Key.Library, expected.LibraryName, StringComparison.Ordinal)
                || !string.Equals(actual.Key.FrameworkModule, expected.FrameworkModule, StringComparison.Ordinal)
                || !string.Equals(actual.Key.Platform, expected.Platform, StringComparison.Ordinal)
                || !string.Equals(actual.Mode, expected.Mode, StringComparison.Ordinal)
                || actual.Tier != expected.Tier
                || actual.KnownErrors != expected.KnownErrors
                || !actual.Dependencies.SequenceEqual(expected.Dependencies, StringComparer.Ordinal)
                || !actual.WrapperDependencies.SequenceEqual(expected.WrapperDeps, StringComparer.Ordinal)
                || !string.Equals(actual.NamespacePattern, expected.NamespacePattern, StringComparison.Ordinal))
                throw new InvalidDataException($"P1 request metadata differs from the frozen manifest for '{expected.Name}'.");
            if (string.IsNullOrWhiteSpace(actual.ActualSdk)
                || string.IsNullOrWhiteSpace(actual.Architecture)
                || string.IsNullOrWhiteSpace(actual.TargetTriple))
                throw new InvalidDataException(
                    $"P1 request target '{expected.Name}' lacks actual SDK, architecture or target triple provenance.");
            SurfaceAccountingEngine.ValidateTargetProvenance(actual);
        }
    }
}
