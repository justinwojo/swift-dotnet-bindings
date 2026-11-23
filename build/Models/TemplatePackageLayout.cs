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
        else if (!templateJsons[0].StartsWith(ContentRoot, StringComparison.Ordinal))
            violations.Add($"'{templateJsons[0]}' ships outside {ContentRoot}");

        foreach (var entry in entries.Where(e => e.StartsWith(ContentFilesRoot, StringComparison.Ordinal)))
            violations.Add($"'{entry}' ships under {ContentFilesRoot}; template files belong under {ContentRoot} only");

        // A zip can hold two entries of one name; the second is a second copy all the same.
        foreach (var group in entries.GroupBy(e => e, StringComparer.Ordinal).Where(g => g.Count() > 1))
            violations.Add($"'{group.Key}' ships {group.Count()} times");

        return violations;
    }
}
