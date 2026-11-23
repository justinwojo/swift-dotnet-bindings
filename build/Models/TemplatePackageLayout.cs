// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The layout rule of the <c>SwiftBindings.Templates</c> nupkg: every template file ships exactly
/// once, under <c>content/</c>.
/// </summary>
/// <remarks>
/// The template engine scans the whole installed package for <c>.template.config</c> folders, so a
/// second copy of a template anywhere in the package (NuGet's default also packs <c>Content</c> items
/// under <c>contentFiles/any/&lt;tfm&gt;/</c>) installs the same template identity twice, and every
/// <c>dotnet new install</c> warns that two templates share it. Kept dependency-free (no Nuke types)
/// so it can be link-compiled into the unit-test project and asserted directly.
/// </remarks>
public static class TemplatePackageLayout
{
    /// <summary>The package folder the template files ship under.</summary>
    public const string ContentRoot = "content/";

    const string ContentFilesRoot = "contentFiles/";
    const string TemplateJsonSuffix = ".template.config/template.json";

    /// <summary>
    /// Returns one line per layout violation in the given nupkg entry names (forward-slash paths as
    /// a zip reports them); empty when the package ships each template file once under
    /// <c>content/</c> and holds exactly one template.
    /// </summary>
    public static IReadOnlyList<string> Violations(IEnumerable<string> entryNames)
    {
        var entries = entryNames.ToList();
        var violations = new List<string>();

        var templateJsons = entries.Where(e => e.EndsWith(TemplateJsonSuffix, StringComparison.Ordinal)).ToList();
        if (templateJsons.Count != 1)
            violations.Add($"expected exactly one '{TemplateJsonSuffix}', found {templateJsons.Count}" +
                           (templateJsons.Count == 0 ? "" : $": {string.Join(", ", templateJsons)}"));

        foreach (var entry in entries.Where(e => e.StartsWith(ContentFilesRoot, StringComparison.Ordinal)))
            violations.Add($"'{entry}' ships under {ContentFilesRoot}; template files belong under {ContentRoot} only");

        // A template file is anything under content/ or contentFiles/<language>/<tfm>/. Reduce each
        // to its path below that root so the same file shipped in both folders (or twice in one)
        // collides.
        foreach (var group in entries
                     .Select(e => (Entry: e, Relative: RelativeToContentRoot(e)))
                     .Where(x => x.Relative is not null)
                     .GroupBy(x => x.Relative!, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            violations.Add($"template file '{group.Key}' ships {group.Count()} times: " +
                           string.Join(", ", group.Select(x => x.Entry)));
        }

        return violations;
    }

    // content/<path> → <path>; contentFiles/<language>/<tfm>/<path> → <path>; anything else → null.
    static string? RelativeToContentRoot(string entry)
    {
        if (entry.StartsWith(ContentRoot, StringComparison.Ordinal))
            return entry.Substring(ContentRoot.Length);
        if (entry.StartsWith(ContentFilesRoot, StringComparison.Ordinal))
        {
            var parts = entry.Substring(ContentFilesRoot.Length).Split('/', 3);
            return parts.Length == 3 ? parts[2] : entry;
        }
        return null;
    }
}
