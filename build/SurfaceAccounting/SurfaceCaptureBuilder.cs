// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

// Reflection JSON is fine in the Nuke host; the unit-test project link-compiles this file
// under its AOT analyzers.
#pragma warning disable IL2026, IL3050

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

/// <summary>The checkout state <c>nuke validate</c> read: HEAD, <c>git status --porcelain</c> and <c>git diff HEAD</c>.</summary>
public sealed record SurfaceSourceState(string? Commit, string Porcelain, string Diff)
{
    public bool Clean => Commit is not null && string.IsNullOrWhiteSpace(Porcelain);
}

/// <summary>One target's result as validate recorded it, in validate's own status vocabulary.</summary>
public sealed record SurfaceValidateTargetResult(
    string TargetName,
    SurfaceCaptureTargetProvenance Provenance,
    string Generation,
    int? GeneratorExitCode,
    string Compile,
    string? DepCompile,
    string SwiftCompile,
    int Errors,
    int Lines);

/// <summary>A library input directory (an xcframework under <c>.libraries</c>).</summary>
public sealed record SurfaceLibraryInput(string Library, string RelativePath, string FullPath);

public sealed record SurfaceCaptureBuildInput
{
    public required string OutputBase { get; init; }
    public required string ManifestSha256 { get; init; }
    public required SurfaceToolchainIdentity Toolchain { get; init; }
    public required SurfaceSourceState SourceAtStart { get; init; }
    public required SurfaceSourceState SourceAtEnd { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }
    public required IReadOnlyList<SurfaceValidateTargetResult> Targets { get; init; }
    public required IReadOnlyList<SurfaceLibraryInput> Inputs { get; init; }
    /// <summary>Why validate itself failed (compile failures, regressions, withdrawal qualification). Empty when it passed.</summary>
    public IReadOnlyList<string> RunFailures { get; init; } = [];
}

/// <summary>
/// Writes the surface capture of a full validate run into its output base: <c>input-lock.json</c>,
/// one hashed command record per target and for the run, and <c>surface-capture.json</c>. The
/// capture is complete only when the checkout was clean and unchanged for the whole run, validate
/// passed, and every target generated, compiled its C# and compiled (or had no) Swift wrapper.
/// </summary>
public static class SurfaceCaptureBuilder
{
    public const string CaptureFileName = "surface-capture.json";
    public const string InputLockFileName = "input-lock.json";
    public const string EvidenceDirectoryName = "surface-evidence";
    public const string RunRecordRelativePath = EvidenceDirectoryName + "/validate-run.json";
    public const string RunCommand = "nuke validate";
    private const string UnknownCommit = "0000000000000000000000000000000000000000";

    public static SurfaceValidateCapture Write(SurfaceCaptureBuildInput input)
    {
        var outputBase = Path.GetFullPath(input.OutputBase);
        var reasons = new List<string>();

        // Source identity: one commit, clean, for the whole run.
        var start = input.SourceAtStart;
        var end = input.SourceAtEnd;
        if (start.Commit is null || end.Commit is null || !IsLowerHex(start.Commit, 40) || !IsLowerHex(end.Commit, 40))
            reasons.Add("source commit unavailable");
        else if (!string.Equals(start.Commit, end.Commit, StringComparison.Ordinal))
            reasons.Add($"HEAD moved during validate ({start.Commit} -> {end.Commit})");
        string? dirtyPatch = null;
        if (!start.Clean || !end.Clean)
        {
            reasons.Add("source checkout is dirty (git status --porcelain is not empty)");
            dirtyPatch = SurfaceCanonicalJson.Sha256(Encoding.UTF8.GetBytes(
                $"start {start.Commit}\n{start.Porcelain}\n{start.Diff}\nend {end.Commit}\n{end.Porcelain}\n{end.Diff}"));
        }
        var sourceSha = start.Commit is { } commit && IsLowerHex(commit, 40) ? commit : UnknownCommit;
        foreach (var failure in input.RunFailures)
            reasons.Add("validate failed: " + failure);

        // Input lock: input identities only, so equal inputs give byte-identical locks.
        var inputLock = new SurfaceInputLock
        {
            Libraries = input.Inputs
                .GroupBy(i => i.Library, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new SurfaceInputLockLibrary(g.Key, g
                    .GroupBy(i => i.RelativePath, StringComparer.Ordinal)
                    .Select(i => i.First())
                    .OrderBy(i => i.RelativePath, StringComparer.Ordinal)
                    .Select(i =>
                    {
                        if (Directory.Exists(i.FullPath))
                            return new SurfaceInputLockEntry(i.RelativePath, SurfaceAccountingEngine.HashDirectoryTree(i.FullPath));
                        reasons.Add($"library input missing: {i.RelativePath}");
                        return new SurfaceInputLockEntry(i.RelativePath, "missing");
                    })
                    .ToList()))
                .ToList(),
        };
        var inputLockPath = Path.Combine(outputBase, InputLockFileName);
        File.WriteAllText(inputLockPath, SurfaceCanonicalJson.Serialize(inputLock) + "\n");
        var inputLockSha = SurfaceCanonicalJson.Sha256File(inputLockPath);
        var toolchainSha = SurfaceCanonicalJson.Hash(input.Toolchain);
        var captureId = $"validate-{sourceSha[..12]}-{input.StartedAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}";

        var duplicateTargets = input.Targets.GroupBy(t => t.TargetName, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicateTargets.Count > 0)
            throw new InvalidDataException($"Validate results name a target more than once: {string.Join(", ", duplicateTargets)}.");
        if (input.Targets.Count == 0)
            throw new InvalidDataException("A surface capture needs at least one validate target.");

        // Per-target command records and stage receipts, from validate's own results.
        var commandDirectory = Path.Combine(outputBase, EvidenceDirectoryName, "commands");
        if (Directory.Exists(commandDirectory)) Directory.Delete(commandDirectory, recursive: true);
        Directory.CreateDirectory(commandDirectory);
        var commands = new List<SurfaceCommandReceipt>();
        var stages = new List<SurfaceTargetStageReceipt>();
        foreach (var target in input.Targets.OrderBy(t => t.TargetName, StringComparer.Ordinal))
        {
            var (generation, csharp, swift) = Stages(target);
            var directory = Path.Combine(outputBase, target.TargetName);
            string? reason = null;
            string treeSha;
            if (Directory.Exists(directory))
                treeSha = SurfaceAccountingEngine.HashDirectoryTree(directory);
            else
            {
                treeSha = SurfaceCanonicalJson.Hash(Array.Empty<object>());
                reason = "output directory missing";
            }
            if (generation != "success" || csharp != "success" || swift is not ("success" or "not-applicable"))
                reason ??= $"generation {generation}, C# compile {csharp}, Swift compile {swift}";
            if (reason is not null)
                reasons.Add($"{target.TargetName}: {reason}");
            stages.Add(new SurfaceTargetStageReceipt(
                target.TargetName, generation, csharp, swift, reason, captureId, sourceSha, toolchainSha, treeSha));

            var relative = $"{EvidenceDirectoryName}/commands/{target.TargetName}.json";
            var record = new
            {
                target = target.TargetName,
                provenance = target.Provenance,
                generation = target.Generation,
                generator_exit_code = target.GeneratorExitCode,
                compile = target.Compile,
                dep_compile = target.DepCompile,
                swift_compile = target.SwiftCompile,
                errors = target.Errors,
                lines = target.Lines,
                output_tree_sha256 = treeSha,
            };
            commands.Add(WriteRecord(outputBase, relative, record,
                $"nuke validate: generate and compile {target.TargetName}",
                target.GeneratorExitCode ?? -1, input.StartedAt, input.FinishedAt));
        }

        commands.Add(WriteRecord(outputBase, RunRecordRelativePath, new
        {
            capture_id = captureId,
            source_at_start = new { start.Commit, start.Porcelain },
            source_at_end = new { end.Commit, end.Porcelain },
            toolchain = input.Toolchain,
            input_lock_sha256 = inputLockSha,
            manifest_sha256 = input.ManifestSha256,
            run_failures = input.RunFailures,
        }, RunCommand, input.RunFailures.Count == 0 ? 0 : 1, input.StartedAt, input.FinishedAt));

        var capture = new SurfaceValidateCapture
        {
            ManifestSha256 = input.ManifestSha256,
            InputLockRelativePath = InputLockFileName,
            Toolchain = input.Toolchain,
            Targets = input.Targets.OrderBy(t => t.TargetName, StringComparer.Ordinal).Select(t => t.Provenance).ToList(),
            Capture = new SurfaceCaptureRequest
            {
                CaptureId = captureId,
                Side = "tip",
                SourceSha = sourceSha,
                InputLockSha256 = inputLockSha,
                ToolchainSha256 = toolchainSha,
                EvidenceDirectory = ".",
                DirtyPatchSha256 = dirtyPatch,
                StartedAt = input.StartedAt,
                FinishedAt = input.FinishedAt,
                Commands = commands,
                TargetStages = stages,
                Complete = reasons.Count == 0,
                IncompletenessReasons = reasons,
            },
        };
        File.WriteAllText(Path.Combine(outputBase, CaptureFileName), SurfaceCanonicalJson.Serialize(capture) + "\n");
        return capture;
    }

    /// <summary>
    /// The library inputs to lock for a run whose generator read <paramref name="generatorInputs"/>:
    /// each one under <paramref name="librariesDirectory"/>, and every xcframework beside it. Validate
    /// also hands the generator the sibling xcframeworks a target reaches through its import graph
    /// (and its wrapper dependencies), which no target declares, so the lock covers every
    /// xcframework in each library directory a target generated from.
    /// </summary>
    public static IReadOnlyList<SurfaceLibraryInput> LibraryInputs(
        IEnumerable<string> generatorInputs, string librariesDirectory, string rootDirectory)
    {
        var libraries = Path.TrimEndingDirectorySeparator(Path.GetFullPath(librariesDirectory)) + Path.DirectorySeparatorChar;
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var input in generatorInputs.Select(Path.GetFullPath))
        {
            if (!input.StartsWith(libraries, StringComparison.Ordinal)) continue;
            paths.Add(Path.TrimEndingDirectorySeparator(input));
            var libraryDirectory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(input))!;
            if (Directory.Exists(libraryDirectory))
                foreach (var sibling in Directory.EnumerateDirectories(libraryDirectory, "*.xcframework"))
                    paths.Add(Path.GetFullPath(sibling));
        }
        return paths.Select(path => new SurfaceLibraryInput(
                Path.GetFileName(Path.GetDirectoryName(path)!),
                Path.GetRelativePath(rootDirectory, path).Replace(Path.DirectorySeparatorChar, '/'),
                path))
            .ToList();
    }

    /// <summary>Reads the capture a validate run wrote into <paramref name="outputBase"/>.</summary>
    public static SurfaceValidateCapture Read(string outputBase)
    {
        var path = Path.Combine(Path.GetFullPath(outputBase), CaptureFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"No surface capture at {path}. Only a full `nuke validate` (unfiltered, without --quick) writes one.", path);
        var capture = SurfaceCanonicalJson.DeserializeStrict<SurfaceValidateCapture>(path);
        if (!string.Equals(capture.Schema, SurfaceAccountingSchema.ValidateCapture, StringComparison.Ordinal))
            throw new InvalidDataException($"{path}: unsupported surface capture schema '{capture.Schema}'.");
        return capture;
    }

    /// <summary>
    /// Re-hashes every target tree the capture names under <paramref name="outputBase"/> and returns
    /// one line per tree that no longer matches its receipt (missing trees included).
    /// </summary>
    public static IReadOnlyList<string> VerifyOutputTrees(SurfaceValidateCapture capture, string outputBase)
    {
        var mismatches = new List<string>();
        foreach (var stage in capture.Capture.TargetStages)
        {
            var directory = Path.Combine(Path.GetFullPath(outputBase), stage.TargetName);
            if (!Directory.Exists(directory))
            {
                mismatches.Add($"{stage.TargetName}: output directory missing");
                continue;
            }
            var actual = SurfaceAccountingEngine.HashDirectoryTree(directory);
            if (!string.Equals(actual, stage.OutputTreeSha256, StringComparison.Ordinal))
                mismatches.Add($"{stage.TargetName}: output tree {actual} differs from validate's {stage.OutputTreeSha256}");
        }
        return mismatches;
    }

    /// <summary>
    /// Checks a capture's labels against the hashed records validate wrote beside it, and returns
    /// one line per disagreement. The capture's source commit, ID, toolchain and input lock must be
    /// the ones its <c>validate-run.json</c> records (HEAD at start and end), and each target stage
    /// must be the verdict and output-tree hash its own command record holds. A capture whose labels
    /// were edited (an old side relabelled as the baseline's source) fails here even when every
    /// record still matches its hash.
    /// </summary>
    public static IReadOnlyList<string> VerifyRecords(SurfaceCaptureRequest capture)
    {
        var problems = new List<string>();
        var runs = capture.Commands.Where(c => string.Equals(c.LogRelativePath, RunRecordRelativePath, StringComparison.Ordinal)).ToList();
        if (runs.Count != 1 || !string.Equals(runs[0].Command, RunCommand, StringComparison.Ordinal))
        {
            problems.Add($"capture '{capture.CaptureId}' does not hold exactly one `{RunCommand}` record at {RunRecordRelativePath}");
            return problems;
        }
        using (var run = ReadRecord(capture, runs[0], problems))
        {
            if (run is not null)
            {
                var root = run.RootElement;
                var startCommit = Text(root, "source_at_start", "commit");
                var endCommit = Text(root, "source_at_end", "commit");
                if (!string.Equals(startCommit, capture.SourceSha, StringComparison.Ordinal)
                    || !string.Equals(endCommit, capture.SourceSha, StringComparison.Ordinal))
                    problems.Add($"capture '{capture.CaptureId}' names source {capture.SourceSha}, its validate record ran at " +
                        $"{startCommit ?? "<none>"} (start) and {endCommit ?? "<none>"} (end)");
                if (!string.Equals(Text(root, "capture_id"), capture.CaptureId, StringComparison.Ordinal))
                    problems.Add($"capture '{capture.CaptureId}' is not the capture its validate record names ({Text(root, "capture_id") ?? "<none>"})");
                if (!string.Equals(Text(root, "input_lock_sha256"), capture.InputLockSha256, StringComparison.Ordinal))
                    problems.Add($"capture '{capture.CaptureId}' input lock differs from its validate record");
                string? toolchainSha = null;
                if (root.TryGetProperty("toolchain", out var toolchain) && toolchain.ValueKind == JsonValueKind.Object)
                {
                    try
                    {
                        var identity = toolchain.Deserialize<SurfaceToolchainIdentity>(SurfaceCanonicalJson.OutputOptions);
                        toolchainSha = identity is null ? null : SurfaceCanonicalJson.Hash(identity);
                    }
                    catch (JsonException) { }
                }
                if (!string.Equals(toolchainSha, capture.ToolchainSha256, StringComparison.Ordinal))
                    problems.Add($"capture '{capture.CaptureId}' toolchain differs from its validate record");
            }
        }

        foreach (var stage in capture.TargetStages)
        {
            var relative = $"{EvidenceDirectoryName}/commands/{stage.TargetName}.json";
            var records = capture.Commands.Where(c => string.Equals(c.LogRelativePath, relative, StringComparison.Ordinal)).ToList();
            if (records.Count != 1)
            {
                problems.Add($"capture '{capture.CaptureId}' target '{stage.TargetName}' has no single command record at {relative}");
                continue;
            }
            using var record = ReadRecord(capture, records[0], problems);
            if (record is null) continue;
            SurfaceValidateTargetResult? result = null;
            try
            {
                var root = record.RootElement;
                result = new SurfaceValidateTargetResult(
                    Text(root, "target") ?? "",
                    root.GetProperty("provenance").Deserialize<SurfaceCaptureTargetProvenance>(SurfaceCanonicalJson.OutputOptions)!,
                    Text(root, "generation") ?? "",
                    root.GetProperty("generator_exit_code").ValueKind == JsonValueKind.Number
                        ? root.GetProperty("generator_exit_code").GetInt32() : null,
                    Text(root, "compile") ?? "",
                    Text(root, "dep_compile"),
                    Text(root, "swift_compile") ?? "",
                    root.GetProperty("errors").GetInt32(),
                    root.GetProperty("lines").GetInt32());
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException)
            {
                problems.Add($"capture '{capture.CaptureId}' target '{stage.TargetName}' command record is malformed: {ex.Message}");
            }
            if (result is null) continue;
            if (!string.Equals(result.TargetName, stage.TargetName, StringComparison.Ordinal)
                || !string.Equals(Text(record.RootElement, "output_tree_sha256"), stage.OutputTreeSha256, StringComparison.Ordinal)
                || Stages(result) != (stage.Generation, stage.CSharpCompile, stage.SwiftCompile))
                problems.Add($"capture '{capture.CaptureId}' target '{stage.TargetName}' stage receipt differs from its command record");
        }
        return problems;
    }

    private static JsonDocument? ReadRecord(SurfaceCaptureRequest capture, SurfaceCommandReceipt command, List<string> problems)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(capture.EvidenceDirectory));
        var path = Path.GetFullPath(Path.Combine(root, command.LogRelativePath));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
        {
            problems.Add($"capture '{capture.CaptureId}' record {command.LogRelativePath} is missing");
            return null;
        }
        if (!string.Equals(SurfaceCanonicalJson.Sha256File(path), command.LogSha256, StringComparison.Ordinal))
        {
            problems.Add($"capture '{capture.CaptureId}' record {command.LogRelativePath} differs from its hash");
            return null;
        }
        try
        {
            return SurfaceCanonicalJson.ParseStrictFile(path);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            problems.Add($"capture '{capture.CaptureId}' record {command.LogRelativePath} is not valid JSON: {ex.Message}");
            return null;
        }
    }

    private static string? Text(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
                return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    /// <summary>Maps validate's status vocabulary onto the stage receipt's.</summary>
    public static (string Generation, string CSharpCompile, string SwiftCompile) Stages(SurfaceValidateTargetResult target)
    {
        var generation = target.Generation switch
        {
            "ok" when target.GeneratorExitCode == 0 => "success",
            "ok" => "failed",
            "fail" => "failed",
            _ => "not-run",
        };
        // known_errors compiles with errors; the surface it accounts for did not fully compile.
        var csharp = target.Compile switch
        {
            "ok" => "success",
            "known_errors" => "known-errors",
            "fail" or "regressed" or "infra_fail" => "failed",
            _ => "not-run",
        };
        var swift = target.SwiftCompile switch
        {
            "ok" => "success",
            "no_wrapper" => "not-applicable",
            "fail" => "failed",
            _ => "not-run",
        };
        return (generation, csharp, swift);
    }

    private static SurfaceCommandReceipt WriteRecord<T>(
        string outputBase,
        string relativePath,
        T record,
        string command,
        int exitCode,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt)
    {
        var path = Path.Combine(outputBase, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, SurfaceCanonicalJson.Serialize(record) + "\n");
        return new SurfaceCommandReceipt(command, exitCode, startedAt, finishedAt, relativePath,
            SurfaceCanonicalJson.Sha256File(path));
    }

    internal static bool IsLowerHex(string value, int length)
        => value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
