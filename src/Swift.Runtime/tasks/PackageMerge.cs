// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Xml.Linq;
using SwiftBindings.TransitionGate;

namespace SwiftBindings.Tasks;

/// <summary>The outcome of <see cref="SwiftBindingPackageMerge.Merge"/>.</summary>
/// <param name="Package">The merged package, staged beside <paramref name="Destination"/> until it is checked; null when refused.</param>
/// <param name="Destination">Where <see cref="SwiftBindingPackageMerge.Publish"/> moves the checked package.</param>
/// <param name="Copies">Its copies, in order.</param>
/// <param name="Problems">Why the contributions were refused; empty when merged.</param>
/// <param name="Notes">What the merge did, for the log.</param>
/// <param name="PathBudget">Entries past the Windows restore path budget (SWIFTBIND140): warnings, or with
/// <c>requireWindowsPathBudget</c> the reason the merge was refused.</param>
public sealed record PackageMergeResult(string? Package, string? Destination, ImmutableArray<string> Copies, ImmutableArray<string> Problems, ImmutableArray<string> Notes,
    ImmutableArray<string> PathBudget);

/// <summary>The merge of per-toolchain binding package contributions, free of MSBuild.</summary>
public static class SwiftBindingPackageMerge
{
    private const int WindowsMaxPath = 260;
    private const int AssumedMaxUserProfile = 40;

    private sealed record Contribution(string Path, string Id, string Version, XDocument Nuspec, string NuspecName,
        Dictionary<string, byte[]> Files, ImmutableArray<string> Copies, ImmutableDictionary<string, string> Payloads);

    private sealed record Copy(string Tfm, Version Net, string Platform, Version PlatformVersion, Contribution From)
    {
        /// <summary>Where this copy's natives are, as its declaration states (<see cref="PackageCopyDeclarationEmitter"/>).</summary>
        public string Payload => From.Payloads[Tfm];
    }

    private const string PayloadNative = BindingsGeneration.PackageCopyDeclarationEmitter.NativePayloadNative;
    private const string PayloadNone = BindingsGeneration.PackageCopyDeclarationEmitter.NativePayloadNone;
    private const string PayloadResources = BindingsGeneration.PackageCopyDeclarationEmitter.NativePayloadResources;

    /// <summary>
    /// Merges <paramref name="contributions"/> into one package carrying exactly
    /// <paramref name="expectedTfms"/>, written to <paramref name="outputDirectory"/>. Each contribution
    /// must be a package of the copy layout (<c>lib/&lt;tfm&gt;/</c>, <c>buildTransitive/&lt;tfm&gt;/</c>
    /// with the copy's declaration, <c>native/&lt;tfm&gt;/</c>, the same set of TFMs in each, no
    /// <c>runtimes/</c>); all must share one id, version, SwiftBindings SDK and metadata; together they
    /// carry each expected TFM exactly once; and of two copies of one platform, the newer's public API
    /// and native exports contain the older's, since a library compiles against the oldest copy while
    /// the app that consumes it runs the newest.
    /// </summary>
    public static PackageMergeResult Merge(IReadOnlyList<string> contributions, IReadOnlyList<string> expectedTfms, string outputDirectory,
        bool requireWindowsPathBudget = false)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        var pathBudget = new List<string>();
        var read = new List<Contribution>();
        if (contributions.Count == 0)
            problems.Add("No contribution packages were given.");
        if (expectedTfms.Count == 0)
            problems.Add("No expected target frameworks were given: name the matrix the merged package must carry.");
        foreach (var path in contributions)
        {
            var contribution = Read(path, problems);
            if (contribution is not null)
                read.Add(contribution);
        }
        if (problems.Count > 0 || read.Count == 0)
            return Refused(problems, notes);

        var first = read[0];
        foreach (var c in read.Skip(1))
        {
            if (!string.Equals(c.Id, first.Id, StringComparison.OrdinalIgnoreCase) || c.Version != first.Version)
                problems.Add($"{Name(c)} is {c.Id} {c.Version}, but {Name(first)} is {first.Id} {first.Version}: every contribution must be the same package id and version.");
            foreach (var difference in MetadataDifferences(first, c))
                problems.Add($"{Name(c)} and {Name(first)} differ in their nuspec <{difference}>: contributions must come from one source revision and project, differing only in their copies.");
        }

        // Each TFM exactly once, and exactly the expected matrix.
        var owner = new Dictionary<string, Contribution>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in read)
            foreach (var tfm in c.Copies)
            {
                if (owner.TryGetValue(tfm, out var other))
                    problems.Add($"{tfm} is carried by both {Name(other)} and {Name(c)}: each copy must come from exactly one contribution.");
                else
                    owner[tfm] = c;
            }
        var expected = new HashSet<string>(expectedTfms, StringComparer.OrdinalIgnoreCase);
        foreach (var missing in expected.Where(t => !owner.ContainsKey(t)).Order(StringComparer.Ordinal))
            problems.Add($"No contribution carries the expected copy {missing}: build it on its toolchain and pass its package to the merge.");
        foreach (var extra in owner.Keys.Where(t => !expected.Contains(t)).Order(StringComparer.Ordinal))
            problems.Add($"{Name(owner[extra])} carries {extra}, which is not in the expected matrix ({string.Join(", ", expectedTfms)}).");

        // One SwiftBindings SDK across every copy: the runtime contract epoch and the dependency on the
        // runtime derive from it.
        var generators = owner.Values.Distinct().SelectMany(c => c.Copies.Select(t => (Tfm: t, Generator: DeclarationValue(c, t, "Generator") ?? "")))
            .GroupBy(g => g.Generator, StringComparer.Ordinal).ToList();
        if (generators.Count > 1)
            problems.Add("The copies were packed with different SwiftBindings SDK versions ("
                         + string.Join("; ", generators.Select(g => $"{(g.Key.Length == 0 ? "unrecorded" : g.Key)}: {string.Join(", ", g.Select(x => x.Tfm))}"))
                         + "): pack every contribution with the same SDK.");
        // One kind of binding across every copy: where a copy's natives are follows from the binding's
        // kind (Swift or mixed, pure Objective-C of an Apple framework, pure Objective-C of a library).
        var payloads = owner.Select(kv => (Tfm: kv.Key, Payload: kv.Value.Payloads[kv.Key]))
            .GroupBy(p => p.Payload, StringComparer.Ordinal).ToList();
        if (payloads.Count > 1)
            problems.Add("The copies declare different native payloads ("
                         + string.Join("; ", payloads.Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Tfm).Order(StringComparer.Ordinal))}"))
                         + "): every copy of one binding ships its natives the same way, so the contributions are not one binding.");
        if (problems.Count > 0)
            return Refused(problems, notes);

        var copies = owner.Select(kv => ToCopy(kv.Key, kv.Value)).OrderBy(c => c.Platform, StringComparer.Ordinal)
            .ThenBy(c => c.Net).ThenBy(c => c.PlatformVersion).ToList();
        foreach (var copy in copies)
        {
            notes.Add($"  {copy.Tfm}: {Name(copy.From)} (built on {DeclarationValue(copy.From, copy.Tfm, "Toolchain") ?? "an unrecorded toolchain"})");
            problems.AddRange(WitnessedSwiftDeclarations(copy));
        }

        // Newer copies contain older ones: API and native exports, per platform.
        foreach (var platform in copies.GroupBy(c => c.Platform))
        {
            var ordered = platform.ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                problems.AddRange(ApiSuperset(ordered[i - 1], ordered[i], notes));
                problems.AddRange(SameCompiledInValues(ordered[i - 1], ordered[i], notes));
                problems.AddRange(ExportSuperset(ordered[i - 1], ordered[i], notes));
            }
            // An archive need not define what an older copy's archive gave a link only from a member
            // that does not read (LLVM bitcode), so a symbol can pass one copy and be lost by the next:
            // each archive is compared with every older copy's, not only the previous one's.
            for (var i = 0; i < ordered.Count; i++)
                for (var j = i + 2; j < ordered.Count; j++)
                    problems.AddRange(ExportSuperset(ordered[i], ordered[j], [], archivesOnly: true));
            // A library compiled against any copy may run against any newer one, and a C# name can
            // move to another Swift declaration and back, so every pair is compared.
            for (var i = 0; i < ordered.Count; i++)
                for (var j = i + 1; j < ordered.Count; j++)
                    problems.AddRange(SameSwiftDeclarations(ordered[i], ordered[j], notes));
        }

        // Shared files (outside the copies and the package's own parts) must agree; the readme is the
        // newest copy's (its surface contains every older one's).
        var newest = copies.OrderBy(c => c.Net).ThenBy(c => c.PlatformVersion).Last().From;
        var readme = first.Nuspec.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "readme")?.Value.Replace('\\', '/');
        var output = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var c in read)
            foreach (var (name, data) in c.Files)
            {
                if (IsCopyPath(name, out _) || IsPackagePart(name, c))
                {
                    if (IsCopyPath(name, out _))
                        output[name] = data;
                    continue;
                }
                if (readme is not null && string.Equals(name, readme, StringComparison.OrdinalIgnoreCase))
                {
                    if (c == newest)
                        output[name] = data;
                    continue;
                }
                if (output.TryGetValue(name, out var existing) && !existing.AsSpan().SequenceEqual(data))
                    problems.Add($"{name} differs between contributions ({Name(c)}): a file outside the copies must be the same in every contribution.");
                else
                    output[name] = data;
            }
        if (readme is not null && !output.ContainsKey(readme))
            output[readme] = first.Files.TryGetValue(readme, out var r) ? r : [];

        // The Windows restore path budget, for the real id and version. An entry past it restores with
        // the dotnet CLI, which handles long paths, but not in a Visual Studio restore, which stops at
        // MAX_PATH: a warning naming the longest user profile that still fits, or with
        // requireWindowsPathBudget, a refusal. Packages of long Apple framework ids are past it in the
        // single-copy layout too, so refusing by default would refuse those ids outright.
        var stem = $"{first.Id}.{first.Version}";
        var budget = WindowsRestoreEntryBudget(stem.Length, AssumedMaxUserProfile);
        var longest = output.Keys.Append(first.NuspecName).MaxBy(k => k.Length)!;
        if (longest.Length > budget)
        {
            var fits = AssumedMaxUserProfile - (longest.Length - budget);
            var finding = $"{longest} is {longest.Length} characters, past the Windows restore budget of {budget} for {stem} "
                          + $"(a {AssumedMaxUserProfile}-character user profile under the 260-character MAX_PATH). "
                          + (fits > 0
                              ? $"A Visual Studio restore on Windows fails for a user profile (C:\\Users\\<name>) longer than {fits} characters; "
                              : "A Visual Studio restore on Windows fails for every user profile; ")
                          + "the dotnet CLI restores it on any profile.";
            pathBudget.Add(requireWindowsPathBudget ? finding : finding + " Set SwiftBindingsMergeRequireWindowsPathBudget=true to refuse such a merge.");
        }
        else
            notes.Add($"  Windows path budget: longest entry {longest.Length}/{budget} ({longest}).");
        if (problems.Count > 0 || (requireWindowsPathBudget && pathBudget.Count > 0))
            return Refused(problems, notes) with { PathBudget = pathBudget.ToImmutableArray() };

        var nuspec = MergedNuspec(first, copies, problems);
        var destination = Path.GetFullPath(Path.Combine(outputDirectory, $"{stem}.nupkg"));
        // Compared by real path (symlinked directories resolved, as /tmp is on macOS) and without
        // case: the default Apple and Windows volumes are case-insensitive, so a differently cased
        // name is the same file. Refusing a distinct file on a case-sensitive volume costs only a rename.
        var real = RealPath(destination);
        foreach (var c in read.Where(c => string.Equals(RealPath(c.Path), real, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"The merge would replace the contribution {c.Path} with the merged package {destination}: write it to another directory (SwiftBindingsMergeOutput).");
        if (problems.Count > 0)
            return Refused(problems, notes);

        // Staged under a name of this merge's own: nothing at the destination changes until the merged
        // package has passed its checks (Publish), and no other merge or contribution shares the name.
        Directory.CreateDirectory(outputDirectory);
        var target = Path.Combine(outputDirectory, $"{stem}.{Guid.NewGuid():N}.merging.nupkg");
        using (var zip = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            Write(zip, first.NuspecName, Encoding.UTF8.GetBytes(nuspec));
            foreach (var part in first.Files.Keys.Where(k => k.StartsWith("_rels/", StringComparison.Ordinal) || k.StartsWith("package/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
                Write(zip, part, first.Files[part]);
            Write(zip, "[Content_Types].xml", Encoding.UTF8.GetBytes(ContentTypes(read, output.Keys)));
            foreach (var (name, data) in output)
                Write(zip, name, data);
        }
        return new PackageMergeResult(target, destination, copies.Select(c => c.Tfm).ToImmutableArray(), [], notes.ToImmutableArray(), pathBudget.ToImmutableArray());
    }

    /// <summary>Moves a merged package that passed its checks from its staging name to its destination.</summary>
    public static string Publish(PackageMergeResult result)
    {
        File.Move(result.Package!, result.Destination!, overwrite: true);
        return result.Destination!;
    }

    /// <summary><paramref name="path"/> with every symbolic link along it resolved.</summary>
    private static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } resolved)
                current = RealPath(resolved.FullName);
        }
        return current;
    }

    private static bool IsPlainName(string value) =>
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or '+') && !value.Contains("..", StringComparison.Ordinal);

    private static PackageMergeResult Refused(List<string> problems, List<string> notes) =>
        new(null, null, [], problems.ToImmutableArray(), notes.ToImmutableArray(), []);

    /// <summary>
    /// The longest package-relative entry that restores under MAX_PATH (259 usable characters) at
    /// <c>C:\Users\&lt;profile&gt;\.nuget\packages\&lt;id&gt;\&lt;version&gt;\</c>: NuGet writes each entry
    /// there directly, and the folder is as long as the <c>&lt;id&gt;.&lt;version&gt;</c> stem.
    /// </summary>
    public static int WindowsRestoreEntryBudget(int stemLength, int userProfileLength) =>
        WindowsMaxPath - 1 - (@"C:\Users\".Length + userProfileLength + @"\.nuget\packages\".Length + stemLength + 1);

    private static string Name(Contribution c) => System.IO.Path.GetFileName(c.Path);

    private static Contribution? Read(string path, List<string> problems)
    {
        if (!File.Exists(path))
        {
            problems.Add($"The contribution {path} does not exist.");
            return null;
        }
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                    continue;
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                files[entry.FullName.Replace('\\', '/')] = buffer.ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            problems.Add($"{System.IO.Path.GetFileName(path)} does not read as a package: {ex.Message}");
            return null;
        }
        var name = System.IO.Path.GetFileName(path);
        var nuspecs = files.Keys.Where(k => !k.Contains('/') && k.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)).ToList();
        if (nuspecs.Count != 1)
        {
            problems.Add($"{name} holds {nuspecs.Count} nuspec files at its root; a package holds one.");
            return null;
        }
        if (files.ContainsKey(".signature.p7s"))
        {
            problems.Add($"{name} is signed; merge unsigned contributions and sign the merged package.");
            return null;
        }
        XDocument nuspec;
        try
        {
            nuspec = XDocument.Parse(Encoding.UTF8.GetString(files[nuspecs[0]]).TrimStart('\uFEFF'));
        }
        catch (System.Xml.XmlException ex)
        {
            problems.Add($"{name}'s nuspec does not read: {ex.Message}");
            return null;
        }
        var metadata = nuspec.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        var id = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "id")?.Value ?? "";
        var version = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "version")?.Value ?? "";
        if (id.Length == 0 || version.Length == 0)
        {
            problems.Add($"{name}'s nuspec names no id or version.");
            return null;
        }
        // The id and version name the merged package's file, so they must be plain names (NuGet's
        // grammar allows neither separators nor a parent segment).
        if (!IsPlainName(id) || !IsPlainName(version))
        {
            problems.Add($"{name}'s nuspec id '{id}' or version '{version}' is not a plain package name.");
            return null;
        }

        var lib = new SortedSet<string>(StringComparer.Ordinal);
        var build = new SortedSet<string>(StringComparer.Ordinal);
        var native = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files.Keys)
        {
            var parts = file.Split('/');
            switch (parts[0])
            {
                case "lib" when parts.Length >= 3:
                    lib.Add(parts[1]);
                    break;
                case "buildTransitive" when parts.Length >= 3:
                    build.Add(parts[1]);
                    break;
                case "buildTransitive":
                    problems.Add($"{name} holds {file}, a buildTransitive/ file outside any copy: every consumer file of a binding package belongs to its copy's buildTransitive/<tfm>/.");
                    break;
                case "native" when parts.Length >= 3:
                    native.Add(parts[1]);
                    break;
                case "runtimes":
                case "build":
                case "native":
                    problems.Add($"{name} holds {file}: a binding package of the copy layout ships natives only under native/<tfm>/ and consumer files only under buildTransitive/<tfm>/ (pack it with the current SwiftBindings.Sdk).");
                    break;
            }
        }
        foreach (var tfm in lib.Concat(build).Concat(native).Distinct().Where(t => !PackageCopyCheck.IsCopyTfm(t)))
            problems.Add($"{name} holds a copy folder '{tfm}', which is not a platform-versioned target framework.");
        if (lib.Count == 0)
            problems.Add($"{name} carries no lib/<tfm>/ copy.");
        if (!lib.SetEquals(build))
            problems.Add($"{name} carries lib/ [{string.Join(", ", lib)}] and buildTransitive/ [{string.Join(", ", build)}]: "
                         + "NuGet selects lib/ and buildTransitive/ separately, so every copy must appear in both.");
        var payloads = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var tfm in lib)
        {
            var declaration = $"buildTransitive/{tfm}/{id}.props";
            if (!files.TryGetValue(declaration, out var bytes))
            {
                problems.Add($"{name} carries no copy declaration {declaration}.");
                continue;
            }
            var declared = Declaration(bytes, "CopyTfm");
            if (!string.Equals(declared, tfm, StringComparison.Ordinal))
                problems.Add($"{name}: {declaration} declares the copy {declared ?? "(none)"}, not {tfm}.");
            // A declaration from before the field means native/<tfm>/.
            var payload = Declaration(bytes, "NativePayload") is { Length: > 0 } p ? p.Trim() : PayloadNative;
            payloads[tfm] = payload;
            var declaresTargets = Declaration(bytes, "TargetsFile") is not { } t || t.Trim().Length > 0;
            foreach (var problem in PayloadProblems(name, files, tfm, payload, native.Contains(tfm)))
                problems.Add(problem);
            var targets = $"buildTransitive/{tfm}/{id}.targets";
            if (!files.TryGetValue(targets, out var targetsBytes))
            {
                // Swift and mixed copies reach their natives only through their targets.
                if (declaresTargets || payload == PayloadNative)
                    problems.Add($"{name} carries no consumer targets {targets}.");
            }
            else
            {
                if (!declaresTargets)
                    problems.Add($"{name}: {declaration} declares that the copy ships no consumer targets, but it carries {targets}.");
                foreach (var problem in PackageCopyRules.CheckTargetsReferences(tfm, Encoding.UTF8.GetString(targetsBytes), payload))
                    problems.Add($"{name}: {targets} {problem}; each copy's targets must reference only its own native/{tfm}/.");
            }
        }
        // native/ holds exactly the copies whose natives are there.
        foreach (var tfm in native.Where(t => !lib.Contains(t)))
            problems.Add($"{name} carries native/{tfm}/ with no lib/{tfm}/ copy: NuGet selects lib/ and buildTransitive/ together, so natives of no copy would never be linked.");
        return new Contribution(path, id, version, nuspec, nuspecs[0], files, lib.ToImmutableArray(), payloads.ToImmutable());
    }

    /// <summary>
    /// What is wrong with the natives of the copy <paramref name="tfm"/> for its declared
    /// <paramref name="payload"/>: under <c>native/&lt;tfm&gt;/</c> exactly when it declares them there, a
    /// resource package in <c>lib/&lt;tfm&gt;/</c> exactly when it declares one, and nothing for a copy
    /// that declares no natives.
    /// </summary>
    private static IEnumerable<string> PayloadProblems(string name, Dictionary<string, byte[]> files, string tfm, string payload, bool hasNativeFolder)
    {
        if (!BindingsGeneration.PackageCopyDeclarationEmitter.IsNativePayload(payload))
        {
            yield return $"{name}: buildTransitive/{tfm}/ declares the native payload '{payload}', which is not one a copy can declare (native, none or resources).";
            yield break;
        }
        var resources = ResourcePackages(files, tfm).ToList();
        if (payload == PayloadNative && !hasNativeFolder)
            yield return $"{name} declares its {tfm} copy's natives under native/{tfm}/, but carries no native/{tfm}/: every copy must appear in lib/, buildTransitive/ and native/.";
        if (payload != PayloadNative && hasNativeFolder)
            yield return $"{name} declares its {tfm} copy's native payload '{payload}', but carries native/{tfm}/: a copy's declaration must state where its natives are.";
        if (payload == PayloadResources && resources.Count == 0)
            yield return $"{name} declares that its {tfm} copy's natives travel in lib/{tfm}/ as the binding's resource package, but lib/{tfm}/ carries no <Assembly>.resources.zip or <Assembly>.resources/.";
        foreach (var resource in resources.Where(r => !files.ContainsKey($"lib/{tfm}/{ResourceAssembly(r)}.dll")))
            yield return $"{name}: lib/{tfm}/{resource} is no binding assembly's resource package: lib/{tfm}/ carries no {ResourceAssembly(resource)}.dll beside it.";
        if (payload != PayloadResources && resources.Count > 0)
            yield return $"{name} declares its {tfm} copy's native payload '{payload}', but lib/{tfm}/ carries {resources[0]}: a copy's declaration must state where its natives are.";
    }

    /// <summary>The binding resource packages (<c>&lt;Assembly&gt;.resources.zip</c> or <c>&lt;Assembly&gt;.resources/</c>) of <c>lib/&lt;tfm&gt;/</c>.</summary>
    private static IEnumerable<string> ResourcePackages(Dictionary<string, byte[]> files, string tfm)
    {
        var prefix = $"lib/{tfm}/";
        return files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k[prefix.Length..].Split('/'))
            .Where(parts => (parts.Length == 1 && parts[0].EndsWith(".resources.zip", StringComparison.OrdinalIgnoreCase))
                            || (parts.Length > 1 && parts[0].EndsWith(".resources", StringComparison.OrdinalIgnoreCase)))
            .Select(parts => parts.Length == 1 ? parts[0] : parts[0] + "/")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
    }

    /// <summary>The assembly name a resource package (<c>&lt;Assembly&gt;.resources.zip</c> or <c>&lt;Assembly&gt;.resources/</c>) belongs to.</summary>
    private static string ResourceAssembly(string resource) =>
        resource.EndsWith(".resources.zip", StringComparison.OrdinalIgnoreCase) ? resource[..^".resources.zip".Length]
        : resource.TrimEnd('/')[..^".resources".Length];

    private static string? DeclarationValue(Contribution c, string tfm, string metadata) =>
        c.Files.TryGetValue($"buildTransitive/{tfm}/{c.Id}.props", out var bytes) ? Declaration(bytes, metadata) : null;

    private static string? Declaration(byte[] props, string metadata)
    {
        try
        {
            var doc = XDocument.Parse(Encoding.UTF8.GetString(props).TrimStart('\uFEFF'));
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "SwiftBindingPackageCopy")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == metadata)?.Value;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static Copy ToCopy(string tfm, Contribution from)
    {
        PackageCopyCheck.TryParse(tfm, out var net, out var platform, out var version);
        return new Copy(tfm, net, platform, version ?? new Version(0, 0), from);
    }

    private static bool IsCopyPath(string name, out string tfm)
    {
        var parts = name.Split('/');
        tfm = parts.Length >= 3 ? parts[1] : "";
        return parts.Length >= 3 && parts[0] is "lib" or "buildTransitive" or "native";
    }

    private static bool IsPackagePart(string name, Contribution c) =>
        name == c.NuspecName || name == "[Content_Types].xml"
        || name.StartsWith("_rels/", StringComparison.Ordinal) || name.StartsWith("package/", StringComparison.Ordinal);

    /// <summary>The nuspec metadata elements, other than the per-copy groups, that differ.</summary>
    private static IEnumerable<string> MetadataDifferences(Contribution a, Contribution b)
    {
        static Dictionary<string, string> Shared(Contribution c) =>
            (c.Nuspec.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata")?.Elements() ?? [])
                .Where(e => e.Name.LocalName is not ("dependencies" or "frameworkReferences" or "frameworkAssemblies"))
                .GroupBy(e => e.Name.LocalName)
                .ToDictionary(g => g.Key, g => string.Join("\n", g.Select(e => e.ToString(SaveOptions.DisableFormatting))));
        var left = Shared(a);
        var right = Shared(b);
        return left.Keys.Union(right.Keys).Where(k => !left.TryGetValue(k, out var l) || !right.TryGetValue(k, out var r) || l != r).Order(StringComparer.Ordinal);
    }

    /// <summary>The first contribution's nuspec with every contribution's per-copy groups, one per copy, never unioned across copies.</summary>
    private static string MergedNuspec(Contribution first, List<Copy> copies, List<string> problems)
    {
        var doc = new XDocument(first.Nuspec);
        var metadata = doc.Root!.Elements().First(e => e.Name.LocalName == "metadata");
        foreach (var section in new[] { "dependencies", "frameworkReferences", "frameworkAssemblies" })
        {
            var groups = new List<XElement>();
            var any = false;
            foreach (var copy in copies)
            {
                var element = copy.From.Nuspec.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata")?
                    .Elements().FirstOrDefault(e => e.Name.LocalName == section);
                if (element is null)
                {
                    // Every copy depends on the runtime, so a copy without a dependency group would
                    // restore another copy's.
                    if (section == "dependencies")
                        problems.Add($"{Name(copy.From)}'s nuspec has no dependency group for {copy.Tfm}; a copy has exactly one.");
                    continue;
                }
                any = true;
                if (element.Elements().Any(e => e.Name.LocalName != "group"))
                {
                    problems.Add($"{Name(copy.From)}'s nuspec <{section}> holds entries outside a target-framework group; each copy's must be grouped by its target framework.");
                    continue;
                }
                var mine = element.Elements().Where(g => SameFramework(g.Attribute("targetFramework")?.Value, copy)).ToList();
                if (section == "dependencies" && mine.Count != 1)
                    problems.Add($"{Name(copy.From)}'s nuspec has {mine.Count} dependency group(s) for {copy.Tfm}; a copy has exactly one.");
                groups.AddRange(mine.Select(g => new XElement(g)));
            }
            var existing = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == section);
            if (!any)
            {
                existing?.Remove();
                continue;
            }
            if (existing is null)
            {
                existing = new XElement(metadata.Name.Namespace + section);
                metadata.Add(existing);
            }
            existing.RemoveNodes();
            foreach (var g in groups)
                existing.Add(g);
        }
        var text = new StringBuilder();
        using (var writer = System.Xml.XmlWriter.Create(text, new System.Xml.XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8, OmitXmlDeclaration = false }))
            doc.Save(writer);
        return text.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.Ordinal);
    }

    /// <summary>Whether a nuspec group's target framework (<c>net10.0-ios26.0</c>, or the long form NuGet also writes) is the copy's.</summary>
    private static bool SameFramework(string? targetFramework, Copy copy)
    {
        if (targetFramework is null)
            return false;
        if (string.Equals(targetFramework, copy.Tfm, StringComparison.OrdinalIgnoreCase))
            return true;
        // Long form: .NETCoreApp10.0-ios26.0 / net10.0-ios26.0 written as ".NETCoreApp,Version=v10.0"... (platform appended).
        var normalized = targetFramework.Replace(".NETCoreApp", "net", StringComparison.OrdinalIgnoreCase);
        return PackageCopyCheck.TryParse(normalized, out var net, out var platform, out var version)
               && net == copy.Net && platform == copy.Platform && version == copy.PlatformVersion;
    }

    /// <summary>Each public API member of every <c>lib/&lt;older&gt;/</c> assembly must be in <c>lib/&lt;newer&gt;/</c>.</summary>
    private static IEnumerable<string> ApiSuperset(Copy older, Copy newer, List<string> notes)
    {
        var problems = new List<string>();
        foreach (var (name, image) in older.From.Files.Where(f => f.Key.StartsWith($"lib/{older.Tfm}/", StringComparison.Ordinal) && f.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            var file = name[$"lib/{older.Tfm}/".Length..];
            if (!newer.From.Files.TryGetValue($"lib/{newer.Tfm}/{file}", out var newerImage))
            {
                problems.Add($"lib/{newer.Tfm}/ lacks {file}, which the older copy {older.Tfm} ships: a newer copy must contain every older copy's API.");
                continue;
            }
            var olderSurface = ApiContract(image);
            var newerSurface = ApiContract(newerImage);
            var (before, after) = (olderSurface.Members, newerSurface.Members);
            // A protected member the newer copy made public is still reachable from every subclass.
            var removed = before.Except(after)
                .Where(k => !(k.StartsWith("protected ", StringComparison.Ordinal) && after.Contains("public " + k["protected ".Length..])))
                .Order(StringComparer.Ordinal).ToList();
            notes.Add($"  API {file}: {older.Tfm} {before.Count} -> {newer.Tfm} {after.Count} ({removed.Count} removed, {after.Except(before).Count()} added)");
            if (removed.Count > 0)
            {
                problems.Add($"{file}: the copy {newer.Tfm} lacks {removed.Count} public API member(s) of the older copy {older.Tfm} ({string.Join("; ", removed.Take(5))}{(removed.Count > 5 ? "; …" : "")}). "
                             + "A library compiles against the oldest copy and the app that consumes it runs the newest, so each newer copy must contain every older copy's API.");
                // The refusal names five; the log names every one.
                notes.AddRange(removed.Select(r => $"  removed API {file} ({older.Tfm} -> {newer.Tfm}): {r}"));
            }
            // A library may also derive from the older copy's classes and override their members, and
            // calls generic members with the type arguments the older constraints allow: a class the
            // newer copy seals or makes abstract, a method it makes non-virtual or final, an abstract
            // method it adds to a class the library may derive from, and a constraint it adds each fail
            // that library when it loads on the newer copy, with every member still present.
            var narrowed = olderSurface.Extensible.Except(newerSurface.Extensible).Select(Narrowed)
                .Concat(newerSurface.Obligations.Except(olderSurface.Obligations)
                    .Where(o => !o.StartsWith("P ", StringComparison.Ordinal))
                    .Where(o => !o.StartsWith("A ", StringComparison.Ordinal) || olderSurface.Extensible.Contains("D " + o[2..o.IndexOf(' ', 2)]))
                    .Where(o => !o.StartsWith("G ", StringComparison.Ordinal) || olderSurface.Obligations.Contains(ParameterOf(o)))
                    .Where(o => !Loosened(o, olderSurface.Obligations, newerSurface.Members))
                    .Select(Added))
                .Order(StringComparer.Ordinal).ToList();
            if (narrowed.Count > 0)
            {
                problems.Add($"{file}: the copy {newer.Tfm} narrows {narrowed.Count} inheritance or constraint contract(s) of the older copy {older.Tfm} ({string.Join("; ", narrowed.Take(5))}{(narrowed.Count > 5 ? "; …" : "")}). "
                             + "A library compiled against the oldest copy may derive from its classes, override their members and pass any type argument its constraints allow, and the app that consumes it runs the newest, "
                             + "so each newer copy must keep every class derivable and constructible, every method overridable and every constraint no stricter than in each older copy.");
                notes.AddRange(narrowed.Select(r => $"  narrowed API {file} ({older.Tfm} -> {newer.Tfm}): {r}"));
            }
        }
        return problems;

        // The generic parameter a constraint key names (P {owner} parameter {index}), which the older
        // copy records when it has that parameter: a constraint on a member or type it lacks binds no
        // library compiled against it.
        static string ParameterOf(string constraint)
        {
            var colon = constraint.IndexOf(" : ", StringComparison.Ordinal);
            var head = colon < 0 ? constraint : constraint[..colon];
            var at = head.LastIndexOf(" parameter ", StringComparison.Ordinal) + " parameter ".Length;
            var end = at;
            while (end < head.Length && char.IsAsciiDigit(head[end]))
                end++;
            return "P " + head[2..end];
        }

        // A type constraint the newer copy adds is no stricter when the older copy constrained the same
        // parameter to a type of this assembly that converts to it (where T : IChild becomes where T :
        // IParent): every type argument the older constraint allowed still meets it.
        static bool Loosened(string obligation, HashSet<string> olderObligations, HashSet<string> newerMembers)
        {
            var colon = obligation.LastIndexOf(" : ", StringComparison.Ordinal);
            if (!obligation.StartsWith("G ", StringComparison.Ordinal) || colon < 0)
                return false;
            var parameter = obligation[..(colon + 3)];
            var added = obligation[(colon + 3)..];
            return olderObligations.Where(o => o.StartsWith(parameter, StringComparison.Ordinal) && o.IndexOf(" : ", parameter.Length, StringComparison.Ordinal) < 0)
                .Select(o => o[parameter.Length..])
                .Any(type => ConvertsTo(type, added, newerMembers));
        }

        // Whether the type named type (a definition of this assembly, or an instantiation of one:
        // .IChild`1<Int32>) converts to target, by the base and interface keys of its definition, which
        // name them in the definition's own parameters (!0) and are read here in type's arguments.
        static bool ConvertsTo(string type, string target, HashSet<string> members)
        {
            var open = type.IndexOf('<');
            if (open < 0)
                return members.Contains($"B {type} : {target}") || members.Contains($"I {type} : {target}");
            if (!type.EndsWith('>'))
                return false;
            var definition = type[..open];
            var arguments = new List<string>();
            var depth = 0;
            var start = open + 1;
            for (var i = start; i < type.Length - 1; i++)
            {
                // An array's rank commas (Int32[,]) are inside its brackets.
                if (type[i] is '<' or '[')
                    depth++;
                else if (type[i] is '>' or ']')
                    depth--;
                else if (type[i] == ',' && depth == 0)
                {
                    arguments.Add(type[start..i]);
                    start = i + 1;
                }
            }
            arguments.Add(type[start..^1]);
            foreach (var prefix in new[] { $"B {definition} : ", $"I {definition} : " })
                foreach (var key in members.Where(m => m.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    var converted = System.Text.RegularExpressions.Regex.Replace(key[prefix.Length..], @"(?<!!)!(\d+)",
                        m => int.TryParse(m.Groups[1].Value, out var index) && index < arguments.Count ? arguments[index] : m.Value);
                    if (converted == target)
                        return true;
                }
            return false;
        }

        static string Narrowed(string key) => key[0] switch
        {
            'D' => $"{key[2..]} can no longer be derived from",
            'N' => $"{key[2..]} can no longer be constructed",
            _ => $"{key[(key.IndexOf(' ', 2) + 1)..]} of {key[2..key.IndexOf(' ', 2)]} can no longer be overridden",
        };

        static string Added(string key) => key[0] == 'A'
            ? $"leaves {key[2..key.IndexOf(' ', 2)]} with the abstract method {key[(key.IndexOf(' ', 2) + 1)..]} to implement"
            : $"adds the constraint {key[2..]}";
    }

    /// <summary>
    /// Every C# type and member name a copy's Swift declaration record
    /// (<c>buildTransitive/&lt;tfm&gt;/*.swift-declarations.json</c>, one per generated module,
    /// <c>SwiftDeclarationMapEmitter</c>) shares with an older copy's must stand
    /// for the same Swift declaration. The generator renames a nested type named like its container, a
    /// member whose name differs from a sibling's only by case and overloads that project to one C#
    /// signature, and a rename depends on the declaration's siblings: a newer SDK that adds one can give
    /// an existing C# name to another Swift declaration, with every C# signature and export unchanged.
    /// A library compiled against the older copy would then call the other declaration in an app that
    /// runs the newer one. A module one copy records and the other does not is refused too, since its
    /// names cannot be checked.
    /// </summary>
    private static IEnumerable<string> SameSwiftDeclarations(Copy older, Copy newer, List<string> notes)
    {
        var problems = new List<string>();
        static Dictionary<string, byte[]> Records(Copy copy)
        {
            var prefix = $"buildTransitive/{copy.Tfm}/";
            return copy.From.Files
                .Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal) && f.Key.IndexOf('/', prefix.Length) < 0
                            && f.Key.EndsWith(BindingsGeneration.SwiftDeclarationRecordFormat.FileSuffix, StringComparison.Ordinal))
                .ToDictionary(f => f.Key[prefix.Length..], f => f.Value, StringComparer.Ordinal);
        }
        var olderRecords = Records(older);
        var newerRecords = Records(newer);
        // Either side lacking a record the other carries leaves that module's names unchecked.
        foreach (var (lacking, carrying, file) in olderRecords.Keys.Where(f => !newerRecords.ContainsKey(f)).Select(f => (newer, older, f))
                     .Concat(newerRecords.Keys.Where(f => !olderRecords.ContainsKey(f)).Select(f => (older, newer, f)))
                     .OrderBy(m => m.f, StringComparer.Ordinal))
            problems.Add($"buildTransitive/{lacking.Tfm}/ lacks {file}, the record of which Swift declaration each C# name stands for that the copy {carrying.Tfm} carries, "
                         + "so the copies' names cannot be checked against each other. Pack every copy with the same SwiftBindings SDK from the same source.");
        foreach (var (file, bytes) in olderRecords.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            if (!newerRecords.TryGetValue(file, out var newerBytes))
                continue;
            if (ReadSwiftDeclarations(bytes, out var problem) is not { } before)
            {
                problems.Add($"buildTransitive/{older.Tfm}/{file} {problem}");
                continue;
            }
            if (ReadSwiftDeclarations(newerBytes, out problem) is not { } after)
            {
                problems.Add($"buildTransitive/{newer.Tfm}/{file} {problem}");
                continue;
            }
            var moved = new List<string>();
            foreach (var (kind, olderMap, newerMap) in new[] { ("type", before.Types, after.Types), ("member", before.Members, after.Members) })
                foreach (var (key, was) in olderMap.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    if (newerMap.TryGetValue(key, out var now) && !string.Equals(was, now, StringComparison.Ordinal))
                        moved.Add($"the C# {kind} {before.Module}.{key} is Swift `{was}` in {older.Tfm} but Swift `{now}` in {newer.Tfm}");
            // A key spells its parameters as the member declares them, nullable annotations and tuple
            // element names included, and neither is part of the CLR signature a library binds:
            // `Load(string?)` in one copy and `Load(string)` in the other are one signature. An older
            // member whose key the newer copy lacks is compared with the newer copy's own keys of its
            // member name (MemberName), which every spelling of one CLR signature shares whatever it
            // spells its parameters as (a generic parameter's name included): it can only hold too many
            // keys, never too few. Which of them stand for the older member's signature is read from the
            // copies' assemblies (ClrSignatures), each key in its own copy: a candidate whose compiled
            // signature differs from the older member's (`Load(string, int)` beside `Load(string, int?)`,
            // or a binding's own `Int32` beside `System.Int32`) is another member and is not compared. A key either assembly does not
            // resolve to one signature stays a candidate, and the name moved unless every candidate
            // keeps its declaration. An older signature the newer copy lost outright is refused by the
            // public API comparison.
            var @namespace = file[..^BindingsGeneration.SwiftDeclarationRecordFormat.FileSuffix.Length];
            var olderSignatures = new ClrSignatures(older, @namespace, before.Module, before.Types.Keys.ToHashSet(StringComparer.Ordinal));
            var newerSignatures = new ClrSignatures(newer, @namespace, after.Module, after.Types.Keys.ToHashSet(StringComparer.Ordinal));
            var newerOnly = after.Members.Keys.Where(k => !before.Members.ContainsKey(k)).ToLookup(MemberName, StringComparer.Ordinal);
            foreach (var (olderKey, was) in before.Members.Where(m => !after.Members.ContainsKey(m.Key)).OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                var signature = olderSignatures.Of(olderKey);
                var candidates = newerOnly[MemberName(olderKey)]
                    .Where(k => signature is null || newerSignatures.Of(k) is not { } candidate || candidate == signature)
                    .OrderBy(k => k, StringComparer.Ordinal).ToList();
                var others = candidates.Where(k => !string.Equals(after.Members[k], was, StringComparison.Ordinal)).ToList();
                if (others.Count == 0)
                    continue;
                moved.Add($"the C# member {before.Module}.{olderKey} is Swift `{was}` in {older.Tfm} but Swift "
                          + string.Join(" or ", others.Select(k => $"`{after.Members[k]}`")) + $" in {newer.Tfm}, as "
                          + string.Join(" or ", others.Select(k => $"{before.Module}.{k}")));
            }
            notes.Add($"  Swift declarations {file}: {older.Tfm} -> {newer.Tfm}, {before.Types.Count + before.Members.Count} names, {moved.Count} moved");
            if (moved.Count == 0)
                continue;
            problems.Add($"{file}: {moved.Count} C# name(s) stand for a different Swift declaration in the copy {newer.Tfm} than in the older copy {older.Tfm} "
                         + $"({string.Join("; ", moved.Take(10))}{(moved.Count > 10 ? "; …" : "")}). "
                         + "The newer SDK added a declaration that took over the C# name (a nested type named like its container, a case-only or an overload rename). "
                         + "A library compiled against the older copy would call the other declaration in an app that runs the newer copy, so these copies cannot ship in one package: "
                         + "ship them as separate packages, or pin the name in the binding.");
            // The refusal names ten; the log names every one.
            notes.AddRange(moved.Select(m => $"  moved name {file}: {m}"));
        }
        return problems;
    }

    /// <summary>
    /// The compiled CLR signature of a record key in one copy: its parameter types as the copy's
    /// <c>lib/&lt;tfm&gt;/</c> assemblies declare them, each named in full (namespace, enclosing types,
    /// generic arity; <see cref="BindingsGeneration.SwiftDeclarationRecordKeys.Member.QualifiedParameters"/>)
    /// with <c>Nullable&lt;T&gt;</c> told apart from a nullable annotation, and the method's generic
    /// arity. The key names the members it spells by simple names, whatever their generic arity
    /// (<see cref="BindingsGeneration.SwiftDeclarationRecordKeys.AssemblyIndex.Spelled"/>: the key's
    /// Swift arity also counts parameters the C# member does not declare, so it cannot tell which it is),
    /// whose full parameter types its own qualification can name (<see cref="BindingsGeneration.SwiftDeclarationRecordKeys.CanName"/>).
    /// Null for a key that does not resolve to exactly one signature (a property, a member no
    /// assembly of the copy declares in the record's namespace), which the comparison then treats as
    /// any signature.
    /// </summary>
    private sealed class ClrSignatures(Copy copy, string @namespace, string module, IReadOnlySet<string> swiftTypes)
    {
        private List<BindingsGeneration.SwiftDeclarationRecordKeys.AssemblyIndex>? _indexes;
        private readonly Dictionary<string, string?> _resolved = new(StringComparer.Ordinal);

        public string? Of(string key)
        {
            if (_resolved.TryGetValue(key, out var known))
                return known;
            _indexes ??= copy.From.Files
                .Where(f => f.Key.StartsWith($"lib/{copy.Tfm}/", StringComparison.Ordinal) && f.Key.IndexOf('/', $"lib/{copy.Tfm}/".Length) < 0
                            && f.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => Index(f.Value)).ToList();
            string? signature = null;
            if (BindingsGeneration.SwiftDeclarationRecordKeys.Parse(key) is { Parameters: not null, QualifiedParameters: { } spelled } parsed)
            {
                var signatures = _indexes.SelectMany(index => index.Spelled(parsed, module, swiftTypes)
                        .Where(m => m.QualifiedParameters.Length == spelled.Length
                                    && spelled.Zip(m.QualifiedParameters).All(p => BindingsGeneration.SwiftDeclarationRecordKeys.CanName(
                                        p.First, p.Second, m.QualifiedParent, head => OwnNestedType(index, m, head)))))
                    .Select(m => string.Join(",", m.QualifiedParameters) + "`" + m.GenericArity)
                    .Distinct(StringComparer.Ordinal).ToList();
                signature = signatures.Count == 1 ? signatures[0] : null;
            }
            return _resolved[key] = signature;
        }

        // Whether the emitted code reads a parameter type's first segment `head` (`Name` or `Name`N`)
        // as the member's declaring type's own nested type: generation emitted that type (the record
        // lists it), so the C# qualifier, which leaves alone a namespace root the module declares a
        // type of, keeps the key's spelling, and C# finds a type's own nested types first. A name the
        // qualifier may respell through a using directive, or as the module's namespace, is not
        // read so.
        private bool OwnNestedType(BindingsGeneration.SwiftDeclarationRecordKeys.AssemblyIndex index,
            BindingsGeneration.SwiftDeclarationRecordKeys.Member member, string head)
        {
            var name = head.IndexOf('`') is var tick and >= 0 ? head[..tick] : head;
            return Requalified is { } requalified && !requalified.Contains(name) && name != @namespace.Split('.')[0]
                   && member.ParentPath.Length > 0 && swiftTypes.Contains(member.ParentPath + "." + name)
                   && index.QualifiedTypes.Contains(member.QualifiedParent + "+" + head);
        }

        private BindingsGeneration.SwiftDeclarationRecordKeys.AssemblyIndex Index(byte[] image)
        {
            try
            {
                return BindingsGeneration.SwiftDeclarationRecordKeys.Index(image, @namespace);
            }
            catch (BadImageFormatException)
            {
                // Not an assembly this check can read: its keys stay unresolved, so every candidate is compared.
                return new BindingsGeneration.SwiftDeclarationRecordKeys.AssemblyIndex();
            }
        }
    }

    /// <summary>
    /// The bare type names the generator's C# qualifier spells from <c>global::</c> after a record key
    /// is taken, where the module does not declare one in scope (<c>imported-type-names.json</c>, the
    /// table the qualifier reads); null when the table cannot be read, and then no key's spelling is
    /// read narrower than its name.
    /// </summary>
    private static readonly IReadOnlySet<string>? Requalified = ReadRequalified();

    private static IReadOnlySet<string>? ReadRequalified()
    {
        try
        {
            using var stream = typeof(SwiftBindingPackageMerge).Assembly.GetManifestResourceStream("SwiftBindings.Tasks.imported-type-names.json");
            if (stream is null)
                return null;
            using var document = System.Text.Json.JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("namespaces", out var namespaces) || namespaces.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ns in namespaces.EnumerateObject())
                foreach (var name in ns.Value.EnumerateArray())
                    if (name.GetString() is { } text)
                        names.Add(text);
            return names.Count > 0 ? names : null;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The C# member a key names, by its declaring type and name alone: the key's text ahead of its
    /// parameter list (<c>Parent.Name</c>, an indexer's <c>Parent.this</c>, a property's whole key),
    /// without generic argument lists. Every spelling of one CLR signature shares it, whatever it spells
    /// its types as, and whether or not the rest of the key reads; which of the keys it buckets name
    /// the same signature the copies' assemblies decide (<see cref="ClrSignatures"/>).
    /// </summary>
    internal static string MemberName(string key)
    {
        var name = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var c in key)
        {
            if (depth == 0 && c is '(' or '[')
                break;
            if (c == '<')
                depth++;
            else if (c == '>')
                depth = Math.Max(0, depth - 1);
            else if (depth == 0)
                name.Append(c);
        }
        return name.ToString();
    }

    /// <summary>
    /// Every Swift declaration record a copy's assemblies name (<see cref="BindingsGeneration.SwiftDeclarationRecordFormat.AttributeKey"/>,
    /// written by generation beside each module's wrapper identity) must be packed in the copy's
    /// <c>buildTransitive/&lt;tfm&gt;/</c> with the bytes the assembly hashed, and every module with a
    /// wrapper identity must name one. The pairwise comparison checks only records both copies carry,
    /// so a record no copy carries (a pack with <c>--no-build</c> after the intermediate directory was
    /// cleaned, a hand-assembled package) would otherwise leave the module's names unchecked, and a
    /// record from another generation would compare names the assembly does not declare.
    /// </summary>
    private static IEnumerable<string> WitnessedSwiftDeclarations(Copy copy)
    {
        var problems = new List<string>();
        var lib = $"lib/{copy.Tfm}/";
        var records = $"buildTransitive/{copy.Tfm}/";
        const string repack = "Pack the copy again from a clean build of the binding (delete its bin/ and obj/), with the same SwiftBindings SDK as the other copies.";
        foreach (var (name, image) in copy.From.Files
                     .Where(f => f.Key.StartsWith(lib, StringComparison.Ordinal) && f.Key.IndexOf('/', lib.Length) < 0
                                 && f.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var witnessed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var witness in SwiftBindings.TransitionGate.SwiftWrapperIdentityCheck.ReadMetadata(image, BindingsGeneration.SwiftDeclarationRecordFormat.AttributeKey))
            {
                if (!BindingsGeneration.SwiftDeclarationRecordFormat.TryParseWitness(witness, out var module, out var record, out var hash))
                {
                    problems.Add($"{name} names a Swift declaration record as '{witness}', which does not read as one. {repack}");
                    continue;
                }
                witnessed.Add(module);
                if (!copy.From.Files.TryGetValue(records + record, out var bytes))
                    problems.Add($"{name} was generated with the Swift declaration record {record} of the module {module}, but the copy {copy.Tfm} does not carry it in {records}, "
                                 + $"so the C# names of {module} cannot be checked against the other copies. {repack}");
                else if (BindingsGeneration.SwiftDeclarationRecordFormat.Hash(bytes) != hash)
                    problems.Add($"{records}{record} is not the Swift declaration record {name} was generated with ({hash}): it comes from another generation of the binding, "
                                 + $"so it does not describe the names the copy {copy.Tfm} declares. {repack}");
            }
            var identities = SwiftBindings.TransitionGate.SwiftWrapperIdentityCheck.Read(image).Identities;
            foreach (var identity in identities.Where(i => !witnessed.Contains(i.Module)).OrderBy(i => i.Module, StringComparer.Ordinal))
                problems.Add($"{name} declares the wrapper of the Swift module {identity.Module} but names no Swift declaration record for it: it was generated by a SwiftBindings SDK "
                             + $"that does not record which Swift declaration each C# name stands for, so the copy {copy.Tfm}'s names cannot be checked. {repack}");
        }
        return problems;
    }

    private sealed record SwiftDeclarations(string Module, Dictionary<string, string> Types, Dictionary<string, string> Members);

    private static SwiftDeclarations? ReadSwiftDeclarations(byte[] bytes, out string? problem)
    {
        problem = null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("schema_version", out var schema) || schema.ValueKind != System.Text.Json.JsonValueKind.Number
                || schema.GetInt32() != BindingsGeneration.SwiftDeclarationRecordFormat.SchemaVersion)
            {
                problem = $"is not a Swift declaration record of schema {BindingsGeneration.SwiftDeclarationRecordFormat.SchemaVersion}; pack it with the current SwiftBindings SDK.";
                return null;
            }
            // A record missing a map, or holding a value that is not a declaration, would compare as
            // "nothing moved", so it is refused rather than read as empty.
            static Dictionary<string, string>? Map(System.Text.Json.JsonElement root, string property)
            {
                if (!root.TryGetProperty(property, out var element) || element.ValueKind != System.Text.Json.JsonValueKind.Object)
                    return null;
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in element.EnumerateObject())
                {
                    if (entry.Value.ValueKind != System.Text.Json.JsonValueKind.String || string.IsNullOrEmpty(entry.Value.GetString()))
                        return null;
                    map[entry.Name] = entry.Value.GetString()!;
                }
                return map;
            }
            if (!root.TryGetProperty("module", out var m) || m.ValueKind != System.Text.Json.JsonValueKind.String || string.IsNullOrEmpty(m.GetString())
                || Map(root, "types") is not { } types || Map(root, "members") is not { } members)
            {
                problem = "is not a complete Swift declaration record (a module name and a map of types and of members, each naming a Swift declaration); pack it with the current SwiftBindings SDK.";
                return null;
            }
            return new SwiftDeclarations(m.GetString()!, types, members);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            problem = $"does not read as a Swift declaration record: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Every value a compiler copies out of a <c>lib/&lt;older&gt;/</c> assembly into the code that uses
    /// it (constant fields, enum members included, and parameter default values) must be the same in
    /// <c>lib/&lt;newer&gt;/</c>: a library compiled against the older copy keeps the older value when
    /// the app that consumes it runs the newer copy, so a changed value changes what the library does
    /// with no member missing. A value only the newer copy declares is an addition.
    /// </summary>
    private static IEnumerable<string> SameCompiledInValues(Copy older, Copy newer, List<string> notes)
    {
        var problems = new List<string>();
        foreach (var (name, image) in older.From.Files.Where(f => f.Key.StartsWith($"lib/{older.Tfm}/", StringComparison.Ordinal) && f.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            var file = name[$"lib/{older.Tfm}/".Length..];
            if (!newer.From.Files.TryGetValue($"lib/{newer.Tfm}/{file}", out var newerImage))
                continue; // ApiSuperset names the missing assembly.
            var after = CompiledInValues(newerImage);
            // A value the newer copy no longer declares is as different as a changed one: the older
            // library's call site still holds it (a dropped default, or a constant turned into a field).
            // A value is its text and its constant's type: a boxed Int32 1 and a boxed Int64 1 read alike
            // but are different objects to the code that receives them.
            var changed = CompiledInValues(image)
                .Where(kv => !after.TryGetValue(kv.Key, out var value) || value != kv.Value)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => after.TryGetValue(kv.Key, out var value)
                    ? value.Text == kv.Value.Text
                        ? $"{kv.Key} is {kv.Value.Text} ({kv.Value.Type}) in {older.Tfm} and {value.Text} ({value.Type}) in {newer.Tfm}"
                        : $"{kv.Key} is {kv.Value.Text} in {older.Tfm} and {value.Text} in {newer.Tfm}"
                    : $"{kv.Key} is {kv.Value.Text} in {older.Tfm} and has no compiled-in value in {newer.Tfm}")
                .ToList();
            if (changed.Count > 0)
            {
                problems.Add($"{file}: the copy {newer.Tfm} changes {changed.Count} compiled-in value(s) of the older copy {older.Tfm} ({string.Join("; ", changed.Take(10))}{(changed.Count > 10 ? "; …" : "")}). "
                             + "A compiler copies a constant, an enum member's value or a parameter's default into the code that uses it, so a library compiled against the older copy "
                             + "keeps the older value when the app runs the newer copy: every value the older copy declares must be the same in each newer copy. "
                             + "For enum values: a non-frozen Swift enum's CaseTag constants, and the constants of a plain C# enum of 32 bits or more projected from a non-frozen, non-@objc one, are keyed by case name, so an inserted case moves none of them; "
                             + "such a value changes only when it now names a different Swift case (a case the newer SDK adds took its C# name, as Swift names that differ only by case do) "
                             + "or when its name's hash collides with an added case's. A plain enum that is @frozen or narrower than 32 bits follows its SDK's case order, so a case inserted before others moves them; an @objc enum's constants are its raw values.");
                // The refusal names ten; the log names every one.
                notes.AddRange(changed.Select(c => $"  changed value {file}: {c}"));
            }
        }
        return problems;
    }

    /// <summary>
    /// The compiled-in values of an assembly's visible API, keyed as <see cref="PublicApi"/> names the
    /// member: <c>Type::Field</c> for a constant field (an enum member is one, and a decimal or
    /// <c>DateTime</c> constant is a static readonly field carrying its value in an attribute) and
    /// <c>Type::Method`n(parameters):return parameter i</c> for a parameter's default value.
    /// A change of a field's or parameter's type is <see cref="PublicApi"/>'s to report; a value's
    /// <c>Type</c> is its metadata constant's type, which a boxed default (of an <c>object</c>
    /// parameter) carries apart from the parameter's, and null for a value an attribute records.
    /// </summary>
    public static Dictionary<string, (string Text, string? Type)> CompiledInValues(byte[] image)
    {
        var values = new Dictionary<string, (string Text, string? Type)>(StringComparer.Ordinal);
        using var pe = new PEReader(new MemoryStream(image, writable: false));
        if (!pe.HasMetadata)
            return values;
        var md = pe.GetMetadataReader();
        var provider = new SignatureNames();
        string TypeName(TypeDefinitionHandle h)
        {
            var t = md.GetTypeDefinition(h);
            var declaring = t.GetDeclaringType();
            return declaring.IsNil ? $"{md.GetString(t.Namespace)}.{md.GetString(t.Name)}" : $"{TypeName(declaring)}+{md.GetString(t.Name)}";
        }
        bool Visible(TypeDefinitionHandle h)
        {
            var t = md.GetTypeDefinition(h);
            return (t.Attributes & TypeAttributes.VisibilityMask) switch
            {
                TypeAttributes.Public => true,
                TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem => Visible(t.GetDeclaringType()),
                _ => false,
            };
        }
        (string Text, string? Type) Value(ConstantHandle handle)
        {
            var constant = md.GetConstant(handle);
            var blob = md.GetBlobReader(constant.Value);
            var value = blob.ReadConstant(constant.TypeCode);
            return (value switch
            {
                null => "null",
                string s => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
                char c => $"'{c}' (U+{(int)c:X4})",
                float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => value.ToString() ?? "",
            }, constant.TypeCode.ToString());
        }
        // A decimal or DateTime constant has no metadata constant: C# records it in an attribute
        // (on a static readonly field, or on an optional parameter), and still compiles it in. The
        // attribute counts only on a member of its own type, as the compilers read it.
        static bool Is(string typeName, string fullName) => typeName == fullName || typeName.EndsWith("]" + fullName, StringComparison.Ordinal);
        // The member's type, a nullable of it, or object (a boxed default) carries the constant.
        static bool Carries(string memberType, string fullName) => Is(memberType, fullName) || memberType == "Object"
            || memberType.EndsWith("System.Nullable`1<" + fullName + ">", StringComparison.Ordinal)
            || memberType.Contains("System.Nullable`1<", StringComparison.Ordinal) && memberType.EndsWith("]" + fullName + ">", StringComparison.Ordinal);
        static string DateTimeValue(long ticks) => ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
            ? new DateTime(ticks).ToString("o", System.Globalization.CultureInfo.InvariantCulture)
            : $"DateTime(ticks {ticks})";
        // A decimal's text drops the sign of zero; the bits keep it.
        static string DecimalValue(int lo, int mid, int hi, bool negative, byte scale) => scale <= 28
            ? (negative && (lo | mid | hi) == 0 ? "-" : "") + new decimal(lo, mid, hi, negative, scale).ToString(System.Globalization.CultureInfo.InvariantCulture) + "m"
            : $"decimal(scale {scale}, {(negative ? "-" : "+")}, {hi:X8}{mid:X8}{lo:X8})";
        // A field is a constant only of the attribute's own type; a parameter's default may also be
        // passed to a nullable or object parameter.
        string? AttributeConstant(CustomAttributeHandleCollection attributes, string memberType, bool parameter)
        {
            foreach (var ah in attributes)
            {
                var attribute = md.GetCustomAttribute(ah);
                var parent = attribute.Constructor.Kind switch
                {
                    HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
                    HandleKind.MethodDefinition => (EntityHandle)md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
                    _ => default(EntityHandle),
                };
                var (ns, typeName) = parent.Kind switch
                {
                    HandleKind.TypeReference => (md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Namespace), md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name)),
                    HandleKind.TypeDefinition => (md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)parent).Namespace), md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)parent).Name)),
                    _ => ("", ""),
                };
                if (ns != "System.Runtime.CompilerServices"
                    || !(typeName == "DecimalConstantAttribute" && (parameter ? Carries(memberType, "System.Decimal") : Is(memberType, "System.Decimal"))
                         || typeName == "DateTimeConstantAttribute" && (parameter ? Carries(memberType, "System.DateTime") : Is(memberType, "System.DateTime"))))
                    continue;
                var blob = md.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() != 1)
                    continue;
                if (typeName == "DateTimeConstantAttribute")
                    return DateTimeValue(blob.ReadInt64());
                var scale = blob.ReadByte();
                var negative = blob.ReadByte() != 0;
                var (hi, mid, lo) = (blob.ReadInt32(), blob.ReadInt32(), blob.ReadInt32());
                return DecimalValue(lo, mid, hi, negative, scale);
            }
            return null;
        }
        foreach (var handle in md.TypeDefinitions)
        {
            if (!Visible(handle))
                continue;
            var type = md.GetTypeDefinition(handle);
            var name = TypeName(handle);
            foreach (var fh in type.GetFields())
            {
                var field = md.GetFieldDefinition(fh);
                var access = field.Attributes & FieldAttributes.FieldAccessMask;
                if (access is not (FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem))
                    continue;
                var constant = field.GetDefaultValue();
                var value = (field.Attributes & FieldAttributes.Literal) != 0
                    ? constant.IsNil ? null : Value(constant)
                    : (field.Attributes & (FieldAttributes.Static | FieldAttributes.InitOnly)) == (FieldAttributes.Static | FieldAttributes.InitOnly)
                      && AttributeConstant(field.GetCustomAttributes(), field.DecodeSignature(provider, md), parameter: false) is { } recorded
                        ? (recorded, null)
                        : ((string Text, string? Type)?)null;
                if (value is { } v)
                    values[$"{name}::{md.GetString(field.Name)}"] = v;
            }
            foreach (var mh in type.GetMethods())
            {
                var method = md.GetMethodDefinition(mh);
                var access = method.Attributes & MethodAttributes.MemberAccessMask;
                if (access is not (MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem))
                    continue;
                MethodSignature<string>? signature = null;
                foreach (var ph in method.GetParameters())
                {
                    var parameter = md.GetParameter(ph);
                    if (parameter.SequenceNumber == 0)
                        continue;
                    var hasDefault = (parameter.Attributes & ParameterAttributes.HasDefault) != 0;
                    if (!hasDefault && (parameter.Attributes & ParameterAttributes.Optional) == 0)
                        continue;
                    signature ??= method.DecodeSignature(provider, md);
                    var parameterType = parameter.SequenceNumber <= signature.Value.ParameterTypes.Length
                        ? signature.Value.ParameterTypes[parameter.SequenceNumber - 1]
                        : "";
                    var constant = parameter.GetDefaultValue();
                    var value = hasDefault && !constant.IsNil
                        ? Value(constant)
                        : AttributeConstant(parameter.GetCustomAttributes(), parameterType, parameter: true) is { } recorded
                            ? (recorded, null)
                            : ((string Text, string? Type)?)null;
                    // `= default` of a DateTime or decimal parameter is a null constant, and passes the
                    // same value as the attribute's zero.
                    if (value?.Text == "null" && Is(parameterType, "System.DateTime"))
                        value = (DateTimeValue(0), null);
                    else if (value?.Text == "null" && Is(parameterType, "System.Decimal"))
                        value = (DecimalValue(0, 0, 0, false, 0), null);
                    if (value is null)
                        continue;
                    // Keyed by position, not name: a caller binds the default by the parameter's place,
                    // so a renamed parameter whose default changed is still a changed value.
                    var key = $"{name}::{md.GetString(method.Name)}`{signature.Value.GenericParameterCount}({string.Join(",", signature.Value.ParameterTypes)}):{signature.Value.ReturnType}";
                    values[$"{key} parameter {parameter.SequenceNumber}"] = value.Value;
                }
            }
        }
        return values;
    }

    /// <summary>
    /// Each export of every framework binary of the older copy's natives must be in its counterpart among
    /// the newer copy's, per architecture: under <c>native/&lt;tfm&gt;/</c>, or inside the binding's
    /// resource package in <c>lib/&lt;tfm&gt;/</c> for a copy that ships its natives there. A copy that
    /// declares no natives has none to compare.
    /// </summary>
    private static IEnumerable<string> ExportSuperset(Copy older, Copy newer, List<string> notes, bool archivesOnly = false)
    {
        var problems = new List<string>();
        if (older.Payload == PayloadNone)
        {
            if (archivesOnly)
                return problems;
            notes.Add($"  natives: {older.Tfm} and {newer.Tfm} declare none");
            return problems;
        }
        // What the copies' natives fail to read is reported by the comparison with the next copy.
        var reading = archivesOnly ? new List<string>() : problems;
        var olderImages = NativeImages(older, reading);
        var newerImages = NativeImages(newer, reading);
        var olderSymbols = new Dictionary<string, ImmutableArray<MachOSlice>>(StringComparer.Ordinal);
        var newerSymbols = new Dictionary<string, ImmutableArray<MachOSlice>>(StringComparer.Ordinal);
        var compared = new HashSet<(string Older, string Newer)>();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        // An image beside the library a slice's Info.plist names is never linked, in either copy.
        foreach (var path in olderImages.Paths.Where(IsLinkedImage).Where(p => LinkedBySlice(olderImages, p)))
        {
            // A symbolic link (a versioned framework's root binary, or a path through Versions/Current)
            // is compared as the image it names, in each copy. Two paths that name the same pair of
            // images are compared once.
            if (olderImages.Resolve(path) is not { } real)
            {
                reading.Add(UnresolvedLink(older, olderImages, path));
                continue;
            }
            // The newer copy's slice of the same platform and variant, whatever its directory is named:
            // a newer build that adds an architecture names its slice for it (macos-arm64 becomes
            // macos-arm64_x86_64), and the architectures are compared below.
            var newerPath = CounterpartSlicePath(path, olderImages, newerImages, out var lacking);
            // Only an archive's symbols are compared past the next copy; the rest is transitive.
            if (archivesOnly && (newerPath is null || newerImages.Resolve(newerPath) is not { } reached
                                 || !IsStaticArchive(olderImages.Files[real]) || !IsStaticArchive(newerImages.Files[reached])))
                continue;
            if (newerPath is null)
            {
                problems.Add($"The copy {newer.Tfm} lacks {NativeRoot(newer)}{lacking}, which the older copy {older.Tfm} ships.");
                continue;
            }
            if (!LinkedBySlice(newerImages, newerPath))
            {
                problems.Add($"{NativeRoot(newer)}{newerPath}: the newer copy's Info.plist links {SliceLibrary(newerImages, newerPath)} from its slice, not this library, which the older copy {older.Tfm} links.");
                continue;
            }
            if (newerImages.Resolve(newerPath) is not { } newerReal)
            {
                problems.Add(newerImages.LinkAlong(newerPath) is null
                    ? $"{NativeRoot(newer)} lacks {(newerPath == path ? path : $"{newerPath} ({path} in the older copy)")}, which the older copy {older.Tfm} ships."
                    : UnresolvedLink(newer, newerImages, newerPath));
                continue;
            }
            if (!compared.Add((real, newerReal)))
                continue;
            var relative = real == newerReal ? real : newerPath == path ? path : newerPath;
            if (!archivesOnly && DroppedArchitectures(path, newerPath, olderImages, newerImages) is { Count: > 0 } dropped
                && declared.Add(SliceDirectory(newerPath)))
                problems.Add($"{NativeRoot(newer)}{relative}: the newer copy's Info.plist does not declare {string.Join(", ", dropped)} for its slice, which the older copy's declares, so a build for {(dropped.Count == 1 ? "it" : "them")} finds no slice.");
            var image = olderImages.Files[real];
            var newerImage = newerImages.Files[newerReal];
            // A static framework's binary is an archive the app links in: its linkable symbols are the
            // external symbols its members define, which code built against the older copy links to.
            if (IsStaticArchive(image) != IsStaticArchive(newerImage))
            {
                problems.Add($"{NativeRoot(newer)}{relative} is {(IsStaticArchive(newerImage) ? "a static archive" : "a dynamic image")}, but the older copy's is {(IsStaticArchive(image) ? "a static archive" : "a dynamic image")}.");
                continue;
            }
            if (IsStaticArchive(image))
            {
                problems.AddRange(ArchiveSuperset(older, newer, relative, image, newerImage, notes));
                continue;
            }
            IReadOnlyList<(string Architecture, IReadOnlySet<string> Exports)> before, after;
            try
            {
                before = Symbols(olderSymbols, real, image);
                after = Symbols(newerSymbols, newerReal, newerImage);
            }
            catch (BadImageFormatException ex)
            {
                problems.Add($"{relative} does not read as a Mach-O image: {ex.Message}");
                continue;
            }
            foreach (var slice in before)
            {
                var counterpart = after.FirstOrDefault(s => s.Architecture == slice.Architecture);
                if (counterpart.Exports is null)
                {
                    problems.Add($"{NativeRoot(newer)}{relative} has no {slice.Architecture} slice, which the older copy's has.");
                    continue;
                }
                var missing = slice.Exports.Except(counterpart.Exports, StringComparer.Ordinal)
                    .Where(s => !s.StartsWith(SwiftTransitionManifestFormatPrefix, StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal).ToList();
                notes.Add($"  exports {relative} ({slice.Architecture}): {older.Tfm} {slice.Exports.Count} -> {newer.Tfm} {counterpart.Exports.Count} ({missing.Count} missing)");
                if (missing.Count > 0)
                    problems.Add($"{NativeRoot(newer)}{relative} ({slice.Architecture}) lacks {missing.Count} export(s) of the older copy {older.Tfm} ({string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? ", …" : "")}): "
                                 + "a newer copy's native payload must contain every older copy's exports.");
            }
        }
        return problems;

        static IReadOnlyList<(string, IReadOnlySet<string>)> Symbols(Dictionary<string, ImmutableArray<MachOSlice>> read, string path, byte[] image)
        {
            if (!read.TryGetValue(path, out var slices))
                read[path] = slices = MachOSymbols.Read(image);
            return slices.Select(s => (s.Architecture, (IReadOnlySet<string>)s.Exports)).ToList();
        }
    }

    // How a thin archive of no Mach-O member names its architecture.
    private const string UnknownArchitecture = "an unknown architecture";

    /// <summary>
    /// A static library's binary (a static framework's, or a bare archive) compared per architecture:
    /// every external symbol the older copy's Mach-O members define, which code built against it links
    /// to, must be one the newer copy's archive gives a link. Private externals are not compared: they
    /// are the library's own (compiler helpers that differ between toolchains), not ones code built
    /// against it references.
    /// </summary>
    private static IEnumerable<string> ArchiveSuperset(Copy older, Copy newer, string relative, byte[] image, byte[] newerImage, List<string> notes)
    {
        IReadOnlyList<ArchiveSlice> before, after;
        try
        {
            before = ArchiveSymbols(image);
            after = ArchiveSymbols(newerImage);
        }
        catch (BadImageFormatException ex)
        {
            return [$"{relative} does not read as a static archive: {ex.Message}"];
        }
        var problems = new List<string>();
        foreach (var slice in before)
        {
            // A thin archive of bitcode members alone says no architecture (a bitcode wrapper records
            // none), so a thin archive pairs with the other copy's thin archive.
            var counterpart = after.FirstOrDefault(s => s.Architecture == slice.Architecture)
                              ?? (before.Count == 1 && after.Count == 1 && (slice.Architecture == UnknownArchitecture || after[0].Architecture == UnknownArchitecture) ? after[0] : null);
            if (counterpart is null)
            {
                // An older archive that names no architecture has no Mach-O member, so it requires no
                // symbol of a newer one (a dropped architecture is the Info.plist's to show).
                if (slice.Architecture == UnknownArchitecture)
                {
                    notes.Add($"  archive symbols {relative}: the older copy's archive has no Mach-O member, so none of its symbols are required");
                    continue;
                }
                problems.Add($"{NativeRoot(newer)}{relative} has no {slice.Architecture} slice, which the older copy's has.");
                continue;
            }
            if (counterpart.Unread > 0 && !counterpart.Indexed)
            {
                // What the members that do not read define is said only by a table of contents.
                notes.Add($"  {relative} ({slice.Architecture}): the newer copy's archive has {counterpart.Unread} member(s) that do not read as Mach-O objects and no table of contents, linkable symbols not compared");
                continue;
            }
            var missing = slice.Required.Except(counterpart.Available, StringComparer.Ordinal)
                .Where(s => !s.StartsWith(SwiftTransitionManifestFormatPrefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToList();
            notes.Add($"  archive symbols {relative} ({slice.Architecture}): {older.Tfm} {slice.Required.Count} -> {newer.Tfm} {counterpart.Available.Count} ({missing.Count} missing)"
                      + (slice.Unread > 0 ? $"; the older copy's {slice.Unread} member(s) that do not read as Mach-O objects are not required" : ""));
            if (missing.Count > 0)
                problems.Add($"{NativeRoot(newer)}{relative} ({slice.Architecture}) lacks {missing.Count} linkable symbol(s) of the older copy {older.Tfm} ({string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? ", …" : "")}): "
                             + "code built against the older copy's static library links to them, so a newer copy's archive must define every older copy's external symbol.");
        }
        return problems;
    }

    /// <summary>
    /// One architecture of a static archive: the external symbols its Mach-O members define
    /// (<see cref="Required"/>, what code built against the archive may link to), and those plus the
    /// symbols its table of contents gives members that do not read as Mach-O objects (LLVM bitcode
    /// from a link-time-optimized build), which a link still finds (<see cref="Available"/>).
    /// </summary>
    /// <param name="Architecture">The architecture, as <see cref="MachOSymbols.ArchitectureName"/> names it.</param>
    /// <param name="Required">The external, non-private symbols the Mach-O members define.</param>
    /// <param name="Available"><see cref="Required"/> and the table-of-contents symbols of the members that do not read.</param>
    /// <param name="Unread">How many members, other than the symbol tables, do not read as Mach-O objects.</param>
    /// <param name="Indexed">Whether the archive has a table of contents to say what those members define.</param>
    internal sealed record ArchiveSlice(string Architecture, IReadOnlySet<string> Required, IReadOnlySet<string> Available, int Unread, bool Indexed);

    /// <summary>
    /// The <see cref="ArchiveSlice"/>s of the static archive <paramref name="image"/>: a thin archive's
    /// architecture, or each slice of a fat one. Throws <see cref="BadImageFormatException"/> when its
    /// structure does not read.
    /// </summary>
    internal static IReadOnlyList<ArchiveSlice> ArchiveSymbols(byte[] image)
    {
        var magic = image.Length >= 8 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(image) : 0;
        var archives = new List<(long Offset, long Length, string? Architecture)>();
        if (magic is 0xCAFEBABE or 0xCAFEBABF)
        {
            var is64 = magic == 0xCAFEBABF;
            var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(4));
            var stride = is64 ? 32 : 20;
            if (count == 0 || 8 + (long)count * stride > image.Length)
                throw new BadImageFormatException("the fat header does not read");
            for (var i = 0; i < count; i++)
            {
                var entry = image.AsSpan(8 + i * stride);
                var cpuType = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(entry);
                var cpuSubtype = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
                var offset = is64 ? (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(entry[8..]) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
                var size = is64 ? (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(entry[16..]) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
                if (offset < 0 || size < 0 || offset > image.Length || size > image.Length - offset)
                    throw new BadImageFormatException($"fat slice {i} lies outside the archive");
                archives.Add((offset, size, MachOSymbols.ArchitectureName(cpuType, cpuSubtype)));
            }
        }
        else
        {
            archives.Add((0, image.Length, null));
        }
        var result = new List<ArchiveSlice>();
        foreach (var (start, length, fatArchitecture) in archives)
        {
            var end = start + length;
            if (length < 8 || !image.AsSpan((int)start, 8).SequenceEqual("!<arch>\n"u8))
                throw new BadImageFormatException("a slice is not an archive");
            var required = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var unread = new List<long>();
            Dictionary<long, List<string>>? index = null;
            for (var at = start + 8; at + 60 <= end;)
            {
                var header = Encoding.ASCII.GetString(image, (int)at, 60);
                if (!long.TryParse(header.Substring(48, 10).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size)
                    || at + 60 + size > end)
                    throw new BadImageFormatException($"the member header at offset {at - start} does not read");
                var name = header[..16].TrimEnd();
                var data = at + 60;
                var dataLength = size;
                // A BSD long name (#1/<length>) is stored at the start of the member's data.
                if (name.StartsWith("#1/", StringComparison.Ordinal))
                {
                    if (!int.TryParse(name[3..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var nameLength) || nameLength > dataLength)
                        throw new BadImageFormatException($"the member name at offset {at - start} does not read");
                    name = Encoding.UTF8.GetString(image, (int)data, nameLength).TrimEnd('\0');
                    data += nameLength;
                    dataLength -= nameLength;
                }
                var member = at - start;
                at += 60 + size + (size & 1);
                var bytes = image.AsSpan((int)data, (int)dataLength);
                if (name.StartsWith("__.SYMDEF", StringComparison.Ordinal) || name is "/" or "/SYM64/")
                {
                    index ??= TableOfContents(name, bytes);
                    continue;
                }
                if (name == "//")
                    continue;
                ImmutableArray<MachOSlice> slices;
                try
                {
                    slices = MachOSymbols.Read(bytes.ToArray());
                }
                catch (BadImageFormatException)
                {
                    unread.Add(member);
                    continue;
                }
                foreach (var slice in slices)
                {
                    var architecture = fatArchitecture ?? slice.Architecture;
                    if (!required.TryGetValue(architecture, out var set))
                        required[architecture] = set = new SortedSet<string>(StringComparer.Ordinal);
                    set.UnionWith(slice.Exports);
                }
            }
            var architectures = fatArchitecture is not null ? [fatArchitecture] : required.Keys.DefaultIfEmpty(UnknownArchitecture).ToList();
            var unreadSymbols = index is null ? [] : unread.SelectMany(m => index.TryGetValue(m, out var names) ? names : []).ToList();
            foreach (var architecture in architectures)
            {
                var own = required.TryGetValue(architecture, out var set) ? set : new SortedSet<string>(StringComparer.Ordinal);
                var available = new SortedSet<string>(own, StringComparer.Ordinal);
                available.UnionWith(unreadSymbols);
                result.Add(new ArchiveSlice(architecture, own, available, unread.Count, index is not null));
            }
        }
        return result;

        // The symbols an archive's table of contents names, keyed by the offset of the member that
        // defines each: a BSD __.SYMDEF (little-endian, 32- or 64-bit) or a GNU / or /SYM64/ table
        // (big-endian). Null when it does not read.
        static Dictionary<long, List<string>>? TableOfContents(string name, ReadOnlySpan<byte> data)
        {
            var symbols = new Dictionary<long, List<string>>();
            // Named as MachOSymbols names a symbol, without the C prefix.
            void Add(long member, string symbol)
            {
                if (!symbols.TryGetValue(member, out var list))
                    symbols[member] = list = new List<string>();
                list.Add(symbol.StartsWith('_') ? symbol[1..] : symbol);
            }
            static string? NameAt(ReadOnlySpan<byte> strings, long offset)
            {
                if (offset < 0 || offset >= strings.Length)
                    return null;
                var tail = strings[(int)offset..];
                var nul = tail.IndexOf((byte)0);
                return Encoding.UTF8.GetString(nul < 0 ? tail : tail[..nul]);
            }
            if (name.StartsWith("__.SYMDEF", StringComparison.Ordinal))
            {
                var wide = name.StartsWith("__.SYMDEF_64", StringComparison.Ordinal);
                var word = wide ? 8 : 4;
                long Read(ReadOnlySpan<byte> s, long at) => wide
                    ? (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(s[(int)at..])
                    : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s[(int)at..]);
                if (data.Length < word)
                    return null;
                var entries = Read(data, 0);
                var stringsAt = word + entries;
                if (entries < 0 || entries % (2 * word) != 0 || stringsAt + word > data.Length)
                    return null;
                var stringsLength = Read(data, stringsAt);
                if (stringsLength < 0 || stringsAt + word + stringsLength > data.Length)
                    return null;
                var strings = data.Slice((int)(stringsAt + word), (int)stringsLength);
                for (long e = word; e < stringsAt; e += 2 * word)
                    if (NameAt(strings, Read(data, e)) is { } symbol)
                        Add(Read(data, e + word), symbol);
                return symbols;
            }
            var gnuWord = name == "/SYM64/" ? 8 : 4;
            long ReadBig(ReadOnlySpan<byte> s, long at) => gnuWord == 8
                ? (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(s[(int)at..])
                : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(s[(int)at..]);
            if (data.Length < gnuWord)
                return null;
            var count = ReadBig(data, 0);
            var namesAt = gnuWord + count * gnuWord;
            if (count < 0 || namesAt > data.Length)
                return null;
            var namesData = data[(int)namesAt..];
            long next = 0;
            for (long i = 0; i < count; i++)
            {
                if (NameAt(namesData, next) is not { } symbol)
                    return null;
                // A GNU table's offsets are of the member header from the start of the archive.
                Add(ReadBig(data, gnuWord + i * gnuWord), symbol);
                next += Encoding.UTF8.GetByteCount(symbol) + 1;
            }
            return symbols;
        }
    }

    /// <summary>
    /// <paramref name="path"/> (under an older copy's natives) as the newer copy names it: a path into
    /// an XCFramework slice maps to the newer XCFramework's slice of the same platform and variant
    /// (<see cref="SliceKind"/>), since the slice directory names its architectures and a newer build
    /// may add or drop one; any other path, or one whose slice says no platform, is itself. Null, with
    /// the slice it lacks in <paramref name="lacking"/>, when the newer XCFramework has no slice of that
    /// platform and variant.
    /// </summary>
    private static string? CounterpartSlicePath(string path, NativeFiles older, NativeFiles newer, out string? lacking)
    {
        lacking = null;
        var parts = path.Split('/');
        var index = Array.FindIndex(parts, p => p.EndsWith(".xcframework", StringComparison.Ordinal));
        if (index < 0 || index + 2 >= parts.Length)
            return path;
        var root = string.Join('/', parts.Take(index + 1));
        var slice = parts[index + 1];
        if (SliceKind(older, root, slice) is not { } kind)
            return path;
        var matches = newer.Paths.Where(p => p.StartsWith(root + "/", StringComparison.Ordinal))
            .Select(p => p[(root.Length + 1)..].Split('/')).Where(s => s.Length > 1).Select(s => s[0])
            .Distinct(StringComparer.Ordinal)
            .Where(s => SliceKind(newer, root, s) == kind).ToList();
        if (matches.Count == 0)
        {
            lacking = $"{root}'s {kind} slice ({slice} in the older copy)";
            return null;
        }
        return matches.Count == 1 && matches[0] != slice
            ? string.Join('/', parts.Take(index + 1).Append(matches[0]).Concat(parts.Skip(index + 2)))
            : path;
    }

    /// <summary>
    /// The platform and variant of an XCFramework slice (<c>ios</c>, <c>ios simulator</c>, <c>ios
    /// maccatalyst</c>): from the XCFramework's <c>Info.plist</c> when it has one that lists slices,
    /// since that is what a build chooses the slice by; otherwise from the slice's directory name
    /// (<see cref="XCFrameworkSlices.KindFromName"/>); null when neither says.
    /// </summary>
    private static string? SliceKind(NativeFiles files, string root, string slice)
    {
        if (Listed(files, root) is { Count: > 0 } listed)
            return listed.TryGetValue(slice, out var entry) ? entry.Kind : null;
        return XCFrameworkSlices.KindFromName(slice);
    }

    /// <summary>
    /// Whether <paramref name="path"/> lies in the library its XCFramework slice's <c>Info.plist</c> entry
    /// names (<c>LibraryPath</c>), which is what a build links from the slice; true outside an
    /// XCFramework slice or where the plist names none.
    /// </summary>
    private static bool LinkedBySlice(NativeFiles files, string path)
    {
        if (SliceLibrary(files, path) is not { } library)
            return true;
        var parts = path.Split('/');
        var within = string.Join('/', parts.Skip(Array.FindIndex(parts, p => p.EndsWith(".xcframework", StringComparison.Ordinal)) + 2));
        return within == library || within.StartsWith(library.TrimEnd('/') + "/", StringComparison.Ordinal);
    }

    /// <summary>The library the <c>Info.plist</c> entry of <paramref name="path"/>'s XCFramework slice names; null when none does.</summary>
    private static string? SliceLibrary(NativeFiles files, string path)
    {
        var parts = path.Split('/');
        var index = Array.FindIndex(parts, p => p.EndsWith(".xcframework", StringComparison.Ordinal));
        return index >= 0 && index + 2 < parts.Length && Listed(files, string.Join('/', parts.Take(index + 1))).TryGetValue(parts[index + 1], out var slice)
            ? slice.LibraryPath
            : null;
    }

    /// <summary>The XCFramework slice directory <paramref name="path"/> lies in (<c>Kit.xcframework/ios-arm64</c>), or the path itself outside one.</summary>
    private static string SliceDirectory(string path)
    {
        var parts = path.Split('/');
        var index = Array.FindIndex(parts, p => p.EndsWith(".xcframework", StringComparison.Ordinal));
        return index < 0 || index + 1 >= parts.Length ? path : string.Join('/', parts.Take(index + 2));
    }

    /// <summary>The slices the <c>Info.plist</c> of the XCFramework at <paramref name="root"/> lists; empty without one.</summary>
    private static IReadOnlyDictionary<string, XCFrameworkSlice> Listed(NativeFiles files, string root) =>
        files.Files.TryGetValue(root + "/Info.plist", out var plist) ? XCFrameworkSlices.Listed(plist) : new Dictionary<string, XCFrameworkSlice>();

    /// <summary>
    /// The architectures the older <c>Info.plist</c> declares for the slice of <paramref name="path"/>
    /// that the newer one does not declare for its counterpart at <paramref name="newerPath"/>: a build
    /// for one of them no longer finds the slice, whatever its binary holds. Empty when either plist
    /// does not list the slice.
    /// </summary>
    private static List<string> DroppedArchitectures(string path, string newerPath, NativeFiles older, NativeFiles newer)
    {
        var parts = path.Split('/');
        var index = Array.FindIndex(parts, p => p.EndsWith(".xcframework", StringComparison.Ordinal));
        if (index < 0 || index + 2 >= parts.Length)
            return [];
        var root = string.Join('/', parts.Take(index + 1));
        var newerSlice = newerPath.Split('/')[index + 1];
        return Listed(older, root).TryGetValue(parts[index + 1], out var before) && Listed(newer, root).TryGetValue(newerSlice, out var after)
            ? before.Architectures.Except(after.Architectures, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
            : [];
    }

    /// <summary>Where <paramref name="copy"/>'s natives are, as a package path prefix.</summary>
    private static string NativeRoot(Copy copy) => copy.Payload == PayloadResources ? $"lib/{copy.Tfm}/" : $"native/{copy.Tfm}/";

    /// <summary>
    /// The native files of <paramref name="copy"/>, keyed by their path under <see cref="NativeRoot"/>:
    /// <c>native/&lt;tfm&gt;/</c>, or each resource package of <c>lib/&lt;tfm&gt;/</c> (a zip read in
    /// place, keyed as if it were its directory). A resource package stores a versioned framework's
    /// symbolic links as links (<c>zip -y</c>, which the consumer's extraction restores), so a link
    /// entry is kept as the path it names, never read as the file it stands for.
    /// </summary>
    private static NativeFiles NativeImages(Copy copy, List<string> problems)
    {
        var images = new NativeFiles();
        var prefix = NativeRoot(copy);
        foreach (var (name, data) in copy.From.Files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var relative = name[prefix.Length..];
            if (copy.Payload == PayloadNative)
            {
                images.Files[relative] = data;
                continue;
            }
            if (relative.Contains('/') && relative.Split('/')[0].EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            {
                images.Files[relative] = data;
                continue;
            }
            if (relative.Contains('/') || !relative.EndsWith(".resources.zip", StringComparison.OrdinalIgnoreCase))
                continue;
            var directory = relative[..^".zip".Length] + "/";
            try
            {
                using var zip = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
                foreach (var entry in zip.Entries)
                {
                    // Keyed as extraction names it: a "." or empty component names no folder.
                    var path = directory + string.Join('/', entry.FullName.Replace('\\', '/').Split('/').Where(c => c is not ("" or ".")));
                    if (entry.FullName.EndsWith('/'))
                    {
                        images.Folders.Add(path);
                        continue;
                    }
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    if (IsSymbolicLink(entry))
                        images.Links[path] = Encoding.UTF8.GetString(buffer.ToArray());
                    else
                        images.Files[path] = buffer.ToArray();
                }
            }
            catch (InvalidDataException ex)
            {
                problems.Add($"{name} does not read as a zip archive: {ex.Message}");
            }
        }
        return images;
    }

    /// <summary>Whether a zip entry is a symbolic link: its Unix mode (the high half of the external attributes) is <c>S_IFLNK</c>.</summary>
    private static bool IsSymbolicLink(ZipArchiveEntry entry) => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    /// <summary>A copy's native files and the symbolic links among them, keyed by path under <see cref="NativeRoot"/>.</summary>
    private sealed class NativeFiles
    {
        // The most links one path resolves through, as SYMLOOP_MAX bounds it.
        private const int MaxLinks = 32;

        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        /// <summary>Each symbolic link, with the path it names, relative to the folder that holds it.</summary>
        public Dictionary<string, string> Links { get; } = new(StringComparer.Ordinal);

        /// <summary>The folders the archive stores as entries of their own, which may hold nothing.</summary>
        public HashSet<string> Folders { get; } = new(StringComparer.Ordinal);

        public IEnumerable<string> Paths => Files.Keys.Concat(Links.Keys);

        private HashSet<string>? directories;

        /// <summary>
        /// The file <paramref name="path"/> names, resolved one component at a time as the file
        /// system resolves it: a link is replaced by its target, read from the folder that holds the
        /// link, and every component before the last must be a folder. Null when a link names
        /// nothing the copy carries, leaves the resource package that holds it (the first path
        /// segment), or loops.
        /// </summary>
        public string? Resolve(string path)
        {
            directories ??= Paths.SelectMany(Holders).Concat(Folders).Concat(Folders.SelectMany(Holders)).ToHashSet(StringComparer.Ordinal);
            var pending = new List<string>(path.Split('/'));
            var resolved = new List<string>();
            var followed = 0;
            while (pending.Count > 0)
            {
                var part = pending[0];
                pending.RemoveAt(0);
                if (part is "" or ".")
                    continue;
                if (part == "..")
                {
                    // Every resolved component is a real folder, so its parent is the folder above.
                    if (resolved.Count <= 1)
                        return null;
                    resolved.RemoveAt(resolved.Count - 1);
                    continue;
                }
                resolved.Add(part);
                var current = string.Join('/', resolved);
                if (Links.TryGetValue(current, out var target))
                {
                    if (++followed > MaxLinks || target.StartsWith('/'))
                        return null;
                    resolved.RemoveAt(resolved.Count - 1);
                    pending.InsertRange(0, target.Split('/'));
                    continue;
                }
                if (pending.Count > 0 && !directories.Contains(current))
                    return null;
            }
            var real = string.Join('/', resolved);
            return Files.ContainsKey(real) ? real : null;
        }

        /// <summary>Every folder that holds <paramref name="path"/>, outermost first.</summary>
        private static IEnumerable<string> Holders(string path)
        {
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                yield return path[..slash];
        }

        /// <summary>The first symbolic link along <paramref name="path"/>, if any.</summary>
        public string? LinkAlong(string path)
        {
            var parts = path.Split('/');
            for (var i = 1; i <= parts.Length; i++)
            {
                var prefix = string.Join('/', parts.Take(i));
                if (Links.ContainsKey(prefix))
                    return prefix;
            }
            return null;
        }
    }

    private static string UnresolvedLink(Copy copy, NativeFiles files, string path)
    {
        if (files.LinkAlong(path) is not { } link)
            return $"{NativeRoot(copy)}{path} does not resolve to a file its resource package carries.";
        return $"{NativeRoot(copy)}{link} is a symbolic link to '{files.Links[link]}', which names no file its resource package carries"
               + (link == path ? "." : $", so {path} reads as nothing.");
    }

    // The manifest hash symbol differs per copy by design.
    private const string SwiftTransitionManifestFormatPrefix = BindingsGeneration.SwiftTransitionManifestFormat.NativeSymbolPrefix;

    /// <summary>
    /// An image an app links: a framework's binary (<c>X.framework/X</c> flat or
    /// <c>X.framework/Versions/A/X</c>), dynamic or static, or a bare dylib or static archive slice.
    /// </summary>
    private static bool IsLinkedImage(string path)
    {
        if ((path.EndsWith(".dylib", StringComparison.Ordinal) || path.EndsWith(".a", StringComparison.Ordinal)) && !path.Contains(".dSYM/", StringComparison.Ordinal))
            return true;
        var parts = path.Split('/');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!parts[i].EndsWith(".framework", StringComparison.Ordinal))
                continue;
            var module = parts[i][..^".framework".Length];
            var rest = parts.Skip(i + 1).ToArray();
            return (rest.Length == 1 && rest[0] == module) || (rest.Length == 3 && rest[0] == "Versions" && rest[2] == module);
        }
        return false;
    }

    /// <summary>Whether <paramref name="image"/> is a static archive (<c>!&lt;arch&gt;</c>), thin or as the first slice of a fat file.</summary>
    private static bool IsStaticArchive(byte[] image)
    {
        static bool At(byte[] data, long offset) =>
            offset >= 0 && offset <= data.Length - 8 && data.AsSpan((int)offset, 8).SequenceEqual("!<arch>\n"u8);
        if (At(image, 0))
            return true;
        if (image.Length < 8)
            return false;
        var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(image);
        if (magic is not (0xCAFEBABE or 0xCAFEBABF))
            return false;
        var entry = image.AsSpan(8);
        long offset = magic == 0xCAFEBABF
            ? (image.Length >= 8 + 32 ? (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(entry[8..]) : -1)
            : (image.Length >= 8 + 20 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(entry[8..]) : -1);
        return At(image, offset);
    }

    /// <summary>The public API of an assembly: visible types, and their public or protected methods and fields with decoded signatures.</summary>
    public static HashSet<string> PublicApi(byte[] image) => ApiContract(image).Members;

    /// <summary>
    /// What an assembly's visible API lets a library compiled against it do beyond calling it.
    /// </summary>
    /// <param name="Members">The <see cref="PublicApi"/> keys.</param>
    /// <param name="Extensible">What a library may derive from or override, each of which a newer copy must keep:
    /// <c>D type</c> a class it may derive from (not sealed, with a public or protected instance constructor),
    /// <c>N type</c> one it may construct (not abstract, with a public constructor), and
    /// <c>V type slot</c> a method a class derived from the type may override (virtual, not final and visible
    /// to it, declared by the type or a base this assembly defines).</param>
    /// <param name="Obligations">What a library that derives from a class, or calls a generic member, must
    /// satisfy, none of which a newer copy may add: <c>A type member</c> an abstract method of a class, and
    /// <c>G owner parameter …</c> a constraint of a generic parameter; with <c>P owner parameter N</c> each
    /// generic parameter, which a constraint added to it is judged against.</param>
    public sealed record ApiSurface(HashSet<string> Members, HashSet<string> Extensible, HashSet<string> Obligations);

    /// <summary>The <see cref="ApiSurface"/> of <paramref name="image"/>.</summary>
    public static ApiSurface ApiContract(byte[] image)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var extensible = new HashSet<string>(StringComparer.Ordinal);
        var obligations = new HashSet<string>(StringComparer.Ordinal);
        var surface = new ApiSurface(set, extensible, obligations);
        using var pe = new PEReader(new MemoryStream(image, writable: false));
        if (!pe.HasMetadata)
            return surface;
        var md = pe.GetMetadataReader();
        var provider = new SignatureNames();
        string TypeName(TypeDefinitionHandle h)
        {
            var t = md.GetTypeDefinition(h);
            var declaring = t.GetDeclaringType();
            return declaring.IsNil ? $"{md.GetString(t.Namespace)}.{md.GetString(t.Name)}" : $"{TypeName(declaring)}+{md.GetString(t.Name)}";
        }
        bool Visible(TypeDefinitionHandle h)
        {
            var t = md.GetTypeDefinition(h);
            return (t.Attributes & TypeAttributes.VisibilityMask) switch
            {
                TypeAttributes.Public => true,
                TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem => Visible(t.GetDeclaringType()),
                _ => false,
            };
        }
        string EntityName(SignatureNames names, EntityHandle h) => h.Kind switch
        {
            HandleKind.TypeDefinition => names.GetTypeFromDefinition(md, (TypeDefinitionHandle)h, 0),
            HandleKind.TypeReference => names.GetTypeFromReference(md, (TypeReferenceHandle)h, 0),
            HandleKind.TypeSpecification => names.GetTypeFromSpecification(md, md, (TypeSpecificationHandle)h, 0),
            _ => h.Kind.ToString(),
        };
        // The definition in this assembly a base names, directly or as a generic instantiation, with
        // the instantiation's type arguments written in the derived type's terms; null for a base
        // defined elsewhere.
        (TypeDefinitionHandle? Definition, string[]? Arguments) DefinedHere(SignatureNames names, EntityHandle h)
        {
            if (h.Kind == HandleKind.TypeDefinition)
                return ((TypeDefinitionHandle)h, null);
            if (h.Kind != HandleKind.TypeSpecification)
                return (null, null);
            var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)h).Signature);
            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                return (null, null);
            blob.ReadSignatureTypeCode();
            var generic = blob.ReadTypeHandle();
            if (generic.Kind != HandleKind.TypeDefinition)
                return (null, null);
            var decoder = new System.Reflection.Metadata.Ecma335.SignatureDecoder<string, MetadataReader>(names, md, md);
            var arguments = new string[blob.ReadCompressedInteger()];
            for (var i = 0; i < arguments.Length; i++)
                arguments[i] = decoder.DecodeType(ref blob);
            return ((TypeDefinitionHandle)generic, arguments);
        }
        foreach (var handle in md.TypeDefinitions)
        {
            if (!Visible(handle))
                continue;
            var type = md.GetTypeDefinition(handle);
            var name = TypeName(handle);
            set.Add($"T {((type.Attributes & TypeAttributes.Interface) != 0 ? "interface" : "type")} {name}");
            // What the type converts to: a newer copy that drops a base or an interface breaks a
            // library compiled against the older copy at its first cast, even with every member kept.
            // Read through the base chain this assembly defines, so moving a conformance onto a base
            // or inserting an intermediate base keeps every conversion the older copy had.
            // A generic base's own type parameters are read as the arguments the derived type gives it.
            var seen = new HashSet<TypeDefinitionHandle>();
            var level = provider;
            for (TypeDefinitionHandle? current = handle; current is { } h && seen.Add(h);)
            {
                var t = md.GetTypeDefinition(h);
                foreach (var ih in t.GetInterfaceImplementations())
                    set.Add($"I {name} : {EntityName(level, md.GetInterfaceImplementation(ih).Interface)}");
                if (t.BaseType.IsNil)
                    break;
                set.Add($"B {name} : {EntityName(level, t.BaseType)}");
                var (definition, arguments) = DefinedHere(level, t.BaseType);
                current = definition;
                level = new SignatureNames(arguments);
            }
            // Whether a library may derive from the type: a class, not sealed (a static class is sealed),
            // with a constructor a derived class can call.
            var baseName = type.BaseType.IsNil ? "" : EntityName(provider, type.BaseType);
            var isClass = (type.Attributes & TypeAttributes.Interface) == 0 && !type.BaseType.IsNil
                          && baseName[(baseName.LastIndexOf(']') + 1)..] is not ("System.ValueType" or "System.Enum" or "System.MulticastDelegate");
            var constructors = type.GetMethods().Select(md.GetMethodDefinition)
                .Where(m => (m.Attributes & MethodAttributes.Static) == 0 && md.GetString(m.Name) == ".ctor")
                .Select(m => m.Attributes & MethodAttributes.MemberAccessMask).ToList();
            var derivable = isClass && (type.Attributes & TypeAttributes.Sealed) == 0
                            && constructors.Any(a => a is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem);
            if (derivable)
            {
                extensible.Add($"D {name}");
                var (open, overridable) = Inheritance(handle);
                foreach (var slot in open)
                    obligations.Add($"A {name} {slot}");
                foreach (var slot in overridable)
                    extensible.Add($"V {name} {slot}");
            }
            if (isClass && (type.Attributes & TypeAttributes.Abstract) == 0 && constructors.Contains(MethodAttributes.Public))
                extensible.Add($"N {name}");
            Constraints(type.GetGenericParameters(), $"type {name}");
            foreach (var mh in type.GetMethods())
            {
                var method = md.GetMethodDefinition(mh);
                var access = method.Attributes & MethodAttributes.MemberAccessMask;
                if (access is not (MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem))
                    continue;
                var signature = method.DecodeSignature(provider, md);
                var binding = (method.Attributes & MethodAttributes.Static) != 0 ? "static" : "instance";
                var key = $"M {binding} {name}::{md.GetString(method.Name)}`{signature.GenericParameterCount}({string.Join(",", signature.ParameterTypes)}):{signature.ReturnType}";
                set.Add((access == MethodAttributes.Public ? "public " : "protected ") + key);
                Constraints(method.GetGenericParameters(), $"method {key}");
            }
            foreach (var fh in type.GetFields())
            {
                var field = md.GetFieldDefinition(fh);
                var access = field.Attributes & FieldAttributes.FieldAccessMask;
                if (access is not (FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem))
                    continue;
                var key = $"F {((field.Attributes & FieldAttributes.Static) != 0 ? "static" : "instance")} {name}::{md.GetString(field.Name)}:{field.DecodeSignature(provider, md)}";
                set.Add((access == FieldAttributes.Public ? "public " : "protected ") + key);
            }
        }
        return surface;

        // What a class derived from the type inherits from it and the bases this assembly defines
        // (generic bases in the type's terms; a base defined elsewhere is its own package's contract),
        // each method named by its slot: the abstract methods it must implement, those no class
        // between overrides, at any access (an internal or private protected one is an obligation no
        // library can meet); and the methods it may override, virtual, not final and visible to it,
        // as the most derived declaration of the slot has them, each with the class that introduces
        // the slot an override binds to (a new virtual method of the same signature hides a base's).
        (SortedSet<string> Open, SortedSet<string> Overridable) Inheritance(TypeDefinitionHandle handle)
        {
            var open = new SortedSet<string>(StringComparer.Ordinal);
            var overridable = new SortedSet<string>(StringComparer.Ordinal);
            var decided = new HashSet<string>(StringComparer.Ordinal);
            // The overridable slots whose introducing class is still to be found, further up.
            var introducing = new HashSet<string>(StringComparer.Ordinal);
            var overridden = new HashSet<string>(StringComparer.Ordinal);
            var explicitly = new HashSet<MethodDefinitionHandle>();
            var seen = new HashSet<TypeDefinitionHandle>();
            var level = provider;
            for (TypeDefinitionHandle? current = handle; current is { } h && seen.Add(h);)
            {
                var t = md.GetTypeDefinition(h);
                // An explicit override (a covariant return, a renamed override) names the method it implements.
                foreach (var ih in t.GetMethodImplementations())
                    if (Declared(md.GetMethodImplementation(ih).MethodDeclaration) is { } declaration)
                        explicitly.Add(declaration);
                foreach (var mh in t.GetMethods())
                {
                    var method = md.GetMethodDefinition(mh);
                    if ((method.Attributes & MethodAttributes.Virtual) == 0)
                        continue;
                    var signature = method.DecodeSignature(level, md);
                    var slot = $"{md.GetString(method.Name)}`{signature.GenericParameterCount}({string.Join(",", signature.ParameterTypes)}):{signature.ReturnType}";
                    var access = method.Attributes & MethodAttributes.MemberAccessMask;
                    if (decided.Add(slot) && (method.Attributes & MethodAttributes.Final) == 0
                        && access is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem)
                        introducing.Add(slot);
                    if ((method.Attributes & MethodAttributes.NewSlot) != 0 && introducing.Remove(slot))
                        overridable.Add($"{TypeName(h)}::{slot}");
                    if ((method.Attributes & MethodAttributes.Abstract) != 0)
                    {
                        if (!overridden.Contains(slot) && !explicitly.Contains(mh))
                            open.Add(slot);
                    }
                    else if ((method.Attributes & MethodAttributes.NewSlot) == 0)
                    {
                        overridden.Add(slot);
                    }
                }
                if (t.BaseType.IsNil)
                    break;
                var (definition, arguments) = DefinedHere(level, t.BaseType);
                current = definition;
                level = new SignatureNames(arguments);
            }
            // An override of a slot a base of another assembly introduces.
            foreach (var slot in introducing)
                overridable.Add($"::{slot}");
            return (open, overridable);
        }

        // The method of this assembly an explicit override's declaration names: a definition, or a
        // reference to one through a generic instantiation of its type (Base<int>::F).
        MethodDefinitionHandle? Declared(EntityHandle declaration)
        {
            if (declaration.Kind == HandleKind.MethodDefinition)
                return (MethodDefinitionHandle)declaration;
            if (declaration.Kind != HandleKind.MemberReference)
                return null;
            var reference = md.GetMemberReference((MemberReferenceHandle)declaration);
            if (reference.GetKind() != MemberReferenceKind.Method || DefinedHere(provider, reference.Parent).Definition is not { } owner)
                return null;
            var wanted = md.GetBlobContent(reference.Signature);
            foreach (var mh in md.GetTypeDefinition(owner).GetMethods())
            {
                var candidate = md.GetMethodDefinition(mh);
                if (md.StringComparer.Equals(candidate.Name, md.GetString(reference.Name)) && md.GetBlobContent(candidate.Signature).SequenceEqual(wanted))
                    return mh;
            }
            return null;
        }

        // Each constraint of a generic parameter, by position: its special constraints (class, struct,
        // new()) and each type it must convert to. A caller or derived type that met the older
        // constraints need not meet an added one, and fails to load where it does not.
        void Constraints(GenericParameterHandleCollection parameters, string owner)
        {
            foreach (var gh in parameters)
            {
                var parameter = md.GetGenericParameter(gh);
                obligations.Add($"P {owner} parameter {parameter.Index}");
                var special = parameter.Attributes & GenericParameterAttributes.SpecialConstraintMask;
                foreach (var flag in new[] { GenericParameterAttributes.ReferenceTypeConstraint, GenericParameterAttributes.NotNullableValueTypeConstraint, GenericParameterAttributes.DefaultConstructorConstraint })
                    if ((special & flag) != 0)
                        obligations.Add($"G {owner} parameter {parameter.Index} {flag}");
                foreach (var ch in parameter.GetConstraints())
                    obligations.Add($"G {owner} parameter {parameter.Index} : {EntityName(provider, md.GetGenericParameterConstraint(ch).Type)}");
            }
        }
    }

    /// <summary>Type names for signatures; <paramref name="typeArguments"/> stand for a generic type's own parameters when given.</summary>
    private sealed class SignatureNames(string[]? typeArguments = null) : ISignatureTypeProvider<string, MetadataReader>
    {
        public string GetArrayType(string element, ArrayShape shape) => $"{element}[{new string(',', shape.Rank - 1)}]";
        public string GetByReferenceType(string element) => element + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => $"fnptr({string.Join(",", signature.ParameterTypes)}):{signature.ReturnType}";
        public string GetGenericInstantiation(string generic, ImmutableArray<string> arguments) => $"{generic}<{string.Join(",", arguments)}>";
        public string GetGenericMethodParameter(MetadataReader context, int index) => $"!!{index}";
        public string GetGenericTypeParameter(MetadataReader context, int index) =>
            typeArguments is not null && index < typeArguments.Length ? typeArguments[index] : $"!{index}";
        public string GetModifiedType(string modifier, string unmodified, bool isRequired) => isRequired ? $"{unmodified} modreq({modifier})" : unmodified;
        public string GetPinnedType(string element) => element;
        public string GetPointerType(string element) => element + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string element) => element + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var t = reader.GetTypeDefinition(handle);
            var declaring = t.GetDeclaringType();
            return declaring.IsNil ? $"{reader.GetString(t.Namespace)}.{reader.GetString(t.Name)}" : $"{GetTypeFromDefinition(reader, declaring, rawTypeKind)}+{reader.GetString(t.Name)}";
        }
        // A referenced type is named with the assembly it resolves in: two copies naming types of one
        // name in different assemblies name different types, which a compiled library tells apart.
        // A type of the assembly itself (a definition, or a reference scoped to its own module) is
        // named bare, the same in every copy.
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var t = reader.GetTypeReference(handle);
            var name = $"{reader.GetString(t.Namespace)}.{reader.GetString(t.Name)}";
            return t.ResolutionScope.Kind switch
            {
                HandleKind.TypeReference => $"{GetTypeFromReference(reader, (TypeReferenceHandle)t.ResolutionScope, rawTypeKind)}+{reader.GetString(t.Name)}",
                HandleKind.AssemblyReference => $"[{reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)t.ResolutionScope).Name)}]{name}",
                HandleKind.ModuleReference => $"[module {reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)t.ResolutionScope).Name)}]{name}",
                _ => name,
            };
        }
        public string GetTypeFromSpecification(MetadataReader reader, MetadataReader context, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, context);
    }

    private static string ContentTypes(IEnumerable<Contribution> contributions, IEnumerable<string> names)
    {
        var defaults = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in contributions)
            if (c.Files.TryGetValue("[Content_Types].xml", out var bytes))
                try
                {
                    foreach (var d in XDocument.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')).Descendants().Where(e => e.Name.LocalName == "Default"))
                        if (d.Attribute("Extension")?.Value is { } ext && d.Attribute("ContentType")?.Value is { } type)
                            defaults.TryAdd(ext, type);
                }
                catch (System.Xml.XmlException)
                {
                }
        foreach (var name in names)
        {
            var file = name[(name.LastIndexOf('/') + 1)..];
            var dot = file.LastIndexOf('.');
            if (dot > 0)
                defaults.TryAdd(file[(dot + 1)..], "application/octet");
        }
        var text = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\n");
        foreach (var (ext, type) in defaults)
            text.Append($"  <Default Extension=\"{System.Security.SecurityElement.Escape(ext)}\" ContentType=\"{System.Security.SecurityElement.Escape(type)}\" />\n");
        // Extensionless entries (framework binaries) need an override per part.
        foreach (var name in names.Where(n => !n[(n.LastIndexOf('/') + 1)..].Contains('.')))
            text.Append($"  <Override PartName=\"/{System.Security.SecurityElement.Escape(Uri.EscapeDataString(name).Replace("%2F", "/", StringComparison.Ordinal))}\" ContentType=\"application/octet\" />\n");
        text.Append("</Types>\n");
        return text.ToString();
    }

    private static void Write(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(data);
    }
}
