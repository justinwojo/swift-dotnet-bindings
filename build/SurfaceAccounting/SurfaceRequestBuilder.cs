// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>The live corpus a request was checked against.</summary>
public sealed record SurfaceRosterSummary(string ManifestSha256, int Libraries, int Modules, int Targets);

/// <summary>
/// Builds a <c>surface-accounting-request/1</c> from two validate output bases, and checks any
/// request against the live <c>build/validation-libraries.json</c>: its SHA-256 now, and its
/// <see cref="ValidationManifest.ExpandTargets"/> roster exactly.
/// </summary>
public static class SurfaceRequestBuilder
{
    /// <summary>
    /// The old side is a validate output base kept from an earlier run; the tip side is this
    /// invocation's validate output base and the capture validate wrote into it.
    /// </summary>
    public static SurfaceAccountingRequest Build(
        string liveManifestPath,
        string oldOutputBase,
        string? tipOutputBase,
        SurfaceValidateCapture? tipCapture)
    {
        if (tipOutputBase is null || tipCapture is null)
            throw new InvalidOperationException(
                "--surface-old compares against this invocation's validate output, and no validate capture exists in this " +
                "invocation. Run `nuke validate ... surface-accounting --surface-old <old output base>` in one invocation, " +
                "unfiltered and without --quick.");
        RequireComplete(tipCapture, "tip (this invocation's validate)");
        var tipBase = Path.GetFullPath(tipOutputBase);
        var oldBase = Path.GetFullPath(oldOutputBase);
        if (!Directory.Exists(oldBase))
            throw new DirectoryNotFoundException($"--surface-old names no directory: {oldBase}.");
        var oldCapture = SurfaceCaptureBuilder.Read(oldBase);
        RequireComplete(oldCapture, $"old ({oldBase})");

        var oldLock = ReadInputLock(oldBase, oldCapture);
        var tipLock = ReadInputLock(tipBase, tipCapture);
        if (!string.Equals(oldCapture.Capture.InputLockSha256, tipCapture.Capture.InputLockSha256, StringComparison.Ordinal))
        {
            var oldInputs = oldLock.Libraries.SelectMany(l => l.Inputs).ToHashSet();
            var differing = tipLock.Libraries.SelectMany(l => l.Inputs).ToHashSet();
            differing.SymmetricExceptWith(oldInputs);
            throw new InvalidDataException(
                "Old and tip captures generated from different library inputs, so their surfaces are not comparable. " +
                "Run the old validate against the candidate's .libraries. Differing inputs: " +
                string.Join(", ", differing.Select(i => i.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) + ".");
        }

        var manifestPath = Path.GetFullPath(liveManifestPath);
        var manifestSha = SurfaceCanonicalJson.Sha256File(manifestPath);
        var manifest = ValidationManifest.Load(manifestPath);
        var libraries = manifest.Libraries.ToDictionary(l => l.Name, StringComparer.Ordinal);
        var oldProvenance = Provenance(oldCapture, "old");
        var tipProvenance = Provenance(tipCapture, "tip");
        var targets = new List<SurfaceTargetRequest>();
        foreach (var expected in manifest.ExpandTargets(librariesDir: LibrariesDirectoryBeside(manifestPath)))
        {
            if (!tipProvenance.TryGetValue(expected.Name, out var tip))
                throw new InvalidDataException($"Tip capture has no target '{expected.Name}' from the live manifest.");
            if (!oldProvenance.TryGetValue(expected.Name, out var old))
                throw new InvalidDataException($"Old capture has no target '{expected.Name}' from the live manifest.");
            if (old != tip)
                throw new InvalidDataException(
                    $"Target '{expected.Name}' was generated against different SDK, architecture or triple: " +
                    $"old {old.ActualSdk}/{old.Architecture}/{old.TargetTriple}, tip {tip.ActualSdk}/{tip.Architecture}/{tip.TargetTriple}.");
            var library = libraries[expected.LibraryName];
            targets.Add(new SurfaceTargetRequest
            {
                Key = new SurfaceTargetKey(expected.LibraryName, expected.FrameworkModule ?? expected.Name, expected.Platform, expected.Name),
                OldDirectory = Path.Combine(oldBase, expected.Name),
                TipDirectory = Path.Combine(tipBase, expected.Name),
                Mode = expected.Mode,
                Tier = expected.Tier,
                KnownErrors = expected.KnownErrors,
                Dependencies = expected.Dependencies,
                WrapperDependencies = expected.WrapperDeps,
                NamespacePattern = expected.NamespacePattern,
                InputRevision = library.Revision,
                InputVersion = library.Version,
                ActualSdk = tip.ActualSdk,
                Architecture = tip.Architecture,
                TargetTriple = tip.TargetTriple,
            });
        }

        return new SurfaceAccountingRequest
        {
            CorpusId = $"validation-libraries@{manifestSha[..12]}",
            ManifestPath = manifestPath,
            ManifestSha256 = manifestSha,
            InputLockPath = Path.Combine(tipBase, tipCapture.InputLockRelativePath),
            InputLockSha256 = tipCapture.Capture.InputLockSha256,
            ExpectedTargetCount = targets.Count,
            OldCapture = oldCapture.Capture with { Side = "old", EvidenceDirectory = oldBase },
            TipCapture = tipCapture.Capture with { Side = "tip", EvidenceDirectory = tipBase },
            Targets = targets,
        };
    }

    /// <summary>
    /// Checks a request against the live manifest: the request must name the manifest's SHA-256
    /// as it is now, and its rows must be exactly the manifest's expanded targets with the same
    /// metadata and a provenance <see cref="SurfaceAccountingEngine.ValidateTargetProvenance"/> accepts.
    /// </summary>
    public static SurfaceRosterSummary ValidateLiveRoster(SurfaceAccountingRequest request, string liveManifestPath)
    {
        var liveSha = SurfaceCanonicalJson.Sha256File(liveManifestPath);
        if (!string.Equals(request.ManifestSha256, liveSha, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"The request names manifest {request.ManifestSha256}; the live build/validation-libraries.json is {liveSha}.");
        var manifest = ValidationManifest.Load(Path.GetFullPath(liveManifestPath));
        var expanded = manifest.ExpandTargets(librariesDir: LibrariesDirectoryBeside(liveManifestPath)).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        if (request.ExpectedTargetCount != expanded.Count)
            throw new InvalidDataException(
                $"The request expects {request.ExpectedTargetCount} target(s); the live manifest expands to {expanded.Count}.");

        var requested = new Dictionary<string, SurfaceTargetRequest>(StringComparer.Ordinal);
        foreach (var target in request.Targets)
            if (!requested.TryAdd(target.Key.TargetName, target))
                throw new InvalidDataException($"The request names target '{target.Key.TargetName}' more than once.");
        var extra = requested.Keys.Except(expanded.Select(t => t.Name), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (extra.Count > 0)
            throw new InvalidDataException($"The request names target(s) the live manifest does not: {string.Join(", ", extra)}.");
        foreach (var expected in expanded)
        {
            if (!requested.TryGetValue(expected.Name, out var actual))
                throw new InvalidDataException($"The request is missing target '{expected.Name}'.");
            if (!string.Equals(actual.Key.Library, expected.LibraryName, StringComparison.Ordinal)
                || !string.Equals(actual.Key.FrameworkModule, expected.FrameworkModule, StringComparison.Ordinal)
                || !string.Equals(actual.Key.Platform, expected.Platform, StringComparison.Ordinal)
                || !string.Equals(actual.Mode, expected.Mode, StringComparison.Ordinal)
                || actual.Tier != expected.Tier
                || actual.KnownErrors != expected.KnownErrors
                || !actual.Dependencies.SequenceEqual(expected.Dependencies, StringComparer.Ordinal)
                || !actual.WrapperDependencies.SequenceEqual(expected.WrapperDeps, StringComparer.Ordinal)
                || !string.Equals(actual.NamespacePattern, expected.NamespacePattern, StringComparison.Ordinal))
                throw new InvalidDataException($"The request's metadata for '{expected.Name}' differs from the live manifest.");
            SurfaceAccountingEngine.ValidateTargetProvenance(actual);
        }

        var modules = manifest.Libraries.SelectMany(l => l.Products).Select(p => p.Framework)
            .Distinct(StringComparer.Ordinal).Count();
        return new SurfaceRosterSummary(liveSha, manifest.Libraries.Count, modules, expanded.Count);
    }

    // The roster never reads input paths; a rooted directory keeps ExpandTargets off its
    // relative-path fallback, which Nuke's AbsolutePath refuses.
    private static string LibrariesDirectoryBeside(string manifestPath)
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, "..", ".libraries"));

    private static void RequireComplete(SurfaceValidateCapture capture, string side)
    {
        if (!capture.Capture.Complete)
            throw new InvalidDataException(
                $"The {side} surface capture is incomplete: {string.Join("; ", capture.Capture.IncompletenessReasons)}.");
    }

    private static SurfaceInputLock ReadInputLock(string outputBase, SurfaceValidateCapture capture)
    {
        var path = Path.Combine(outputBase, capture.InputLockRelativePath);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Surface capture input lock is missing: {path}.", path);
        if (!string.Equals(SurfaceCanonicalJson.Sha256File(path), capture.Capture.InputLockSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Input lock {path} differs from the hash its capture recorded.");
        return SurfaceCanonicalJson.DeserializeStrict<SurfaceInputLock>(path);
    }

    private static Dictionary<string, SurfaceCaptureTargetProvenance> Provenance(SurfaceValidateCapture capture, string side)
    {
        var map = new Dictionary<string, SurfaceCaptureTargetProvenance>(StringComparer.Ordinal);
        foreach (var target in capture.Targets)
            if (!map.TryAdd(target.TargetName, target))
                throw new InvalidDataException($"The {side} capture names target '{target.TargetName}' more than once.");
        return map;
    }
}

/// <summary>What <c>nuke surface-accounting</c> may do with a complete comparison.</summary>
public enum SurfaceReceiptVerdict
{
    /// <summary>Record the promotion receipt.</summary>
    Receipt,
    /// <summary>Record the receipt and write the bootstrap justification into the baseline.</summary>
    Bootstrap,
    /// <summary>The tip side is not this invocation's validate output: artifacts only, no receipt.</summary>
    NotBound,
    /// <summary>The tip side is this invocation's validate output but cannot authorize promotion: fail.</summary>
    Refused,
}

public sealed record SurfaceReceiptDecision(
    SurfaceReceiptVerdict Verdict,
    string Message,
    string? BootstrapOldSource = null,
    string? BootstrapJustification = null);

/// <summary>
/// Binds the surface-accounting promotion receipt to the candidate: the tip side must be this
/// invocation's validate output, re-hashed now, and the old side must be the source the current
/// baseline was promoted from (its <c>git_sha</c>), unless a bootstrap is justified.
/// </summary>
public static class SurfacePromotionReceipt
{
    public static SurfaceReceiptDecision Decide(
        SurfaceAccountingRequest request,
        SurfaceValidateCapture? invocationCapture,
        string? invocationOutputBase,
        string baselineGitSha,
        string? bootstrapJustification)
    {
        if (invocationCapture is null || invocationOutputBase is null)
            return new(SurfaceReceiptVerdict.NotBound,
                "No full validate ran in this invocation, so this comparison is not a promotion receipt.");
        var tip = request.TipCapture;
        var own = invocationCapture.Capture;
        if (!string.Equals(tip.CaptureId, own.CaptureId, StringComparison.Ordinal)
            || !string.Equals(tip.SourceSha, own.SourceSha, StringComparison.Ordinal)
            || !string.Equals(tip.ToolchainSha256, own.ToolchainSha256, StringComparison.Ordinal)
            || !string.Equals(tip.InputLockSha256, own.InputLockSha256, StringComparison.Ordinal))
            return new(SurfaceReceiptVerdict.NotBound,
                $"The tip capture '{tip.CaptureId}' is not this invocation's validate capture '{own.CaptureId}', " +
                "so this comparison is not a promotion receipt.");

        var outputBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(invocationOutputBase));
        var stages = own.TargetStages.ToDictionary(s => s.TargetName, StringComparer.Ordinal);
        foreach (var target in request.Targets)
        {
            var expected = Path.Combine(outputBase, target.Key.TargetName);
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.TipDirectory)), expected, StringComparison.Ordinal)
                || !stages.ContainsKey(target.Key.TargetName))
                return new(SurfaceReceiptVerdict.NotBound,
                    $"Tip target '{target.Key.TargetName}' is not this invocation's validate output ({expected}), " +
                    "so this comparison is not a promotion receipt.");
        }
        if (!own.Complete)
            return new(SurfaceReceiptVerdict.Refused,
                $"This invocation's validate capture is incomplete: {string.Join("; ", own.IncompletenessReasons)}.");

        var drift = SurfaceCaptureBuilder.VerifyOutputTrees(invocationCapture, outputBase)
            .Where(line => request.Targets.Any(t => line.StartsWith(t.Key.TargetName + ":", StringComparison.Ordinal)))
            .ToList();
        if (drift.Count > 0)
            return new(SurfaceReceiptVerdict.Refused,
                "Validate's output changed after it was captured, so the comparison no longer describes the candidate: " +
                string.Join("; ", drift));

        var oldSource = request.OldCapture.SourceSha;
        if (MatchesBaselineSource(oldSource, baselineGitSha))
            return new(SurfaceReceiptVerdict.Receipt,
                $"Surface accounting receipt recorded: old {oldSource} is the baseline's source, tip is this invocation's validate.");
        if (!string.IsNullOrWhiteSpace(bootstrapJustification))
            return new(SurfaceReceiptVerdict.Bootstrap,
                $"Surface accounting bootstrap: old {oldSource} is not the baseline's git_sha '{baselineGitSha}'; " +
                "the justification is written into the baseline.",
                oldSource, bootstrapJustification.Trim());
        return new(SurfaceReceiptVerdict.Refused,
            $"The old capture's source {oldSource} is not the source the baseline was promoted from (git_sha '{baselineGitSha}'). " +
            "Compare against a validate of that commit, or pass --surface-bootstrap-justification \"<why>\".");
    }

    /// <summary>The baseline's <c>git_sha</c> is a short SHA; it matches a full SHA it is a prefix of.</summary>
    public static bool MatchesBaselineSource(string oldSource, string baselineGitSha)
        => baselineGitSha.Length is >= 7 and <= 40
           && SurfaceCaptureBuilder.IsLowerHex(baselineGitSha, baselineGitSha.Length)
           && oldSource.StartsWith(baselineGitSha, StringComparison.Ordinal);
}
