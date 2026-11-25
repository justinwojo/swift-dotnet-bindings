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
    /// Declares the library product <paramref name="product"/> dynamic by appending a top-level
    /// statement to the manifest rather than editing its <c>.library(...)</c> call: the package's
    /// <c>products</c> are mapped so the product named <paramref name="product"/>, when it is a
    /// library, becomes <c>.library(name:type: .dynamic, targets:)</c> over the same targets.
    /// Swift evaluates it after the package is built, so it sees the product however the manifest
    /// spells it (a computed <c>type:</c>, a name held in a constant, a product list built in a
    /// helper) and nothing a comment holds. The statement names the variable the manifest binds
    /// its <c>Package</c> to (<see cref="PackageVariableName"/>); the caller proves the result
    /// with <c>swift package dump-package</c> (<see cref="LibraryTypeOf"/>).
    /// </summary>
    public static string AppendDynamicProductDeclaration(string manifest, string product)
    {
        var literal = SwiftStringLiteral(product);
        var package = PackageVariableName(manifest);
        var separator = manifest.EndsWith('\n') ? "\n" : "\n\n";
        return manifest + separator
            + $"// Appended by nuke fetch: archive the library product {literal} as a dynamic framework.\n"
            + $"{package}.products = {package}.products.map {{ product in\n"
            + $"    guard product.name == {literal}, let library = product as? Product.Library else {{ return product }}\n"
            + "    return .library(name: library.name, type: .dynamic, targets: library.targets)\n"
            + "}\n";
    }

    /// <summary>
    /// The top-level variable a manifest binds its <c>Package</c> to (<c>let package = Package(</c>,
    /// by convention), read with comments removed, or <c>package</c> when no binding reads as one;
    /// a wrong guess fails <c>dump-package</c>, loudly.
    /// </summary>
    public static string PackageVariableName(string manifest)
    {
        var code = Regex.Replace(manifest, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        code = Regex.Replace(code, @"//[^\n]*", "");
        var match = Regex.Match(code,
            @"^\s*(?:public\s+)?(?:let|var)\s+(\w+)\s*(?::\s*(?:PackageDescription\.)?Package\s*)?=\s*(?:(?:PackageDescription\.)?Package|\.init)\s*\(",
            RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : "package";
    }

    /// <summary>
    /// The library type <c>swift package dump-package</c> reports for <paramref name="product"/>
    /// (<c>dynamic</c>, <c>static</c>, <c>automatic</c>), or null when the package declares no
    /// library product of that name.
    /// </summary>
    public static string? LibraryTypeOf(string dumpPackageJson, string product)
    {
        using var document = System.Text.Json.JsonDocument.Parse(dumpPackageJson);
        if (!document.RootElement.TryGetProperty("products", out var products))
            return null;
        foreach (var entry in products.EnumerateArray())
        {
            if (entry.GetProperty("name").GetString() != product)
                continue;
            if (entry.TryGetProperty("type", out var type)
                && type.TryGetProperty("library", out var library)
                && library.ValueKind == System.Text.Json.JsonValueKind.Array
                && library.GetArrayLength() > 0)
                return library[0].GetString();
        }
        return null;
    }

    static string SwiftStringLiteral(string value)
    {
        var escaped = new System.Text.StringBuilder("\"");
        foreach (var c in value)
        {
            escaped.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        }
        return escaped.Append('"').ToString();
    }
}
