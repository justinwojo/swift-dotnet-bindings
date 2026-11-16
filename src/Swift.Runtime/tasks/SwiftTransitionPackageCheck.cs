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
/// the package against the manifest hash and bridges. The referenced assemblies (the runtime) come
/// from the packing project's own resolved references, for carrier types.
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
            var natives = new PackageImages(root);
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
                var result = ConsumerTransitionCheck.Check(assembly, natives, requireNativeImages: true,
                    Path.Combine(ScratchDirectory, "plans", $"{refused.Count + verified}"));
                if (result.Verdict != ConsumerTransitionVerdict.Refused)
                {
                    verified++;
                    Log.LogMessage(MessageImportance.Normal, "{0}: {1} ({2}).", shown, result.Verdict, result.State);
                    continue;
                }
                refused.Add(shown);
                var named = string.Join("; ", result.Problems.Take(3));
                var rest = result.Problems.Length > 3 ? $"; and {result.Problems.Length - 3} more (listed below)" : "";
                Log.LogError(null, Code, null, package, 0, 0, 0, 0, "{0}",
                    $"{Path.GetFileName(package)}: {shown} would be refused by its consumers' Swift transition check (evidence {result.State}): {named}{rest}. "
                    + "A wrapper that keeps the bridges or manifest hash of an earlier routing is the usual cause; rebuild the binding clean (delete its bin/ and obj/) and pack again. "
                    + "The package was deleted.");
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

    /// <summary>The framework slices of a library in the package, read when a manifest routes through it.</summary>
    private sealed class PackageImages(string root) : IReadOnlyList<PackagedNative>
    {
        // ConsumerTransitionCheck asks only for the libraries the manifests route through, so the
        // package's frameworks are read on first use, once.
        private readonly Lazy<List<PackagedNative>> all = new(() =>
            Directory.GetDirectories(root, "*.framework", SearchOption.AllDirectories)
                .Select(d => Path.GetFileNameWithoutExtension(d)).Distinct(StringComparer.Ordinal)
                .SelectMany(framework => ConsumerTransitionCheck.FrameworkBinaries(root, framework)
                    .Select(p => new PackagedNative(framework, Path.GetRelativePath(root, p), File.ReadAllBytes(p))))
                .ToList());

        private List<PackagedNative> All => all.Value;

        public PackagedNative this[int index] => All[index];
        public int Count => All.Count;
        public IEnumerator<PackagedNative> GetEnumerator() => All.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
