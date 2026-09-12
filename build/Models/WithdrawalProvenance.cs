// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

/// <summary>Current-invocation fingerprints; no report or cache can supply its own expected values.</summary>
public sealed record WithdrawalProvenance(string SourceAndDirtyHash, string GeneratorHash,
    string ManifestSelectionHash, string Platform, string Architecture, string TargetTriple, string Sdk,
    Dictionary<string, string> InputHashes)
{
    public static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    public static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string HashTree(string path)
    {
        if (File.Exists(path)) return HashFile(path);
        if (!Directory.Exists(path)) throw new InvalidDataException($"Missing provenance input: {path}");
        var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw new InvalidDataException($"Empty provenance input: {path}");
        return HashText(string.Join("\n", files.Select(file => Path.GetRelativePath(path, file) + ":" + HashFile(file))));
    }

    public void Verify(string sourceHash, string generatorHash, string manifestHash, WithdrawalToolchain liveToolchain)
    {
        if (string.IsNullOrWhiteSpace(Platform) || string.IsNullOrWhiteSpace(Architecture) || string.IsNullOrWhiteSpace(TargetTriple) ||
            !TargetTriple.StartsWith(Architecture + "-", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(Sdk) ||
            sourceHash != SourceAndDirtyHash || generatorHash != GeneratorHash || manifestHash != ManifestSelectionHash || InputHashes.Count == 0 ||
            liveToolchain != new WithdrawalToolchain(Platform, Architecture, TargetTriple, Sdk))
            throw new InvalidDataException("Stale or incomplete withdrawal provenance");
        foreach (var (path, hash) in InputHashes)
            if (HashTree(path) != hash) throw new InvalidDataException($"Withdrawal input changed during invocation: {path}");
    }
}

public sealed record WithdrawalToolchain(string Platform, string Architecture, string TargetTriple, string Sdk);

public static class WithdrawalEvidenceArchive
{
    // Only these exact artifacts are recovery evidence. Never discover target applicability by
    // globbing caches, and never archive or remove another target's outputs.
    static readonly string[] Names = ["binding-emission-report.json", "binding-report.json",
        "withdrawal-receipt.json", "withdrawal-provenance.json", "generator-exit-code"];

    public static string? Rotate(string outputDirectory, string targetArchiveDirectory)
    {
        var present = Names.Where(name => File.Exists(Path.Combine(outputDirectory, name))).ToArray();
        if (present.Length == 0) return null;
        var archive = Path.Combine(targetArchiveDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archive);
        // Copy first. A failed copy aborts generation before output cleanup and retains originals.
        foreach (var name in present)
            File.Copy(Path.Combine(outputDirectory, name), Path.Combine(archive, name));
        return archive;
    }
}

public sealed record WithdrawalReceipt(WithdrawalProvenance Provenance, string ReportHash,
    int GeneratorExit, string CSharpVerdict, string SwiftVerdict, string DependencyVerdict)
{
    public void RequirePureObjCSuccess(string artifactManifestPath, string module)
    {
        if (GeneratorExit != 0 || ReportHash != WithdrawalProvenance.HashFile(artifactManifestPath) ||
            CSharpVerdict != "ok" || SwiftVerdict != "no_wrapper" ||
            DependencyVerdict is not ("none" or "ok"))
            throw new InvalidDataException($"Pure-ObjC withdrawal qualification requires a clean ObjC compile receipt: {artifactManifestPath}");
        WithdrawalGate.RequirePureObjCManifest(artifactManifestPath, module);
    }

    public void RequireSuccess(string reportPath, string? target = null, string? module = null)
    {
        var pureObjC = WithdrawalGate.IsPureObjCUmbrella(target, module);
        if (GeneratorExit != 0 || ReportHash != WithdrawalProvenance.HashFile(reportPath) ||
            CSharpVerdict != "ok" || (SwiftVerdict != "ok" && !(pureObjC && SwiftVerdict == "no_wrapper")) ||
            DependencyVerdict is not ("none" or "ok"))
            throw new InvalidDataException($"Withdrawal qualification requires separate clean compile receipts: {reportPath}");
        if (pureObjC && SwiftVerdict == "no_wrapper")
            WithdrawalGate.RequireZero(WithdrawalGate.Read(reportPath, module!, [], WithdrawalGate.ParseIdentity,
                externalVerificationPassed: true));
    }
}
