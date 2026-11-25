// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// How `nuke fetch` archives a source library that is only a Swift package (a <c>Package.swift</c>
/// at the clone root and no Xcode project or workspace beside it).
///
/// <para>An Xcode project builds a framework target, and the fetch's <c>MACH_O_TYPE=mh_dylib</c>
/// keeps it dynamic. A package's library product of automatic type is not a framework: Xcode
/// archives its target as an object file, a product type that links with
/// <c>LINK_WITH_STANDARD_LIBRARIES=NO</c>. Overriding that target to <c>mh_dylib</c> therefore
/// links a dylib without libSystem or the compiler runtime, and Xcode 26.3 does exactly that:
/// every such archive fails with undefined <c>_malloc</c>, <c>__Block_copy</c> and
/// <c>___chkstk_darwin</c>. The fetch instead declares the product <c>type: .dynamic</c>, which
/// Xcode archives as <c>usr/local/lib/&lt;Product&gt;.framework</c>, and passes no
/// <c>MACH_O_TYPE</c> for a package.</para>
/// </summary>
public static class SwiftPackageArchive
{
    /// <summary>
    /// Whether xcodebuild run in <paramref name="sourceDirectory"/> builds the Swift package rather
    /// than an Xcode project: a manifest at the root, no project named by the product, and no
    /// <c>.xcodeproj</c> or <c>.xcworkspace</c> at the root (xcodebuild prefers those).
    /// </summary>
    public static bool IsPackageBuild(string sourceDirectory, string? project)
    {
        if (!string.IsNullOrEmpty(project)) return false;
        if (!File.Exists(Path.Combine(sourceDirectory, "Package.swift"))) return false;
        return !Directory.EnumerateDirectories(sourceDirectory)
            .Any(d => d.EndsWith(".xcodeproj", StringComparison.Ordinal)
                      || d.EndsWith(".xcworkspace", StringComparison.Ordinal));
    }

    /// <summary>The root manifests SwiftPM may read: <c>Package.swift</c> and its version-specific variants.</summary>
    public static IReadOnlyList<string> Manifests(string sourceDirectory) =>
        Directory.EnumerateFiles(sourceDirectory, "Package*.swift")
            .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^Package(@swift-[0-9.]+)?\.swift$"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Declares the library product <paramref name="product"/> dynamic in a manifest's text. A
    /// product with no <c>type:</c> gains <c>type: .dynamic</c>; any other type argument
    /// (<c>.static</c>, <c>nil</c>, a qualified <c>LibraryType</c>) is replaced by it. Returns the text unchanged when the manifest declares no such library
    /// product, so the caller can require that at least one manifest did.
    /// </summary>
    public static string DeclareProductDynamic(string manifest, string product, out bool declared)
    {
        var count = 0;
        var pattern = new Regex(
            @"(\.library\s*\(\s*name\s*:\s*""" + Regex.Escape(product) + @"""\s*,)(\s*type\s*:[^,()]*,)?",
            RegexOptions.CultureInvariant);
        var result = pattern.Replace(manifest, m =>
        {
            count++;
            return m.Groups[1].Value + " type: .dynamic,";
        });
        declared = count > 0;
        return result;
    }
}
