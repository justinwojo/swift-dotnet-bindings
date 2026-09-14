// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

public static class SurfaceAccountingEngine
{
    public static SurfaceAccountingResult Analyze(SurfaceAccountingRequest request)
    {
        ValidateRequest(request);
        var oldArtifacts = AnalyzeCapture(request, request.OldCapture, oldSide: true);
        var tipArtifacts = AnalyzeCapture(request, request.TipCapture, oldSide: false);
        var comparison = SurfaceAccountingComparer.Compare(request, oldArtifacts, tipArtifacts);
        return new SurfaceAccountingResult(request, oldArtifacts, tipArtifacts, comparison);
    }

    public static void ValidateTargetProvenance(SurfaceTargetRequest target)
    {
        var sdk = target.ActualSdk;
        var architecture = target.Architecture;
        var triple = target.TargetTriple;
        if (string.IsNullOrWhiteSpace(sdk)
            || string.IsNullOrWhiteSpace(architecture)
            || string.IsNullOrWhiteSpace(triple))
            throw InvalidTargetProvenance(target, sdk, architecture, triple);

        var architecturePattern = Regex.Escape(architecture);
        const string versionPattern = "[0-9]+(?:\\.[0-9]+)*";
        var simulator = triple.EndsWith("-simulator", StringComparison.Ordinal);
        var expectedSdk = target.Key.Platform switch
        {
            "ios" => simulator ? "iphonesimulator" : "iphoneos",
            "macos" or "maccatalyst" => "macosx",
            "tvos" => simulator ? "appletvsimulator" : "appletvos",
            _ => null,
        };
        var triplePattern = target.Key.Platform switch
        {
            "ios" => $"^{architecturePattern}-apple-ios{versionPattern}{(simulator ? "-simulator" : "")}$",
            "macos" => $"^{architecturePattern}-apple-macosx?{versionPattern}$",
            "maccatalyst" => $"^{architecturePattern}-apple-ios{versionPattern}-macabi$",
            "tvos" => $"^{architecturePattern}-apple-tvos{versionPattern}{(simulator ? "-simulator" : "")}$",
            _ => "(?!)",
        };

        if (!Regex.IsMatch(architecture, "^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)
            || !string.Equals(sdk, expectedSdk, StringComparison.Ordinal)
            || !Regex.IsMatch(triple, triplePattern, RegexOptions.CultureInvariant))
            throw InvalidTargetProvenance(target, sdk, architecture, triple);
    }

    private static InvalidDataException InvalidTargetProvenance(
        SurfaceTargetRequest target,
        string? sdk,
        string? architecture,
        string? triple)
        => new(
            $"P1 request target '{target.Key.TargetName}' has placeholder or inconsistent SDK/architecture/triple provenance: " +
            $"'{sdk}', '{architecture}', '{triple}'.");

    public static void ValidateRequest(SurfaceAccountingRequest request)
    {
        if (!string.Equals(request.Schema, SurfaceAccountingSchema.Request, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported surface-accounting request schema '{request.Schema}'.");
        if (request.ExpectedTargetCount != request.Targets.Count)
            throw new InvalidDataException(
                $"Expected {request.ExpectedTargetCount} target(s), request contains {request.Targets.Count}.");
        if (request.Targets.Count == 0)
            throw new InvalidDataException("Surface-accounting request has no targets.");

        var duplicateKeys = request.Targets.GroupBy(t => SurfaceCanonicalJson.Hash(t.Key), StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.First().Key.TargetName).ToList();
        if (duplicateKeys.Count > 0)
            throw new InvalidDataException($"Duplicate TargetKey rows: {string.Join(", ", duplicateKeys)}.");
        var duplicateNames = request.Targets.GroupBy(t => t.Key.TargetName, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicateNames.Count > 0)
            throw new InvalidDataException($"Duplicate target names: {string.Join(", ", duplicateNames)}.");

        var targetLanes = request.Targets.ToDictionary(
            target => SurfaceCanonicalJson.Hash(target.Key),
            target => target.SurfaceLane,
            StringComparer.Ordinal);
        var duplicateCrosswalks = request.OriginCrosswalks
            .GroupBy(crosswalk => $"{crosswalk.Side}\u001f{crosswalk.QualifiedPublicKey}", StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicateCrosswalks.Count > 0)
            throw new InvalidDataException("Duplicate origin crosswalk rows identify the same captured public member.");
        foreach (var crosswalk in request.OriginCrosswalks)
        {
            if (!string.Equals(crosswalk.Schema, SurfaceAccountingSchema.Artifact, StringComparison.Ordinal))
                throw new InvalidDataException($"Origin crosswalk has unsupported schema '{crosswalk.Schema}'.");
            if (crosswalk.Side is not ("old" or "tip"))
                throw new InvalidDataException($"Origin crosswalk side '{crosswalk.Side}' is not old or tip.");
            var targetDigest = SurfaceCanonicalJson.Hash(crosswalk.TargetKey);
            if (!targetLanes.TryGetValue(targetDigest, out var lane)
                || !string.Equals(lane, crosswalk.SurfaceLane, StringComparison.Ordinal))
                throw new InvalidDataException("Origin crosswalk target or surface lane is outside the request roster.");
            if (string.IsNullOrWhiteSpace(crosswalk.OriginId)
                || !IsLowerHex(crosswalk.Evidence.FileSha256, 64)
                || string.IsNullOrWhiteSpace(crosswalk.Evidence.RelativePath)
                || Path.IsPathRooted(crosswalk.Evidence.RelativePath)
                || crosswalk.Evidence.RelativePath.Split('/', '\\').Contains("..", StringComparer.Ordinal))
                throw new InvalidDataException("Origin crosswalk has malformed identity or evidence provenance.");
        }

        ValidateHashedFile(request.ManifestPath, request.ManifestSha256, "manifest");
        ValidateHashedFile(request.InputLockPath, request.InputLockSha256, "input lock");
        ValidateDistinctCaptureDirectories(request);
        ValidateCapture(request, request.OldCapture, "old");
        ValidateCapture(request, request.TipCapture, "tip");
        if (request.OldCapture.CaptureId == request.TipCapture.CaptureId)
            throw new InvalidDataException("Old and tip capture IDs must differ.");
        if (!string.Equals(request.InputLockSha256, request.OldCapture.InputLockSha256, StringComparison.Ordinal)
            || !string.Equals(request.InputLockSha256, request.TipCapture.InputLockSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Request and capture input-lock hashes differ.");
    }

    private static void ValidateCapture(
        SurfaceAccountingRequest request,
        SurfaceCaptureRequest capture,
        string expectedSide)
    {
        if (!string.Equals(capture.Schema, SurfaceAccountingSchema.Artifact, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Capture '{capture.CaptureId}' has unsupported schema '{capture.Schema}'.");
        if (!string.Equals(capture.Side, expectedSide, StringComparison.Ordinal))
            throw new InvalidDataException($"Capture '{capture.CaptureId}' side is '{capture.Side}', expected '{expectedSide}'.");
        if (capture.Complete && capture.DirtyPatchSha256 is not null)
            throw new InvalidDataException($"Complete capture '{capture.CaptureId}' records a dirty source patch.");
        if (capture.FinishedAt < capture.StartedAt)
            throw new InvalidDataException($"Capture '{capture.CaptureId}' finishes before it starts.");
        if (capture.Complete && (capture.Commands.Count == 0 || capture.Commands.Any(c => c.ExitCode != 0)))
            throw new InvalidDataException($"Complete capture '{capture.CaptureId}' lacks successful command receipts.");
        if (capture.Complete && capture.IncompletenessReasons.Count > 0)
            throw new InvalidDataException($"Complete capture '{capture.CaptureId}' lists incompleteness reasons.");
        if (!capture.Complete && capture.IncompletenessReasons.Count == 0)
            throw new InvalidDataException($"Incomplete capture '{capture.CaptureId}' does not explain why.");
        if (!IsLowerHex(capture.SourceSha, 40)
            || !IsLowerHex(capture.InputLockSha256, 64)
            || !IsLowerHex(capture.ToolchainSha256, 64)
            || capture.DirtyPatchSha256 is not null && !IsLowerHex(capture.DirtyPatchSha256, 64))
            throw new InvalidDataException($"Capture '{capture.CaptureId}' contains a malformed source or evidence hash.");

        if (!Directory.Exists(capture.EvidenceDirectory))
            throw new DirectoryNotFoundException(
                $"Capture '{capture.CaptureId}' evidence directory does not exist: {capture.EvidenceDirectory}.");
        foreach (var command in capture.Commands)
        {
            var logPath = ResolveEvidencePath(capture, command.LogRelativePath, "command log");
            if (!IsLowerHex(command.LogSha256, 64))
                throw new InvalidDataException(
                    $"Capture '{capture.CaptureId}' command log has a malformed SHA-256.");
            ValidateHashedFile(logPath, command.LogSha256, "command log");
        }

        var stageNames = capture.TargetStages.Select(s => s.TargetName).ToList();
        var duplicates = stageNames.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new InvalidDataException($"Capture '{capture.CaptureId}' has duplicate target-stage receipts: {string.Join(", ", duplicates)}.");
        var expected = request.Targets.Select(t => t.Key.TargetName).ToHashSet(StringComparer.Ordinal);
        var actual = stageNames.ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(actual))
            throw new InvalidDataException(
                $"Capture '{capture.CaptureId}' target-stage roster differs from the request: " +
                $"missing [{string.Join(", ", expected.Except(actual).Order())}], " +
                $"extra [{string.Join(", ", actual.Except(expected).Order())}].");
        if (capture.Complete && capture.TargetStages.Any(s =>
                !string.Equals(s.Generation, "success", StringComparison.Ordinal)
                || !string.Equals(s.CSharpCompile, "success", StringComparison.Ordinal)
                || s.SwiftCompile is not ("success" or "not-applicable")))
            throw new InvalidDataException($"Complete capture '{capture.CaptureId}' contains an incomplete target stage.");

        foreach (var stage in capture.TargetStages)
        {
            if (!string.Equals(stage.CaptureId, capture.CaptureId, StringComparison.Ordinal)
                || !string.Equals(stage.SourceSha, capture.SourceSha, StringComparison.Ordinal)
                || !string.Equals(stage.ToolchainSha256, capture.ToolchainSha256, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Capture '{capture.CaptureId}' target '{stage.TargetName}' output receipt is bound to different capture provenance.");
            if (!IsLowerHex(stage.OutputTreeSha256, 64))
                throw new InvalidDataException(
                    $"Capture '{capture.CaptureId}' target '{stage.TargetName}' has a malformed output-tree SHA-256.");
            var target = request.Targets.Single(t => t.Key.TargetName == stage.TargetName);
            var directory = expectedSide == "old" ? target.OldDirectory : target.TipDirectory;
            if (!Directory.Exists(directory))
            {
                if (capture.Complete)
                    throw new DirectoryNotFoundException(
                        $"Complete capture '{capture.CaptureId}' is missing target directory {directory}.");
                continue;
            }
            var actualTreeHash = HashDirectoryTree(directory);
            if (!string.Equals(actualTreeHash, stage.OutputTreeSha256, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Capture '{capture.CaptureId}' target '{stage.TargetName}' output-tree SHA-256 differs from its receipt.");
        }
    }

    private static void ValidateDistinctCaptureDirectories(SurfaceAccountingRequest request)
    {
        var pathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var paths = new Dictionary<string, string>(pathComparer);
        foreach (var target in request.Targets)
        {
            Add("old", target.Key.TargetName, target.OldDirectory);
            Add("tip", target.Key.TargetName, target.TipDirectory);
        }

        void Add(string side, string targetName, string path)
        {
            var canonical = CanonicalDirectoryPath(path);
            if (paths.TryGetValue(canonical, out var prior))
                throw new InvalidDataException(
                    $"Surface capture directories must be distinct; {side}/{targetName} aliases {prior}: {canonical}.");
            paths.Add(canonical, $"{side}/{targetName}");
        }
    }

    private static string CanonicalDirectoryPath(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath))
            return fullPath;

        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException($"Directory path has no filesystem root: {path}.");
        var resolved = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, segment);
            var linkTarget = new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true);
            if (linkTarget is not null)
                resolved = CanonicalDirectoryPath(linkTarget.FullName);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
    }

    private static string ResolveEvidencePath(
        SurfaceCaptureRequest capture,
        string relativePath,
        string label)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException(
                $"Capture '{capture.CaptureId}' {label} path must be relative to its evidence directory.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(capture.EvidenceDirectory));
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Capture '{capture.CaptureId}' {label} path escapes its evidence directory.");
        return resolved;
    }

    internal static string HashDirectoryTree(string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var entries = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                path = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                sha256 = SurfaceCanonicalJson.Sha256File(path),
                bytes = new FileInfo(path).Length,
            })
            .OrderBy(entry => entry.path, StringComparer.Ordinal)
            .ToList();
        return SurfaceCanonicalJson.Hash(entries);
    }

    private static SurfaceCaptureArtifacts AnalyzeCapture(
        SurfaceAccountingRequest request,
        SurfaceCaptureRequest capture,
        bool oldSide)
    {
        var files = new List<SurfaceFileRecord>();
        var members = new List<SurfaceMemberObservation>();
        var withdrawals = new List<SurfaceWithdrawalObservation>();
        var coverage = new List<SurfaceCoverageRecord>();

        foreach (var target in request.Targets.OrderBy(t => t.Key.TargetName, StringComparer.Ordinal))
        {
            var directory = oldSide ? target.OldDirectory : target.TipDirectory;
            var stages = capture.TargetStages.Single(s => s.TargetName == target.Key.TargetName);
            if (!Directory.Exists(directory))
            {
                if (capture.Complete)
                    throw new DirectoryNotFoundException(
                        $"Complete capture '{capture.CaptureId}' is missing target directory {directory}.");
                coverage.Add(Coverage(
                    capture,
                    target,
                    stages,
                    ["missing target directory"],
                    0, 0, 0, 0, 0, 0, 0,
                    new Dictionary<string, int?>()));
                continue;
            }

            var targetFiles = InventoryFiles(capture.CaptureId, target, directory);
            files.AddRange(targetFiles);
            var sourceFiles = SelectCompileSources(directory);
            var sidecars = SurfaceSidecarReader.Read(directory);
            var errors = sidecars.Errors.ToList();
            if (sourceFiles.Count == 0) errors.Add("no generated C# compile inputs");
            var scan = SurfaceSyntaxScanner.Scan(
                capture.CaptureId,
                target.Key,
                target.SurfaceLane,
                sidecars.Module.Length == 0 ? target.Key.FrameworkModule : sidecars.Module,
                sourceFiles,
                request.PreprocessorSymbols.Concat(target.PreprocessorSymbols)
                    .Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList(),
                directory);
            errors.AddRange(scan.Diagnostics.Select(d => "C# parse: " + d));
            scan = scan with
            {
                Members = ApplyOriginCrosswalks(
                    request,
                    capture.Side,
                    target,
                    scan.Members,
                    targetFiles,
                    errors),
            };

            var reconciled = ReconcileManifest(scan, sidecars, out var matched, out var phantom, out var ambiguous);
            if (sidecars.ApiManifestFileCount == 0)
                errors.Add("missing API manifest without not-produced-empty producer proof");
            if (phantom.Count > 0)
                errors.Add($"API manifest phantom candidate(s): {string.Join(", ", phantom.Order())}");
            if (ambiguous.Count > 0)
                errors.Add($"ambiguous API manifest match(es): {string.Join(", ", ambiguous.Order())}");
            members.AddRange(reconciled);
            var targetWithdrawals = SurfaceSidecarReader.ReconcileWithdrawals(capture.CaptureId, target.Key, sidecars);
            withdrawals.AddRange(targetWithdrawals);
            var unresolvedWithdrawals = targetWithdrawals.Count(w => w.CanonicalRecoveryId is null);
            if (unresolvedWithdrawals > 0) errors.Add($"{unresolvedWithdrawals} unresolved withdrawal occurrence(s)");

            coverage.Add(Coverage(
                capture,
                target,
                stages,
                errors,
                reconciled.Count,
                matched,
                reconciled.Count(m => m.ApiManifest.Count == 0),
                phantom.Count,
                ambiguous.Count,
                reconciled.Count(m => m.OriginId is not null),
                reconciled.Count(m => m.NativeBindings.Count > 0),
                sidecars.Counters));
        }

        return new SurfaceCaptureArtifacts(
            capture,
            files.OrderBy(f => f.TargetKey.TargetName, StringComparer.Ordinal).ThenBy(f => f.RelativePath, StringComparer.Ordinal).ToList(),
            members.OrderBy(m => m.TargetKey.TargetName, StringComparer.Ordinal).ThenBy(m => m.PublicKey.Display, StringComparer.Ordinal).ToList(),
            withdrawals.OrderBy(w => w.TargetKey.TargetName, StringComparer.Ordinal).ThenBy(w => w.CanonicalRecoveryId ?? w.ObservationId, StringComparer.Ordinal).ToList(),
            coverage.OrderBy(c => c.TargetKey.TargetName, StringComparer.Ordinal).ToList());
    }

    private static IReadOnlyList<SurfaceMemberObservation> ApplyOriginCrosswalks(
        SurfaceAccountingRequest request,
        string side,
        SurfaceTargetRequest target,
        IReadOnlyList<SurfaceMemberObservation> members,
        IReadOnlyList<SurfaceFileRecord> files,
        List<string> errors)
    {
        var crosswalks = request.OriginCrosswalks.Where(crosswalk =>
                string.Equals(crosswalk.Side, side, StringComparison.Ordinal)
                && string.Equals(SurfaceCanonicalJson.Hash(crosswalk.TargetKey),
                    SurfaceCanonicalJson.Hash(target.Key), StringComparison.Ordinal)
                && string.Equals(crosswalk.SurfaceLane, target.SurfaceLane, StringComparison.Ordinal))
            .ToDictionary(crosswalk => crosswalk.PublicKey.Digest, StringComparer.Ordinal);
        if (crosswalks.Count == 0) return members;

        var memberKeys = members.Select(member => member.PublicKey.Digest).ToHashSet(StringComparer.Ordinal);
        foreach (var unmatched in crosswalks.Where(pair => !memberKeys.Contains(pair.Key)).Select(pair => pair.Value))
            errors.Add($"origin crosswalk does not match captured public key '{unmatched.PublicKey.Display}'");

        return members.Select(member =>
        {
            if (!crosswalks.TryGetValue(member.PublicKey.Digest, out var crosswalk)) return member;
            var evidenceMatches = files.Any(file =>
                string.Equals(file.RelativePath, crosswalk.Evidence.RelativePath, StringComparison.Ordinal)
                && string.Equals(file.Sha256, crosswalk.Evidence.FileSha256, StringComparison.Ordinal));
            if (!evidenceMatches)
            {
                errors.Add($"origin crosswalk evidence does not match captured file for '{member.PublicKey.Display}'");
                return member;
            }
            return member with
            {
                OriginId = crosswalk.OriginId,
                OriginShape = crosswalk.OriginShape,
                JoinStatus = member.JoinStatus + ";origin-explicit-crosswalk",
            };
        }).ToList();
    }

    private static IReadOnlyList<SurfaceMemberObservation> ReconcileManifest(
        SurfaceSyntaxScanResult scan,
        SurfaceSidecarResult sidecars,
        out int matched,
        out IReadOnlyList<string> phantom,
        out IReadOnlyList<string> ambiguous)
    {
        matched = 0;
        var phantomRows = new List<string>();
        var ambiguousRows = new List<string>();
        var membersBySignature = scan.Members
            .GroupBy(
                m => SurfaceSyntaxScanner.NormalizeManifestSignature(
                    SurfaceSyntaxScanner.BuildManifestSignature(m.PublicKey)),
                StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var bindingsByObservation = new Dictionary<string, List<SurfaceManifestBinding>>(StringComparer.Ordinal);

        foreach (var row in sidecars.ManifestRows)
        {
            var normalizedSignature = SurfaceSyntaxScanner.NormalizeManifestSignature(row.Signature);
            if (!membersBySignature.TryGetValue(normalizedSignature, out var candidates) || candidates.Count == 0)
            {
                phantomRows.Add(row.Signature);
                continue;
            }
            if (candidates.Count != 1)
            {
                ambiguousRows.Add(row.Signature);
                continue;
            }
            var member = candidates[0];
            var symbols = row.Symbol.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var knownImports = scan.Imports.Select(i => i.EntryPoint).ToHashSet(StringComparer.Ordinal);
            var actualImports = member.NativeBindings.Select(i => i.EntryPoint).ToHashSet(StringComparer.Ordinal);
            var status = symbols.All(actualImports.Contains) ? "matched-source-and-call-edge"
                : symbols.All(knownImports.Contains) ? "matched-source-symbol-present-call-unresolved"
                : "matched-source-symbol-unresolved";
            if (!bindingsByObservation.TryGetValue(member.ObservationId, out var list))
                bindingsByObservation[member.ObservationId] = list = [];
            list.Add(new SurfaceManifestBinding(row.Signature, row.Symbol, status, row.Evidence));
            matched++;
        }

        phantom = phantomRows;
        ambiguous = ambiguousRows;
        return scan.Members.Select(member =>
        {
            bindingsByObservation.TryGetValue(member.ObservationId, out var manifests);
            manifests ??= [];
            return member with
            {
                ApiManifest = manifests.OrderBy(m => m.Signature, StringComparer.Ordinal).ToList(),
                JoinStatus = manifests.Count == 0
                    ? member.JoinStatus + ";manifest-unrecorded"
                    : member.JoinStatus + ";" + string.Join(',', manifests.Select(m => m.MatchStatus).Distinct().Order()),
            };
        }).ToList();
    }

    private static IReadOnlyList<SurfaceFileRecord> InventoryFiles(
        string captureId,
        SurfaceTargetRequest target,
        string directory)
    {
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !HasExcludedDirectory(directory, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new FileInfo(path))
            .Select(file => new SurfaceFileRecord(
                SurfaceAccountingSchema.Artifact,
                captureId,
                target.Key,
                target.SurfaceLane,
                Path.GetRelativePath(directory, file.FullName).Replace('\\', '/'),
                SurfaceCanonicalJson.Sha256File(file.FullName),
                file.Length,
                FileRole(file.Name),
                ProducingStage(file.Name)))
            .ToList();
    }

    private static IReadOnlyList<string> SelectCompileSources(string directory)
    {
        var allSources = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !HasExcludedDirectory(directory, path))
            .Select(Path.GetFullPath)
            .ToList();
        var projects = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).ToList();
        if (projects.Count == 0)
            return allSources.Where(path => Path.GetDirectoryName(path) == Path.GetFullPath(directory))
                .OrderBy(path => path, StringComparer.Ordinal).ToList();

        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var projectPath in projects)
        {
            var project = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
            var defaultItems = !project.Descendants().Any(e => e.Name.LocalName == "EnableDefaultCompileItems"
                && string.Equals(e.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase));
            if (defaultItems) selected.UnionWith(allSources);
            foreach (var include in project.Descendants().Where(e => e.Name.LocalName == "Compile")
                         .Select(e => e.Attribute("Include")?.Value).OfType<string>())
                selected.UnionWith(ExpandCompilePattern(directory, include, allSources));
            foreach (var remove in project.Descendants().Where(e => e.Name.LocalName == "Compile")
                         .Select(e => e.Attribute("Remove")?.Value).OfType<string>())
                selected.ExceptWith(ExpandCompilePattern(directory, remove, allSources));
        }
        return selected.OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> ExpandCompilePattern(
        string directory,
        string include,
        IReadOnlyList<string> allSources)
    {
        foreach (var rawPattern in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pattern = rawPattern.Replace('\\', '/');
            if (pattern.Contains("$(", StringComparison.Ordinal)) continue;
            if (!pattern.Contains('*') && !pattern.Contains('?'))
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, pattern));
                if (IsUnder(directory, candidate) && File.Exists(candidate)) yield return candidate;
                continue;
            }

            var regex = "^" + Regex.Escape(pattern)
                .Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
                .Replace("\\*\\*", ".*", StringComparison.Ordinal)
                .Replace("\\*", "[^/]*", StringComparison.Ordinal)
                .Replace("\\?", "[^/]", StringComparison.Ordinal) + "$";
            foreach (var source in allSources)
            {
                var relative = Path.GetRelativePath(directory, source).Replace('\\', '/');
                if (Regex.IsMatch(relative, regex, RegexOptions.CultureInvariant)) yield return source;
            }
        }
    }

    private static bool IsUnder(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.Ordinal);
    }

    private static bool HasExcludedDirectory(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.StartsWith("bin/", StringComparison.Ordinal)
            || relative.StartsWith("obj/", StringComparison.Ordinal)
            || relative.Contains("/.deps/", StringComparison.Ordinal)
            || relative.StartsWith(".deps/", StringComparison.Ordinal);
    }

    private static string FileRole(string name)
        => name switch
        {
            "binding-report.json" or "binding-emission-report.json" or "binding-artifact-manifest.json" => "report",
            _ when name.EndsWith(".api-manifest.json", StringComparison.Ordinal) => "report",
            _ when name.EndsWith(".cs", StringComparison.Ordinal) => "source",
            _ when name.EndsWith(".swift", StringComparison.Ordinal) => "source",
            _ when name.EndsWith(".abi.json", StringComparison.Ordinal) => "input",
            _ when name.EndsWith(".dylib", StringComparison.Ordinal)
                || name.EndsWith(".a", StringComparison.Ordinal)
                || name.EndsWith(".o", StringComparison.Ordinal) => "native",
            _ => "artifact",
        };

    private static string ProducingStage(string name)
        => name switch
        {
            _ when name.EndsWith(".cs", StringComparison.Ordinal)
                || name.EndsWith(".swift", StringComparison.Ordinal)
                || name.EndsWith(".json", StringComparison.Ordinal) => "generation",
            _ when name.EndsWith(".o", StringComparison.Ordinal)
                || name.EndsWith(".a", StringComparison.Ordinal)
                || name.EndsWith(".dylib", StringComparison.Ordinal) => "swift-compile",
            _ => "capture",
        };

    private static SurfaceCoverageRecord Coverage(
        SurfaceCaptureRequest capture,
        SurfaceTargetRequest target,
        SurfaceTargetStageReceipt stages,
        IReadOnlyList<string> errors,
        int syntaxMembers,
        int manifestMatched,
        int manifestUnrecorded,
        int manifestPhantom,
        int manifestAmbiguous,
        int originJoined,
        int nativeJoined,
        IReadOnlyDictionary<string, int?> reportCounters)
        => new()
        {
            Schema = SurfaceAccountingSchema.Artifact,
            CaptureId = capture.CaptureId,
            TargetKey = target.Key,
            SurfaceLane = target.SurfaceLane,
            StageStates = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["generation"] = stages.Generation,
                ["csharp_compile"] = stages.CSharpCompile,
                ["swift_compile"] = stages.SwiftCompile,
                ["inventory"] = errors.Count == 0 ? "complete" : "partial",
            },
            SyntaxMembers = syntaxMembers,
            ManifestMatched = manifestMatched,
            ManifestUnrecorded = manifestUnrecorded,
            ManifestPhantom = manifestPhantom,
            ManifestAmbiguous = manifestAmbiguous,
            OriginJoined = originJoined,
            NativeBindingJoined = nativeJoined,
            ReportCounters = reportCounters,
            Errors = errors,
        };

    private static void ValidateHashedFile(string path, string expectedHash, string role)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Surface-accounting {role} does not exist.", path);
        var actual = SurfaceCanonicalJson.Sha256File(path);
        if (!string.Equals(actual, expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Surface-accounting {role} hash mismatch for {path}: expected {expectedHash}, got {actual}.");
    }

    private static bool IsLowerHex(string value, int length)
        => value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
