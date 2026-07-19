// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BindingsGeneration;

/// <summary>
/// The reference set the in-process Roslyn probe compiles against, resolved best-effort at
/// generation time. This is the concrete measurement of the parity gap: the probe can locate the
/// BCL/iOS reference packs and the in-tree Swift.Runtime, but the base ref-pack version is a guess
/// (the workload pins it), and dependency-binding + Apple-supplement assemblies are restored by
/// NuGet at build time and simply do not exist yet. Every gap is recorded on
/// <see cref="MissingReasons"/> so the probe result can honestly report why it is an approximation.
/// </summary>
public sealed class CSharpProbeReferenceSet
{
    /// <summary>Absolute paths of the metadata references (BCL ref pack + Microsoft.iOS.dll + Swift.Runtime).</summary>
    public IReadOnlyList<string> MetadataReferencePaths { get; }

    /// <summary>
    /// Path to <c>Microsoft.Interop.LibraryImportGenerator.dll</c> (the source generator that turns
    /// <c>[LibraryImport]</c> partial declarations into P/Invoke bodies), or null if not found.
    /// Without it every generated extern reports a false error, so its resolution is load-bearing.
    /// </summary>
    public string? InteropGeneratorPath { get; }

    /// <summary>The base netcore ref-pack version the probe chose (newest installed). The workload
    /// pins a specific one; this is a recorded guess.</summary>
    public string? NetCoreRefPackVersion { get; }

    /// <summary>Reasons the reference set is incomplete relative to the real build — the parity gap, itemized.</summary>
    public IReadOnlyList<string> MissingReasons { get; }

    private CSharpProbeReferenceSet(
        IReadOnlyList<string> metadataReferencePaths,
        string? interopGeneratorPath,
        string? netCoreRefPackVersion,
        IReadOnlyList<string> missingReasons)
    {
        MetadataReferencePaths = metadataReferencePaths;
        InteropGeneratorPath = interopGeneratorPath;
        NetCoreRefPackVersion = netCoreRefPackVersion;
        MissingReasons = missingReasons;
    }

    /// <summary>
    /// Test-only factory: build a reference set from explicit metadata reference paths, bypassing
    /// disk ref-pack resolution so probe unit tests are deterministic and independent of which
    /// workloads happen to be installed. The generator itself always goes through
    /// <see cref="Resolve"/>.
    /// </summary>
    internal static CSharpProbeReferenceSet ForTesting(
        IReadOnlyList<string> metadataReferencePaths,
        string? interopGeneratorPath = null,
        IReadOnlyList<string>? missingReasons = null)
        => new CSharpProbeReferenceSet(
            metadataReferencePaths,
            interopGeneratorPath,
            netCoreRefPackVersion: null,
            missingReasons ?? Array.Empty<string>());

    /// <summary>
    /// Resolve the probe's reference set. Pure disk inspection — no network, no writes.
    /// </summary>
    public static CSharpProbeReferenceSet Resolve()
    {
        var refs = new List<string>();
        var missing = new List<string>();

        // Base BCL ref pack (newest installed). The workload pins a specific version by TFM;
        // choosing the newest is a recorded approximation (parity item: reference-pack version).
        var (netCoreRefDir, netCoreVersion) = FindNewestNetCoreAppRefDir();
        if (netCoreRefDir is not null)
        {
            refs.AddRange(Directory.GetFiles(netCoreRefDir, "*.dll"));
        }
        else
        {
            missing.Add("base netcore ref pack (Microsoft.NETCore.App.Ref) not found");
        }

        // iOS reference assembly (Microsoft.iOS.dll). The generated code targets net10.0-ios.
        var iosRef = FindNewestMicrosoftIosRefAssembly();
        if (iosRef is not null)
            refs.Add(iosRef);
        else
            missing.Add("iOS reference assembly (Microsoft.iOS.Ref/Microsoft.iOS.dll) not found");

        // Swift.Runtime — the generated bindings reference it heavily. It is loaded into the
        // running generator process, so its path is on TRUSTED_PLATFORM_ASSEMBLIES (AOT/trim-clean
        // resolution — no Assembly.Location).
        var swiftRuntime = FindTrustedPlatformAssembly("Swift.Runtime.dll");
        if (swiftRuntime is not null)
            refs.Add(swiftRuntime);
        else
            missing.Add("Swift.Runtime.dll not resolvable from TRUSTED_PLATFORM_ASSEMBLIES");

        // The interop source generator ships in the base ref pack's analyzers folder.
        var interop = netCoreRefDir is not null ? FindInteropGenerator(netCoreRefDir) : null;
        if (interop is null)
            missing.Add("Microsoft.Interop.LibraryImportGenerator.dll not found (externs will false-red without it)");

        // Parity gaps the probe structurally cannot close at generation time.
        missing.Add("dependency-binding assemblies are restored by NuGet at build time — absent at generation time");
        missing.Add("Apple supplement (SwiftBindings.Apple) assembly is restored by NuGet at build time — absent at generation time");
        missing.Add("workload-injected platform DefineConstants (e.g. __IOS__) are not reproduced in-process");

        // De-dup while preserving order (a ref dir can list an assembly the iOS pack re-ships).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = refs.Where(r => r is not null && File.Exists(r) && seen.Add(r)).ToList();

        return new CSharpProbeReferenceSet(deduped, interop, netCoreVersion, missing);
    }

    private static string? FindTrustedPlatformAssembly(string fileName)
    {
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        return tpa.Split(Path.PathSeparator)
            .FirstOrDefault(p => !string.IsNullOrEmpty(p) &&
                                 string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase) &&
                                 File.Exists(p));
    }

    private static (string? Dir, string? Version) FindNewestNetCoreAppRefDir()
    {
        string? bestDir = null;
        Version? bestVersion = null;
        foreach (var root in DotNetRoots())
        {
            var packDir = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
            if (!Directory.Exists(packDir))
                continue;

            foreach (var systemRuntime in Directory.GetFiles(packDir, "System.Runtime.dll", SearchOption.AllDirectories))
            {
                if (!IsRefAssemblyPath(systemRuntime))
                    continue;
                var version = VersionFromRefPath(systemRuntime);
                if (bestVersion is null || version > bestVersion)
                {
                    bestVersion = version;
                    bestDir = Path.GetDirectoryName(systemRuntime);
                }
            }
        }
        return (bestDir, bestVersion?.ToString());
    }

    private static string? FindNewestMicrosoftIosRefAssembly()
    {
        string? best = null;
        Version? bestVersion = null;
        foreach (var root in DotNetRoots())
        {
            var packsDir = Path.Combine(root, "packs");
            if (!Directory.Exists(packsDir))
                continue;
            foreach (var packDir in Directory.GetDirectories(packsDir, "Microsoft.iOS.Ref*"))
            {
                foreach (var dll in Directory.GetFiles(packDir, "Microsoft.iOS.dll", SearchOption.AllDirectories))
                {
                    if (!IsRefAssemblyPath(dll))
                        continue;
                    var version = VersionFromRefPath(dll);
                    if (bestVersion is null || version > bestVersion)
                    {
                        bestVersion = version;
                        best = dll;
                    }
                }
            }
        }
        return best;
    }

    private static string? FindInteropGenerator(string netCoreRefDir)
    {
        // {pack}/{version}/ref/{tfm}/System.Runtime.dll -> climb to {pack}/{version}, then analyzers.
        var versionDir = Path.GetDirectoryName(Path.GetDirectoryName(netCoreRefDir));
        if (versionDir is null)
            return null;
        var analyzers = Path.Combine(versionDir, "analyzers", "dotnet", "cs");
        if (!Directory.Exists(analyzers))
            return null;
        return Directory.GetFiles(analyzers, "Microsoft.Interop.LibraryImportGenerator.dll", SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    private static bool IsRefAssemblyPath(string path)
        => path.Replace('\\', '/').Contains("/ref/", StringComparison.Ordinal);

    internal static Version VersionFromRefPath(string file)
    {
        var versionDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(file)));
        var segment = versionDir is null ? null : Path.GetFileName(versionDir);
        // Ref-pack folders can carry a prerelease suffix (e.g. "10.0.0-rc.1"). Version.TryParse
        // rejects the "-rc.1" and would collapse every such pack to 0.0, making the newest-pack
        // pick arbitrary when only prerelease packs are installed. Parse the release core before
        // the first '-' so ordering stays meaningful.
        var core = segment;
        var dash = core is null ? -1 : core.IndexOf('-');
        var isPrerelease = dash > 0;
        if (isPrerelease)
            core = core!.Substring(0, dash);
        if (!Version.TryParse(core, out var v))
            return new Version(0, 0);
        // A stable pack must outrank a prerelease of the same numeric core (SemVer precedence:
        // 10.0.0 > 10.0.0-rc.1). Ref-pack folders are 3-part cores, so the revision slot is free
        // to carry the stable/prerelease rank — stable = 1, prerelease = 0 — which the newest-pack
        // ">" comparison then breaks correctly instead of falling to arbitrary first-wins.
        return new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), isPrerelease ? 0 : 1);
    }

    private static IEnumerable<string> DotNetRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        if (!string.IsNullOrEmpty(runtimeDir))
        {
            var inferred = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
            if (seen.Add(inferred))
                yield return inferred;
        }
        foreach (var candidate in new[]
                 {
                     Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                     "/usr/local/share/dotnet",
                     "/usr/share/dotnet",
                     Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home
                         ? Path.Combine(home, ".dotnet")
                         : null,
                 })
        {
            if (!string.IsNullOrEmpty(candidate) && seen.Add(candidate))
                yield return candidate;
        }
    }
}
