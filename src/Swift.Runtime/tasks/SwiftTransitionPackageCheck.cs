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
/// the package against the manifest hash and bridges. A <c>lib/&lt;tfm&gt;/</c> assembly is paired with
/// the slices a consumer of that target framework links: its own copy's <c>native/&lt;tfm&gt;/</c> (the
/// same full target framework: a package may carry one copy per toolchain), a legacy
/// <c>runtimes/&lt;rid&gt;/</c> of the same platform, and natives shared by every copy. A binding that
/// targets several platforms or toolchains ships one wrapper per copy, and their manifests differ
/// wherever the copies' routing does. The referenced assemblies (the runtime) come from the packing project's
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
            var packaged = new Lazy<List<(PackagedNative Native, NativeScope Scope)>>(() => ReadFrameworks(root));
            var declaresCopies = DeclaresCopies(root);
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
                var natives = new PlatformImages(packaged, declaresCopies, TargetFrameworkPlatform(shown), TargetFrameworkFolder(shown));
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
            ConsumerTransitionRemedy.WrapperMismatch =>
                "A native wrapper in the package is not the one this assembly was generated with: each copy's lib/<tfm>/ and native/<tfm>/ must come from one build. "
                + "Rebuild the binding clean (delete its bin/ and obj/) and pack again; for a merged package, merge the contributions exactly as each toolchain packed them.",
            _ => "Regenerate the binding with the current SwiftBindings.Sdk (a clean build of the binding project) and pack again.",
        };
    }

    /// <summary>
    /// Every native image in the package, with the consumers it serves (<see cref="Scope"/>): each
    /// framework binary, and each bare dylib under the library names a consumer's native reference
    /// resolves it by (<c>{library}.dylib</c>, <c>lib{library}.dylib</c>;
    /// <see cref="ConsumerTransitionCheck.NativeReferenceImages"/>).
    /// </summary>
    private static List<(PackagedNative Native, NativeScope Scope)> ReadFrameworks(string root)
    {
        var frameworks = Directory.GetDirectories(root, "*.framework", SearchOption.AllDirectories)
            .Select(d => Path.GetFileNameWithoutExtension(d)).Distinct(StringComparer.Ordinal)
            .SelectMany(framework => ConsumerTransitionCheck.FrameworkBinaries(root, framework)
                .Select(p => (Library: framework, Shown: Path.GetRelativePath(root, p).Replace('\\', '/'))));
        var dylibs = Directory.GetFiles(root, "*.dylib", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Where(shown => !shown.Contains(".dSYM/", StringComparison.Ordinal) && !shown.Contains(".framework/", StringComparison.Ordinal))
            .SelectMany(shown =>
            {
                var name = Path.GetFileNameWithoutExtension(shown);
                return (name.StartsWith("lib", StringComparison.Ordinal) && name.Length > 3 ? new[] { name, name[3..] } : new[] { name })
                    .Select(library => (Library: library, Shown: shown));
            });
        return frameworks.Concat(dylibs)
            .Select(n => (new PackagedNative(n.Library, n.Shown, File.ReadAllBytes(Path.Combine(root, n.Shown))), Scope(n.Shown)))
            .ToList();
    }

    /// <summary>
    /// Whether the package is one of per-toolchain copies: a <c>buildTransitive/&lt;tfm&gt;/</c> props
    /// file declares a <c>SwiftBindingPackageCopy</c>. Read from the declarations, not from where the
    /// natives landed, so a copy whose wrapper is outside its <c>native/&lt;tfm&gt;/</c> is not taken
    /// for a package without copies.
    /// </summary>
    internal static bool DeclaresCopies(string root)
    {
        var build = Path.Combine(root, "buildTransitive");
        return Directory.Exists(build)
            && Directory.EnumerateFiles(build, "*.props", SearchOption.AllDirectories)
                .Any(f => PackageCopyDeclaration.Declares(File.ReadAllText(f)));
    }

    /// <summary>
    /// The consumers a packaged native serves: a <c>native/&lt;tfm&gt;/</c> copy serves only the
    /// <c>lib/&lt;tfm&gt;/</c> of the same full target framework (a package can carry several copies of
    /// one platform, one per toolchain); a legacy <c>runtimes/&lt;rid&gt;/</c> native serves its
    /// platform's target frameworks; anything else (the runtime's root <c>native/</c>, the supplement's
    /// <c>runtimes/native/</c>) serves every target framework.
    /// </summary>
    internal static NativeScope Scope(string packagePath)
    {
        var parts = packagePath.Split('/');
        if (parts.Length >= 3 && parts[0].Equals("native", StringComparison.OrdinalIgnoreCase) && PackageCopyCheck.IsCopyTfm(parts[1]))
            return new NativeScope(parts[1], null);
        return new NativeScope(null, RuntimePlatform(packagePath));
    }

    /// <summary>The <c>lib/&lt;tfm&gt;/</c> folder of a packaged assembly; null for any other path.</summary>
    internal static string? TargetFrameworkFolder(string packagePath)
    {
        var parts = packagePath.Split('/');
        return parts.Length >= 3 && parts[0].Equals("lib", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }

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
    /// The framework slices a consumer of one copy's target framework links: its own
    /// <c>native/&lt;tfm&gt;/</c> in a package of copies; otherwise the legacy <c>runtimes/&lt;rid&gt;/</c>
    /// of its platform and natives shared by every target framework. Read when a manifest routes
    /// through a library, once per package.
    /// </summary>
    private sealed class PlatformImages(Lazy<List<(PackagedNative Native, NativeScope Scope)>> packaged, bool declaresCopies, string? platform, string? folder) : IReadOnlyList<PackagedNative>
    {
        // In a package of copies an assembly links only its own copy's natives, never a shared or
        // legacy image; those serve packages without copies (the runtime, the supplement, earlier layouts).
        private readonly Lazy<List<PackagedNative>> scoped = new(() =>
        {
            var copies = declaresCopies || packaged.Value.Any(n => n.Scope.CopyTfm is not null);
            return packaged.Value.Where(n => n.Scope.Serves(platform, folder) && (!copies || n.Scope.CopyTfm is not null))
                .Select(n => n.Native).ToList();
        });

        private List<PackagedNative> All => scoped.Value;

        public PackagedNative this[int index] => All[index];
        public int Count => All.Count;
        public IEnumerator<PackagedNative> GetEnumerator() => All.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>Which consumers a packaged native serves (<see cref="SwiftTransitionPackageCheck.Scope"/>).</summary>
/// <param name="CopyTfm">The full target framework of the copy it belongs to; null for a native not keyed by copy.</param>
/// <param name="Platform">The Apple platform of its legacy <c>runtimes/&lt;rid&gt;/</c>; null when shared.</param>
internal sealed record NativeScope(string? CopyTfm, string? Platform)
{
    /// <summary>Whether a consumer of the <c>lib/<paramref name="folder"/>/</c> assembly of <paramref name="platform"/> links it.</summary>
    public bool Serves(string? platform, string? folder) =>
        CopyTfm is not null
            ? folder is not null && string.Equals(CopyTfm, folder, StringComparison.OrdinalIgnoreCase)
            : platform is null || Platform is null || Platform == platform;
}
