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

    // Matches imports that are explicitly non-public: `@_implementationOnly`, `private`,
    // `internal`, `fileprivate`, `package`. These imports don't propagate to consumers OUTSIDE
    // the bound module's Swift package boundary — and the generated wrapper is a separate
    // module produced by swiftc with its own `-module-name`, NOT a package-member of the bound
    // source. Re-emitting any of these as plain `import X` would force swiftc to resolve X even
    // though no public surface needs it (X is often a C++-only sibling like absl/grpc/leveldb
    // that swiftc cannot import; or for `package`, a peer module the wrapper has no package-
    // boundary access to).
    private static readonly Regex NonPublicImportRegex = new(
        @"^\s*(?:@[A-Za-z_][A-Za-z0-9_]*(?:\([^)]*\))?\s+)*(?:@_implementationOnly|private|internal|fileprivate|package)\s+(?:@[A-Za-z_][A-Za-z0-9_]*(?:\([^)]*\))?\s+)*import\s+(?:(?:typealias|struct|class|enum|protocol|let|var|func)\s+)?([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // Structured form used by the closure preflight (ExtractImportEdges): captures the whole
    // attribute/access-modifier PREFIX before `import` (group "prefix") so we can read @_exported /
    // @_implementationOnly and the access level, plus the leading module name (group "module"). The
    // prefix is inspected separately rather than encoded as alternatives so an attribute appearing
    // either before OR after the access modifier is handled uniformly. This is the single matcher for
    // every `import` line: ExtractImports (the bare-name projection) and the closure-preflight edge
    // reader both derive from it, so there is no second matcher to keep in lockstep. NonPublicImportRegex
    // below is a deliberate SUBSET (the non-public modifiers only), not a parallel full matcher.
    private static readonly Regex ImportEdgeRegex = new(
        @"^[ \t]*(?<prefix>(?:@[A-Za-z_][A-Za-z0-9_]*(?:\([^)]*\))?\s+|(?:" + AccessModifiers + @")\s+)*)import\s+(?:(?:typealias|struct|class|enum|protocol|let|var|func)\s+)?(?<module>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex ExportedAttrRegex = new(@"@_exported\b", RegexOptions.Compiled);
    private static readonly Regex ImplementationOnlyAttrRegex = new(@"@_implementationOnly\b", RegexOptions.Compiled);

    /// <summary>
    /// Parses a swiftinterface's text content and returns the set of imported module names.
    /// Deduplicates and preserves first-seen order. Drops the leading-comment lines
    /// (<c>// swift-interface-format-version</c> etc.) implicitly because the regex requires
    /// the <c>import</c> keyword.
    /// </summary>
    public static List<string> ExtractImports(string swiftInterfaceText)
    {
        // Compatibility projection over the structured ExtractImportEdges: bare module names,
        // deduplicated, first-seen order preserved. The interface path is irrelevant to this
        // projection, so a placeholder is passed.
        if (string.IsNullOrEmpty(swiftInterfaceText))
            return new List<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var edge in ExtractImportEdges(swiftInterfaceText, interfacePath: string.Empty))
        {
            if (seen.Add(edge.ModuleName))
                result.Add(edge.ModuleName);
        }
        return result;
    }

    /// <summary>
    /// Parses a swiftinterface's text content into structured <see cref="ImportEdge"/>s, one per
    /// <c>import</c> declaration, in source order (NOT deduplicated). Each edge carries the imported
    /// module name, the access level, whether it is <c>@_exported</c> / <c>@_implementationOnly</c>,
    /// the originating <paramref name="interfacePath"/>, and the 1-based line number — everything the
    /// closure preflight needs to attribute a missing-module obligation to its importer and evidence line.
    /// </summary>
    /// <param name="swiftInterfaceText">The full text of the <c>.swiftinterface</c>.</param>
    /// <param name="interfacePath">Absolute path recorded on each edge (used for the structured error).</param>
    public static List<ImportEdge> ExtractImportEdges(string swiftInterfaceText, string interfacePath)
    {
        var result = new List<ImportEdge>();
        if (string.IsNullOrEmpty(swiftInterfaceText))
            return result;

        foreach (Match match in ImportEdgeRegex.Matches(swiftInterfaceText))
        {
            var module = match.Groups["module"].Value;
            if (string.IsNullOrEmpty(module))
                continue;

            var prefix = match.Groups["prefix"].Value;
            result.Add(new ImportEdge
            {
                ModuleName = module,
                Access = ClassifyAccess(prefix),
                IsExported = ExportedAttrRegex.IsMatch(prefix),
                IsImplementationOnly = ImplementationOnlyAttrRegex.IsMatch(prefix),
                InterfacePath = interfacePath,
                Line = LineOf(swiftInterfaceText, match.Index),
            });
        }

        return result;
    }

    /// <summary>Reads the first access keyword out of an import's attribute/modifier prefix.</summary>
    private static ImportAccess ClassifyAccess(string prefix)
    {
        foreach (Match token in AccessTokenRegex.Matches(prefix))
        {
            switch (token.Value)
            {
                case "public":
                case "open":       return ImportAccess.Public;
                case "package":    return ImportAccess.Package;
                case "internal":   return ImportAccess.Internal;
                case "fileprivate": return ImportAccess.FilePrivate;
                case "private":    return ImportAccess.Private;
            }
        }
        return ImportAccess.Plain;
    }

    // A whole-word access keyword. Anchored on word boundaries so `@_implementationOnly` (which
    // contains no access keyword) and attribute names never spoof one.
    private static readonly Regex AccessTokenRegex = new(
        @"\b(?:" + AccessModifiers + @")\b", RegexOptions.Compiled);

    /// <summary>1-based line number of the character offset <paramref name="index"/> in <paramref name="text"/>.</summary>
    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    /// <summary>
    /// Returns the set of module names imported via non-public access (<c>@_implementationOnly</c>,
    /// <c>private</c>, <c>internal</c>, <c>fileprivate</c>). Callers that re-emit imports into a
    /// downstream wrapper module use this to skip imports that aren't transitively visible — the
    /// wrapper inherits the bound module's public surface only, so non-public imports MUST NOT be
    /// re-emitted (they'd force swiftc to resolve C++-only siblings like absl/grpc/leveldb that
    /// the wrapper has no reason to load).
    /// </summary>
    public static HashSet<string> ExtractNonPublicImports(string swiftInterfaceText)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(swiftInterfaceText))
            return result;

        foreach (Match match in NonPublicImportRegex.Matches(swiftInterfaceText))
        {
            var module = match.Groups[1].Value;
            if (!string.IsNullOrEmpty(module))
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
            // Skip self-references, in BOTH spellings a module's own types can carry.
            //
            // The plain spelling is the obvious one. The second is the umbrella: when Apple marks a
            // module @_implementationOnly through another (RealityFoundation is declared with
            // compileImportModule="RealityKit"), the canonical Swift names in the source module's own
            // ABI JSON are written with the UMBRELLA prefix — RealityFoundation's `Entity` appears as
            // `RealityKit.Entity`. Those are not cross-framework references; they are this module's
            // own types wearing the umbrella name. Emitting a PackageReference for them points the
            // package at the framework it is a facet of, which is a dependency cycle: the umbrella's
            // own binding legitimately references this one, so NuGet sees
            // RealityKit -> RealityFoundation -> RealityKit and fails the restore.
            //
            // Deliberately ASYMMETRIC — only the current module's umbrella is dropped, never the
            // reverse. Generating the umbrella (RealityKit) and seeing an import of a source module
            // (RealityFoundation) IS a real edge: they ship as separate packages, and the umbrella's
            // emitted C# genuinely names the source module's types.
            if (string.Equals(module, currentModule, StringComparison.Ordinal))
                continue;
            if (string.Equals(module, AppleFrameworkRegistry.MapModuleToCompileImport(currentModule), StringComparison.Ordinal))
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
        // Use the BROAD ExtractImports set on purpose — non-public imports
        // (@_implementationOnly / private / internal import) are deliberately NOT
        // filtered here, unlike the wrapper re-emission path in ModuleHandler which
        // drops them via ExtractNonPublicImports. The two paths answer different
        // questions:
        //   * Wrapper re-emission asks "may the generated wrapper module write
        //     `import X`?" — no, if X is non-public (often a C++-only sibling swiftc
        //     can't import), so it filters them out.
        //   * Dependency detection (here) asks "does the consumer's package need X's
        //     binding package present at restore/runtime?" — YES even for a non-public
        //     import: this binding's compiled dylib still LINKS X, so X.dylib must load
        //     at runtime, which means the consumer must transitively pull X's package.
        //     Dropping non-public imports here would ship a nupkg whose dylib references
        //     an absent sibling → DllNotFound. Registry filtering in ResolveDependencies
        //     still keeps this to modules that actually ship as binding packages (system
        //     frameworks present in the OS are unregistered and drop out).
        var imports = ExtractImports(text);
        return ResolveDependencies(imports, currentModule, appleVersion);
    }
}
