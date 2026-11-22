// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.PackageTransitionEvidence.cs — the pack-time transition evidence gate.
//
// Every SwiftBindings package whose assemblies make Swift transitions carries their call plans: each
// assembly embeds a transition manifest (SwiftTransitionManifestFormat), located and authenticated by
// an AssemblyMetadataAttribute, and each native slice of a library the manifests route bridges
// through exports a data symbol holding the manifest's SHA-256. `nuke pack` checks the Runtime and
// Apple packages from their nupkgs alone, with no build tree:
// build/SwiftTransitionGate/PackageTransitionEvidence.cs over every packed assembly (every target
// framework) and every packed native slice, then the generator's linked-bridge check of the runtime's
// slices against the plans its packed assembly embeds. Before the verdict counts, negative controls
// re-run the same check over doctored copies of the package and must go red: a manifest byte changed
// (the hash no longer matches), the manifest hidden (an assembly with bridged transitions and no
// manifest), a bridge twin renamed so its plan is withheld (a rule-R3 violation), and the routing
// requirement over a package without native images. Policy, not baseline: no reseed and no
// --permissive arm.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BindingsGeneration;
using NuGet.Frameworks;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using SwiftBindings.TransitionGate;

partial class Build
{
    /// <summary>
    /// One package the gate checks: its assembly file, the directory its native frameworks are under
    /// (null: anywhere in the package) and the library those frameworks are.
    /// </summary>
    sealed record PackageEvidenceSubject(string Assembly, string? NativeRoot, string Library, bool RoutesBridges);

    static readonly PackageEvidenceSubject RuntimeEvidenceSubject =
        new("Swift.Runtime.dll", RuntimeXcframeworkRoot, RuntimeNativeLibraryName, RoutesBridges: true);

    static readonly PackageEvidenceSubject AppleEvidenceSubject =
        new("SwiftBindings.Apple.dll", "runtimes/native/SBApple.xcframework", "SBApple", RoutesBridges: false);

    /// <summary>
    /// The pack gate over <paramref name="nupkg"/>: its assemblies' transition evidence, the rules
    /// against the plans they embed, and every native slice against the manifests; then the negative
    /// controls. <paramref name="references"/> are packages whose <c>lib/</c> assemblies the checked
    /// ones reference (the Apple supplement references the runtime). Returns the evidence of the
    /// packaged assemblies.
    /// </summary>
    IReadOnlyList<TransitionEvidence> AssertPackageTransitionEvidence(AbsolutePath nupkg, PackageEvidenceSubject subject, params AbsolutePath[] references)
    {
        if (!File.Exists(nupkg))
            Assert.Fail($"Package transition evidence: {nupkg} does not exist.");
        var scratch = (AbsolutePath)Path.Combine(Path.GetTempPath(), $"swift-package-evidence-{Guid.NewGuid():N}");
        scratch.CreateDirectory();
        try
        {
            var package = scratch / "package";
            ZipFile.ExtractToDirectory(nupkg, package);
            var referenced = references.Select((r, i) =>
            {
                var directory = scratch / $"reference-{i}";
                ZipFile.ExtractToDirectory(r, directory);
                return directory;
            }).ToList();

            var assemblies = LoadPackagedAssemblies(package, subject.Assembly, referenced);
            var natives = LoadPackagedNatives(package, subject);
            if (assemblies.Count == 0)
                Assert.Fail($"Package transition evidence: {nupkg} packs no lib/*/{subject.Assembly}.");

            var result = PackageTransitionEvidence.Check(assemblies, natives, subject.RoutesBridges, scratch / "plans");
            foreach (var evidence in result.Evidence)
                Log.Information("[{Package}] {Assembly}: {State}; {Manifests} manifest(s), {Bridges} bridge(s), {Twins} twin(s), {Swift} Swift-convention import(s)",
                    Path.GetFileName(nupkg), RelativeTo(package, evidence.Path), evidence.State, evidence.Manifests.Length,
                    evidence.Manifests.Sum(m => m.Manifest.Bridges.Length), evidence.Census.Inventory.BridgeTwins, evidence.Census.Inventory.SwiftPInvokes);
            Log.Information("[{Package}] {Natives} native slice(s) of {Library} checked against the manifests", Path.GetFileName(nupkg), natives.Count, subject.Library);
            if (!result.Passed)
            {
                foreach (var problem in result.Problems)
                    Log.Error("  {Problem}", problem.Replace(package, "<package>", StringComparison.Ordinal));
                Assert.Fail($"Package transition evidence: {nupkg} fails {result.Problems.Length} check(s) — see the lines above.");
            }

            AssertPackageEvidenceControls(nupkg, subject, assemblies, natives, scratch / "controls");
            Log.Information("Package transition evidence OK — {Package}: {Assemblies} assembly(ies), {Natives} native slice(s)",
                Path.GetFileName(nupkg), assemblies.Count, natives.Count);
            return result.Evidence;
        }
        finally
        {
            if (Directory.Exists(scratch))
                scratch.DeleteDirectory();
        }
    }

    static List<PackagedAssembly> LoadPackagedAssemblies(AbsolutePath package, string assembly, IReadOnlyList<AbsolutePath> references)
    {
        var host = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var lib = package / "lib";
        if (!Directory.Exists(lib))
            return [];
        return Directory.GetFiles(lib, assembly, SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path =>
            {
                var framework = NuGetFramework.ParseFolder(Path.GetFileName(Path.GetDirectoryName(path)!));
                var directories = new[] { Path.GetDirectoryName(path)! }
                    .Concat(references.Select(r => NearestLibDirectory(r, framework)).OfType<string>())
                    .Append(host)
                    .ToArray();
                // In a package of per-toolchain copies, lib/<tfm>/ is held to native/<tfm>/ only.
                var folder = Path.GetFileName(Path.GetDirectoryName(path)!);
                var copy = IsCopyFolder(folder) && Directory.Exists(package / "native" / folder) ? folder : null;
                return new PackagedAssembly(path, File.ReadAllBytes(path), directories, copy);
            })
            .ToList();
    }

    /// <summary>
    /// The <c>lib/</c> folder of a referenced package that a consumer targeting
    /// <paramref name="framework"/> would restore, by NuGet's nearest-framework rule: a binding packed
    /// for <c>net11.0-ios27.2</c> gets the runtime's <c>net10.0-ios26.0</c> assemblies. Null when no
    /// folder is compatible.
    /// </summary>
    static string? NearestLibDirectory(AbsolutePath package, NuGetFramework framework)
    {
        var lib = package / "lib";
        if (!Directory.Exists(lib))
            return null;
        var folders = Directory.GetDirectories(lib).ToDictionary(d => NuGetFramework.ParseFolder(Path.GetFileName(d)), d => d);
        var nearest = new FrameworkReducer().GetNearest(framework, folders.Keys);
        return nearest is null ? null : folders[nearest];
    }

    static List<PackagedNative> LoadPackagedNatives(AbsolutePath package, PackageEvidenceSubject subject)
    {
        var root = subject.NativeRoot is null ? package : package / subject.NativeRoot;
        if (!Directory.Exists(root))
            Assert.Fail($"Package transition evidence: the package holds no {subject.NativeRoot}.");
        return ConsumerTransitionCheck.FrameworkBinaries(root, subject.Library)
            .Select(p => new PackagedNative(subject.Library, p, File.ReadAllBytes(p), NativeCopy(package, p)))
            .ToList();
    }

    /// <summary>The copy whose <c>native/&lt;tfm&gt;/</c> holds <paramref name="path"/>; null for a shared native.</summary>
    static string? NativeCopy(AbsolutePath package, string path)
    {
        var parts = Path.GetRelativePath(package, path).Replace('\\', '/').Split('/');
        return parts.Length >= 3 && parts[0] == "native" && IsCopyFolder(parts[1]) ? parts[1] : null;
    }

    /// <summary>Whether a package folder names a copy: a platform-versioned TFM such as <c>net10.0-ios26.0</c>.</summary>
    static bool IsCopyFolder(string folder)
    {
        var framework = NuGetFramework.ParseFolder(folder);
        return framework.HasPlatform && framework.PlatformVersion > new Version(0, 0, 0, 0);
    }

    /// <summary>
    /// The gate's negative controls: the same check over doctored copies of the package must go red,
    /// each for its own reason, or the gate is not checking what it claims.
    /// </summary>
    static void AssertPackageEvidenceControls(AbsolutePath nupkg, PackageEvidenceSubject subject,
        IReadOnlyList<PackagedAssembly> assemblies, IReadOnlyList<PackagedNative> natives, AbsolutePath scratch)
    {
        // The platform build of the assembly, the one apps load.
        var assembly = assemblies.FirstOrDefault(a => a.DisplayPath.Contains("-ios", StringComparison.Ordinal)) ?? assemblies[0];
        var key = Encoding.UTF8.GetBytes(SwiftTransitionManifestFormat.AttributeKey);
        var suffix = Encoding.UTF8.GetBytes(SwiftTransitionManifestFormat.FileSuffix);
        var reader = SwiftTransitionManifestReader.Read(assembly.Image, assembly.DisplayPath,
            new SwiftTransitionScanOptions { ReferenceDirectories = assembly.ReferenceDirectories });
        var manifest = reader.Manifests[0];
        var at = assembly.Image.AsSpan().IndexOf(manifest.Bytes);
        if (at < 0)
            Assert.Fail($"Package transition evidence control: {assembly.DisplayPath} does not hold its manifest's bytes verbatim.");

        var controls = new List<(string Name, byte[] Image, IReadOnlyList<PackagedNative> Natives, bool Routes, string Expect)>
        {
            // One byte of the manifest changed (an indentation space): the locator's hash no longer matches.
            ("tampered manifest", Patched(assembly.Image, at + 2, (byte)'\t'), natives, subject.RoutesBridges, "hashes to"),
            // The locator and resource renamed: the assembly carries no manifest at all.
            ("missing manifest", Hidden(assembly.Image, key, suffix), natives, subject.RoutesBridges, "transition evidence is Absent"),
        };
        if (subject.RoutesBridges)
        {
            // A bridge twin renamed outside the manifest: its plan is withheld, which rule R3 refuses.
            var twin = Encoding.UTF8.GetBytes(manifest.Manifest.Bridges[0]);
            var renamed = (byte[])twin.Clone();
            renamed[^1] = (byte)(renamed[^1] == (byte)'0' ? '1' : '0');
            controls.Add(("seeded rule violation", ReplacedOutside(assembly.Image, twin, renamed, at, manifest.Bytes.Length), natives, true, " R3 "));
            // The routing requirement over a package without its native images.
            controls.Add(("removed vacuity fixture", assembly.Image, [], true, "holds no native image"));
        }
        else
        {
            // The routing requirement over a package that routes nothing.
            controls.Add(("removed vacuity fixture", assembly.Image, natives, true, "routes no transition through a bridge"));
        }

        foreach (var (name, image, controlNatives, routes, expect) in controls)
        {
            var directory = scratch / name.Replace(' ', '-');
            var result = PackageTransitionEvidence.Check([assembly with { Image = image }], controlNatives, routes, directory);
            var hit = result.Problems.FirstOrDefault(p => p.Contains(expect, StringComparison.Ordinal));
            if (result.Passed || hit is null)
                throw new Exception($"Package transition evidence control '{name}' over {Path.GetFileName(nupkg)} did not go red with '{expect}': " +
                            (result.Passed ? "the check passed" : string.Join("; ", result.Problems.Take(5))));
            Log.Information("  control {Name}: red as expected — {Problem}", name, hit.Replace(assembly.DisplayPath, Path.GetFileName(assembly.DisplayPath), StringComparison.Ordinal));
        }
    }

    static byte[] Patched(byte[] image, int offset, byte value)
    {
        var copy = (byte[])image.Clone();
        if (copy[offset] == value)
            Assert.Fail("Package transition evidence control: the byte to tamper with already holds its tampered value.");
        copy[offset] = value;
        return copy;
    }

    static byte[] Hidden(byte[] image, byte[] key, byte[] suffix)
    {
        var copy = (byte[])image.Clone();
        foreach (var name in new[] { key, suffix })
        {
            var hidden = (byte[])name.Clone();
            hidden[^2] = (byte)'X';
            ReplaceAll(copy, name, hidden, skipFrom: -1, skipLength: 0);
        }
        return copy;
    }

    static byte[] ReplacedOutside(byte[] image, byte[] from, byte[] to, int skipFrom, int skipLength)
    {
        var copy = (byte[])image.Clone();
        if (ReplaceAll(copy, from, to, skipFrom, skipLength) == 0)
            Assert.Fail($"Package transition evidence control: the assembly holds no {Encoding.UTF8.GetString(from)} outside its manifest.");
        return copy;
    }

    static int ReplaceAll(byte[] image, byte[] from, byte[] to, int skipFrom, int skipLength)
    {
        var count = 0;
        for (var at = 0; at <= image.Length - from.Length;)
        {
            var next = image.AsSpan(at).IndexOf(from);
            if (next < 0)
                break;
            var hit = at + next;
            if (hit < skipFrom || hit >= skipFrom + skipLength)
            {
                to.CopyTo(image, hit);
                count++;
            }
            at = hit + from.Length;
        }
        return count;
    }

    static string RelativeTo(AbsolutePath root, string path) => Path.GetRelativePath(root, path);

    /// <summary>
    /// The compile gate's evidence step, over the compile check's assemblies as built: the binding
    /// (both BindingTests modules compiled into CompileCheck.dll), the runtime and the Apple supplement
    /// each embed valid transition manifests, and on a routed platform the binding's manifests route
    /// bridges and every slice of each wrapper they route through holds the manifest hash and exports
    /// exactly those bridges. Fail-closed, no baseline.
    /// </summary>
    IEnumerable<string> CheckCompiledTransitionEvidence(AbsolutePath compileCheckOutput, bool routed)
    {
        var references = new[] { compileCheckOutput.ToString(), System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory() };
        var failed = new List<string>();
        foreach (var name in new[] { "CompileCheck.dll", RuntimeAssemblyName, "SwiftBindings.Apple.dll" })
        {
            var evidence = SwiftTransitionManifestReader.Read(compileCheckOutput / name, new SwiftTransitionScanOptions { ReferenceDirectories = references });
            Log.Information("[{Assembly}] transition evidence: {State}; manifests [{Manifests}]", Path.GetFileNameWithoutExtension(name), evidence.State,
                string.Join(", ", evidence.Manifests.Select(m => $"{m.Resource}: {m.Manifest.Bridges.Length} bridge(s)")));
            foreach (var problem in evidence.Problems)
                Log.Error("  evidence {Problem}", problem);
            if (evidence.State != TransitionEvidenceState.PresentValid)
            {
                failed.Add($"{name} (transition evidence {evidence.State})");
                continue;
            }
            if (name != "CompileCheck.dll")
                continue;
            var modules = evidence.Manifests.Select(m => m.Manifest.Module).Order(StringComparer.Ordinal).ToArray();
            if (!modules.SequenceEqual(new[] { ModuleName, DepModuleName }.Order(StringComparer.Ordinal)))
            {
                Log.Error("  evidence: CompileCheck.dll embeds the manifests of [{Modules}], not of {Module} and {Dependency}", string.Join(", ", modules), ModuleName, DepModuleName);
                failed.Add($"{name} (manifests)");
            }
            if (routed && !evidence.Routing.Any())
            {
                Log.Error("  evidence: no manifest of CompileCheck.dll routes a bridge on a routed platform");
                failed.Add($"{name} (no routing manifest)");
            }
            foreach (var library in evidence.Routing.Select(m => m.Manifest.NativeLibrary!).Distinct(StringComparer.Ordinal))
            {
                var slices = ConsumerTransitionCheck.FrameworkBinaries(BtOutputDir, library).ToList();
                if (slices.Count == 0)
                {
                    Log.Error("  evidence: no framework slice of {Library}, which the manifests route through, under {Output}", library, BtOutputDir);
                    failed.Add($"{name} (no {library} slice)");
                    continue;
                }
                var problems = slices.SelectMany(p => SwiftTransitionManifestReader.CheckNative(evidence, library, File.ReadAllBytes(p), p)).ToList();
                foreach (var problem in problems)
                    Log.Error("  evidence {Problem}", problem);
                if (problems.Count > 0)
                    failed.Add($"{name} ({library} native evidence)");
                else
                    Log.Information("  evidence: {Count} slice(s) of {Library} hold the manifest hash and export exactly its bridges", slices.Count, library);
            }
        }
        return failed;
    }
}
