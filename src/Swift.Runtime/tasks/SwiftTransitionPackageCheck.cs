// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using SwiftBindings.TransitionGate;

namespace SwiftBindings.Tasks;

/// <summary>
/// Reads back the binding package <c>dotnet pack</c> just wrote and refuses it (SWIFTBIND133) when
/// an assembly in it would be refused by its consumers' check (<see cref="SwiftTransitionConsumerCheck"/>),
/// or when a library its manifests route bridges through is missing from the package or does not
/// match them. A refused package is deleted, so it cannot be pushed by mistake.
/// </summary>
/// <remarks>
/// The check reads the package alone, as a consumer would receive it: every <c>lib/</c> assembly
/// that can make a Swift transition is checked against the plans its own manifests embed
/// (<see cref="ConsumerTransitionCheck.Check"/>), and every framework slice of a routed library in
/// the package against the manifest hash and bridges. A <c>lib/&lt;tfm&gt;/</c> assembly of an Apple
/// platform is paired with the slices a consumer of that target framework links: those under a
/// <c>runtimes/&lt;rid&gt;/</c> of the same platform, and any not under a platform's RID. A binding that
/// targets several platforms ships one wrapper per platform, and their manifests differ wherever the
/// platforms' routing does. The referenced assemblies (the runtime) come from the packing project's
/// own resolved references, for carrier types.
/// </remarks>
public sealed class SwiftTransitionPackageCheck : Microsoft.Build.Utilities.Task
{
    /// <summary>The diagnostic code of a refused package.</summary>
    public const string Code = "SWIFTBIND133";

    /// <summary>The packages written (<c>@(NuGetPackOutput)</c>); only <c>.nupkg</c> files are read.</summary>
    [Required]
    public ITaskItem[] Packages { get; set; } = [];

    /// <summary>Directories the packed assemblies' references resolve in.</summary>
    public ITaskItem[] ReferenceDirectories { get; set; } = [];

    [Required]
    public string ScratchDirectory { get; set; } = "";

    public override bool Execute()
    {
        var references = ReferenceDirectories.Select(d => d.ItemSpec.TrimEnd('/', '\\')).Where(Directory.Exists).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var package in Packages.Select(p => p.GetMetadata("FullPath"))
                     .Where(p => p.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.Ordinal))
            CheckPackage(package, references);
        return !Log.HasLoggedErrors;
    }

    private void CheckPackage(string package, string[] references)
    {
        if (!File.Exists(package))
        {
            Log.LogError(null, Code, null, package, 0, 0, 0, 0, "{0}", $"The package {Path.GetFileName(package)} was not written, so its Swift transitions cannot be checked.");
            return;
        }
        var root = Path.Combine(ScratchDirectory, Path.GetFileNameWithoutExtension(package));
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        var refuse = true;
        try
        {
            ZipFile.ExtractToDirectory(package, root);
            var lib = Path.Combine(root, "lib");
            var assemblies = Directory.Exists(lib)
                ? Directory.GetFiles(lib, "*.dll", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList()
                : new List<string>();
            var packaged = new Lazy<List<(PackagedNative Native, string? Platform)>>(() => ReadFrameworks(root));
            var refused = new List<string>();
            var verified = 0;
            foreach (var path in assemblies)
            {
                var image = File.ReadAllBytes(path);
                if (!ConsumerTransitionCheck.MayHaveSwiftTransitions(image))
                    continue;
                var shown = Path.GetRelativePath(root, path).Replace('\\', '/');
                var assembly = new PackagedAssembly(shown, image,
                    new[] { Path.GetDirectoryName(path)! }.Concat(references).ToArray());
                var natives = new PlatformImages(packaged, TargetFrameworkPlatform(shown));
                var result = ConsumerTransitionCheck.Check(assembly, natives, requireNativeImages: true,
                    Path.Combine(ScratchDirectory, "plans", $"{refused.Count + verified}"));
                if (result.Verdict != ConsumerTransitionVerdict.Refused)
                {
                    verified++;
                    Log.LogMessage(MessageImportance.Normal, "{0}: {1} ({2}).", shown, result.Verdict, result.State);
                    foreach (var withdrawal in result.Withdrawals)
                        Log.LogMessage(MessageImportance.High,
                            "{0}: {1} packs as a partial binding: {2}'s bridges were withdrawn ({3}), so {4} member(s) keep their guarded direct import and throw before Swift runs on the engines named: {5}.",
                            Path.GetFileName(package), shown, withdrawal.Module, withdrawal.Reason, withdrawal.Transitions.Length, withdrawal.Describe());
                    continue;
                }
                refused.Add(shown);
                var named = string.Join("; ", result.Problems.Take(3));
                var rest = result.Problems.Length > 3 ? $"; and {result.Problems.Length - 3} more (listed below)" : "";
                Log.LogError(null, Code, null, package, 0, 0, 0, 0, "{0}",
                    $"{Path.GetFileName(package)}: {shown} would be refused by its consumers' Swift transition check (evidence {result.State}): {named}{rest}. "
                    + Remedy(result) + " The package was deleted.");
                foreach (var problem in result.Problems)
                    Log.LogMessage(MessageImportance.Normal, "  {0}: {1}", shown, problem);
            }
            refuse = refused.Count > 0;
            if (!refuse)
                Log.LogMessage(MessageImportance.Normal, "Swift transition check of {0}: {1} assembly(ies) with Swift transitions accepted.", Path.GetFileName(package), verified);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException)
        {
            Log.LogError(null, Code, null, package, 0, 0, 0, 0, "{0}", $"{Path.GetFileName(package)}: its Swift transitions could not be checked: {ex.Message}. The package was deleted.");
        }
        finally
        {
            // A package that was refused, or could not be checked, does not stay where it can be pushed.
            if (refuse && File.Exists(package))
                File.Delete(package);
            if (Directory.Exists(ScratchDirectory))
                Directory.Delete(ScratchDirectory, recursive: true);
        }
    }

    /// <summary>What SWIFTBIND133 tells the author to do, from what refused the assembly.</summary>
    internal static string Remedy(ConsumerTransitionResult result)
    {
        var withdrawal = result.Withdrawals.FirstOrDefault();
        var relink = withdrawal is null ? "" : $"{withdrawal.Module}'s bridges were withdrawn because {withdrawal.Reason}. ";
        return result.Remedy switch
        {
            ConsumerTransitionRemedy.CleanRebuild =>
                relink + "A wrapper that keeps the bridges or manifest hash of an earlier routing is the usual cause; rebuild the binding clean (delete its bin/ and obj/) and pack again.",
            ConsumerTransitionRemedy.ReceiptMismatch =>
                relink + "The relinked wrapper contradicts the lowering receipt the bridges were authorized by, so the restored imports cannot be shown safe and the binding is not packed as a partial one. "
                + "Make the wrapper's inputs stable between its compiles (the same sources, SDK and flags), then rebuild the binding clean and pack again.",
            ConsumerTransitionRemedy.WithdrawalIncoherent =>
                relink + "The binding does not hold up as the withdrawal it records, which a clean rebuild does not fix: fix the relink failure so the binding routes its bridges, "
                + "or regenerate it with the current SwiftBindings.Sdk if the package was altered after it was built.",
            _ => "Regenerate the binding with the current SwiftBindings.Sdk (a clean build of the binding project) and pack again.",
        };
    }

    /// <summary>Every framework binary in the package, with the Apple platform of the <c>runtimes/&lt;rid&gt;/</c> it is under, if any.</summary>
    private static List<(PackagedNative Native, string? Platform)> ReadFrameworks(string root) =>
        Directory.GetDirectories(root, "*.framework", SearchOption.AllDirectories)
            .Select(d => Path.GetFileNameWithoutExtension(d)).Distinct(StringComparer.Ordinal)
            .SelectMany(framework => ConsumerTransitionCheck.FrameworkBinaries(root, framework)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .Select(shown => (new PackagedNative(framework, shown, File.ReadAllBytes(Path.Combine(root, shown))), RuntimePlatform(shown))))
            .ToList();

    /// <summary>The Apple platform of a <c>lib/&lt;tfm&gt;/</c> path, as the SDK reads it from the target framework; null for any other.</summary>
    internal static string? TargetFrameworkPlatform(string packagePath)
    {
        var parts = packagePath.Split('/');
        if (parts.Length < 3 || !parts[0].Equals("lib", StringComparison.OrdinalIgnoreCase))
            return null;
        var dash = parts[1].IndexOf('-');
        var platform = dash < 0 ? "" : new string(parts[1][(dash + 1)..].TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant();
        return platform is "ios" or "tvos" or "maccatalyst" or "macos" ? platform : null;
    }

    /// <summary>The Apple platform of a <c>runtimes/&lt;rid&gt;/</c> path; null for a path under no Apple RID.</summary>
    internal static string? RuntimePlatform(string packagePath)
    {
        var parts = packagePath.Split('/');
        if (parts.Length < 3 || !parts[0].Equals("runtimes", StringComparison.OrdinalIgnoreCase))
            return null;
        var dash = parts[1].IndexOf('-');
        return (dash < 0 ? parts[1] : parts[1][..dash]).ToLowerInvariant() switch
        {
            "ios" or "iossimulator" => "ios",
            "tvos" or "tvossimulator" => "tvos",
            "maccatalyst" => "maccatalyst",
            "osx" => "macos",
            _ => null,
        };
    }

    /// <summary>
    /// The framework slices a consumer of one platform's target framework links: those of its own
    /// platform's RIDs and those under no Apple RID. Read when a manifest routes through a library,
    /// once per package.
    /// </summary>
    private sealed class PlatformImages(Lazy<List<(PackagedNative Native, string? Platform)>> packaged, string? platform) : IReadOnlyList<PackagedNative>
    {
        private readonly Lazy<List<PackagedNative>> scoped = new(() => packaged.Value
            .Where(n => platform is null || n.Platform is null || n.Platform == platform)
            .Select(n => n.Native).ToList());

        private List<PackagedNative> All => scoped.Value;

        public PackagedNative this[int index] => All[index];
        public int Count => All.Count;
        public IEnumerator<PackagedNative> GetEnumerator() => All.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
