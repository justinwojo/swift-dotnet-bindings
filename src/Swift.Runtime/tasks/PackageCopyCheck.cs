// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SwiftBindings.Tasks;

/// <summary>
/// A declared package copy, its paths as the declaration states them. An empty
/// <paramref name="TargetsFile"/> declares a copy that ships no consumer targets, and
/// <paramref name="NativePayload"/> where its natives are (an empty one means <c>native</c>).
/// </summary>
public sealed record PackageCopy(string PackageId, string CopyTfm, string Toolchain, string LibDirectory, string NativeDirectory, string TargetsFile,
    string NativePayload = "");

/// <summary>A compile reference, with the package NuGet resolved it from (empty when none).</summary>
public sealed record ResolvedReference(string Path, string PackageId, string PackageVersion);

/// <summary>What the copy check reports.</summary>
public enum PackageCopyFindingKind
{
    /// <summary>The copy a package resolved, announced.</summary>
    Selected,

    /// <summary>The resolved copy is older than the build's toolchain (informational).</summary>
    Fallback,

    /// <summary>As <see cref="Fallback"/>, refused because the build requires an exact copy.</summary>
    FallbackRefused,

    /// <summary>The build resolved a binding assembly from another copy than its consumer targets, or a copy with no consumer targets.</summary>
    Mismatch,
}

/// <summary>One finding of <see cref="PackageCopyCheck.Evaluate"/>.</summary>
public sealed record PackageCopyFinding(PackageCopyFindingKind Kind, string PackageId, string Text);

/// <summary>The copy check's logic (<see cref="SwiftBindingCopyCheck"/>), free of MSBuild.</summary>
public static class PackageCopyCheck
{
    /// <summary>
    /// The findings for a build of <paramref name="targetFramework"/> (platform version
    /// <paramref name="targetPlatformVersion"/>) that declared <paramref name="copies"/> and resolved
    /// <paramref name="references"/>.
    /// </summary>
    public static IReadOnlyList<PackageCopyFinding> Evaluate(
        IReadOnlyList<PackageCopy> copies, IReadOnlyList<ResolvedReference> references,
        string targetFramework, string targetPlatformVersion, bool requireExactCopy)
    {
        var findings = new List<PackageCopyFinding>();
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // One declaration per package; a package referenced twice imports its props once, but be tolerant.
        foreach (var copy in copies.GroupBy(c => c.PackageId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            declared.Add(copy.PackageId);
            var expectedLib = Directory(copy.LibDirectory);
            var packageRoot = Directory(Path.Combine(copy.LibDirectory, "..", ".."));
            var resolved = references
                .Where(r => r.PackageId.Equals(copy.PackageId, StringComparison.OrdinalIgnoreCase)
                            || (r.PackageId.Length == 0 && Under(r.Path, packageRoot)))
                .ToList();
            var version = resolved.Select(r => r.PackageVersion).FirstOrDefault(v => v.Length > 0) ?? "";
            var named = version.Length > 0 ? $"{copy.PackageId} {version}" : copy.PackageId;
            var strays = resolved.Where(r => !string.Equals(Directory(Path.GetDirectoryName(r.Path) ?? ""), expectedLib, PathComparison)).ToList();
            if (strays.Count > 0)
            {
                var stray = strays[0];
                findings.Add(new PackageCopyFinding(PackageCopyFindingKind.Mismatch, copy.PackageId,
                    $"{named}: this build ({targetFramework}) resolved {Path.GetFileName(stray.Path)} from '{Directory(Path.GetDirectoryName(stray.Path) ?? "")}', "
                    + $"but NuGet chose the package's {copy.CopyTfm} consumer targets, whose native payload only matches '{expectedLib}'. "
                    + "The package's lib/ and buildTransitive/ copies disagree, so the build would link a native payload built for another toolchain. "
                    + "Restore again; if this persists the package is malformed: every lib/<tfm>/ copy must carry its own buildTransitive/<tfm>/ and native/<tfm>/ copy (rebuild it with SwiftBindings.Sdk, or merge its contributions with SwiftBindingsMergePackages)."));
                continue;
            }
            if (resolved.Count > 0 && (copy.TargetsFile.Length > 0 || IsNativePayload(copy)) && !File.Exists(copy.TargetsFile))
            {
                findings.Add(new PackageCopyFinding(PackageCopyFindingKind.Mismatch, copy.PackageId,
                    $"{named}: this build ({targetFramework}) uses the package's {copy.CopyTfm} copy, but its consumer targets '{Full(copy.TargetsFile)}' are missing, "
                    + "so no native payload would be linked. The package is malformed: every copy carries its declaration, its consumer targets and its native/<tfm>/ payload."));
                continue;
            }
            var built = copy.Toolchain.Length > 0 ? $" (built on {copy.Toolchain})" : "";
            var native = IsNativePayload(copy) ? Directory(copy.NativeDirectory) : $"({copy.NativePayload.Trim()})";
            var targets = copy.TargetsFile.Length > 0 ? Full(copy.TargetsFile) : "(none)";
            findings.Add(new PackageCopyFinding(PackageCopyFindingKind.Selected, copy.PackageId,
                $"SwiftBindings copy: {named} -> {copy.CopyTfm}{built}; lib={expectedLib} native={native} targets={targets}"));
            if (resolved.Count == 0)
                continue;
            var fallback = Fallback(copy.CopyTfm, targetFramework, targetPlatformVersion);
            if (fallback is null)
                continue;
            var text = $"{named}: this build ({targetFramework}, platform {targetPlatformVersion}) uses the package's {copy.CopyTfm} copy{built}, {fallback}, "
                       + "so the binding exposes that copy's API and native payload, not one built for this toolchain.";
            findings.Add(requireExactCopy
                ? new PackageCopyFinding(PackageCopyFindingKind.FallbackRefused, copy.PackageId,
                    text + " SwiftBindingsRequireExactCopy is set: use a package version that carries a copy for this toolchain, or remove the property to accept the older copy.")
                : new PackageCopyFinding(PackageCopyFindingKind.Fallback, copy.PackageId,
                    text + " Set SwiftBindingsRequireExactCopy=true to make this an error."));
        }

        // A binding package laid out per copy, resolved with no copy declared: NuGet chose its lib/
        // copy but no buildTransitive/ copy at all, so no native would be linked.
        foreach (var group in references.Where(r => r.PackageId.Length > 0 && !declared.Contains(r.PackageId))
                     .GroupBy(r => r.PackageId, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var root = Directory(Path.Combine(Path.GetDirectoryName(first.Path) ?? "", "..", ".."));
            if (!IsCopyLayout(root, group.Key))
                continue;
            findings.Add(new PackageCopyFinding(PackageCopyFindingKind.Mismatch, group.Key,
                $"{group.Key} {first.PackageVersion}: this build ({targetFramework}) resolved {Path.GetFileName(first.Path)} from '{Directory(Path.GetDirectoryName(first.Path) ?? "")}', "
                + "but NuGet chose none of the package's buildTransitive/ copies, so the copy's consumer files (and any native payload they link) are missing. "
                + "The package is malformed: every lib/<tfm>/ copy must carry its own buildTransitive/<tfm>/ copy, with its declaration."));
        }
        return findings;
    }

    /// <summary>Whether <paramref name="copy"/> declares its natives under <c>native/&lt;tfm&gt;/</c> (a declaration that names no payload does).</summary>
    private static bool IsNativePayload(PackageCopy copy) =>
        copy.NativePayload.Trim().Length == 0 || copy.NativePayload.Trim() == BindingsGeneration.PackageCopyDeclarationEmitter.NativePayloadNative;

    /// <summary>
    /// Whether the extracted package at <paramref name="root"/> is laid out per copy: natives under a
    /// copy's <c>native/&lt;tfm&gt;/</c>, or a copy declaration (<c>buildTransitive/&lt;tfm&gt;/&lt;id&gt;.props</c>),
    /// which a copy with no natives of its own still carries.
    /// </summary>
    private static bool IsCopyLayout(string root, string packageId)
    {
        var native = Path.Combine(root, "native");
        if (System.IO.Directory.Exists(native) && System.IO.Directory.EnumerateDirectories(native).Any(d => IsCopyTfm(Path.GetFileName(d))))
            return true;
        var build = Path.Combine(root, "buildTransitive");
        return System.IO.Directory.Exists(build) && System.IO.Directory.EnumerateDirectories(build)
            .Any(d => IsCopyTfm(Path.GetFileName(d)) && File.Exists(Path.Combine(d, BindingsGeneration.PackageCopyDeclarationEmitter.FileName(packageId))));
    }

    /// <summary>
    /// Why the copy <paramref name="copyTfm"/> is older than a build of <paramref name="targetFramework"/>
    /// on platform version <paramref name="targetPlatformVersion"/>; null when it is the build's own
    /// (same .NET version and the same platform SDK major — a <c>26.0</c> copy serves every 26.x build).
    /// </summary>
    public static string? Fallback(string copyTfm, string targetFramework, string targetPlatformVersion)
    {
        if (!TryParse(copyTfm, out var copyNet, out _, out var copyPlatform))
            return null;
        if (!TryParse(targetFramework, out var appNet, out _, out var appPlatform))
            return null;
        if (appNet > copyNet)
            return $"built for .NET {copyNet.ToString(2)} rather than this build's .NET {appNet.ToString(2)}";
        if (!Version.TryParse(targetPlatformVersion, out var tpv) && appPlatform is null)
            return null;
        tpv ??= appPlatform;
        if (copyPlatform is not null && tpv is not null && tpv.Major > copyPlatform.Major)
            return $"built against the platform {copyPlatform.ToString(2)} SDK rather than this build's {tpv.Major}.x";
        return null;
    }

    /// <summary>Reads <c>net10.0-ios26.0</c> as its .NET version, platform and platform version (null when the TFM names none).</summary>
    public static bool TryParse(string tfm, out Version net, out string platform, out Version? platformVersion)
    {
        net = new Version(0, 0);
        platform = "";
        platformVersion = null;
        if (!tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            return false;
        var dash = tfm.IndexOf('-');
        var netPart = dash < 0 ? tfm[3..] : tfm[3..dash];
        if (!Version.TryParse(netPart, out var parsed))
            return false;
        net = parsed;
        if (dash < 0)
            return true;
        var rest = tfm[(dash + 1)..];
        var letters = new string(rest.TakeWhile(char.IsLetter).ToArray());
        platform = letters.ToLowerInvariant();
        var version = rest[letters.Length..];
        if (version.Length > 0)
        {
            if (!Version.TryParse(version.Contains('.') ? version : version + ".0", out var pv))
                return false;
            platformVersion = pv;
        }
        return true;
    }

    /// <summary>Whether a <c>native/</c> child folder names a copy (a platform-versioned .NET TFM).</summary>
    public static bool IsCopyTfm(string folder) =>
        TryParse(folder, out _, out var platform, out var version) && platform.Length > 0 && version is not null;

    private static StringComparison PathComparison => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static string Full(string path) => path.Length == 0 ? "" : Path.GetFullPath(path);

    private static string Directory(string path) =>
        path.Length == 0 ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Replace('\\', '/'))) + "/";

    private static bool Under(string path, string root) =>
        root.Length > 0 && path.Length > 0 && Path.GetFullPath(path).StartsWith(root, PathComparison);
}
