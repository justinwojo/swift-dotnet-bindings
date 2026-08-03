// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.PackGate.PInvokeGate.cs — P/Invoke-vs-nupkg resolution gate
//
// Every other pack-gate leg asks "is the native artifact we expected in the
// nupkg?" — an expectation written by hand, per fixture, per RID. This leg asks
// the inverse and self-describing question: "does every native library the
// SHIPPED ASSEMBLY names actually exist?" It reflects the P/Invoke library name
// out of each managed assembly in lib/ and resolves it against what the package
// carries plus what the OS supplies, failing on a name nothing can satisfy.
//
// The failure it exists to catch: a binding emitted public session types whose
// [LibraryImport("<Module>Bridge")] named a native library the pack never
// produced. The types were constructible-looking and IntelliSense-visible, the
// generated consumer .targets guarded the NativeReference with Exists() so the
// missing artifact no-oped silently at build time, and the package shipped —
// green through every gate — to throw DllNotFoundException at first use. Nothing
// reflected over P/Invoke attributes, so "emission decided views exist" and
// "pack decided the native exists" were never reconciled.
//
// Deliberately generic: it is keyed on nothing bridge-specific, so any future
// managed surface that names a native we forget to ship fails here too.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

partial class Build
{
    /// <summary>How a P/Invoke library name was (or was not) accounted for.</summary>
    internal enum PackGatePInvokeVerdict
    {
        /// <summary>A native artifact of that name ships in the package for this TFM's RID.</summary>
        Packed,

        /// <summary>The OS supplies it — an Apple SDK framework, /usr/lib, or a statically linked symbol.</summary>
        OsResident,

        /// <summary>
        /// No artifact of that name ships, but the module's generated wrapper does. This is the
        /// static-source shape: the wrapper force-loads the static archive and is the sole runtime
        /// carrier, so pack deliberately drops the source xcframework. Accepted, but always logged —
        /// it is the one verdict where the name a P/Invoke uses is not the name of anything shipped.
        /// </summary>
        WrapperCarried,

        /// <summary>Nothing in the package or on the OS can satisfy the name. Fails the gate.</summary>
        Unresolved,
    }

    internal sealed record PackGatePInvoke(
        string Library, string Tfm, string AssemblyFileName, string DeclaringType, string Method);

    internal sealed record PackGatePInvokeFinding(
        PackGatePInvoke Site, PackGatePInvokeVerdict Verdict, string Detail);

    // Library names that resolve without any filesystem evidence.
    //   __Internal — the symbol is linked into the executable itself (NativeAOT / static link).
    //   libc / libSystem / libobjc — the platform C and Objective-C runtimes, present on every
    //   Apple OS and not exposed as a .framework directory in the SDK's Frameworks folder.
    static readonly string[] PackGateAlwaysResidentLibraries =
    [
        "__Internal", "libc", "libSystem", "libSystem.B.dylib", "libSystem.dylib",
        "libobjc", "libobjc.A.dylib", "libobjc.dylib", "libdl", "libpthread", "libm",
    ];

    // Absolute-path prefixes that belong to the OS image rather than the package.
    static readonly string[] PackGateOsPathPrefixes =
    [
        "/usr/lib/", "/System/Library/", "/System/iOSSupport/",
    ];

    // dyld load-command tokens: the name is a path relative to something already loaded, so the
    // bare framework/dylib name inside it is what has to resolve.
    static readonly string[] PackGateDyldPrefixes =
    [
        "@rpath/", "@executable_path/", "@loader_path/",
    ];

    static IReadOnlyDictionary<string, string[]>? s_packGateAppleFrameworks;

    /// <summary>
    /// Apple system-framework module names mapped to the platforms they are NOT available on, read
    /// from the generator's own <c>apple-frameworks.json</c> registry.
    ///
    /// Deliberately NOT an <c>xcrun</c> probe of the host SDK. The question this gate asks is
    /// "does the package ship what its assemblies name?", and the registry is the exact oracle
    /// that decided those names in the first place — a bare Apple framework only ends up in a
    /// P/Invoke because the generator bound that framework, which it only does for modules listed
    /// here. Probing the host instead would make the pass/fail answer depend on which Xcode is
    /// installed: a name absent from an older SDK would go red on one machine and green on
    /// another, and a name present in the host's newest SDK would be certified even for a TFM
    /// whose deployment floor predates it. A checked-in list is reviewable and moves only when
    /// someone deliberately adds a framework.
    ///
    /// The platform list is what makes acceptance TFM-scoped rather than global: UIKit is real, but
    /// a UIKit P/Invoke sitting in a net10.0-macos assembly resolves to nothing at runtime. Note
    /// the registry annotates unavailability only where it applies, so a module with no entry is
    /// treated as available everywhere — this narrows false greens, it does not claim to catch
    /// every platform mismatch.
    /// </summary>
    static IReadOnlyDictionary<string, string[]> PackGateAppleFrameworks()
    {
        if (s_packGateAppleFrameworks != null)
            return s_packGateAppleFrameworks;

        var registry = RootDirectory / "src" / "Swift.Bindings" / "src" / "Data" / "apple-frameworks.json";
        var frameworks = new Dictionary<string, string[]>(StringComparer.Ordinal);

        using (var doc = JsonDocument.Parse(File.ReadAllText(registry)))
        {
            foreach (var entry in doc.RootElement.GetProperty("frameworks").EnumerateArray())
            {
                if (!entry.TryGetProperty("module", out var module) || module.GetString() is not { } name)
                    continue;

                var unavailable = entry.TryGetProperty("platformUnavailable", out var pu)
                    ? pu.EnumerateArray().Select(p => p.GetString()!).ToArray()
                    : Array.Empty<string>();
                frameworks[name] = unavailable;
            }
        }

        Assert.True(frameworks.Count > 0,
            $"PackGate (pinvoke): {registry} yielded no framework module names. The OS-resident set " +
            "would be empty and every bare Apple framework name would report as unresolved.");

        s_packGateAppleFrameworks = frameworks;
        return frameworks;
    }

    /// <summary>
    /// The <c>apple-frameworks.json</c> platform token for a pack TFM, matching the registry's
    /// schema enum (iOS / tvOS / macOS / MacCatalyst). Null for a TFM with no Apple platform.
    /// </summary>
    static string? PackGateApplePlatformForTfm(string tfm)
    {
        if (tfm.Contains("-maccatalyst", StringComparison.OrdinalIgnoreCase)) return "MacCatalyst";
        if (tfm.Contains("-ios", StringComparison.OrdinalIgnoreCase)) return "iOS";
        if (tfm.Contains("-tvos", StringComparison.OrdinalIgnoreCase)) return "tvOS";
        if (tfm.Contains("-macos", StringComparison.OrdinalIgnoreCase)) return "macOS";
        return null;
    }

    /// <summary>
    /// Reduces a file name to the bare library name a P/Invoke would use:
    /// <c>libFoo.dylib</c> / <c>libFoo.tbd</c> / <c>Foo.dylib</c> -> <c>Foo</c>.
    /// </summary>
    static string PackGateBareLibraryName(string fileName)
    {
        var name = fileName;
        foreach (var ext in new[] { ".dylib", ".tbd", ".a", ".framework", ".xcframework" })
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^ext.Length];
                break;
            }
        }
        if (name.StartsWith("lib", StringComparison.Ordinal) && name.Length > 3)
            name = name[3..];
        return name;
    }

    /// <summary>
    /// The comparable identity of a P/Invoke library name: dyld prefix stripped, reduced to its last
    /// path segment, then to a bare name. '@rpath/Foo.framework/Foo', 'Foo.framework/Foo' and
    /// 'libFoo.dylib' all reduce to 'Foo'.
    ///
    /// Classification and the negative control MUST both go through this: comparing a packaged
    /// file's bare name against a raw ModuleRef would silently stop matching the moment a binding
    /// emits a path-shaped library name.
    /// </summary>
    static string PackGateNormalizeLibraryName(string raw)
    {
        var name = raw;
        foreach (var prefix in PackGateDyldPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        var lastSegment = name.Split('/').Last(s => !string.IsNullOrEmpty(s));
        return PackGateBareLibraryName(lastSegment);
    }

    /// <summary>
    /// Every P/Invoke declared by the assembly at <paramref name="assemblyPath"/>. Walks method
    /// definitions carrying <see cref="MethodAttributes.PinvokeImpl"/> and reads the ModuleRef the
    /// import points at, which covers both hand-written <c>DllImport</c> and the <c>LibraryImport</c>
    /// source generator (whose generated <c>__PInvoke</c> local function is a real IL P/Invoke).
    /// </summary>
    static List<PackGatePInvoke> PackGateCollectPInvokes(string assemblyPath, string tfm)
    {
        var results = new List<PackGatePInvoke>();
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
            return results;

        var md = peReader.GetMetadataReader();
        var fileName = Path.GetFileName(assemblyPath);

        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0)
                continue;

            var import = method.GetImport();
            if (import.Module.IsNil)
                continue;

            var library = md.GetString(md.GetModuleReference(import.Module).Name);
            if (string.IsNullOrWhiteSpace(library))
                continue;

            var declaringType = "<unknown>";
            var typeHandle = method.GetDeclaringType();
            if (!typeHandle.IsNil)
            {
                var type = md.GetTypeDefinition(typeHandle);
                var ns = md.GetString(type.Namespace);
                var name = md.GetString(type.Name);
                declaringType = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            }

            results.Add(new PackGatePInvoke(
                library, tfm, fileName, declaringType, md.GetString(method.Name)));
        }

        return results;
    }

    /// <summary>
    /// Maps a pack TFM (e.g. <c>net10.0-ios26.2</c>) to the NuGet RID the SDK packs native content
    /// under, mirroring <c>_SwiftBindingNuGetRid</c> in Sdk.props. Returns null for a TFM with no
    /// Apple platform (a plain <c>net10.0</c> lib folder ships no native and needs no RID).
    /// </summary>
    static string? PackGateRidForTfm(string tfm)
    {
        // Order matters: "-maccatalyst" must be tested before "-macos" would ever be considered,
        // and neither contains the other, but keep the explicit list so a new TFM fails loudly
        // rather than silently mapping to the wrong RID.
        if (tfm.Contains("-maccatalyst", StringComparison.OrdinalIgnoreCase)) return "maccatalyst-arm64";
        if (tfm.Contains("-ios", StringComparison.OrdinalIgnoreCase)) return "ios-arm64";
        if (tfm.Contains("-tvos", StringComparison.OrdinalIgnoreCase)) return "tvos-arm64";
        if (tfm.Contains("-macos", StringComparison.OrdinalIgnoreCase)) return "osx-arm64";
        return null;
    }

    /// <summary>
    /// Bare native-artifact names the package ships for <paramref name="rid"/>: the xcframework /
    /// framework / dylib / archive entries directly under <c>runtimes/&lt;rid&gt;/native/</c>, plus
    /// anything carried in the pure-ObjC lane's <c>lib/&lt;tfm&gt;/*.resources[.zip]</c> sidecar,
    /// which is where a Microsoft.iOS binding project's NativeReferences ship instead of runtimes/.
    /// </summary>
    static HashSet<string> PackGatePackagedNativeNames(AbsolutePath extractDir, string rid, string tfm)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        var nativeDir = extractDir / "runtimes" / rid / "native";
        if (Directory.Exists(nativeDir))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(nativeDir))
                names.Add(PackGateBareLibraryName(Path.GetFileName(entry)));
        }

        var libDir = extractDir / "lib" / tfm;
        if (Directory.Exists(libDir))
        {
            foreach (var sidecar in Directory.EnumerateFiles(libDir, "*.resources*"))
            {
                try
                {
                    using var archive = ZipFile.OpenRead(sidecar);
                    foreach (var zipEntry in archive.Entries)
                    {
                        // Sidecar entries are paths inside the embedded xcframework; the first
                        // segment is the artifact directory the consumer's build extracts.
                        var first = zipEntry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault();
                        if (!string.IsNullOrEmpty(first))
                            names.Add(PackGateBareLibraryName(first));
                    }
                }
                catch (InvalidDataException)
                {
                    // Not a zip (a plain .resources file) — nothing to enumerate.
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Classifies every P/Invoke in every assembly under <c>lib/</c> of the extracted nupkg at
    /// <paramref name="extractDir"/>. Pure analysis — the caller decides what to do with the result,
    /// which is what lets the negative control run the identical code path against a mutated copy.
    /// </summary>
    static List<PackGatePInvokeFinding> PackGateAnalyzePInvokes(
        AbsolutePath extractDir, IReadOnlyCollection<string>? staticSoleCarrierModules = null)
    {
        var findings = new List<PackGatePInvokeFinding>();
        var libRoot = extractDir / "lib";
        if (!Directory.Exists(libRoot))
            return findings;

        var appleFrameworks = PackGateAppleFrameworks();
        var soleCarrier = new HashSet<string>(
            staticSoleCarrierModules ?? Array.Empty<string>(), StringComparer.Ordinal);

        foreach (var tfmDir in Directory.EnumerateDirectories(libRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var tfm = Path.GetFileName(tfmDir);
            var rid = PackGateRidForTfm(tfm);
            var packagedNatives = rid == null
                ? new HashSet<string>(StringComparer.Ordinal)
                : PackGatePackagedNativeNames(extractDir, rid, tfm);

            foreach (var dll in Directory.EnumerateFiles(tfmDir, "*.dll").OrderBy(f => f, StringComparer.Ordinal))
            {
                foreach (var site in PackGateCollectPInvokes(dll, tfm))
                {
                    findings.Add(PackGateClassify(site, packagedNatives, appleFrameworks, soleCarrier, rid));
                }
            }
        }

        return findings;
    }

    static PackGatePInvokeFinding PackGateClassify(
        PackGatePInvoke site,
        HashSet<string> packagedNatives,
        IReadOnlyDictionary<string, string[]> appleFrameworks,
        HashSet<string> staticSoleCarrierModules,
        string? rid)
    {
        var raw = site.Library;

        // An absolute path names the OS image directly (e.g. /usr/lib/swift/libswiftCore.dylib).
        // Anything absolute that is NOT under an OS prefix points outside both the package and the
        // OS and cannot resolve on a consumer's machine.
        if (raw.StartsWith('/'))
        {
            return PackGateOsPathPrefixes.Any(p => raw.StartsWith(p, StringComparison.Ordinal))
                ? new PackGatePInvokeFinding(site, PackGatePInvokeVerdict.OsResident, $"absolute OS path '{raw}'")
                : new PackGatePInvokeFinding(site, PackGatePInvokeVerdict.Unresolved,
                    $"absolute path '{raw}' is neither packaged nor under an OS-image prefix");
        }

        var bare = PackGateNormalizeLibraryName(raw);

        if (PackGateAlwaysResidentLibraries.Contains(bare, StringComparer.Ordinal)
            || PackGateAlwaysResidentLibraries.Contains(raw, StringComparer.Ordinal))
        {
            return new PackGatePInvokeFinding(site, PackGatePInvokeVerdict.OsResident,
                $"'{raw}' is linked into the image or supplied by the platform runtime");
        }

        if (packagedNatives.Contains(bare))
        {
            return new PackGatePInvokeFinding(site, PackGatePInvokeVerdict.Packed,
                $"runtimes/{rid}/native/ ships '{bare}'");
        }

        if (appleFrameworks.TryGetValue(bare, out var unavailableOn))
        {
            // Being a real Apple framework is not enough — it has to be real on THIS pack's
            // platform. A UIKit P/Invoke in a net10.0-macos assembly names a framework that exists,
            // just not anywhere osx-arm64 can load it, and accepting it globally would certify a
            // pack that cannot run.
            var platform = PackGateApplePlatformForTfm(site.Tfm);
            if (platform != null
                && unavailableOn.Contains(platform, StringComparer.OrdinalIgnoreCase))
            {
                return new PackGatePInvokeFinding(site, PackGatePInvokeVerdict.Unresolved,
                    $"'{bare}' is an Apple system framework, but the framework registry marks it " +
                    $"unavailable on {platform} — nothing under {site.Tfm} can supply it");
            }

            return new PackGatePInvokeFinding(site, PackGatePInvokeVerdict.OsResident,
                $"'{bare}' is an Apple system framework the OS supplies");
        }

        // Static-source carve-out. When the source xcframework is a STATIC archive, the generated
        // wrapper force-loads it and becomes the sole runtime carrier, so pack deliberately drops
        // the source — while the emitted metadata-recovery arm still names the bare module. That
        // recovery arm cannot load (a library name needs a loadable image, and symbol presence in
        // the wrapper is a different question), but it is unreachable on the happy path, so a hard
        // failure here would be permanently red on a deliberate policy.
        //
        // The exemption is opt-in per call site, NOT inferred from "the wrapper is present". A
        // dynamic-source package ships BOTH the source and its wrapper; inferring the carve-out
        // would silently accept a dynamic source that went missing — the very defect class this
        // gate exists for — because its wrapper happened to be there. Callers that know their
        // fixture is static say so; everyone else is fail-closed.
        if (staticSoleCarrierModules.Contains(bare) && packagedNatives.Contains($"{bare}SwiftBindings"))
        {
            return new PackGatePInvokeFinding(site, PackGatePInvokeVerdict.WrapperCarried,
                $"'{bare}' is a declared static-source module: not packed by design, and its wrapper " +
                $"'{bare}SwiftBindings' is the sole carrier");
        }

        var detail = rid == null
            ? $"'{raw}' is not an OS library and the TFM maps to no RID, so the package ships no native for it"
            : $"'{raw}' is neither packaged under runtimes/{rid}/native/ nor supplied by the OS";
        if (staticSoleCarrierModules.Contains(bare))
        {
            // Declared static, but the wrapper that was supposed to carry it is missing too — the
            // carve-out's precondition failed, so say which half is absent rather than reporting a
            // generic miss for a name the caller expected to be exempt.
            detail += $"; it was declared a static-source module but its wrapper " +
                      $"'{bare}SwiftBindings' is not packed either, so nothing carries it";
        }
        return new PackGatePInvokeFinding(site, PackGatePInvokeVerdict.Unresolved, detail);
    }

    /// <summary>
    /// Fails the gate when any assembly in the extracted nupkg names a native library nothing can
    /// supply. Every unresolvable name is reported, not just the first, so one run names the whole
    /// hole rather than one edge of it.
    /// </summary>
    /// <param name="staticSoleCarrierModules">
    /// Modules whose source xcframework the pack deliberately drops because it is a static archive
    /// the wrapper force-loads. Empty for every dynamic-source fixture, which is what keeps a
    /// vanished dynamic source a failure instead of an inferred exemption.
    /// </param>
    static void VerifyPackagePInvokesResolve(
        AbsolutePath extractDir, string label, params string[] staticSoleCarrierModules)
    {
        var findings = PackGateAnalyzePInvokes(extractDir, staticSoleCarrierModules);
        if (findings.Count == 0)
        {
            Log.Information("PackGate (pinvoke/{Label}) — no P/Invoke declarations in lib/; nothing to resolve", label);
            return;
        }

        foreach (var carried in findings.Where(f => f.Verdict == PackGatePInvokeVerdict.WrapperCarried)
                     .DistinctBy(f => (f.Site.Tfm, f.Site.Library)))
        {
            Log.Information("PackGate (pinvoke/{Label}) — {Tfm}: {Detail}", label, carried.Site.Tfm, carried.Detail);
        }

        var unresolved = findings.Where(f => f.Verdict == PackGatePInvokeVerdict.Unresolved).ToList();
        if (unresolved.Count > 0)
        {
            Log.Error("PackGate (pinvoke/{Label}) FAILED — {Count} P/Invoke declaration(s) name a native library the package does not ship:",
                label, unresolved.Count);
            foreach (var group in unresolved.GroupBy(f => (f.Site.Tfm, f.Site.Library))
                         .OrderBy(g => g.Key.Tfm, StringComparer.Ordinal)
                         .ThenBy(g => g.Key.Library, StringComparer.Ordinal))
            {
                var first = group.First();
                Log.Error("  lib/{Tfm}/{Assembly}: [\"{Library}\"] {Detail} — {Count} declaration(s), e.g. {Type}.{Method}",
                    first.Site.Tfm, first.Site.AssemblyFileName, first.Site.Library, first.Detail,
                    group.Count(), first.Site.DeclaringType, first.Site.Method);
            }

            var names = unresolved.Select(f => $"{f.Site.Tfm}:{f.Site.Library}").Distinct()
                .OrderBy(s => s, StringComparer.Ordinal);
            Assert.Fail(
                $"PackGate (pinvoke/{label}): {unresolved.Count} P/Invoke declaration(s) resolve to nothing " +
                $"[{string.Join(", ", names)}]. The package ships managed API that throws " +
                "DllNotFoundException at first use.");
        }

        var byVerdict = findings.GroupBy(f => f.Verdict)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Site.Library).Distinct().Count());
        Log.Information(
            "PackGate (pinvoke/{Label}) OK — {Sites} P/Invoke declaration(s) across {Libs} librar(ies) all resolve " +
            "(packed: {Packed}, OS-resident: {Os}, wrapper-carried: {Carried})",
            label, findings.Count, findings.Select(f => f.Site.Library).Distinct().Count(),
            byVerdict.GetValueOrDefault(PackGatePInvokeVerdict.Packed),
            byVerdict.GetValueOrDefault(PackGatePInvokeVerdict.OsResident),
            byVerdict.GetValueOrDefault(PackGatePInvokeVerdict.WrapperCarried));
    }

    /// <summary>
    /// Red/green proof that the resolver above can actually fail. Copies the real extracted nupkg,
    /// deletes one native artifact the shipped assemblies genuinely P/Invoke, and asserts the
    /// analysis reports exactly that library as unresolved for exactly the RID it was removed from.
    ///
    /// Mutating a real pack rather than building a second, deliberately-broken one is the point:
    /// it runs the identical code path over real assemblies with real IL, costs no extra pack, and
    /// reproduces the shipping shape precisely — an assembly whose P/Invoke names a native the
    /// package does not contain. A gate with no proof it can go red is a gate nobody can trust.
    /// </summary>
    static void VerifyPInvokeGateDetectsMissingNative(
        AbsolutePath extractDir, AbsolutePath scratch, string label,
        params string[] staticSoleCarrierModules)
    {
        var mutated = scratch / "pinvoke-negative-control";
        if (Directory.Exists(mutated)) mutated.DeleteDirectory();
        extractDir.Copy(mutated);

        // Pick a native that some shipped assembly actually names: analyse the pristine copy and
        // take a Packed verdict. Deriving the victim instead of hardcoding one keeps the control
        // valid if the fixture's module or wrapper naming ever changes.
        var baseline = PackGateAnalyzePInvokes(mutated, staticSoleCarrierModules);
        var victim = baseline
            .Where(f => f.Verdict == PackGatePInvokeVerdict.Packed)
            .OrderBy(f => f.Site.Tfm, StringComparer.Ordinal)
            .ThenBy(f => f.Site.Library, StringComparer.Ordinal)
            .FirstOrDefault();

        if (victim == null)
        {
            Assert.Fail(
                $"PackGate (pinvoke/{label}) negative control: the pristine package has no packaged-native " +
                "P/Invoke to remove, so the control cannot prove the gate goes red. Either the fixture " +
                "stopped shipping native content or P/Invoke collection stopped seeing it — both are failures.");
            return;
        }

        var victimBare = PackGateNormalizeLibraryName(victim.Site.Library);
        var rid = PackGateRidForTfm(victim.Site.Tfm)!;
        var nativeDir = mutated / "runtimes" / rid / "native";
        var removed = Directory.EnumerateFileSystemEntries(nativeDir)
            .Where(e => string.Equals(PackGateBareLibraryName(Path.GetFileName(e)), victimBare,
                StringComparison.Ordinal))
            .ToList();

        foreach (var entry in removed)
        {
            if (Directory.Exists(entry)) ((AbsolutePath)entry).DeleteDirectory();
            else File.Delete(entry);
        }

        var after = PackGateAnalyzePInvokes(mutated, staticSoleCarrierModules);
        var tripped = after
            .Where(f => f.Verdict == PackGatePInvokeVerdict.Unresolved
                        && f.Site.Tfm == victim.Site.Tfm
                        && f.Site.Library == victim.Site.Library)
            .ToList();

        if (tripped.Count == 0)
        {
            Assert.Fail(
                $"PackGate (pinvoke/{label}) negative control: removed " +
                $"runtimes/{rid}/native/{victim.Site.Library}.* but the resolver still reported no unresolved " +
                $"P/Invoke for '{victim.Site.Library}' on {victim.Site.Tfm}. The gate cannot detect a missing " +
                "native and would pass the exact defect it exists to catch.");
        }

        // Removing one RID's native must not implicate the others: the check is per-TFM, which is
        // what makes it catch a native that ships for some platforms and not the one an assembly
        // was compiled for.
        var collateral = after
            .Where(f => f.Verdict == PackGatePInvokeVerdict.Unresolved && f.Site.Tfm != victim.Site.Tfm)
            .Select(f => $"{f.Site.Tfm}:{f.Site.Library}")
            .Distinct()
            .ToList();
        if (collateral.Count > 0)
        {
            Assert.Fail(
                $"PackGate (pinvoke/{label}) negative control: removing {victim.Site.Library} from " +
                $"runtimes/{rid}/ also marked [{string.Join(", ", collateral)}] unresolved on other TFMs — " +
                "resolution is not per-RID and would misreport which platform is actually broken.");
        }

        // When the artifact just removed was a declared static module's sole carrier, that module's
        // own P/Invokes must lose their exemption as well. The wrapper going red does not by itself
        // prove the carve-out is conditional: an exemption that skipped the carrier check would
        // accept the bare module unconditionally and still pass everything above.
        var orphaned = staticSoleCarrierModules.FirstOrDefault(
            m => string.Equals($"{m}SwiftBindings", victimBare, StringComparison.Ordinal));
        var exemptionProven = false;
        if (orphaned != null
            && baseline.Any(f => f.Verdict == PackGatePInvokeVerdict.WrapperCarried
                                 && f.Site.Tfm == victim.Site.Tfm
                                 && PackGateNormalizeLibraryName(f.Site.Library) == orphaned))
        {
            var stillExempt = after
                .Where(f => f.Site.Tfm == victim.Site.Tfm
                            && PackGateNormalizeLibraryName(f.Site.Library) == orphaned
                            && f.Verdict != PackGatePInvokeVerdict.Unresolved)
                .Select(f => $"{f.Verdict}:{f.Site.Method}")
                .Distinct()
                .ToList();
            if (stillExempt.Count > 0)
            {
                Assert.Fail(
                    $"PackGate (pinvoke/{label}) negative control: '{orphaned}' kept a non-unresolved verdict " +
                    $"[{string.Join(", ", stillExempt)}] on {victim.Site.Tfm} after its sole carrier " +
                    $"'{victimBare}' was removed. The static-source exemption is unconditional and would " +
                    "green-light a package that ships neither the source nor its wrapper.");
            }

            exemptionProven = true;
        }

        mutated.DeleteDirectory();

        Log.Information(
            "PackGate (pinvoke/{Label}) negative control OK — removing runtimes/{Rid}/native/{Victim} produced " +
            "{Count} unresolved declaration(s) on {Tfm} and none elsewhere{Exemption}",
            label, rid, victimBare, tripped.Count, victim.Site.Tfm,
            exemptionProven ? $", and revoked the static-source exemption for '{orphaned}'" : string.Empty);
    }
}
