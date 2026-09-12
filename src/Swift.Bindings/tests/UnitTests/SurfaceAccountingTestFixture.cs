// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BindingsGeneration.Tests;

internal sealed class SurfaceAccountingTestFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"surface-accounting-tests-{Guid.NewGuid():N}");
    public string OldDirectory => Path.Combine(Root, "old", "TestTarget");
    public string TipDirectory => Path.Combine(Root, "tip", "TestTarget");
    public string ManifestPath => Path.Combine(Root, "validation-libraries.json");
    public string InputLockPath => Path.Combine(Root, "input-lock.json");

    public SurfaceAccountingTestFixture()
    {
        Directory.CreateDirectory(OldDirectory);
        Directory.CreateDirectory(TipDirectory);
        File.WriteAllText(ManifestPath, "{\"libraries\":[]}");
        File.WriteAllText(InputLockPath, "{\"fixture\":true}");
    }

    public SurfaceAccountingRequest Request(
        string oldSource,
        string tipSource,
        IReadOnlyList<(string Signature, string Symbol)>? oldManifest = null,
        IReadOnlyList<(string Signature, string Symbol)>? tipManifest = null,
        IReadOnlyList<(string Display, string Root)>? oldWithdrawals = null,
        IReadOnlyList<(string Display, string Root)>? tipWithdrawals = null,
        bool oldComplete = true,
        bool tipComplete = true,
        string? tipToolchainHash = null)
    {
        WriteTarget(OldDirectory, oldSource, oldManifest ?? [], oldWithdrawals ?? []);
        WriteTarget(TipDirectory, tipSource, tipManifest ?? [], tipWithdrawals ?? []);
        var target = new SurfaceTargetRequest
        {
            Key = TargetKey,
            OldDirectory = OldDirectory,
            TipDirectory = TipDirectory,
            SurfaceLane = "swift-managed",
            Mode = "source",
            Tier = 1,
        };
        var inputHash = SurfaceCanonicalJson.Sha256File(InputLockPath);
        return new SurfaceAccountingRequest
        {
            CorpusId = "fixture-corpus",
            ManifestPath = ManifestPath,
            ManifestSha256 = SurfaceCanonicalJson.Sha256File(ManifestPath),
            InputLockPath = InputLockPath,
            InputLockSha256 = inputHash,
            ExpectedTargetCount = 1,
            OldCapture = Capture("old-capture", "old", new string('a', 40), inputHash, new string('c', 64), oldComplete),
            TipCapture = Capture("tip-capture", "tip", new string('b', 40), inputHash, tipToolchainHash ?? new string('c', 64), tipComplete),
            Targets = [target],
        };
    }

    public static SurfaceTargetKey TargetKey { get; } = new("TestLibrary", "TestModule", "ios", "TestTarget");

    public static SurfaceCaptureRequest Capture(
        string id,
        string side,
        string sourceSha,
        string inputHash,
        string toolchainHash,
        bool complete)
    {
        var start = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        return new SurfaceCaptureRequest
        {
            CaptureId = id,
            Side = side,
            SourceSha = sourceSha,
            InputLockSha256 = inputHash,
            ToolchainSha256 = toolchainHash,
            StartedAt = start,
            FinishedAt = start.AddMinutes(1),
            Commands = [new SurfaceCommandReceipt("nuke validate --serial --verbose", complete ? 0 : 1, start, start.AddMinutes(1), "validate.log")],
            TargetStages = [new SurfaceTargetStageReceipt(
                "TestTarget",
                complete ? "success" : "failed",
                complete ? "success" : "not-run",
                complete ? "success" : "not-run")],
            Complete = complete,
            IncompletenessReasons = complete ? [] : ["fixture failure"],
        };
    }

    public static string WriteSource(string root, string fileName, string source)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, fileName);
        File.WriteAllText(path, source);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    private static void WriteTarget(
        string directory,
        string source,
        IReadOnlyList<(string Signature, string Symbol)> manifest,
        IReadOnlyList<(string Display, string Root)> withdrawals)
    {
        foreach (var path in Directory.EnumerateFiles(directory)) File.Delete(path);
        WriteSource(directory, "TestModule.cs", source);
        var skippedItems = withdrawals.Select((w, index) => (object)new Dictionary<string, object?>
        {
            ["Kind"] = "Method",
            ["Name"] = "withdrawn" + index,
            ["ContainingType"] = "TestModule.C",
            ["Reason"] = "EmitterFault",
            ["Details"] = $"Withdrawn by wrapper verify-recover: {w.Display}",
            ["DeclId"] = w.Root[..w.Root.LastIndexOf('!')],
            ["RootCauseId"] = w.Root,
            ["CascadeFrom"] = null,
            ["Confidence"] = "High",
        }).ToList();
        WriteJson(Path.Combine(directory, "binding-report.json"), new Dictionary<string, object?>
        {
            ["ModuleName"] = "TestModule",
            ["TotalTypes"] = 1,
            ["EmittedTypes"] = 1,
            ["SkippedTypes"] = 0,
            ["TotalMembers"] = 1,
            ["EmittedMembers"] = 1,
            ["SkippedMembers"] = withdrawals.Count,
            ["SynthesizedMembers"] = 0,
            ["SkippedItems"] = skippedItems,
        });
        WriteJson(Path.Combine(directory, "binding-emission-report.json"), new Dictionary<string, object?>
        {
            ["module"] = "TestModule",
            ["withdrawnUnits"] = withdrawals.Select(w => w.Display).ToList(),
        });
        WriteJson(Path.Combine(directory, "binding-artifact-manifest.json"), new Dictionary<string, object?>
        {
            ["SchemaVersion"] = 3,
            ["Module"] = "TestModule",
            ["Status"] = "Complete",
        });
        WriteJson(Path.Combine(directory, "TestModule.api-manifest.json"), new Dictionary<string, object?>
        {
            ["schema_version"] = 1,
            ["module"] = "TestModule",
            ["members"] = manifest.Select(m => (object)new Dictionary<string, object?>
            {
                ["signature"] = m.Signature,
                ["symbol"] = m.Symbol,
            }).ToList(),
        });
    }

    private static void WriteJson<T>(string path, T value)
        => File.WriteAllText(path, SurfaceCanonicalJson.Serialize(value));
}
