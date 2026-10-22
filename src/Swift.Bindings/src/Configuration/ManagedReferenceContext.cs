// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace BindingsGeneration;

/// <summary>
/// Immutable references for one managed target. SDK builds supply targeting-pack assets; standalone
/// generation uses only the selected dotnet root, exact Apple family and SDK-pinned BCL version.
/// Missing references never authorize borrowing facts from another platform, TPV, framework or root.
/// </summary>
public sealed class ManagedReferenceContext
{
    public PlatformInfo Target { get; }
    public IReadOnlyList<string> ReferencePaths { get; }
    public string? AppleReferencePath { get; }
    public string? NetCoreRefPackVersion { get; }
    public string? InteropGeneratorPath { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public string CacheKey { get; }
    public bool IsAvailable => AppleReferencePath is not null &&
        ReferencePaths.Any(p => Path.GetFileName(p) == "System.Runtime.dll");

    private ManagedReferenceContext(PlatformInfo target, IEnumerable<string> paths, IEnumerable<string> diagnostics)
    {
        Target = target;
        ReferencePaths = Array.AsReadOnly(paths.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        AppleReferencePath = ReferencePaths.SingleOrDefault(p =>
            Path.GetFileName(p) == AppleTypeSurfaceIndex.RefPackName(target.Platform) + ".dll");
        var core = ReferencePaths.SingleOrDefault(p => Path.GetFileName(p) == "System.Runtime.dll");
        if (core is not null)
        {
            var versionDir = Directory.GetParent(core)?.Parent?.Parent;
            NetCoreRefPackVersion = versionDir?.Name;
            var generator = versionDir is null ? null : Path.Combine(versionDir.FullName,
                "analyzers", "dotnet", "cs", "Microsoft.Interop.LibraryImportGenerator.dll");
            InteropGeneratorPath = generator is not null && File.Exists(generator) ? generator : null;
        }
        // Content identities also distinguish restored/replaced packs at the same paths. The index
        // is shared only when every metadata dependency is identical, including absent contexts.
        var identities = ReferencePaths.Select(p => p + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        CacheKey = target.PackTfm + "\n" + string.Join("\n", identities);
    }

    public static ManagedReferenceContext FromReferenceFile(PlatformInfo target, string path)
    {
        var lines = File.ReadAllLines(path).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        var frameworkHeaders = lines.Where(p => p.StartsWith("msbuild-target-framework=", StringComparison.Ordinal)).ToArray();
        var versionHeaders = lines.Where(p => p.StartsWith("msbuild-target-platform-version=", StringComparison.Ordinal)).ToArray();
        var resolvedByMSBuild = frameworkHeaders.Length == 1 && versionHeaders.Length == 1;
        if (frameworkHeaders.Length + versionHeaders.Length > 0)
        {
            if (!resolvedByMSBuild || versionHeaders[0].Split('=')[1] != target.PlatformVersion)
                throw new InvalidOperationException($"MSBuild reference context does not match requested target {target.PackTfm}.");
            var declared = PlatformInfoFactory.Create(target.Platform, target.PlatformVersion, frameworkHeaders[0].Split('=')[1]);
            if (declared.ManagedFramework != target.ManagedFramework)
                throw new InvalidOperationException($"MSBuild reference context does not match requested target {target.PackTfm}.");
        }
        var paths = lines.Where(p => !p.StartsWith("msbuild-target-", StringComparison.Ordinal)).ToArray();
        foreach (var reference in paths)
        {
            if (!Path.IsPathFullyQualified(reference) || !File.Exists(reference))
                throw new InvalidOperationException($"Managed reference '{reference}' must be an existing absolute path.");
            var normalized = reference.Replace('\\', '/');
            if (normalized.Contains("/ref/", StringComparison.OrdinalIgnoreCase) &&
                !normalized.Contains($"/ref/{target.ManagedFramework}/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Managed reference '{reference}' does not target {target.ManagedFramework}.");
            if (Path.GetFileName(reference).StartsWith("Microsoft.", StringComparison.Ordinal) &&
                new[] { "Microsoft.iOS.dll", "Microsoft.macOS.dll", "Microsoft.tvOS.dll", "Microsoft.MacCatalyst.dll" }
                    .Contains(Path.GetFileName(reference), StringComparer.Ordinal))
            {
                var family = $"{AppleTypeSurfaceIndex.RefPackName(target.Platform)}.Ref.{target.ManagedFramework}_";
                var matchesFamily = resolvedByMSBuild
                    ? normalized.Contains($"/{family}", StringComparison.OrdinalIgnoreCase)
                    : normalized.Contains($"/{family}{target.PlatformVersion}/", StringComparison.OrdinalIgnoreCase);
                if (!matchesFamily)
                    throw new InvalidOperationException($"Apple reference '{reference}' is inconsistent with target {target.PackTfm}.");
            }
        }
        var context = new ManagedReferenceContext(target, paths, Array.Empty<string>());
        if (!context.IsAvailable)
            throw new InvalidOperationException($"Reference file '{path}' must contain the {target.PackTfm} Apple reference and System.Runtime.dll.");
        return context;
    }

    public static ManagedReferenceContext Resolve(PlatformInfo target, string? sdkDirectory = null)
    {
        var root = ActiveDotNetRoot();
        try
        {
            sdkDirectory ??= FindSelectedSdk(root);
            var packageCache = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
            return ResolveInstalled(target, root, sdkDirectory, packageCache);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Xml.XmlException)
        {
            return Unavailable(target, $"Managed references for {target.PackTfm} unavailable in '{root}': {ex.Message}");
        }
    }

    internal static ManagedReferenceContext Unavailable(PlatformInfo target, string diagnostic) =>
        new(target, Array.Empty<string>(), new[] { diagnostic });

    internal static ManagedReferenceContext ResolveInstalled(PlatformInfo target, string root, string sdkDirectory, string? packageCache = null)
    {
        var diagnostics = new List<string>();
        var paths = new List<string>();
        if (!Path.GetFullPath(sdkDirectory).StartsWith(Path.Combine(Path.GetFullPath(root), "sdk") + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            return Unavailable(target, $"Selected SDK '{sdkDirectory}' does not belong to dotnet root '{root}'.");
        var family = $"{AppleTypeSurfaceIndex.RefPackName(target.Platform)}.Ref.{target.ManagedFramework}_{target.PlatformVersion}";
        var appleDir = FindAppleRefDirectory(target, root, packageCache, family, diagnostics);
        if (appleDir is not null)
            paths.AddRange(Directory.GetFiles(appleDir, "*.dll"));

        var bundled = Path.Combine(sdkDirectory, "Microsoft.NETCoreSdk.BundledVersions.props");
        if (!File.Exists(bundled))
            diagnostics.Add($"Selected SDK reference metadata missing: '{bundled}'.");
        else
        {
            var pins = XDocument.Load(bundled).Descendants().Where(e => e.Name.LocalName == "KnownFrameworkReference" &&
                (string?)e.Attribute("Include") == "Microsoft.NETCore.App" &&
                (string?)e.Attribute("TargetFramework") == target.ManagedFramework).ToArray();
            var version = pins.Length == 1 ? (string?)pins[0].Attribute("TargetingPackVersion") : null;
            var coreDir = version is null ? null : FindPinnedRefDirectory(root, packageCache,
                "Microsoft.NETCore.App.Ref", version, target.ManagedFramework, "System.Runtime.dll");
            if (coreDir is not null && File.Exists(Path.Combine(coreDir, "System.Runtime.dll")))
                paths.AddRange(Directory.GetFiles(coreDir, "*.dll"));
            else
                diagnostics.Add($"BCL references for {target.ManagedFramework}, pinned version '{version ?? "unresolved"}', are missing in '{root}'.");
        }
        if (diagnostics.Count > 0)
            return Unavailable(target, $"Managed references for {target.PackTfm} unavailable: {string.Join(" ", diagnostics)}");
        return new ManagedReferenceContext(target, paths, diagnostics);
    }

    private static string? FindAppleRefDirectory(PlatformInfo target, string root, string? packageCache,
        string family, List<string> diagnostics)
    {
        var token = AppleTypeSurfaceIndex.RefPackName(target.Platform);
        var sdkFamily = Path.Combine(root, "packs", $"{token}.Sdk.{target.ManagedFramework}_{target.PlatformVersion}");
        var sdkVersions = Directory.Exists(sdkFamily) ? Directory.GetDirectories(sdkFamily) : Array.Empty<string>();
        if (sdkVersions.Length == 1)
        {
            var metadata = Path.Combine(sdkVersions[0], "targets", token + ".Sdk.Versions.props");
            if (File.Exists(metadata))
            {
                var pins = XDocument.Load(metadata).Descendants().Where(e => e.Name.LocalName == "KnownFrameworkReference" &&
                    (string?)e.Attribute("Include") == token &&
                    (string?)e.Attribute("TargetFramework") == target.ManagedFramework).ToArray();
                var name = pins.Length == 1 ? (string?)pins[0].Attribute("TargetingPackName") : null;
                var version = pins.Length == 1 ? (string?)pins[0].Attribute("TargetingPackVersion") : null;
                if (name is not null && version is not null &&
                    name.StartsWith($"{token}.Ref.{target.ManagedFramework}_", StringComparison.Ordinal))
                {
                    var directory = FindPinnedRefDirectory(root, packageCache, name, version, target.ManagedFramework, token + ".dll");
                    if (directory is not null)
                        return directory;
                }
                diagnostics.Add($"Apple SDK metadata '{metadata}' pins unavailable or invalid references '{name}/{version}'.");
                return null;
            }
        }
        if (sdkVersions.Length > 1)
        {
            diagnostics.Add($"Ambiguous Apple SDK family '{sdkFamily}'; supply --reference-assemblies-file with resolved references.");
            return null;
        }
        // For custom installations without SDK metadata, only one version of the exact requested
        // family is acceptable. Never rank versions or search other roots/frameworks/TPVs.
        var pack = Path.Combine(root, "packs", family);
        var versions = Directory.Exists(pack) ? Directory.GetDirectories(pack) : Array.Empty<string>();
        if (versions.Length == 1)
        {
            var directory = Path.Combine(versions[0], "ref", target.ManagedFramework);
            if (File.Exists(Path.Combine(directory, token + ".dll")))
                return directory;
        }
        diagnostics.Add($"Expected one installed version of '{family}' in '{root}', found {versions.Length}; supply --reference-assemblies-file with resolved references.");
        return null;
    }

    private static string? FindPinnedRefDirectory(string root, string? packageCache, string family,
        string version, string framework, string assembly)
    {
        var installed = Path.Combine(root, "packs", family, version, "ref", framework);
        if (File.Exists(Path.Combine(installed, assembly)))
            return installed;
        var restored = packageCache is null ? null : Path.Combine(packageCache, family.ToLowerInvariant(),
            version.ToLowerInvariant(), "ref", framework);
        return restored is not null && File.Exists(Path.Combine(restored, assembly)) ? restored : null;
    }

    internal static string ActiveDotNetRoot()
    {
        // The executing muxer's shared framework is authoritative. DOTNET_ROOT and global machine
        // installations can describe unrelated SDKs and must never form a cross-root search list.
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        return Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
    }

    private static string FindSelectedSdk(string root)
    {
        var start = new ProcessStartInfo(Path.Combine(root, "dotnet"))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start SDK resolution.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("dotnet --version timed out; supply --reference-sdk-directory.");
        }
        var version = output.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0 || version.Length == 0 || version.IndexOfAny(new[] { '/', '\\', '\n', '\r' }) >= 0)
            throw new InvalidOperationException($"dotnet --version failed: {errors.GetAwaiter().GetResult().Trim()}");
        return Path.Combine(root, "sdk", version);
    }
}
