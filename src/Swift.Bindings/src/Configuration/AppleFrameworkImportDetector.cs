// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;

namespace BindingsGeneration;

/// <summary>
/// A cross-module dependency detected from a swiftinterface file's <c>import</c> lines
/// and resolved to a registered SwiftBindings.Apple binding-package ID.
/// </summary>
public sealed record DetectedAppleFrameworkDependency
{
    /// <summary>The Swift module name (e.g., "RealityFoundation").</summary>
    public required string ModuleName { get; init; }

    /// <summary>The NuGet package ID (e.g., "SwiftBindings.Apple.RealityFoundation").</summary>
    public required string PackageId { get; init; }

    /// <summary>The bounded version range (e.g., "[26.2.1,26.3.0)").</summary>
    public required string VersionRange { get; init; }
}

/// <summary>
/// Parses a <c>.swiftinterface</c> file's <c>import</c> lines and resolves them to
/// SwiftBindings.Apple.&lt;Module&gt; package IDs registered in <c>apple-frameworks.json</c>.
///
/// Used by apple-framework-mode (<c>SwiftAppleFrameworkTarget</c>) so the SDK can auto-inject
/// <c>&lt;PackageReference&gt;</c> items for cross-Apple-framework dep edges (e.g., RealityKit's
/// public API references RealityFoundation types). Out-of-tree from the binding-generation
/// pipeline — the MSBuild SDK shells out via <c>--detect-apple-cross-module-deps</c>.
///
/// Modules without a registered <c>packageId</c> (markers like <c>Swift</c>, <c>_Concurrency</c>,
/// <c>simd</c>, and Apple SDK modules that don't ship as standalone binding packages) are
/// silently dropped — only modules with a known binding package emit a dep edge.
/// </summary>
public static class AppleFrameworkImportDetector
{
    // Match `import Module`, `@_exported import Module`, `@_implementationOnly import Module`,
    // `public import Module`, `@_exported public import Module`, etc. We anchor at line start
    // (multiline mode) and require the import keyword as the first non-attribute, non-access-
    // modifier token. The module name is the first dotted-or-bare identifier after `import`.
    // We deliberately ignore `import struct Foundation.URL`-style submember imports because
    // they still pull in the leading module — for our purposes only the leading module matters.
    //
    // Why access modifiers matter: Swift 5.9+ supports access-controlled imports
    // (SE-0409 — `public import`, `private import`, etc.), and Xcode 26.2 SDK swiftinterfaces
    // emit `public import` extensively (e.g., ImagePlayground re-exports its public API
    // surface that way). Without consuming the modifier here, those `public import` lines
    // wouldn't match and the dep edge would silently miss the nuspec.
    private const string AccessModifiers = @"public|private|internal|fileprivate|open|package";
    private static readonly Regex ImportRegex = new(
        @"^\s*(?:@[A-Za-z_][A-Za-z0-9_]*(?:\([^)]*\))?\s+)*(?:(?:" + AccessModifiers + @")\s+)?(?:@[A-Za-z_][A-Za-z0-9_]*(?:\([^)]*\))?\s+)*import\s+(?:(?:typealias|struct|class|enum|protocol|let|var|func)\s+)?([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Parses a swiftinterface's text content and returns the set of imported module names.
    /// Deduplicates and preserves first-seen order. Drops the leading-comment lines
    /// (<c>// swift-interface-format-version</c> etc.) implicitly because the regex requires
    /// the <c>import</c> keyword.
    /// </summary>
    public static List<string> ExtractImports(string swiftInterfaceText)
    {
        if (string.IsNullOrEmpty(swiftInterfaceText))
            return new List<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (Match match in ImportRegex.Matches(swiftInterfaceText))
        {
            var module = match.Groups[1].Value;
            if (string.IsNullOrEmpty(module))
                continue;
            if (seen.Add(module))
                result.Add(module);
        }

        return result;
    }

    /// <summary>
    /// Resolves a list of imported module names to dep edges, filtering out the current
    /// module (self-reference), markers, and unregistered modules.
    /// </summary>
    /// <param name="imports">Module names extracted from the swiftinterface.</param>
    /// <param name="currentModule">The module being generated; self-references are dropped.</param>
    /// <param name="appleVersion">Apple SDK train version (e.g., "26.2.1") used to compute the bounded version range.</param>
    /// <returns>Resolved dep edges in deterministic (alphabetical) order. Unregistered modules silently dropped.</returns>
    public static List<DetectedAppleFrameworkDependency> ResolveDependencies(
        IEnumerable<string> imports,
        string currentModule,
        string appleVersion)
    {
        var versionRange = ComputeBoundedVersionRange(appleVersion);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DetectedAppleFrameworkDependency>();

        foreach (var module in imports)
        {
            if (string.IsNullOrEmpty(module))
                continue;
            // Skip self-references. RealityKit's swiftinterface doesn't `import RealityKit`
            // but the umbrella-module re-export pattern (RealityFoundation has
            // compileImportModule="RealityKit") means future modules might.
            if (string.Equals(module, currentModule, StringComparison.Ordinal))
                continue;
            if (!seen.Add(module))
                continue;
            if (!AppleFrameworkRegistry.TryGetPackageId(module, out var packageId))
                continue;
            result.Add(new DetectedAppleFrameworkDependency
            {
                ModuleName = module,
                PackageId = packageId,
                VersionRange = versionRange,
            });
        }

        // Sort for deterministic stdout (the MSBuild side parses line-by-line; ordering
        // affects the order of injected PackageReference items, which surfaces in nuspec).
        result.Sort((a, b) => string.CompareOrdinal(a.ModuleName, b.ModuleName));
        return result;
    }

    /// <summary>
    /// Computes a bounded NuGet version range covering one Apple SDK train minor cycle:
    /// <c>[X.Y.Z,X.(Y+1).0)</c>. Each Apple SDK train (Xcode minor release) produces
    /// a coordinated set of SwiftBindings.Apple.&lt;Module&gt; packages at the same Y.Z, so the
    /// bounded form ensures cross-framework deps within the same train resolve to a
    /// consistent set without floating into the next train.
    /// </summary>
    public static string ComputeBoundedVersionRange(string appleVersion)
    {
        if (string.IsNullOrWhiteSpace(appleVersion))
            throw new ArgumentException("appleVersion must not be empty.", nameof(appleVersion));

        var parts = appleVersion.Split('.');
        if (parts.Length < 2)
            throw new ArgumentException(
                $"appleVersion '{appleVersion}' must be at least 'major.minor' (e.g. '26.2.1').",
                nameof(appleVersion));

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major))
            throw new ArgumentException($"appleVersion major component '{parts[0]}' is not a number.", nameof(appleVersion));
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor))
            throw new ArgumentException($"appleVersion minor component '{parts[1]}' is not a number.", nameof(appleVersion));

        var lower = appleVersion;
        var upper = $"{major.ToString(CultureInfo.InvariantCulture)}.{(minor + 1).ToString(CultureInfo.InvariantCulture)}.0";
        return $"[{lower},{upper})";
    }

    /// <summary>
    /// Convenience wrapper: reads a swiftinterface file from disk and returns the resolved
    /// dep edges. Throws <see cref="FileNotFoundException"/> if the file is missing.
    /// </summary>
    public static List<DetectedAppleFrameworkDependency> Detect(
        string swiftInterfacePath,
        string currentModule,
        string appleVersion)
    {
        if (!File.Exists(swiftInterfacePath))
            throw new FileNotFoundException("swiftinterface file not found.", swiftInterfacePath);

        var text = File.ReadAllText(swiftInterfacePath);
        var imports = ExtractImports(text);
        return ResolveDependencies(imports, currentModule, appleVersion);
    }
}
