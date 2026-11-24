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
        Dictionary<string, byte[]> Files, ImmutableArray<string> Copies);

    private sealed record Copy(string Tfm, Version Net, string Platform, Version PlatformVersion, Contribution From);

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
        if (problems.Count > 0)
            return Refused(problems, notes);

        var copies = owner.Select(kv => ToCopy(kv.Key, kv.Value)).OrderBy(c => c.Platform, StringComparer.Ordinal)
            .ThenBy(c => c.Net).ThenBy(c => c.PlatformVersion).ToList();
        foreach (var copy in copies)
            notes.Add($"  {copy.Tfm}: {Name(copy.From)} (built on {DeclarationValue(copy.From, copy.Tfm, "Toolchain") ?? "an unrecorded toolchain"})");

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
        if (!lib.SetEquals(build) || !lib.SetEquals(native))
            problems.Add($"{name} carries lib/ [{string.Join(", ", lib)}], buildTransitive/ [{string.Join(", ", build)}] and native/ [{string.Join(", ", native)}]: "
                         + "NuGet selects lib/ and buildTransitive/ separately, so every copy must appear in all three.");
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
            var targets = $"buildTransitive/{tfm}/{id}.targets";
            if (!files.TryGetValue(targets, out var targetsBytes))
                problems.Add($"{name} carries no consumer targets {targets}.");
            else
                foreach (var problem in PackageCopyRules.CheckTargetsReferences(tfm, Encoding.UTF8.GetString(targetsBytes)))
                    problems.Add($"{name}: {targets} {problem}; each copy's targets must reference only its own native/{tfm}/.");
        }
        return new Contribution(path, id, version, nuspec, nuspecs[0], files, lib.ToImmutableArray());
    }

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
            var before = PublicApi(image);
            var after = PublicApi(newerImage);
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
        }
        return problems;
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
            var changed = CompiledInValues(image)
                .Where(kv => !after.TryGetValue(kv.Key, out var value) || !string.Equals(value, kv.Value, StringComparison.Ordinal))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => after.TryGetValue(kv.Key, out var value)
                    ? $"{kv.Key} is {kv.Value} in {older.Tfm} and {value} in {newer.Tfm}"
                    : $"{kv.Key} is {kv.Value} in {older.Tfm} and has no compiled-in value in {newer.Tfm}")
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
    /// A change of a field's or parameter's type is <see cref="PublicApi"/>'s to report.
    /// </summary>
    public static Dictionary<string, string> CompiledInValues(byte[] image)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
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
        string Value(ConstantHandle handle)
        {
            var constant = md.GetConstant(handle);
            var blob = md.GetBlobReader(constant.Value);
            var value = blob.ReadConstant(constant.TypeCode);
            return value switch
            {
                null => "null",
                string s => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
                char c => $"'{c}' (U+{(int)c:X4})",
                float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => value.ToString() ?? "",
            };
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
                        ? AttributeConstant(field.GetCustomAttributes(), field.DecodeSignature(provider, md), parameter: false)
                        : null;
                if (value is not null)
                    values[$"{name}::{md.GetString(field.Name)}"] = value;
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
                        : AttributeConstant(parameter.GetCustomAttributes(), parameterType, parameter: true);
                    // `= default` of a DateTime or decimal parameter is a null constant, and passes the
                    // same value as the attribute's zero.
                    if (value == "null" && Is(parameterType, "System.DateTime"))
                        value = DateTimeValue(0);
                    else if (value == "null" && Is(parameterType, "System.Decimal"))
                        value = DecimalValue(0, 0, 0, false, 0);
                    if (value is null)
                        continue;
                    // Keyed by position, not name: a caller binds the default by the parameter's place,
                    // so a renamed parameter whose default changed is still a changed value.
                    var key = $"{name}::{md.GetString(method.Name)}`{signature.Value.GenericParameterCount}({string.Join(",", signature.Value.ParameterTypes)}):{signature.Value.ReturnType}";
                    values[$"{key} parameter {parameter.SequenceNumber}"] = value;
                }
            }
        }
        return values;
    }

    /// <summary>Each export of every framework binary under <c>native/&lt;older&gt;/</c> must be in its counterpart under <c>native/&lt;newer&gt;/</c>, per architecture.</summary>
    private static IEnumerable<string> ExportSuperset(Copy older, Copy newer, List<string> notes)
    {
        var problems = new List<string>();
        var prefix = $"native/{older.Tfm}/";
        foreach (var (name, image) in older.From.Files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal) && IsDynamicImage(f.Key)))
        {
            var relative = name[prefix.Length..];
            if (!newer.From.Files.TryGetValue($"native/{newer.Tfm}/{relative}", out var newerImage))
            {
                problems.Add($"native/{newer.Tfm}/ lacks {relative}, which the older copy {older.Tfm} ships.");
                continue;
            }
            ImmutableArray<MachOSlice> before, after;
            try
            {
                before = MachOSymbols.Read(image);
                after = MachOSymbols.Read(newerImage);
            }
            catch (BadImageFormatException ex)
            {
                problems.Add($"{relative} does not read as a Mach-O image: {ex.Message}");
                continue;
            }
            foreach (var slice in before)
            {
                var counterpart = after.FirstOrDefault(s => s.Architecture == slice.Architecture);
                if (counterpart is null)
                {
                    problems.Add($"native/{newer.Tfm}/{relative} has no {slice.Architecture} slice, which the older copy's has.");
                    continue;
                }
                var missing = slice.Exports.Except(counterpart.Exports, StringComparer.Ordinal)
                    .Where(s => !s.StartsWith(SwiftTransitionManifestFormatPrefix, StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal).ToList();
                notes.Add($"  exports {relative} ({slice.Architecture}): {older.Tfm} {slice.Exports.Count} -> {newer.Tfm} {counterpart.Exports.Count} ({missing.Count} missing)");
                if (missing.Count > 0)
                    problems.Add($"native/{newer.Tfm}/{relative} ({slice.Architecture}) lacks {missing.Count} export(s) of the older copy {older.Tfm} ({string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? ", …" : "")}): "
                                 + "a newer copy's native payload must contain every older copy's exports.");
            }
        }
        return problems;
    }

    // The manifest hash symbol differs per copy by design.
    private const string SwiftTransitionManifestFormatPrefix = BindingsGeneration.SwiftTransitionManifestFormat.NativeSymbolPrefix;

    /// <summary>
    /// A dynamic image of an xcframework slice: a framework's binary (<c>X.framework/X</c> flat or
    /// <c>X.framework/Versions/A/X</c>) or a bare dylib slice.
    /// </summary>
    private static bool IsDynamicImage(string path)
    {
        if (path.EndsWith(".dylib", StringComparison.Ordinal) && !path.Contains(".dSYM/", StringComparison.Ordinal))
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

    /// <summary>The public API of an assembly: visible types, and their public or protected methods and fields with decoded signatures.</summary>
    public static HashSet<string> PublicApi(byte[] image)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var pe = new PEReader(new MemoryStream(image, writable: false));
        if (!pe.HasMetadata)
            return set;
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
        return set;
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
