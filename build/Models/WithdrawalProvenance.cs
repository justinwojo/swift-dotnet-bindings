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

/// <summary>
/// The toolchain a withdrawal policy is exact for. <see cref="Sdk"/> is
/// <see cref="WithdrawalToolchainIdentity.Compose"/>'s identity, never an install path: the same Xcode
/// installed at another path produces the same output and the same key.
/// </summary>
public sealed record WithdrawalToolchain(string Platform, string Architecture, string TargetTriple, string Sdk)
{
    /// <summary>The exact policy key of <paramref name="target"/> generated on this toolchain.</summary>
    public string PolicyProfile(string target) => target + "/" + Platform + "/" + Architecture + "/" + TargetTriple + "/" + Sdk;
}

/// <summary>
/// Names what decides a generation's output: the SDK the interfaces and the wrapper compile read
/// (name, version and build), the Swift compiler that verifies and compiles the wrapper, and the
/// Xcode build that ships both with its linker. Where Xcode is installed changes none of them, so the
/// identity holds no path; two installs of one Xcode build share one identity, and a different SDK
/// build, compiler or Xcode build is a different identity.
/// </summary>
public static class WithdrawalToolchainIdentity
{
    static readonly System.Text.RegularExpressions.Regex SwiftLang =
        new(@"\((swiftlang-[0-9][0-9.]* clang-[0-9][0-9.]*)\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    static readonly System.Text.RegularExpressions.Regex XcodeBuild =
        new(@"^Build version ([0-9]+[A-Za-z][0-9]+[a-z]?)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant |
            System.Text.RegularExpressions.RegexOptions.Multiline);
    static readonly System.Text.RegularExpressions.Regex Token =
        new(@"^[A-Za-z0-9.]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <param name="sdkName">The canonical SDK name passed to <c>xcrun --sdk</c> (e.g. <c>iphonesimulator</c>).</param>
    /// <param name="sdkVersion"><c>xcrun --sdk … --show-sdk-version</c>.</param>
    /// <param name="sdkBuild"><c>xcrun --sdk … --show-sdk-build-version</c>.</param>
    /// <param name="xcodebuildVersion"><c>xcodebuild -version</c>'s output.</param>
    /// <param name="swiftcVersion"><c>xcrun swiftc --version</c>'s output.</param>
    public static string Compose(string sdkName, string sdkVersion, string sdkBuild, string xcodebuildVersion, string swiftcVersion)
    {
        foreach (var (label, value) in new[] { ("SDK name", sdkName), ("SDK version", sdkVersion), ("SDK build", sdkBuild) })
            if (string.IsNullOrWhiteSpace(value) || !Token.IsMatch(value.Trim()))
                throw new InvalidDataException($"Unreadable withdrawal toolchain {label}: '{value}'");
        var xcode = XcodeBuild.Match(xcodebuildVersion ?? "");
        if (!xcode.Success)
            throw new InvalidDataException($"Unreadable Xcode build version: '{xcodebuildVersion}'");
        var swift = SwiftLang.Match(swiftcVersion ?? "");
        if (!swift.Success)
            throw new InvalidDataException($"Unreadable Swift compiler version: '{swiftcVersion}'");
        return $"{sdkName.Trim()}{sdkVersion.Trim()}({sdkBuild.Trim()})+Xcode({xcode.Groups[1].Value})+{swift.Groups[1].Value.Replace(' ', '+')}";
    }

    /// <summary>
    /// True when <paramref name="profile"/> (target/platform/arch/triple/toolchain) keys its toolchain
    /// by identity rather than by install path. A target name may carry <c>@platform</c>; the
    /// toolchain segment carries no path, no bundle or SDK directory and no <c>@version</c>.
    /// </summary>
    public static bool IsPathFree(string profile)
    {
        var segments = profile.Split('/');
        var toolchain = segments[^1];
        return segments.Length == 5 && segments.All(segment => segment.Length > 0) &&
            !toolchain.Contains(".app", StringComparison.Ordinal) && !toolchain.Contains(".sdk", StringComparison.Ordinal) &&
            !toolchain.Contains('@');
    }
}

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
    /// <summary>
    /// Why a target cannot be withdrawal-qualified at all, or null when it may be. Qualification
    /// requires a clean generator exit and clean compile verdicts, and its evidence file to exist; a
    /// target that failed any of those may not have written the evidence. This only screens out what
    /// <see cref="RequireSuccess"/> and <see cref="RequirePureObjCSuccess"/> are certain to reject,
    /// never something they would accept.
    /// </summary>
    public static string? UnqualifiableReason(int? generatorExit, string csharpVerdict, string swiftVerdict,
        string? dependencyVerdict, string evidencePath)
    {
        bool evidencePresent = File.Exists(evidencePath);
        if (generatorExit == 0 && csharpVerdict == "ok" && (swiftVerdict is "ok" or "no_wrapper") &&
            ((dependencyVerdict ?? "none") is "none" or "ok") && evidencePresent)
            return null;
        return $"generator exit {generatorExit?.ToString() ?? "none"}, C# {csharpVerdict}, Swift {swiftVerdict}, " +
            $"dependency {dependencyVerdict ?? "none"}" + (evidencePresent ? "" : $", no evidence at {evidencePath}");
    }

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
