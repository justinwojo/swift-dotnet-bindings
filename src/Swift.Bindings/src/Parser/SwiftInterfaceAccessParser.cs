// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

[assembly: InternalsVisibleTo("Swift.Bindings.Unit.Tests")]

namespace BindingsGeneration;

/// <summary>
/// Parses a .swiftinterface file to extract access levels for declarations
/// that are ambiguous in the ABI JSON.
///
/// Problem: @inlinable internal declarations with explicit access control
/// (declAttributes: [AccessControl, Inlinable]) are indistinguishable from
/// @inlinable public declarations in the ABI JSON. The swiftinterface is
/// the only reliable source for the actual access level.
///
/// This parser extracts a set of "TypeName.printedName" keys for all
/// internal members, which can then be cross-referenced during ABI parsing
/// to correctly mark these declarations as module-internal.
///
/// Limitation: keys are unqualified ("AES.encrypt(block:)"), not module-qualified.
/// This is safe because a single swiftinterface covers one module, and the ABI
/// parser also processes one module at a time with unqualified parentDecl.Name.
/// </summary>
public static class SwiftInterfaceAccessParser
{
    // Regex for type declarations: matches class/struct/enum/actor/protocol
    // with optional attributes, access modifiers, and 'final' keyword.
    private static readonly Regex TypeDeclRegex = new(
        @"(?:public|internal|open)\s+(?:final\s+)?(?:class|struct|enum|actor|protocol)\s+(\w+)",
        RegexOptions.Compiled);

    // Regex for extension declarations: matches "extension Module.Type" and
    // extracts the full qualified name. The unqualified type name is extracted
    // separately by taking the last dot-component.
    // Handles: extension Mod.Type {, extension Mod.Type : Proto {, extension Mod.Type where ... {
    private static readonly Regex ExtensionDeclRegex = new(
        @"extension\s+([\w.]+)",
        RegexOptions.Compiled);

    // Regex for internal func declarations
    private static readonly Regex InternalFuncRegex = new(
        @"internal\s+(?:final\s+)?(?:static\s+)?func\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    // Regex for internal var/let declarations
    private static readonly Regex InternalVarRegex = new(
        @"internal\s+(?:final\s+)?(?:var|let)\s+(\w+)",
        RegexOptions.Compiled);

    // Regex for internal init declarations
    private static readonly Regex InternalInitRegex = new(
        @"internal\s+(?:convenience\s+)?init\s*\(",
        RegexOptions.Compiled);

    // Regex for public/open type declarations (excludes internal)
    private static readonly Regex PublicTypeDeclRegex = new(
        @"(?:public|open)\s+(?:final\s+)?(?:class|struct|enum|actor|protocol)\s+(\w+)",
        RegexOptions.Compiled);

    // Regex for @MainActor annotation (fully-qualified or bare)
    private static readonly Regex MainActorAnnotationRegex = new(
        @"@(?:_Concurrency\.)?MainActor",
        RegexOptions.Compiled);

    // Regex for actor declarations: "public actor Name" or "open actor Name"
    private static readonly Regex ActorDeclRegex = new(
        @"(?:public|open)\s+actor\s+(\w+)",
        RegexOptions.Compiled);

    // Regex for nonisolated member declarations
    private static readonly Regex NonisolatedRegex = new(
        @"nonisolated\s+(?:public|open|final|var|let|func|static|class)",
        RegexOptions.Compiled);

    // Regex for public/open func declarations (for member-level actor isolation detection)
    private static readonly Regex PublicFuncRegex = new(
        @"(?:public|open)\s+(?:final\s+)?(?:static\s+|class\s+)?(?:mutating\s+)?func\s+(\w+)\s*(?:<[^>]*>\s*)?\(",
        RegexOptions.Compiled);

    // Regex for public/open var/let declarations (for member-level actor isolation detection)
    private static readonly Regex PublicVarRegex = new(
        @"(?:public|open)\s+(?:final\s+)?(?:var|let)\s+(\w+)",
        RegexOptions.Compiled);

    // Regex for public/open init declarations
    private static readonly Regex PublicInitRegex = new(
        @"(?:public|open)\s+(?:convenience\s+)?init\s*\(",
        RegexOptions.Compiled);

    // Bare func/var/init regexes for protocol member declarations.
    // Protocol requirements in .swiftinterface files have no access modifier:
    //   @_Concurrency.MainActor func removeContent()
    //   var identifier: Int { get }
    // Used as fallbacks when PublicFuncRegex/PublicVarRegex/PublicInitRegex don't match.
    private static readonly Regex BareFuncRegex = new(
        @"(?:static\s+|class\s+)?(?:mutating\s+)?func\s+(\w+)\s*(?:<[^>]*>\s*)?\(",
        RegexOptions.Compiled);

    private static readonly Regex BareVarRegex = new(
        @"(?:var|let)\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex BareInitRegex = new(
        @"(?:convenience\s+)?init\s*\(",
        RegexOptions.Compiled);

    // Detects any member-level declaration keyword (func/init/var/let/subscript/typealias/case/deinit)
    // anywhere on the line. Used by deferred-annotation logic to avoid carrying an annotation
    // forward when the same line already contains a member that consumes it.
    private static readonly Regex MemberDeclKeywordRegex = new(
        @"\b(?:func|init|deinit|subscript|typealias|case|operator)\b|\b(?:var|let)\s+\w+",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns true when the line contains a member-level declaration (func/init/var/let/subscript/...).
    /// Used by deferred-annotation parsers to distinguish a standalone annotation line from a
    /// complete annotated declaration that consumes the annotation on the same line.
    /// </summary>
    private static bool IsMemberDeclLine(string trimmed)
        => MemberDeclKeywordRegex.IsMatch(trimmed);

    // Regex for public/open subscript declarations
    private static readonly Regex PublicSubscriptRegex = new(
        @"(?:public|open)\s+(?:static\s+)?subscript\s*(?:<[^>]*>\s*)?\(",
        RegexOptions.Compiled);

    // Broader regex for public var/let — handles static, class, setter-visibility, and annotation prefixes.
    // Used by GetPublicMemberNames where we need to catch ALL public properties,
    // not just the subset needed for actor isolation detection.
    // Handles: public internal(set) var, public private(set) static var, etc.
    // Backtick-escaped identifiers (e.g., `operator`, `class`) are handled with `?(\w+)`?.
    private static readonly Regex BroadPublicVarRegex = new(
        @"(?:^|\s)(?:public|open)\s+(?:(?:final|static|class|lazy|weak|unowned|(?:internal|private|public)\(set\))\s+)*(?:var|let)\s+`?(\w+)`?",
        RegexOptions.Compiled);

    // Broader regex for public func — handles nonisolated, @objc, and other prefixes.
    private static readonly Regex BroadPublicFuncRegex = new(
        @"(?:^|\s)(?:public|open)\s+(?:(?:final|static|class|mutating|nonmutating|override)\s+)*func\s+(\w+)\s*(?:<[^>]*>\s*)?\(",
        RegexOptions.Compiled);

    // Broader regex for public init — handles convenience and other prefixes.
    private static readonly Regex BroadPublicInitRegex = new(
        @"(?:^|\s)(?:public|open)\s+(?:(?:convenience|required|override)\s+)*init\??\s*(?:<[^>]*>\s*)?\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a .swiftinterface file and returns a set of dot-qualified type paths
    /// declared as public or open (e.g., "OrderContainer.Status" for nested types,
    /// "ConstraintMaker" for top-level types).
    /// Types NOT in this set are internal to the module.
    /// </summary>
    /// <param name="swiftInterfacePath">Path to the .swiftinterface file.</param>
    /// <returns>Set of public type names, or empty set if parsing fails.</returns>
    public static HashSet<string> GetPublicTypeNames(string swiftInterfacePath)
    {
        var result = new HashSet<string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            var (openBraces, closeBraces) = CountBraces(line);

            // Check for public/open type declarations
            bool pushedScope = false;
            var publicTypeMatch = PublicTypeDeclRegex.Match(trimmed);
            if (publicTypeMatch.Success && openBraces > 0)
            {
                var typeName = publicTypeMatch.Groups[1].Value;
                typeStack.Push((typeName, braceDepth));
                pushedScope = true;

                // Build dot-qualified path from the type stack
                var qualifiedPath = string.Join(".", typeStack.Reverse().Select(t => t.Name));
                result.Add(qualifiedPath);
            }

            // Also track non-public type declarations (internal types that open a scope)
            // so we can properly track brace depth and nesting
            if (!pushedScope)
            {
                var anyTypeMatch = TypeDeclRegex.Match(trimmed);
                if (anyTypeMatch.Success && openBraces > 0)
                {
                    typeStack.Push((anyTypeMatch.Groups[1].Value, braceDepth));
                    pushedScope = true;
                }
            }

            // Track extensions — but do NOT add extension targets to the public type set.
            // Extensions are for external module types (e.g., "extension Swift.Int : ...")
            // and should not be treated as types defined in this module.
            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    // Strip module prefix (first component) to get the full type path.
                    // e.g., "CryptoKit.P256.Signing" → "P256.Signing" (not just "Signing").
                    // Extensions in swiftinterface files are always module-qualified.
                    var firstDotIdx = qualifiedName.IndexOf('.');
                    var typePath = firstDotIdx >= 0 ? qualifiedName.Substring(firstDotIdx + 1) : qualifiedName;
                    typeStack.Push((typePath, braceDepth));
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
            {
                typeStack.Pop();
            }
        }

        return result;
    }

    /// <summary>
    /// Returns a set of type names annotated with @MainActor / @_Concurrency.MainActor.
    /// Does NOT include custom actor declarations (those need different wrapper treatment).
    /// Type names use dot-qualified paths (e.g., "Outer.Inner" for nested types).
    /// </summary>
    public static HashSet<string> GetMainActorTypes(string swiftInterfacePath)
        => GetMainActorTypes(swiftInterfacePath, out _);

    /// <summary>
    /// Best-effort provenance overload of <see cref="GetMainActorTypes(string)"/>.
    /// <paramref name="positions"/> is keyed identically to the returned set (qualified
    /// type path) and points at the line/column of the type declaration that carried
    /// the <c>@MainActor</c> annotation. Lines and columns are 1-based.
    /// </summary>
    public static HashSet<string> GetMainActorTypes(
        string swiftInterfacePath,
        out Dictionary<string, SourcePosition> positions)
    {
        var result = new HashSet<string>();
        positions = new Dictionary<string, SourcePosition>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;
        bool pendingMainActor = false;

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var trimmed = line.TrimStart();

            var (openBraces, closeBraces) = CountBraces(line);

            // Check for @MainActor annotation on this line or pending from previous line
            bool hasMainActor = pendingMainActor || MainActorAnnotationRegex.IsMatch(trimmed);
            pendingMainActor = false;

            // If this line has @MainActor but no declaration, it's a pending annotation.
            // Also ensure the line doesn't already carry a complete member decl
            // (func/init/var/...) — in that case the annotation belongs to the member
            // and must not be carried forward to the next type declaration.
            if (hasMainActor && !TypeDeclRegex.IsMatch(trimmed) && openBraces == 0
                && !IsMemberDeclLine(trimmed))
            {
                pendingMainActor = true;
                braceDepth += openBraces - closeBraces;
                while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                    typeStack.Pop();
                continue;
            }

            // Check for type declarations
            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                var typeName = typeMatch.Groups[1].Value;
                typeStack.Push((typeName, braceDepth));
                pushedScope = true;

                // If this type has @MainActor and is NOT an actor keyword declaration
                if (hasMainActor && !ActorDeclRegex.IsMatch(trimmed))
                {
                    var qualifiedPath = string.Join(".", typeStack.Reverse().Select(t => t.Name));
                    result.Add(qualifiedPath);
                    int leading = line.Length - trimmed.Length;
                    int column = leading + typeMatch.Index + 1;
                    positions[qualifiedPath] = new SourcePosition(swiftInterfacePath, lineIndex + 1, column);
                }
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    // Strip module prefix (first component) to get the full type path.
                    // e.g., "CryptoKit.P256.Signing" → "P256.Signing" (not just "Signing").
                    // Extensions in swiftinterface files are always module-qualified.
                    var firstDotIdx = qualifiedName.IndexOf('.');
                    var typePath = firstDotIdx >= 0 ? qualifiedName.Substring(firstDotIdx + 1) : qualifiedName;
                    typeStack.Push((typePath, braceDepth));
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                typeStack.Pop();
        }

        return result;
    }

    /// <summary>
    /// Returns a set of type names declared with the 'actor' keyword (custom actors).
    /// Custom actors have implicit isolation to their own executor, NOT MainActor.
    /// </summary>
    public static HashSet<string> GetCustomActorTypes(string swiftInterfacePath)
    {
        var result = new HashSet<string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            var (openBraces, closeBraces) = CountBraces(line);

            bool pushedScope = false;
            var actorMatch = ActorDeclRegex.Match(trimmed);
            if (actorMatch.Success && openBraces > 0)
            {
                var typeName = actorMatch.Groups[1].Value;
                typeStack.Push((typeName, braceDepth));
                pushedScope = true;

                var qualifiedPath = string.Join(".", typeStack.Reverse().Select(t => t.Name));
                result.Add(qualifiedPath);
            }

            if (!pushedScope)
            {
                var typeMatch = TypeDeclRegex.Match(trimmed);
                if (typeMatch.Success && openBraces > 0)
                {
                    typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                    pushedScope = true;
                }
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    // Strip module prefix (first component) to get the full type path.
                    // e.g., "CryptoKit.P256.Signing" → "P256.Signing" (not just "Signing").
                    // Extensions in swiftinterface files are always module-qualified.
                    var firstDotIdx = qualifiedName.IndexOf('.');
                    var typePath = firstDotIdx >= 0 ? qualifiedName.Substring(firstDotIdx + 1) : qualifiedName;
                    typeStack.Push((typePath, braceDepth));
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                typeStack.Pop();
        }

        return result;
    }

    /// <summary>
    /// Regex for qualified imported global-actor annotations such as
    /// <c>@Dependency.ImagePipelineActor</c>. Requires at least one module-prefix
    /// segment and an <c>Actor</c> suffix on the leaf identifier. Excludes the
    /// built-in <c>MainActor</c> (which has its own <see cref="TypeDecl.IsMainActorIsolated"/>
    /// path) via negative lookahead. Qualification + <c>Actor</c> suffix is the
    /// strongest available signal short of consulting cross-module metadata; bare
    /// unqualified <c>@&lt;Name&gt;Actor</c> annotations are deliberately not matched
    /// here because they overlap with property wrappers and macros — the local-actor
    /// path covers them when the actor is declared in the same swiftinterface.
    /// </summary>
    private static readonly Regex ImportedCustomActorAnnotationRegex = new(
        @"@(?:\w+\.)+(?!MainActor\b)(\w*Actor)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns a set of qualified type paths for types annotated with a custom global actor
    /// (e.g., <c>@ImagePipelineActor class ImagePipeline</c>). Distinct from
    /// <see cref="GetCustomActorTypes"/>, which returns types declared with the
    /// <c>actor</c> keyword. The supplied <paramref name="customActorTypeNames"/> is the
    /// short-name set produced by <see cref="GetCustomActorTypes"/>; this method then
    /// scans declarations for matching <c>@&lt;ActorName&gt;</c> annotations and records the
    /// type path so the ABI parser can flag <see cref="TypeDecl.IsCustomActorIsolated"/>.
    /// Also detects fully-qualified imported global-actor annotations
    /// (<c>@&lt;Module&gt;.&lt;Name&gt;Actor</c>) via <see cref="ImportedCustomActorAnnotationRegex"/>,
    /// which lets the SWIFTBIND022 gate fire for types isolated to actors declared in
    /// other modules. Returns an empty set when the swiftinterface doesn't exist
    /// and there are neither local nor qualified imported annotations to scan for.
    /// </summary>
    public static HashSet<string> GetCustomActorIsolatedTypes(
        string swiftInterfacePath, HashSet<string>? customActorTypeNames)
        => new HashSet<string>(GetCustomActorIsolatorMap(swiftInterfacePath, customActorTypeNames).Keys);

    /// <summary>
    /// Same scan as <see cref="GetCustomActorIsolatedTypes"/>, but also records which actor
    /// short name annotates each type. The returned map's keys are qualified type paths
    /// (e.g., "ImagePrefetcher" or "Outer.ImageCache") and values are the matched actor's
    /// leaf identifier (e.g., "ImagePipelineActor"). The value is retained for diagnostics
    /// and SWIFTBIND022 skip-reason reporting and to give the parser context when tagging
    /// constructors as <c>IsAsync</c> for the async-factory rewrite — synchronous
    /// constructors on these types are wholesale-skipped (no <c>assumeIsolated</c> hop is
    /// emitted).
    /// </summary>
    public static Dictionary<string, string> GetCustomActorIsolatorMap(
        string swiftInterfacePath, HashSet<string>? customActorTypeNames)
    {
        var result = new Dictionary<string, string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        // GetCustomActorTypes returns qualified paths (e.g., "Outer.ImagePipelineActor"
        // for nested actors). Swift annotations on the consumer side use the leaf name
        // (e.g., `@ImagePipelineActor`), so normalize to short names before building the regex.
        var shortNames = customActorTypeNames?
            .Select(n => n.Substring(n.LastIndexOf('.') + 1))
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList()
            ?? new List<string>();

        // Local-actor regex is built only when there are short names to escape; otherwise
        // remains null and matching falls through to ImportedCustomActorAnnotationRegex.
        // The single capture group exposes the matched actor's leaf identifier so callers
        // can record it (used for SWIFTBIND022 diagnostics) without re-scanning the line.
        Regex? customActorRegex = null;
        if (shortNames.Count > 0)
        {
            var escapedNames = string.Join("|", shortNames.Select(Regex.Escape));
            customActorRegex = new Regex(
                @"@(?:\w+\.)?(" + escapedNames + @")\b",
                RegexOptions.Compiled);
        }

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;
        bool pendingCustomActor = false;
        string? pendingActorName = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            var (openBraces, closeBraces) = CountBraces(line);

            string? localActorName = null;
            if (customActorRegex != null)
            {
                var localMatch = customActorRegex.Match(trimmed);
                if (localMatch.Success)
                    localActorName = localMatch.Groups[1].Value;
            }

            string? importedActorName = null;
            {
                var importedMatch = ImportedCustomActorAnnotationRegex.Match(trimmed);
                if (importedMatch.Success)
                    importedActorName = importedMatch.Groups[1].Value;
            }

            bool localActorMatch = localActorName != null;
            bool importedActorMatch = importedActorName != null;
            bool hasCustomActor = pendingCustomActor || localActorMatch || importedActorMatch;

            // Pick the effective actor name: a deferred annotation wins, else the local
            // (same-module) match, else the imported qualified-name match. Local matches
            // take priority over imported because a same-module actor is more reliably
            // resolvable when the emitter looks it up later.
            string? matchedActorName = pendingCustomActor ? pendingActorName
                : localActorName ?? importedActorName;

            pendingCustomActor = false;
            pendingActorName = null;

            // Annotation on its own line — defer until the next decl line. We must NOT defer
            // when the same line already carries a member declaration (func/init/var/let/...),
            // because the annotation belongs to that member, not to the next line. Without this
            // check, an actor-isolated init/func would taint the next type declaration encountered
            // (e.g., `@<Actor> public init(...)` followed later by `public struct Other`).
            if (hasCustomActor && !TypeDeclRegex.IsMatch(trimmed) && openBraces == 0
                && !IsMemberDeclLine(trimmed))
            {
                pendingCustomActor = true;
                pendingActorName = matchedActorName;
                braceDepth += openBraces - closeBraces;
                while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                    typeStack.Pop();
                continue;
            }

            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                var typeName = typeMatch.Groups[1].Value;
                typeStack.Push((typeName, braceDepth));
                pushedScope = true;

                // Record only when the annotation lands on a non-actor type (the actor
                // declaration itself is a separate concept tracked by GetCustomActorTypes).
                if (hasCustomActor && !ActorDeclRegex.IsMatch(trimmed) && matchedActorName != null)
                {
                    var qualifiedPath = string.Join(".", typeStack.Reverse().Select(t => t.Name));
                    // First match wins — repeated `@Actor`-prefixed members on the same type
                    // shouldn't overwrite the type-level annotation.
                    if (!result.ContainsKey(qualifiedPath))
                        result[qualifiedPath] = matchedActorName;
                }
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    var firstDotIdx = qualifiedName.IndexOf('.');
                    var typePath = firstDotIdx >= 0 ? qualifiedName.Substring(firstDotIdx + 1) : qualifiedName;
                    typeStack.Push((typePath, braceDepth));
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                typeStack.Pop();
        }

        return result;
    }

    /// <summary>
    /// Returns a set of "QualifiedType.printedName" keys for members that are individually
    /// @MainActor-annotated (when the containing type is NOT globally @MainActor).
    /// Function keys use printed name format (e.g., "Outer.Inner.foo(_:bar:)") to distinguish overloads.
    /// Property keys use "QualifiedType.propName".
    /// Uses qualified type paths from the type stack to avoid nested-type name collisions.
    /// Handles multi-line function signatures via continuation buffer.
    /// </summary>
    public static HashSet<string> GetActorIsolatedMembers(string swiftInterfacePath)
        => GetActorIsolatedMembers(swiftInterfacePath, customActorTypeNames: null, mainActorMembers: out _);

    /// <summary>
    /// Extended overload that also detects custom actor annotations (e.g., @BlinkID.ProcessingActor)
    /// in addition to @MainActor. The customActorTypeNames set contains unqualified actor type names
    /// (e.g., "ProcessingActor") from GetCustomActorTypes().
    ///
    /// The mainActorMembers out-parameter receives only @MainActor-annotated members (a subset of
    /// the return value). This enables distinguishing @MainActor from custom actor isolation in
    /// the ABI parser, which is needed to emit correct wrapper annotations.
    /// </summary>
    public static HashSet<string> GetActorIsolatedMembers(
        string swiftInterfacePath, HashSet<string>? customActorTypeNames,
        out HashSet<string> mainActorMembers)
    {
        var result = new HashSet<string>();
        mainActorMembers = new HashSet<string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        // Build a regex for custom actor annotations: @Module.ActorName or @ActorName
        Regex? customActorRegex = null;
        if (customActorTypeNames != null && customActorTypeNames.Count > 0)
        {
            var escapedNames = string.Join("|", customActorTypeNames.Select(Regex.Escape));
            customActorRegex = new Regex(
                @"@(?:\w+\.)?(?:" + escapedNames + @")\b",
                RegexOptions.Compiled);
        }

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;
        bool pendingActorIsolated = false;
        bool pendingIsMainActor = false;
        // Multi-line continuation: (accumulated line, wasActorIsolated, wasMainActor)
        (string Line, bool IsActorIsolated, bool IsMainActor)? continuation = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Handle multi-line signature continuation
            if (continuation != null)
            {
                var accumulated = continuation.Value.Line + " " + trimmed;
                if (!HasUnmatchedOpenParen(accumulated))
                {
                    // Signature complete — process the full line
                    var wasActorIsolated = continuation.Value.IsActorIsolated;
                    var wasMainActor = continuation.Value.IsMainActor;
                    continuation = null;
                    if (wasActorIsolated && typeStack.Count > 0)
                    {
                        ProcessActorIsolatedMember(accumulated, typeStack, result);
                        if (wasMainActor)
                            ProcessActorIsolatedMember(accumulated, typeStack, mainActorMembers);
                    }
                    // Free function continuation (Finding 4: multiline free functions)
                    if (wasActorIsolated && typeStack.Count == 0)
                    {
                        var funcMatch = PublicFuncRegex.Match(accumulated);
                        if (funcMatch.Success)
                        {
                            var printedName = ExtractPrintedName(accumulated, funcMatch.Groups[1].Value);
                            result.Add(printedName);
                            if (wasMainActor)
                                mainActorMembers.Add(printedName);
                        }
                    }
                }
                else
                {
                    continuation = (accumulated, continuation.Value.IsActorIsolated, continuation.Value.IsMainActor);
                }
                continue;
            }

            var (openBraces, closeBraces) = CountBraces(line);

            // Check for @MainActor or custom actor annotation — track which kind
            bool isMainActorLine = MainActorAnnotationRegex.IsMatch(trimmed);
            bool isCustomActorLine = customActorRegex != null && customActorRegex.IsMatch(trimmed);
            bool hasActorAnnotation = pendingActorIsolated || isMainActorLine || isCustomActorLine;
            bool isMainActor = pendingIsMainActor || isMainActorLine;
            pendingActorIsolated = false;
            pendingIsMainActor = false;

            // Check for pending annotation (attribute on its own line).
            // Also check bare regexes — protocol members have no access modifier and no braces:
            //   @_Concurrency.MainActor func removeContent()
            if (hasActorAnnotation && !TypeDeclRegex.IsMatch(trimmed) &&
                !PublicFuncRegex.IsMatch(trimmed) && !PublicVarRegex.IsMatch(trimmed) &&
                !PublicInitRegex.IsMatch(trimmed) &&
                !BareFuncRegex.IsMatch(trimmed) && !BareVarRegex.IsMatch(trimmed) &&
                !BareInitRegex.IsMatch(trimmed) && openBraces == 0)
            {
                pendingActorIsolated = true;
                pendingIsMainActor = isMainActor;
                braceDepth += openBraces - closeBraces;
                while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                    typeStack.Pop();
                continue;
            }

            // Track type context
            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                pushedScope = true;
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    // Strip module prefix (first component) to get the full type path.
                    // e.g., "CryptoKit.P256.Signing" → "P256.Signing" (not just "Signing").
                    // Extensions in swiftinterface files are always module-qualified.
                    var firstDotIdx = qualifiedName.IndexOf('.');
                    var typePath = firstDotIdx >= 0 ? qualifiedName.Substring(firstDotIdx + 1) : qualifiedName;
                    typeStack.Push((typePath, braceDepth));
                }
            }

            // Check for member-level actor isolation (within a type context)
            if (hasActorAnnotation && typeStack.Count > 0 && !pushedScope)
            {
                // Check for multi-line signature (includes bare protocol members)
                if ((PublicFuncRegex.IsMatch(trimmed) || PublicInitRegex.IsMatch(trimmed) ||
                     BareFuncRegex.IsMatch(trimmed) || BareInitRegex.IsMatch(trimmed)) &&
                    HasUnmatchedOpenParen(trimmed))
                {
                    continuation = (trimmed, true, isMainActor);
                }
                else
                {
                    ProcessActorIsolatedMember(trimmed, typeStack, result);
                    if (isMainActor)
                        ProcessActorIsolatedMember(trimmed, typeStack, mainActorMembers);
                }
            }

            // Check for top-level actor-isolated free functions (no type context)
            if (hasActorAnnotation && typeStack.Count == 0 && !pushedScope)
            {
                var funcMatch = PublicFuncRegex.Match(trimmed);
                if (funcMatch.Success)
                {
                    // Multi-line: regex matches the func name but signature is incomplete
                    if (HasUnmatchedOpenParen(trimmed))
                    {
                        continuation = (trimmed, true, isMainActor);
                    }
                    else
                    {
                        // Single-line: complete signature
                        var printedName = ExtractPrintedName(trimmed, funcMatch.Groups[1].Value);
                        result.Add(printedName);
                        if (isMainActor)
                            mainActorMembers.Add(printedName);
                    }
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                typeStack.Pop();
        }

        return result;
    }

    /// <summary>
    /// Processes a single line for actor-isolated member detection and adds the key to the result set.
    /// </summary>
    private static void ProcessActorIsolatedMember(
        string line, Stack<(string Name, int Depth)> typeStack, HashSet<string> result)
    {
        var qualifiedType = string.Join(".", typeStack.Reverse().Select(t => t.Name));

        var funcMatch = PublicFuncRegex.Match(line);
        if (funcMatch.Success)
        {
            var printedName = ExtractPrintedName(line, funcMatch.Groups[1].Value);
            result.Add($"{qualifiedType}.{printedName}");
            return;
        }

        var varMatch = PublicVarRegex.Match(line);
        if (varMatch.Success)
        {
            result.Add($"{qualifiedType}.{varMatch.Groups[1].Value}");
            return;
        }

        if (PublicInitRegex.IsMatch(line))
        {
            var printedName = ExtractPrintedName(line, "init");
            result.Add($"{qualifiedType}.{printedName}");
            return;
        }

        // Fallback: protocol member declarations have no access modifier in .swiftinterface files
        // (e.g., "@_Concurrency.MainActor func removeContent()" inside a protocol body)
        var bareFuncMatch = BareFuncRegex.Match(line);
        if (bareFuncMatch.Success)
        {
            var printedName = ExtractPrintedName(line, bareFuncMatch.Groups[1].Value);
            result.Add($"{qualifiedType}.{printedName}");
            return;
        }

        var bareVarMatch = BareVarRegex.Match(line);
        if (bareVarMatch.Success)
        {
            result.Add($"{qualifiedType}.{bareVarMatch.Groups[1].Value}");
            return;
        }

        if (BareInitRegex.IsMatch(line))
        {
            var printedName = ExtractPrintedName(line, "init");
            result.Add($"{qualifiedType}.{printedName}");
        }
    }

    /// <summary>
    /// Returns a set of "QualifiedType.printedName" keys for members declared as nonisolated.
    /// These members opt out of their containing type's actor isolation.
    /// Function keys use printed name format (e.g., "Outer.Inner.foo(_:bar:)") to distinguish overloads.
    /// Property keys use "QualifiedType.propName".
    /// Uses qualified type paths from the type stack to avoid nested-type name collisions.
    /// Handles multi-line function signatures via continuation buffer.
    /// </summary>
    public static HashSet<string> GetNonisolatedMembers(string swiftInterfacePath)
    {
        var result = new HashSet<string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;
        string? continuationLine = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Handle multi-line signature continuation
            if (continuationLine != null)
            {
                continuationLine += " " + trimmed;
                if (!HasUnmatchedOpenParen(continuationLine))
                {
                    var completeLine = continuationLine;
                    continuationLine = null;
                    ProcessNonisolatedMember(completeLine, typeStack, result);
                }
                continue;
            }

            var (openBraces, closeBraces) = CountBraces(line);

            // Track type context
            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                pushedScope = true;
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    // Strip module prefix (first component) to get the full type path.
                    // e.g., "CryptoKit.P256.Signing" → "P256.Signing" (not just "Signing").
                    // Extensions in swiftinterface files are always module-qualified.
                    var firstDotIdx = qualifiedName.IndexOf('.');
                    var typePath = firstDotIdx >= 0 ? qualifiedName.Substring(firstDotIdx + 1) : qualifiedName;
                    typeStack.Push((typePath, braceDepth));
                }
            }

            // Check for nonisolated members (only within a type context)
            if (typeStack.Count > 0 && NonisolatedRegex.IsMatch(trimmed))
            {
                // Check for multi-line signature
                if ((AnyFuncRegex.IsMatch(trimmed) || AnyInitRegex.IsMatch(trimmed)) &&
                    HasUnmatchedOpenParen(trimmed))
                {
                    continuationLine = trimmed;
                }
                else
                {
                    ProcessNonisolatedMember(trimmed, typeStack, result);
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                typeStack.Pop();
        }

        return result;
    }

    /// <summary>
    /// Processes a single line for nonisolated member detection and adds the key to the result set.
    /// </summary>
    private static void ProcessNonisolatedMember(
        string line, Stack<(string Name, int Depth)> typeStack, HashSet<string> result)
    {
        var qualifiedType = string.Join(".", typeStack.Reverse().Select(t => t.Name));

        var funcMatch = AnyFuncRegex.Match(line);
        if (funcMatch.Success)
        {
            var printedName = ExtractPrintedName(line, funcMatch.Groups[1].Value);
            result.Add($"{qualifiedType}.{printedName}");
            return;
        }

        if (AnyInitRegex.IsMatch(line))
        {
            var printedName = ExtractPrintedName(line, "init");
            result.Add($"{qualifiedType}.{printedName}");
            return;
        }

        // Try var/let match
        var varMatch = Regex.Match(line, @"nonisolated\s+(?:public\s+|open\s+)?(?:final\s+)?(?:var|let)\s+(\w+)");
        if (varMatch.Success)
            result.Add($"{qualifiedType}.{varMatch.Groups[1].Value}");
    }

    // Regex for conformance extension: "extension Module.Type : Module.Protocol {"
    private static readonly Regex ConformanceExtensionRegex = new(
        @"extension\s+([\w.]+)\s*:\s*([\w.,\s]+)\s*\{",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a .swiftinterface file and returns a dictionary mapping protocol names
    /// to their conforming type names, as declared in extension conformance blocks.
    /// Only includes conformances from empty extension bodies (the conforming type
    /// adds no new members — a signal of a marker protocol conformance).
    /// Keys are unqualified protocol names (e.g., "ConstraintOffsetTarget").
    /// Values are lists of fully-qualified Swift type names (e.g., "Swift.Int").
    /// </summary>
    public static Dictionary<string, List<string>> GetMarkerProtocolConformances(string swiftInterfacePath)
    {
        var result = new Dictionary<string, List<string>>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        // Pass 1: Collect conformances from "extension Type : Protocol { }" blocks
        // We look for extensions with an empty body (open+close on same line or next line is })
        for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var trimmed = lines[lineIdx].TrimStart();
            var match = ConformanceExtensionRegex.Match(trimmed);
            if (!match.Success)
                continue;

            var conformingType = match.Groups[1].Value;
            var protocolList = match.Groups[2].Value;

            // Check for empty body: either "{ }" on same line or next non-empty line is "}"
            var (openBraces, closeBraces) = CountBraces(lines[lineIdx]);
            bool isEmptyBody = openBraces > 0 && closeBraces > 0; // "{ }" on same line

            if (!isEmptyBody && openBraces > 0)
            {
                // Check if next non-whitespace line is "}"
                for (int nextIdx = lineIdx + 1; nextIdx < lines.Length; nextIdx++)
                {
                    var nextTrimmed = lines[nextIdx].TrimStart();
                    if (string.IsNullOrWhiteSpace(nextTrimmed))
                        continue;
                    if (nextTrimmed == "}")
                        isEmptyBody = true;
                    break;
                }
            }

            if (!isEmptyBody)
                continue;

            // Parse protocol list (handles "Proto1, Proto2")
            var protocols = protocolList.Split(',')
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p));

            foreach (var proto in protocols)
            {
                // Use unqualified protocol name as key
                var dotIdx = proto.LastIndexOf('.');
                var unqualifiedName = dotIdx >= 0 ? proto.Substring(dotIdx + 1) : proto;

                if (!result.ContainsKey(unqualifiedName))
                    result[unqualifiedName] = new List<string>();

                if (!result[unqualifiedName].Contains(conformingType))
                    result[unqualifiedName].Add(conformingType);
            }
        }

        return result;
    }

    // Regex for protocol declarations: matches "public protocol Name" or "open protocol Name"
    private static readonly Regex ProtocolDeclRegex = new(
        @"(?:public|open)\s+protocol\s+(\w+)",
        RegexOptions.Compiled);

    // Regex for public/open func in extension (captures function name)
    private static readonly Regex ExtensionFuncRegex = new(
        @"(?:@\S+\s+)*(?:public|open)\s+(?:static\s+)?(?:mutating\s+)?func\s+(\w+)\s*(?:<[^>]*>\s*)?\(",
        RegexOptions.Compiled);

    // Regex for public/open var in extension (captures property name)
    private static readonly Regex ExtensionVarRegex = new(
        @"(?:@\S+\s+)*(?:public|open)\s+(?:static\s+)?var\s+(\w+)\s*:",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a .swiftinterface file and returns the set of protocol names declared in
    /// the module. Names are unqualified (e.g., "KFOptionSetter"). Used to distinguish
    /// protocol extensions from type extensions when parsing extension blocks.
    /// </summary>
    /// <summary>
    /// Parses a .swiftinterface file and returns the names of protocols whose methods
    /// have @convention(c) or @convention(block) closure parameters (either directly
    /// or via typealias). ABI JSON doesn't encode calling conventions on TypeFunc nodes,
    /// so EveryProtocol closure stubs emit @escaping which doesn't match, causing
    /// conformance failures.
    /// </summary>
    public static HashSet<string> GetProtocolsWithConventionClosures(string swiftInterfacePath)
        => GetProtocolsWithConventionClosures(swiftInterfacePath, out _);

    /// <summary>
    /// Best-effort provenance overload of
    /// <see cref="GetProtocolsWithConventionClosures(string)"/>. <paramref name="positions"/>
    /// is keyed by protocol name and points at the protocol declaration line that triggered
    /// detection (not the convention-c reference line itself — the protocol header is the
    /// natural target for diagnostics about that protocol). Lines and columns are 1-based.
    /// </summary>
    public static HashSet<string> GetProtocolsWithConventionClosures(
        string swiftInterfacePath,
        out Dictionary<string, SourcePosition> positions)
    {
        var result = new HashSet<string>();
        positions = new Dictionary<string, SourcePosition>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        // Pass 1: collect typealias names that are @convention(c) or @convention(block)
        var conventionTypealiases = new HashSet<string>();
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            // Match: public typealias FTS5TokenCallback = @convention(c) ...
            // Also: typealias Foo = @convention(block) ...
            if (trimmed.Contains("@convention(c)") || trimmed.Contains("@convention(block)"))
            {
                var typealiasMatch = Regex.Match(trimmed, @"typealias\s+(\w+)\s*=");
                if (typealiasMatch.Success)
                    conventionTypealiases.Add(typealiasMatch.Groups[1].Value);
            }
        }

        // Pass 2: find protocol blocks containing convention-c references
        int braceDepth = 0;
        string? currentProtocol = null;
        int protocolBraceDepth = -1;
        SourcePosition currentProtocolPos = default;

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var trimmed = line.TrimStart();

            // Track protocol declarations
            var protoMatch = ProtocolDeclRegex.Match(trimmed);
            if (protoMatch.Success && !trimmed.TrimStart().StartsWith("//"))
            {
                // Only start tracking if this line opens a brace block
                var (opens, closes) = CountBraces(trimmed);
                int newDepth = braceDepth + opens - closes;
                if (opens > 0)
                {
                    currentProtocol = protoMatch.Groups[1].Value;
                    protocolBraceDepth = braceDepth;
                    int leading = line.Length - trimmed.Length;
                    int column = leading + protoMatch.Index + 1;
                    currentProtocolPos = new SourcePosition(swiftInterfacePath, lineIndex + 1, column);
                }
                braceDepth = newDepth;
                continue;
            }

            var (openCount, closeCount) = CountBraces(trimmed);
            braceDepth += openCount - closeCount;

            // Exit protocol block
            if (currentProtocol != null && braceDepth <= protocolBraceDepth)
            {
                currentProtocol = null;
                protocolBraceDepth = -1;
                continue;
            }

            // Inside a protocol block: check for convention-c references
            if (currentProtocol != null && !result.Contains(currentProtocol))
            {
                // Direct @convention(c) or @convention(block) in the method signature
                if (trimmed.Contains("@convention(c)") || trimmed.Contains("@convention(block)"))
                {
                    result.Add(currentProtocol);
                    positions[currentProtocol] = currentProtocolPos;
                    continue;
                }

                // Check if a method parameter type references a convention-c typealias.
                // Check all non-comment lines inside the protocol block (not just func/init lines)
                // because swiftinterface signatures can wrap across multiple continuation lines
                // and the typealias reference may appear on a continuation line.
                if (conventionTypealiases.Count > 0 && !trimmed.StartsWith("//"))
                {
                    foreach (var alias in conventionTypealiases)
                    {
                        // Match the typealias name as a whole word type reference (e.g., "GRDB.FTS5TokenCallback"
                        // or bare "FTS5TokenCallback"), using word boundary to avoid substring false positives
                        if (Regex.IsMatch(trimmed, $@"\b{Regex.Escape(alias)}\b"))
                        {
                            result.Add(currentProtocol);
                            positions[currentProtocol] = currentProtocolPos;
                            break;
                        }
                    }
                }
            }
        }

        return result;
    }

    public static HashSet<string> GetProtocolNames(string swiftInterfacePath)
    {
        var result = new HashSet<string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var match = ProtocolDeclRegex.Match(trimmed);
            if (match.Success)
            {
                result.Add(match.Groups[1].Value);
            }
        }

        return result;
    }

    // Underscore-prefixed protocol requirement: var/let/func/init/subscript/static var/static func
    // whose member name begins with "__" (e.g., `var __linkSPI: Bool { get }`). swift-api-digester
    // strips these from the ABI JSON, but swiftc still enforces them as protocol requirements.
    private static readonly Regex UnderscoredProtocolMemberRegex = new(
        @"\b(?:var|let|func|init|subscript)\s+(__\w+)|\bvar\s+(__\w+)\s*:|\bsubscript\b",
        RegexOptions.Compiled);

    private static readonly Regex UnderscoredVarRegex = new(
        @"\b(?:var|let)\s+(__\w+)\b",
        RegexOptions.Compiled);

    private static readonly Regex UnderscoredFuncRegex = new(
        @"\bfunc\s+(__\w+)\b",
        RegexOptions.Compiled);

    // Header line format: "// swift-module-flags: ... -module-name <Name> ..."
    private static readonly Regex ModuleNameHeaderRegex = new(
        @"-module-name\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a .swiftinterface file and returns, per protocol, the set of underscore-prefixed
    /// (e.g. <c>__linkSPI</c>) requirement names declared in the protocol body that lack a
    /// matching default implementation in any same-module same-protocol extension.
    ///
    /// The returned names are only candidates: the caller (<c>SwiftABIParser</c>) compares
    /// them against the parsed protocol's children and only acts on names that are also
    /// missing from the ABI JSON. This avoids over-broad suppression in toolchains where
    /// <c>swift-api-digester</c> retains <c>__</c>-prefixed names (they can be witnessed
    /// from C# normally).
    ///
    /// Pass 2 matches extensions only when the target is <c>&lt;moduleName&gt;.&lt;Protocol&gt;</c>
    /// or the unqualified <c>&lt;Protocol&gt;</c> (Swift's same-module form). Foreign extensions
    /// like <c>extension OtherModule.Component</c> never count as defaults for the local
    /// <c>Component</c>. The module name is extracted from the swiftinterface header's
    /// <c>-module-name</c> flag.
    /// </summary>
    public static Dictionary<string, HashSet<string>> GetProtocolsWithUnsatisfiedHiddenRequirements(
        string swiftInterfacePath)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        if (!File.Exists(swiftInterfacePath))
            return result;

        // Pull the module name from the header so Pass 2 can reject foreign extensions
        // (e.g. `extension SomeOtherModule.Component { ... }`) which never satisfy a
        // local Component requirement, despite sharing a simple name.
        string? moduleName = null;

        var lines = File.ReadAllLines(swiftInterfacePath);

        // Header lines (those starting with "//") usually live in the first ~10 lines.
        // Stop scanning at the first non-comment, non-blank line.
        for (int i = 0; i < lines.Length && i < 64; i++)
        {
            var headerLine = lines[i];
            if (headerLine.StartsWith("//", StringComparison.Ordinal))
            {
                var nameMatch = ModuleNameHeaderRegex.Match(headerLine);
                if (nameMatch.Success)
                {
                    moduleName = nameMatch.Groups[1].Value;
                    break;
                }
                continue;
            }
            if (string.IsNullOrWhiteSpace(headerLine))
                continue;
            break;
        }

        // Pass 1: find protocol declarations and the names of __-prefixed members declared
        // directly in their bodies. Track outer brace depth so nested types inside a protocol
        // don't pollute the requirement set.
        var requirements = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        int braceDepth = 0;
        string? currentProtocol = null;
        int protocolBraceDepth = -1;
        int innerNestedDepth = 0; // depth of `{ ... }` opened *inside* the protocol body

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var (opens, closes) = CountBraces(trimmed);

            // Detect a protocol declaration that opens its body on this line
            var protoMatch = ProtocolDeclRegex.Match(trimmed);
            if (protoMatch.Success && opens > 0 && currentProtocol == null)
            {
                currentProtocol = protoMatch.Groups[1].Value;
                protocolBraceDepth = braceDepth;
                innerNestedDepth = 0;
                if (!requirements.ContainsKey(currentProtocol))
                    requirements[currentProtocol] = new HashSet<string>(StringComparer.Ordinal);
                braceDepth += opens - closes;
                continue;
            }

            int prevDepth = braceDepth;
            braceDepth += opens - closes;

            if (currentProtocol != null)
            {
                // Members declared directly inside the protocol body sit at exactly
                // `protocolBraceDepth + 1` (i.e., innerNestedDepth == 0 entering the line).
                // Skip declarations inside nested `{}` (e.g. accessor blocks, nested types).
                if (innerNestedDepth == 0 && !trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    var varMatch = UnderscoredVarRegex.Match(trimmed);
                    if (varMatch.Success)
                        requirements[currentProtocol].Add(varMatch.Groups[1].Value);
                    var funcMatch = UnderscoredFuncRegex.Match(trimmed);
                    if (funcMatch.Success)
                        requirements[currentProtocol].Add(funcMatch.Groups[1].Value);
                }

                // Adjust nested depth using the line's net brace delta (after we've
                // captured pre-line member declarations). A `var __x: Bool { get }`
                // line has opens==closes so innerNestedDepth stays 0 across the line.
                innerNestedDepth += opens - closes;
                if (innerNestedDepth < 0) innerNestedDepth = 0;

                // Exit the protocol body when depth drops to or below the opening depth.
                if (braceDepth <= protocolBraceDepth)
                {
                    currentProtocol = null;
                    protocolBraceDepth = -1;
                    innerNestedDepth = 0;
                }
            }
        }

        // Drop protocols with no hidden requirements.
        var protocolsWithHidden = requirements
            .Where(kvp => kvp.Value.Count > 0)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
        if (protocolsWithHidden.Count == 0)
            return result;

        // Pass 2: walk extension blocks for the same module. For any
        // `extension <qualified>.<protocol>` whose simple name matches a tracked protocol,
        // collect __-prefixed members from the extension body — they're default impls that
        // satisfy the corresponding requirement.
        var satisfied = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        braceDepth = 0;
        string? currentExtensionProto = null;
        int extensionBraceDepth = -1;
        int extInnerNestedDepth = 0;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var (opens, closes) = CountBraces(trimmed);

            if (currentExtensionProto == null)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && opens > 0)
                {
                    var qualified = extMatch.Groups[1].Value;
                    string? simpleName = null;
                    var dot = qualified.IndexOf('.');
                    if (dot < 0)
                    {
                        // Unqualified `extension X`: Swift requires same-module reference.
                        simpleName = qualified;
                    }
                    else if (moduleName != null)
                    {
                        // Qualified `extension <Mod>.<X>`: only count when the module
                        // matches the swiftinterface's own module. Cross-module extensions
                        // (e.g. extension OtherModule.Component) never satisfy a local
                        // Component requirement.
                        var qualifier = qualified.Substring(0, dot);
                        if (string.Equals(qualifier, moduleName, StringComparison.Ordinal))
                            simpleName = qualified.Substring(dot + 1);
                    }
                    if (simpleName != null && protocolsWithHidden.ContainsKey(simpleName))
                    {
                        currentExtensionProto = simpleName;
                        extensionBraceDepth = braceDepth;
                        extInnerNestedDepth = 0;
                        if (!satisfied.ContainsKey(simpleName))
                            satisfied[simpleName] = new HashSet<string>(StringComparer.Ordinal);
                    }
                    braceDepth += opens - closes;
                    continue;
                }
                braceDepth += opens - closes;
                continue;
            }

            braceDepth += opens - closes;

            if (extInnerNestedDepth == 0 && !trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                var varMatch = UnderscoredVarRegex.Match(trimmed);
                if (varMatch.Success)
                    satisfied[currentExtensionProto].Add(varMatch.Groups[1].Value);
                var funcMatch = UnderscoredFuncRegex.Match(trimmed);
                if (funcMatch.Success)
                    satisfied[currentExtensionProto].Add(funcMatch.Groups[1].Value);
            }

            extInnerNestedDepth += opens - closes;
            if (extInnerNestedDepth < 0) extInnerNestedDepth = 0;

            if (braceDepth <= extensionBraceDepth)
            {
                currentExtensionProto = null;
                extensionBraceDepth = -1;
                extInnerNestedDepth = 0;
            }
        }

        // Final: collect, per protocol, the underscored requirements that have no extension
        // default in this swiftinterface. The caller cross-checks against ABI JSON before
        // acting — a name present in the ABI can be witnessed normally.
        foreach (var kvp in protocolsWithHidden)
        {
            satisfied.TryGetValue(kvp.Key, out var defaults);
            HashSet<string>? unsatisfied = null;
            foreach (var name in kvp.Value)
            {
                if (defaults != null && defaults.Contains(name))
                    continue;
                unsatisfied ??= new HashSet<string>(StringComparer.Ordinal);
                unsatisfied.Add(name);
            }
            if (unsatisfied != null)
                result[kvp.Key] = unsatisfied;
        }

        return result;
    }

    /// <summary>
    /// Parses a .swiftinterface file and returns protocol extension methods grouped by
    /// fully-qualified protocol name (e.g., "Kingfisher.KFOptionSetter").
    /// Only collects methods from extensions of known protocols (provided by protocolNames).
    /// Handles #if compiler(...) blocks, multi-line signatures, @MainActor annotations,
    /// and where constraints on extension headers.
    /// </summary>
    public static Dictionary<string, List<ProtocolExtensionMethodDecl>> GetProtocolExtensionMethods(
        string swiftInterfacePath, HashSet<string> protocolNames)
    {
        var result = new Dictionary<string, List<ProtocolExtensionMethodDecl>>();

        if (!File.Exists(swiftInterfacePath) || protocolNames.Count == 0)
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;
        // Track whether we're inside a protocol extension and which protocol
        string? currentProtocolExtension = null; // fully-qualified name
        int protocolExtensionDepth = -1;
        List<string> currentWhereConstraints = new();
        bool pendingMainActor = false;
        string? continuationLine = null;
        bool continuationMainActor = false;
        // Track property setter scope: when inside a var's brace block, look for "set" or "nonmutating set"
        string? pendingPropertyLine = null;
        bool pendingPropertyMainActor = false;
        int propertyBraceDepth = -1;
        bool propertyHasSetter = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Handle multi-line signature continuation
            if (continuationLine != null)
            {
                continuationLine += " " + trimmed;
                if (!HasUnmatchedOpenParen(continuationLine))
                {
                    var completeLine = continuationLine;
                    var wasMainActor = continuationMainActor;
                    continuationLine = null;
                    continuationMainActor = false;
                    if (currentProtocolExtension != null)
                    {
                        ProcessProtocolExtensionMember(completeLine, currentProtocolExtension,
                            currentWhereConstraints, wasMainActor, result);
                    }
                }
                continue;
            }

            // Skip #if / #endif lines (symbols exist in binary regardless)
            if (trimmed.StartsWith("#if ") || trimmed.StartsWith("#endif") || trimmed.StartsWith("#else"))
                continue;

            var (openBraces, closeBraces) = CountBraces(line);

            // Handle property setter detection: track "set" inside property brace block
            if (pendingPropertyLine != null)
            {
                if (trimmed == "set" || trimmed == "nonmutating set" ||
                    trimmed.StartsWith("set ") || trimmed.StartsWith("nonmutating set") ||
                    trimmed == "@objc set" || trimmed.StartsWith("@objc set"))
                {
                    propertyHasSetter = true;
                }

                // Check if property brace block is closing
                var newDepth = braceDepth + openBraces - closeBraces;
                if (newDepth <= propertyBraceDepth)
                {
                    // Property brace block ended — emit the property with setter info
                    ProcessProtocolExtensionMember(pendingPropertyLine, currentProtocolExtension!,
                        currentWhereConstraints, pendingPropertyMainActor, result,
                        propertyHasSetter);
                    pendingPropertyLine = null;
                    propertyBraceDepth = -1;
                    propertyHasSetter = false;
                }
            }

            // Check for @MainActor annotation
            bool hasMainActor = pendingMainActor || MainActorAnnotationRegex.IsMatch(trimmed);
            pendingMainActor = false;

            // Pending annotation (attribute on its own line, no declaration)
            if (hasMainActor && !TypeDeclRegex.IsMatch(trimmed) &&
                !ExtensionFuncRegex.IsMatch(trimmed) && !ExtensionVarRegex.IsMatch(trimmed) &&
                openBraces == 0)
            {
                pendingMainActor = true;
                braceDepth += openBraces - closeBraces;
                while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                    typeStack.Pop();
                continue;
            }

            // Check for extension declarations on known protocols
            bool pushedScope = false;
            var extMatch = ExtensionDeclRegex.Match(trimmed);
            if (extMatch.Success && openBraces > 0)
            {
                var qualifiedName = extMatch.Groups[1].Value;
                // Strip module prefix (first component) to get the full type path.
                var firstDotIdx = qualifiedName.IndexOf('.');
                var typePath = firstDotIdx >= 0 ? qualifiedName.Substring(firstDotIdx + 1) : qualifiedName;
                typeStack.Push((typePath, braceDepth));
                pushedScope = true;

                // Check if this is a protocol extension
                if (protocolNames.Contains(typePath))
                {
                    currentProtocolExtension = qualifiedName;
                    protocolExtensionDepth = braceDepth;
                    currentWhereConstraints = ParseWhereConstraints(trimmed);
                }
            }

            // Track type declarations (class/struct/enum/actor/protocol)
            if (!pushedScope)
            {
                var typeMatch = TypeDeclRegex.Match(trimmed);
                if (typeMatch.Success && openBraces > 0)
                {
                    typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                    pushedScope = true;
                }
            }

            // If inside a protocol extension, look for func/var declarations
            if (!pushedScope && currentProtocolExtension != null)
            {
                // Check for func declarations
                if (ExtensionFuncRegex.IsMatch(trimmed))
                {
                    if (HasUnmatchedOpenParen(trimmed))
                    {
                        continuationLine = trimmed;
                        continuationMainActor = hasMainActor;
                    }
                    else
                    {
                        ProcessProtocolExtensionMember(trimmed, currentProtocolExtension,
                            currentWhereConstraints, hasMainActor, result);
                    }
                }
                // Check for var declarations — need to track property brace block for setter detection
                else if (ExtensionVarRegex.IsMatch(trimmed))
                {
                    if (openBraces > 0)
                    {
                        // Property with brace block on same line — defer to detect setter
                        pendingPropertyLine = trimmed;
                        pendingPropertyMainActor = hasMainActor;
                        propertyBraceDepth = braceDepth;
                        propertyHasSetter = false;
                        // Check for inline "{ get set }" on same line
                        if (trimmed.Contains(" set") && (trimmed.Contains("{ get set }") ||
                            trimmed.Contains("{get set}") || trimmed.Contains("{ get set}")))
                        {
                            propertyHasSetter = true;
                        }
                        // Check if braces close on same line (single-line property like "var x: T { get }")
                        if (closeBraces >= openBraces)
                        {
                            ProcessProtocolExtensionMember(trimmed, currentProtocolExtension,
                                currentWhereConstraints, hasMainActor, result,
                                propertyHasSetter);
                            pendingPropertyLine = null;
                            propertyBraceDepth = -1;
                            propertyHasSetter = false;
                        }
                    }
                    else
                    {
                        // No braces — computed property without inline body
                        ProcessProtocolExtensionMember(trimmed, currentProtocolExtension,
                            currentWhereConstraints, hasMainActor, result);
                    }
                }
            }

            braceDepth += openBraces - closeBraces;

            // Pop scopes and check if we've left the protocol extension
            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
            {
                typeStack.Pop();
            }
            if (currentProtocolExtension != null && braceDepth <= protocolExtensionDepth)
            {
                currentProtocolExtension = null;
                protocolExtensionDepth = -1;
                currentWhereConstraints = new();
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts where constraints from an extension header line.
    /// e.g., "extension Mod.Proto where Self : SomeClass {" → ["Self : SomeClass"]
    /// </summary>
    private static List<string> ParseWhereConstraints(string line)
    {
        var constraints = new List<string>();
        var whereIdx = line.IndexOf(" where ", StringComparison.Ordinal);
        if (whereIdx < 0)
            return constraints;

        var afterWhere = line.Substring(whereIdx + 7);
        // Remove trailing " {" or "{"
        var braceIdx = afterWhere.LastIndexOf('{');
        if (braceIdx >= 0)
            afterWhere = afterWhere.Substring(0, braceIdx).Trim();

        // Split by commas, respecting angle brackets
        var parts = SplitParameters(afterWhere);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                constraints.Add(trimmed);
        }

        return constraints;
    }

    /// <summary>
    /// Processes a single func/var line within a protocol extension block and adds
    /// the parsed ProtocolExtensionMethodDecl to the result dictionary.
    /// </summary>
    /// <summary>
    /// Test-accessible entry point for ProcessProtocolExtensionMember.
    /// </summary>
    internal static void ProcessProtocolExtensionMemberForTesting(
        string line, string protocolQualifiedName,
        List<string> whereConstraints, bool isMainActorIsolated,
        Dictionary<string, List<ProtocolExtensionMethodDecl>> result,
        bool hasSetter = false)
        => ProcessProtocolExtensionMember(line, protocolQualifiedName, whereConstraints, isMainActorIsolated, result, hasSetter);

    private static void ProcessProtocolExtensionMember(
        string line, string protocolQualifiedName,
        List<string> whereConstraints, bool isMainActorIsolated,
        Dictionary<string, List<ProtocolExtensionMethodDecl>> result,
        bool hasSetter = false)
    {
        // Check for func
        var funcMatch = ExtensionFuncRegex.Match(line);
        if (funcMatch.Success)
        {
            var methodName = funcMatch.Groups[1].Value;
            var printedName = ExtractPrintedName(line, methodName);
            bool isStatic = line.Contains("static func ");
            bool returnsSelf = DetectSelfReturn(line);

            bool isMutating = line.Contains("mutating func ");

            var decl = new ProtocolExtensionMethodDecl
            {
                ProtocolQualifiedName = protocolQualifiedName,
                MethodName = methodName,
                RawSignature = line,
                PrintedName = printedName,
                ReturnsSelf = returnsSelf,
                IsMainActorIsolated = isMainActorIsolated || MainActorAnnotationRegex.IsMatch(line),
                IsStatic = isStatic,
                IsMutating = isMutating,
                IsProperty = false,
                WhereConstraints = new List<string>(whereConstraints)
            };

            if (!result.ContainsKey(protocolQualifiedName))
                result[protocolQualifiedName] = new List<ProtocolExtensionMethodDecl>();
            result[protocolQualifiedName].Add(decl);
            return;
        }

        // Check for var
        var varMatch = ExtensionVarRegex.Match(line);
        if (varMatch.Success)
        {
            var propertyName = varMatch.Groups[1].Value;
            bool isStatic = line.Contains("static var ");

            var decl = new ProtocolExtensionMethodDecl
            {
                ProtocolQualifiedName = protocolQualifiedName,
                MethodName = propertyName,
                RawSignature = line,
                PrintedName = propertyName,
                ReturnsSelf = false,
                IsMainActorIsolated = isMainActorIsolated || MainActorAnnotationRegex.IsMatch(line),
                IsStatic = isStatic,
                IsProperty = true,
                HasSetter = hasSetter,
                WhereConstraints = new List<string>(whereConstraints)
            };

            if (!result.ContainsKey(protocolQualifiedName))
                result[protocolQualifiedName] = new List<ProtocolExtensionMethodDecl>();
            result[protocolQualifiedName].Add(decl);
        }
    }

    // Regex for @available(*, deprecated, ...) annotation
    private static readonly Regex DeprecatedAnnotationRegex = new(
        @"@available\(\s*\*\s*,\s*deprecated",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a .swiftinterface file and returns extension members on foreign types
    /// (types not defined in this module and not protocols of this module).
    /// Dictionary key is the fully-qualified foreign type name (e.g., "UIKit.UIView").
    ///
    /// Foreign extensions are detected when:
    /// 1. The extended type has a module qualifier different from the current module
    /// 2. The extended type is NOT in protocolNames (not a protocol extension)
    /// 3. The extended type is NOT in moduleTypeNames (not an owned type)
    /// </summary>
    public static Dictionary<string, List<ProtocolExtensionMethodDecl>> GetForeignTypeExtensionMembers(
        string swiftInterfacePath,
        HashSet<string> protocolNames,
        HashSet<string> moduleTypeNames,
        string moduleName)
    {
        var result = new Dictionary<string, List<ProtocolExtensionMethodDecl>>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;
        string? currentForeignExtension = null; // fully-qualified name (e.g., "UIKit.UIView")
        int foreignExtensionDepth = -1;
        List<string> currentWhereConstraints = new();
        bool pendingMainActor = false;
        bool pendingDeprecated = false;
        string? continuationLine = null;
        bool continuationMainActor = false;
        bool continuationDeprecated = false;
        // Track property setter scope: when inside a var's brace block, look for "set" or "nonmutating set"
        string? pendingPropertyLine = null;
        bool pendingPropertyMainActor = false;
        bool pendingPropertyDeprecated = false;
        int propertyBraceDepth = -1;
        bool propertyHasSetter = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Handle multi-line signature continuation
            if (continuationLine != null)
            {
                continuationLine += " " + trimmed;
                if (!HasUnmatchedOpenParen(continuationLine))
                {
                    var completeLine = continuationLine;
                    var wasMainActor = continuationMainActor;
                    var wasDeprecated = continuationDeprecated;
                    continuationLine = null;
                    continuationMainActor = false;
                    continuationDeprecated = false;
                    if (currentForeignExtension != null)
                    {
                        ProcessForeignExtensionMember(completeLine, currentForeignExtension,
                            currentWhereConstraints, wasMainActor, wasDeprecated, false, result);
                    }
                }
                continue;
            }

            // Skip #if / #endif lines
            if (trimmed.StartsWith("#if ") || trimmed.StartsWith("#endif") || trimmed.StartsWith("#else"))
                continue;

            var (openBraces, closeBraces) = CountBraces(line);

            // Check for @MainActor and @available(*, deprecated) annotations
            bool hasMainActor = pendingMainActor || MainActorAnnotationRegex.IsMatch(trimmed);
            bool hasDeprecated = pendingDeprecated || DeprecatedAnnotationRegex.IsMatch(trimmed);
            pendingMainActor = false;
            pendingDeprecated = false;

            // Pending annotation (attribute on its own line, no declaration)
            if ((hasMainActor || hasDeprecated) && !TypeDeclRegex.IsMatch(trimmed) &&
                !ExtensionFuncRegex.IsMatch(trimmed) && !ExtensionVarRegex.IsMatch(trimmed) &&
                openBraces == 0)
            {
                pendingMainActor = hasMainActor;
                pendingDeprecated = hasDeprecated;
                braceDepth += openBraces - closeBraces;
                while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                    typeStack.Pop();
                continue;
            }

            // Handle property setter detection: track "set" inside property brace block
            if (pendingPropertyLine != null)
            {
                if (trimmed == "set" || trimmed == "nonmutating set" ||
                    trimmed.StartsWith("set ") || trimmed.StartsWith("nonmutating set") ||
                    trimmed == "@objc set" || trimmed.StartsWith("@objc set"))
                {
                    propertyHasSetter = true;
                }

                // Check if property brace block is closing
                var newDepth = braceDepth + openBraces - closeBraces;
                if (newDepth <= propertyBraceDepth)
                {
                    // Property brace block ended — emit the property
                    ProcessForeignExtensionMember(pendingPropertyLine, currentForeignExtension!,
                        currentWhereConstraints, pendingPropertyMainActor, pendingPropertyDeprecated,
                        propertyHasSetter, result);
                    pendingPropertyLine = null;
                    propertyBraceDepth = -1;
                    propertyHasSetter = false;
                }

                // Still inside property brace block — update depth and continue
                braceDepth += openBraces - closeBraces;
                while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
                    typeStack.Pop();
                if (currentForeignExtension != null && braceDepth <= foreignExtensionDepth)
                {
                    currentForeignExtension = null;
                    foreignExtensionDepth = -1;
                    currentWhereConstraints = new();
                }
                continue;
            }

            // Check for extension declarations
            bool pushedScope = false;
            var extMatch = ExtensionDeclRegex.Match(trimmed);
            if (extMatch.Success && openBraces > 0)
            {
                var qualifiedName = extMatch.Groups[1].Value;
                // Strip module prefix (first component) to get the full type path.
                var firstDotIdx = qualifiedName.IndexOf('.');
                var typePath = firstDotIdx >= 0 ? qualifiedName.Substring(firstDotIdx + 1) : qualifiedName;
                typeStack.Push((typePath, braceDepth));
                pushedScope = true;

                // Check if this is a foreign type extension:
                // 1. Has module qualifier AND module != current module
                // 2. NOT a protocol of this module
                // 3. NOT a type of this module (unqualified fallback)
                //
                // Module qualifier is the FIRST segment, not everything before the LAST dot.
                // For nested types like "StoreKit.Product.SubscriptionPeriod", the module is
                // "StoreKit" and "Product.SubscriptionPeriod" is the nested type path.
                bool isForeign = false;
                if (firstDotIdx >= 0)
                {
                    // Qualified name — first segment is the module qualifier
                    var modulePrefix = qualifiedName.Substring(0, firstDotIdx);
                    isForeign = !string.Equals(modulePrefix, moduleName, StringComparison.Ordinal);
                }
                else
                {
                    // Unqualified — foreign if not in this module's types or protocols
                    isForeign = !moduleTypeNames.Contains(typePath) &&
                                !protocolNames.Contains(typePath);
                }

                // Exclude protocol extensions (already handled by GetProtocolExtensionMethods)
                if (isForeign && !protocolNames.Contains(typePath))
                {
                    currentForeignExtension = qualifiedName;
                    foreignExtensionDepth = braceDepth;
                    currentWhereConstraints = ParseWhereConstraints(trimmed);
                }
            }

            // Track type declarations (class/struct/enum/actor/protocol)
            if (!pushedScope)
            {
                var typeMatch = TypeDeclRegex.Match(trimmed);
                if (typeMatch.Success && openBraces > 0)
                {
                    typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                    pushedScope = true;
                }
            }

            // If inside a foreign type extension, look for func/var declarations.
            // BUT: skip members declared inside nested types within the extension body
            // (e.g., `extension Foundation.PredicateExpressions { public enum X { var hashValue... } }`).
            // Those members belong to X, not to PredicateExpressions. Detect this by checking
            // whether the top of typeStack is a nested type pushed deeper than the foreign
            // extension itself.
            bool insideNestedType = currentForeignExtension != null
                && typeStack.Count > 0
                && typeStack.Peek().Depth > foreignExtensionDepth;

            if (!pushedScope && currentForeignExtension != null && !insideNestedType)
            {
                // Check for func declarations
                if (ExtensionFuncRegex.IsMatch(trimmed))
                {
                    if (HasUnmatchedOpenParen(trimmed))
                    {
                        continuationLine = trimmed;
                        continuationMainActor = hasMainActor;
                        continuationDeprecated = hasDeprecated;
                    }
                    else
                    {
                        ProcessForeignExtensionMember(trimmed, currentForeignExtension,
                            currentWhereConstraints, hasMainActor, hasDeprecated, false, result);
                    }
                }
                // Check for var declarations — need to track property brace block for setter detection
                else if (ExtensionVarRegex.IsMatch(trimmed))
                {
                    if (openBraces > 0)
                    {
                        // Property with brace block on same line — defer to detect setter
                        pendingPropertyLine = trimmed;
                        pendingPropertyMainActor = hasMainActor;
                        pendingPropertyDeprecated = hasDeprecated;
                        propertyBraceDepth = braceDepth;
                        propertyHasSetter = false;
                        // Check for inline "{ get set }" on same line
                        if (trimmed.Contains(" set") && (trimmed.Contains("{ get set }") ||
                            trimmed.Contains("{get set}") || trimmed.Contains("{ get set}")))
                        {
                            propertyHasSetter = true;
                        }
                        // Check if braces close on same line (single-line property like "var x: T { get }")
                        if (closeBraces >= openBraces)
                        {
                            ProcessForeignExtensionMember(trimmed, currentForeignExtension,
                                currentWhereConstraints, hasMainActor, hasDeprecated,
                                propertyHasSetter, result);
                            pendingPropertyLine = null;
                            propertyBraceDepth = -1;
                            propertyHasSetter = false;
                        }
                    }
                    else
                    {
                        // No braces — computed property without inline body
                        ProcessForeignExtensionMember(trimmed, currentForeignExtension,
                            currentWhereConstraints, hasMainActor, hasDeprecated, false, result);
                    }
                }
            }

            braceDepth += openBraces - closeBraces;

            // Pop scopes and check if we've left the foreign extension
            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
            {
                typeStack.Pop();
            }
            if (currentForeignExtension != null && braceDepth <= foreignExtensionDepth)
            {
                currentForeignExtension = null;
                foreignExtensionDepth = -1;
                currentWhereConstraints = new();
            }
        }

        return result;
    }

    /// <summary>
    /// Processes a single func/var line within a foreign type extension block.
    /// Creates a ProtocolExtensionMethodDecl (reused for foreign extensions too).
    /// </summary>
    private static void ProcessForeignExtensionMember(
        string line, string foreignTypeQualifiedName,
        List<string> whereConstraints, bool isMainActorIsolated, bool isDeprecated,
        bool hasSetter,
        Dictionary<string, List<ProtocolExtensionMethodDecl>> result)
    {
        // Check for func
        var funcMatch = ExtensionFuncRegex.Match(line);
        if (funcMatch.Success)
        {
            var methodName = funcMatch.Groups[1].Value;
            var printedName = ExtractPrintedName(line, methodName);
            bool isStatic = line.Contains("static func ");
            bool returnsSelf = DetectSelfReturn(line);

            bool isMutatingForeign = line.Contains("mutating func ");

            var decl = new ProtocolExtensionMethodDecl
            {
                ProtocolQualifiedName = foreignTypeQualifiedName,
                MethodName = methodName,
                RawSignature = line,
                PrintedName = printedName,
                ReturnsSelf = returnsSelf,
                IsMainActorIsolated = isMainActorIsolated || MainActorAnnotationRegex.IsMatch(line),
                IsStatic = isStatic,
                IsMutating = isMutatingForeign,
                IsProperty = false,
                IsDeprecated = isDeprecated || DeprecatedAnnotationRegex.IsMatch(line),
                WhereConstraints = new List<string>(whereConstraints)
            };

            if (!result.ContainsKey(foreignTypeQualifiedName))
                result[foreignTypeQualifiedName] = new List<ProtocolExtensionMethodDecl>();
            result[foreignTypeQualifiedName].Add(decl);
            return;
        }

        // Check for var
        var varMatch = ExtensionVarRegex.Match(line);
        if (varMatch.Success)
        {
            var propertyName = varMatch.Groups[1].Value;
            bool isStatic = line.Contains("static var ") || line.Contains("static let ");

            var decl = new ProtocolExtensionMethodDecl
            {
                ProtocolQualifiedName = foreignTypeQualifiedName,
                MethodName = propertyName,
                RawSignature = line,
                PrintedName = propertyName,
                ReturnsSelf = false,
                IsMainActorIsolated = isMainActorIsolated || MainActorAnnotationRegex.IsMatch(line),
                IsStatic = isStatic,
                IsProperty = true,
                IsDeprecated = isDeprecated || DeprecatedAnnotationRegex.IsMatch(line),
                HasSetter = hasSetter,
                WhereConstraints = new List<string>(whereConstraints)
            };

            if (!result.ContainsKey(foreignTypeQualifiedName))
                result[foreignTypeQualifiedName] = new List<ProtocolExtensionMethodDecl>();
            result[foreignTypeQualifiedName].Add(decl);
        }
    }

    /// <summary>
    /// Detects whether a function signature returns Self.
    /// Looks for "-> Self" at the end of the signature (after the last ")").
    /// </summary>
    private static bool DetectSelfReturn(string line)
    {
        var lastParen = line.LastIndexOf(')');
        if (lastParen < 0)
            return false;

        var afterParen = line.Substring(lastParen + 1).Trim();
        // Remove trailing "{ ... }" or "{"
        var braceIdx = afterParen.IndexOf('{');
        if (braceIdx >= 0)
            afterParen = afterParen.Substring(0, braceIdx).Trim();

        return afterParen == "-> Self" || afterParen.EndsWith("-> Self");
    }

    /// <summary>
    /// Parses a .swiftinterface file and returns a set of member keys that are
    /// declared as internal. Keys are formatted as "TypeName.printedName"
    /// (e.g., "AES.encrypt(block:)").
    /// </summary>
    /// <param name="swiftInterfacePath">Path to the .swiftinterface file.</param>
    /// <returns>Set of internal member keys, or empty set if parsing fails.</returns>
    public static HashSet<string> GetInternalMembers(string swiftInterfacePath)
    {
        return GetInternalMembers(swiftInterfacePath, out _);
    }

    /// <summary>
    /// Parses a .swiftinterface file and returns both the set of explicitly internal
    /// member keys AND the set of all public member keys. The public member set is
    /// used for "negative space" detection: any member in the ABI JSON that is NOT
    /// in the public set (and not implicit) is internal.
    ///
    /// Keys use "TypeName.printedName" format for type members and bare "printedName"
    /// for module-level free functions/variables.
    /// </summary>
    public static HashSet<string> GetInternalMembers(string swiftInterfacePath, out HashSet<string> publicMemberNames)
    {
        var result = new HashSet<string>();
        publicMemberNames = new HashSet<string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        // Track type context using a stack with associated brace depths
        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;

        // Multiline continuation state for public member collection.
        // When a func/init signature spans multiple lines (opening '(' without closing ')'),
        // we buffer lines until the signature is complete.
        string? multilineFuncName = null;
        string? multilineType = null;
        string multilineBuffer = "";

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Count braces on this line (outside of string literals)
            var (openBraces, closeBraces) = CountBraces(line);

            // Check for type declarations before updating brace depth.
            // Both nominal types (class/struct/enum) and extensions push a scope.
            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                pushedScope = true;
            }

            // Check for extension declarations (e.g., "extension CryptoSwift.AES {")
            // Extensions can contain internal members that belong to the extended type.
            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    // Use last component (simple type name) for member key matching.
                    // IsInternalFromPublicMemberNames queries with typeDecl.Name (simple name),
                    // so member keys must use the same format.
                    var dotIdx = qualifiedName.LastIndexOf('.');
                    var typeName = dotIdx >= 0 ? qualifiedName.Substring(dotIdx + 1) : qualifiedName;
                    typeStack.Push((typeName, braceDepth));
                }
            }

            // Check for internal member declarations (only within a type context)
            if (typeStack.Count > 0 && trimmed.Contains("internal "))
            {
                var currentType = typeStack.Peek().Name;

                // Check for internal func
                var funcMatch = InternalFuncRegex.Match(trimmed);
                if (funcMatch.Success)
                {
                    var printedName = ExtractPrintedName(line, funcMatch.Groups[1].Value);
                    result.Add($"{currentType}.{printedName}");
                }

                // Check for internal var/let
                var varMatch = InternalVarRegex.Match(trimmed);
                if (varMatch.Success)
                {
                    result.Add($"{currentType}.{varMatch.Groups[1].Value}");
                }

                // Check for internal init
                var initMatch = InternalInitRegex.Match(trimmed);
                if (initMatch.Success)
                {
                    var printedName = ExtractPrintedName(line, "init");
                    result.Add($"{currentType}.{printedName}");
                }
            }

            // Collect public member declarations for negative-space internal detection.
            // Any ABI member NOT in this set (and not implicit) is internal.
            // Multiline continuation: if a func/init signature spans multiple lines,
            // we buffer lines until we find the closing ')'.
            if (multilineFuncName != null)
            {
                multilineBuffer += " " + trimmed;
                if (HasMatchingCloseParen(multilineBuffer))
                {
                    var printedName = ExtractPrintedName(multilineBuffer, multilineFuncName);
                    var key = multilineType != null ? $"{multilineType}.{printedName}" : printedName;
                    publicMemberNames.Add(key);
                    multilineFuncName = null;
                    multilineType = null;
                    multilineBuffer = "";
                }
            }
            else
            {
                CollectPublicMember(trimmed, line, typeStack, braceDepth, publicMemberNames,
                    out var pendingFuncName, out var pendingType, out var pendingBuffer);
                if (pendingFuncName != null)
                {
                    multilineFuncName = pendingFuncName;
                    multilineType = pendingType;
                    multilineBuffer = pendingBuffer ?? line;
                }
            }

            // Update brace depth
            braceDepth += openBraces - closeBraces;

            // Pop types whose scope has closed
            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
            {
                typeStack.Pop();
            }
        }

        return result;
    }

    /// <summary>
    /// Collects a public member declaration from the current line into the publicMemberNames set.
    /// For type members, the key is "TypeName.printedName". For module-level declarations
    /// (braceDepth == 0, no type context), the key is the bare printedName.
    /// </summary>
    private static void CollectPublicMember(
        string trimmed, string line,
        Stack<(string Name, int Depth)> typeStack,
        int braceDepth,
        HashSet<string> publicMemberNames,
        out string? pendingFuncName,
        out string? pendingType,
        out string? pendingBuffer)
    {
        pendingFuncName = null;
        pendingType = null;
        pendingBuffer = null;

        // Skip lines without public/open — most lines in the interface
        if (!trimmed.Contains("public ") && !trimmed.Contains("open "))
            return;

        // Skip compiler conditionals
        if (trimmed.StartsWith("#", StringComparison.Ordinal))
            return;

        // Strip leading annotations (e.g., @_Concurrency.MainActor, @objc, @discardableResult)
        // These can appear before "public" and would prevent regex matching.
        var effective = trimmed;
        while (effective.StartsWith("@", StringComparison.Ordinal))
        {
            // Find end of annotation (after the annotation name and optional parens)
            int spaceIdx = effective.IndexOf(' ');
            if (spaceIdx < 0) break;
            // Handle @annotation(args) by finding the closing paren
            int parenIdx = effective.IndexOf('(');
            if (parenIdx >= 0 && parenIdx < spaceIdx)
            {
                int closeIdx = effective.IndexOf(')', parenIdx);
                if (closeIdx >= 0)
                    spaceIdx = closeIdx + 1;
            }
            effective = effective.Substring(spaceIdx).TrimStart();
        }

        // Also strip "nonisolated" prefix
        if (effective.StartsWith("nonisolated ", StringComparison.Ordinal))
            effective = effective.Substring("nonisolated ".Length).TrimStart();

        // Determine the type context (null for module-level declarations)
        string? currentType = typeStack.Count > 0 ? typeStack.Peek().Name : null;

        // Public func — use broad regex that handles static/class/mutating modifiers
        var funcMatch = BroadPublicFuncRegex.Match(effective);
        if (funcMatch.Success)
        {
            var funcName = funcMatch.Groups[1].Value;
            if (!HasMatchingCloseParen(line))
            {
                // Multiline signature — buffer until closing paren found
                pendingFuncName = funcName;
                pendingType = currentType;
                pendingBuffer = line;
                return;
            }
            var printedName = ExtractPrintedName(line, funcName);
            var key = currentType != null ? $"{currentType}.{printedName}" : printedName;
            publicMemberNames.Add(key);
            return;
        }

        // Public var/let — use broad regex that handles static/class/lazy/weak modifiers
        var varMatch = BroadPublicVarRegex.Match(effective);
        if (varMatch.Success)
        {
            var propName = varMatch.Groups[1].Value;
            var key = currentType != null ? $"{currentType}.{propName}" : propName;
            publicMemberNames.Add(key);
            return;
        }

        // Public init — use broad regex that handles convenience/required/override
        var initMatch = BroadPublicInitRegex.Match(effective);
        if (initMatch.Success)
        {
            if (currentType != null)
            {
                if (!HasMatchingCloseParen(line))
                {
                    pendingFuncName = "init";
                    pendingType = currentType;
                    pendingBuffer = line;
                    return;
                }
                var printedName = ExtractPrintedName(line, "init");
                publicMemberNames.Add($"{currentType}.{printedName}");
            }
            return;
        }

        // Public subscript
        var subMatch = PublicSubscriptRegex.Match(effective);
        if (subMatch.Success)
        {
            if (currentType != null)
            {
                if (!HasMatchingCloseParen(line))
                {
                    pendingFuncName = "subscript";
                    pendingType = currentType;
                    pendingBuffer = line;
                    return;
                }
                var printedName = ExtractPrintedName(line, "subscript");
                publicMemberNames.Add($"{currentType}.{printedName}");
            }
            return;
        }
    }

    /// <summary>
    /// Checks if a line has a matching closing parenthesis for its first opening paren.
    /// Used to detect multiline function/init signatures.
    /// </summary>
    private static bool HasMatchingCloseParen(string text)
    {
        int depth = 0;
        bool foundOpen = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '(') { depth++; foundOpen = true; }
            if (text[i] == ')') depth--;
            if (foundOpen && depth == 0) return true;
        }
        return !foundOpen; // No parens at all → not a signature issue
    }

    /// <summary>
    /// Counts opening and closing braces in a line, ignoring those inside string literals.
    /// </summary>
    internal static (int Open, int Close) CountBraces(string line)
    {
        int open = 0, close = 0;
        bool inString = false;
        bool escape = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (escape)
            {
                escape = false;
                continue;
            }
            if (c == '\\' && inString)
            {
                escape = true;
                continue;
            }
            if (c == '"')
            {
                inString = !inString;
                continue;
            }
            if (!inString)
            {
                if (c == '{') open++;
                if (c == '}') close++;
            }
        }

        return (open, close);
    }

    /// <summary>
    /// Extracts a printed name in ABI format (e.g., "encrypt(block:)") from a Swift function
    /// declaration line. Parses parameter labels from the function signature.
    /// </summary>
    internal static string ExtractPrintedName(string line, string funcName)
    {
        // Find the opening parenthesis after the function name
        var funcNameIdx = line.IndexOf($" {funcName}(", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            funcNameIdx = line.IndexOf($" {funcName} (", StringComparison.Ordinal);
        // Handle failable inits: "init?(" — ABI JSON uses "init(" without "?"
        if (funcNameIdx < 0 && funcName == "init")
            funcNameIdx = line.IndexOf(" init?(", StringComparison.Ordinal);
        // Handle generic funcs: "func name<T>("
        if (funcNameIdx < 0)
            funcNameIdx = line.IndexOf($" {funcName}<", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            return $"{funcName}()";

        var parenStart = line.IndexOf('(', funcNameIdx);
        if (parenStart < 0)
            return $"{funcName}()";

        // Find matching close paren, handling nested parens
        int depth = 0;
        int parenEnd = parenStart;
        for (int i = parenStart; i < line.Length; i++)
        {
            if (line[i] == '(') depth++;
            if (line[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    parenEnd = i;
                    break;
                }
            }
        }

        // Guard: if the matching close paren was not found (e.g., truncated multi-line
        // signature), return as zero-param to avoid crashing on Substring.
        if (parenEnd == parenStart)
            return $"{funcName}()";

        var paramStr = line.Substring(parenStart + 1, parenEnd - parenStart - 1);
        if (string.IsNullOrWhiteSpace(paramStr))
            return $"{funcName}()";

        // Extract external labels from parameter list
        var labels = new List<string>();
        var parts = SplitParameters(paramStr);
        foreach (var part in parts)
        {
            var trimPart = part.Trim();
            // Pattern: "externalLabel internalName: Type" or "_ internalName: Type" or "name: Type"
            var colonIdx = trimPart.IndexOf(':');
            if (colonIdx < 0) continue;
            var beforeColon = trimPart.Substring(0, colonIdx).Trim();
            // Split by whitespace — first token is the external label
            var words = beforeColon.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0)
            {
                labels.Add(words[0]);
            }
        }

        if (labels.Count == 0)
            return $"{funcName}()";

        return $"{funcName}({string.Join(":", labels)}:)";
    }

    /// <summary>
    /// Splits a parameter list string by commas, respecting nested angle brackets,
    /// parentheses, and square brackets.
    /// </summary>
    private static List<string> SplitParameters(string paramStr)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        bool inString = false;

        for (int i = 0; i < paramStr.Length; i++)
        {
            char c = paramStr[i];
            // Track string literals — skip commas inside "..."
            if (c == '"' && (i == 0 || paramStr[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }
            if (inString)
                continue;
            if (c == '<' || c == '(' || c == '[') depth++;
            // Don't treat '>' in '->' (closure return arrow) as a closing bracket
            if (c == '>' && !(i > 0 && paramStr[i - 1] == '-')) depth--;
            else if (c == ')' || c == ']') depth--;
            if (c == ',' && depth == 0)
            {
                result.Add(paramStr.Substring(start, i - start));
                start = i + 1;
            }
        }
        result.Add(paramStr.Substring(start));
        return result;
    }

    /// <summary>
    /// Finds the first colon at depth 0 (not inside brackets, parens, or angle brackets).
    /// Colons inside dictionary types like [String : String] are at depth > 0 and must be skipped.
    /// </summary>
    private static int FindTopLevelColon(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '<' || c == '(' || c == '[') depth++;
            if (c == '>' || c == ')' || c == ']') depth--;
            if (c == ':' && depth == 0)
                return i;
        }
        return -1;
    }

    // Regex for enum case declarations with associated values
    // Matches: case caseName(label: Type) or case caseName(Type) or case caseName(label: Type, label2: Type2)
    private static readonly Regex EnumCaseRegex = new(
        @"case\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a .swiftinterface file and returns a dictionary mapping
    /// "TypeName.caseName" keys to lists of parameter labels.
    /// Labels are null for unlabeled parameters (e.g., "case point(FrozenPoint)").
    ///
    /// For example, for:
    ///   case circle(radius: Swift.Double)
    ///   case point(SwiftBindingsTestLib.FrozenPoint)
    /// This produces:
    ///   { "Shape.circle": ["radius"], "Shape.point": [null] }
    /// </summary>
    public static Dictionary<string, List<string?>> GetEnumCaseLabels(string swiftInterfacePath)
    {
        var result = new Dictionary<string, List<string?>>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;
        string? continuationLine = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Handle multi-line case continuation (rare but possible with many associated values)
            if (continuationLine != null)
            {
                continuationLine += " " + trimmed;
                if (!HasUnmatchedOpenParen(continuationLine))
                {
                    var completeLine = continuationLine;
                    continuationLine = null;
                    ProcessEnumCaseLine(completeLine, typeStack, result);
                }
                continue;
            }

            var (openBraces, closeBraces) = CountBraces(line);

            // Track type context (same logic as other methods)
            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                pushedScope = true;
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    // Strip module prefix (first component) to get the full type path.
                    // e.g., "CryptoKit.P256.Signing" → "P256.Signing" (not just "Signing").
                    // Extensions in swiftinterface files are always module-qualified.
                    var firstDotIdx = qualifiedName.IndexOf('.');
                    var typePath = firstDotIdx >= 0 ? qualifiedName.Substring(firstDotIdx + 1) : qualifiedName;
                    typeStack.Push((typePath, braceDepth));
                }
            }

            // Check for enum case declarations with parentheses
            // Also handle "indirect case" which appears in recursive enums
            var caseLine = trimmed;
            if (caseLine.StartsWith("indirect "))
                caseLine = caseLine.Substring("indirect ".Length);
            if (caseLine.StartsWith("case ") && caseLine.Contains("("))
            {
                if (HasUnmatchedOpenParen(caseLine))
                {
                    continuationLine = caseLine;
                }
                else
                {
                    ProcessEnumCaseLine(caseLine, typeStack, result);
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
            {
                typeStack.Pop();
            }
        }

        return result;
    }

    /// <summary>
    /// Processes a complete enum case line to extract parameter labels.
    /// </summary>
    private static void ProcessEnumCaseLine(
        string line,
        Stack<(string Name, int Depth)> typeStack,
        Dictionary<string, List<string?>> result)
    {
        if (typeStack.Count == 0)
            return;

        var caseMatch = EnumCaseRegex.Match(line);
        if (!caseMatch.Success)
            return;

        var caseName = caseMatch.Groups[1].Value;

        // Build fully-qualified type path from the type stack to disambiguate
        // nested enums with the same local name (e.g., OrderContainer.Status vs PaymentContainer.Status)
        var currentType = string.Join(".", typeStack.Reverse().Select(t => t.Name));

        // Find the opening parenthesis
        var parenStart = line.IndexOf('(', caseMatch.Index);
        if (parenStart < 0)
            return;

        // Find matching close paren
        int depth = 0;
        int parenEnd = parenStart;
        for (int i = parenStart; i < line.Length; i++)
        {
            if (line[i] == '(') depth++;
            if (line[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    parenEnd = i;
                    break;
                }
            }
        }

        var paramStr = line.Substring(parenStart + 1, parenEnd - parenStart - 1);
        if (string.IsNullOrWhiteSpace(paramStr))
            return;

        var labels = new List<string?>();
        var parts = SplitParameters(paramStr);
        foreach (var part in parts)
        {
            var trimPart = part.Trim();
            var colonIdx = FindTopLevelColon(trimPart);
            if (colonIdx < 0)
            {
                // No colon — unlabeled parameter (e.g., "SwiftBindingsTestLib.FrozenPoint")
                labels.Add(null);
            }
            else
            {
                var beforeColon = trimPart.Substring(0, colonIdx).Trim();
                // The label is the text before the colon (e.g., "radius" from "radius: Swift.Double")
                // For "_" labels, treat as unlabeled
                if (beforeColon == "_")
                    labels.Add(null);
                else
                    labels.Add(beforeColon);
            }
        }

        if (labels.Count > 0)
        {
            result[$"{currentType}.{caseName}"] = labels;
        }
    }

    // Regex for enum case declarations with string raw values
    // Matches: case caseName = "stringLiteral" (handles escaped quotes in the value)
    private static readonly Regex EnumCaseRawValueRegex = new(
        @"case\s+(\w+)\s*=\s*""((?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a .swiftinterface file and returns a dictionary mapping
    /// "TypeName.caseName" keys to string raw values.
    /// Only extracts string raw values (e.g., case get = "GET").
    /// Note: integer raw values are NOT present in .swiftinterface files —
    /// the Swift compiler strips them. Non-sequential integer raw values
    /// (e.g., case execute = 4) cannot be recovered from .swiftinterface or ABI JSON.
    /// </summary>
    public static Dictionary<string, string> GetEnumRawValues(string swiftInterfacePath)
    {
        var result = new Dictionary<string, string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            var (openBraces, closeBraces) = CountBraces(line);

            // Track type context (same logic as GetEnumCaseLabels)
            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                pushedScope = true;
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    // Strip module prefix (first component) to get the full type path.
                    // e.g., "CryptoKit.P256.Signing" → "P256.Signing" (not just "Signing").
                    // Extensions in swiftinterface files are always module-qualified.
                    var firstDotIdx = qualifiedName.IndexOf('.');
                    var typePath = firstDotIdx >= 0 ? qualifiedName.Substring(firstDotIdx + 1) : qualifiedName;
                    typeStack.Push((typePath, braceDepth));
                }
            }

            // Check for enum case declarations with string raw values
            if (trimmed.StartsWith("case ") && trimmed.Contains("\""))
            {
                var rawValueMatch = EnumCaseRawValueRegex.Match(trimmed);
                if (rawValueMatch.Success && typeStack.Count > 0)
                {
                    var caseName = rawValueMatch.Groups[1].Value;
                    var rawValue = rawValueMatch.Groups[2].Value;
                    // Keep escape sequences in their Swift form (\n, \t, \", \\).
                    // These map 1:1 to C# escape sequences and will be emitted directly
                    // into C# string literals by EnumHandler.SimpleEnum.
                    var currentType = string.Join(".", typeStack.Reverse().Select(t => t.Name));
                    result[$"{currentType}.{caseName}"] = rawValue;
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
            {
                typeStack.Pop();
            }
        }

        return result;
    }

    // Regex for public/open subscript declarations: captures the parameter list start
    private static readonly Regex SubscriptDeclRegex = new(
        @"(?:public|open)\s+(?:static\s+)?subscript\s*(?:<[^>]*>\s*)?\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a .swiftinterface file and returns a dictionary mapping
    /// "TypeName.subscript(labels:)" keys to lists of external parameter labels.
    /// Labels are the external argument labels used in Swift bracket syntax.
    ///
    /// For example, for:
    ///   public subscript(bitAt index: Int) -> Bool { get set }
    ///   public subscript(key: String, nested nested: String?, delimiter delimiter: String) -> Any? { get set }
    /// This produces:
    ///   { "AES.subscript(bitAt:)": ["bitAt"],
    ///     "Map.subscript(key:nested:delimiter:)": ["key", "nested", "delimiter"] }
    ///
    /// Used to cross-reference subscript parameter labels from ABI JSON,
    /// which may not encode all label variations for subscripts.
    /// </summary>
    public static Dictionary<string, List<string>> GetSubscriptLabels(string swiftInterfacePath)
    {
        var result = new Dictionary<string, List<string>>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;
        string? continuationLine = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Handle multi-line subscript continuation
            if (continuationLine != null)
            {
                continuationLine += " " + trimmed;
                if (!HasUnmatchedOpenParen(continuationLine))
                {
                    var completeLine = continuationLine;
                    continuationLine = null;
                    ProcessSubscriptLine(completeLine, typeStack, result);
                }
                continue;
            }

            var (openBraces, closeBraces) = CountBraces(line);

            // Track type context (same logic as other methods)
            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                pushedScope = true;
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    // Strip module prefix (first component) to get the full type path.
                    // e.g., "CryptoKit.P256.Signing" → "P256.Signing" (not just "Signing").
                    // Extensions in swiftinterface files are always module-qualified.
                    var firstDotIdx = qualifiedName.IndexOf('.');
                    var typePath = firstDotIdx >= 0 ? qualifiedName.Substring(firstDotIdx + 1) : qualifiedName;
                    typeStack.Push((typePath, braceDepth));
                }
            }

            // Check for subscript declarations
            if (SubscriptDeclRegex.IsMatch(trimmed))
            {
                if (HasUnmatchedOpenParen(trimmed))
                {
                    continuationLine = trimmed;
                }
                else
                {
                    ProcessSubscriptLine(trimmed, typeStack, result);
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
            {
                typeStack.Pop();
            }
        }

        return result;
    }

    /// <summary>
    /// Processes a complete subscript declaration line to extract parameter labels.
    /// </summary>
    private static void ProcessSubscriptLine(
        string line,
        Stack<(string Name, int Depth)> typeStack,
        Dictionary<string, List<string>> result)
    {
        if (typeStack.Count == 0)
            return;

        if (!SubscriptDeclRegex.IsMatch(line))
            return;

        // Build fully-qualified type path from the type stack
        var currentType = string.Join(".", typeStack.Reverse().Select(t => t.Name));

        // Find the opening parenthesis of the parameter list
        var subMatch = SubscriptDeclRegex.Match(line);
        if (!subMatch.Success)
            return;

        // The regex match ends right after '(' so find the paren position
        var parenStart = line.IndexOf('(', subMatch.Index);
        if (parenStart < 0)
            return;

        // Find matching close paren
        int depth = 0;
        int parenEnd = parenStart;
        for (int i = parenStart; i < line.Length; i++)
        {
            if (line[i] == '(') depth++;
            if (line[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    parenEnd = i;
                    break;
                }
            }
        }

        if (parenEnd == parenStart)
            return;

        var paramStr = line.Substring(parenStart + 1, parenEnd - parenStart - 1);
        if (string.IsNullOrWhiteSpace(paramStr))
            return;

        var labels = new List<string>();
        var parts = SplitParameters(paramStr);
        foreach (var part in parts)
        {
            var trimPart = part.Trim();
            var colonIdx = FindTopLevelColon(trimPart);
            if (colonIdx < 0)
            {
                // No colon — unlabeled parameter
                labels.Add("_");
                continue;
            }

            var beforeColon = trimPart.Substring(0, colonIdx).Trim();
            // Split by whitespace to distinguish label from parameter name.
            // In Swift subscripts, single-name params have NO argument label:
            //   subscript(key: String)      → words=["key"]      → no label (call: obj[val])
            //   subscript(_ key: String)    → words=["_","key"]  → no label (call: obj[val])
            // Only two-name params where the first isn't "_" have a label:
            //   subscript(bitAt index: Int) → words=["bitAt","index"] → label "bitAt" (call: obj[bitAt: 0])
            //   subscript(key key: String)  → words=["key","key"]     → label "key" (call: obj[key: val])
            var words = beforeColon.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2 && words[0] != "_")
            {
                // Explicit argument label: subscript(bitAt index: Int)
                labels.Add(words[0]);
            }
            else
            {
                // Single name or explicit "_" — no argument label
                labels.Add("_");
            }
        }

        if (labels.Count > 0)
        {
            // Build key as "TypeName.subscript(label1:label2:...)" format
            var labelStr = string.Join("", labels.Select(l => $"{l}:"));
            var key = $"{currentType}.subscript({labelStr})";
            result[key] = labels;
        }
    }

    // Regex for typed throws: captures the error type from "throws(Module.Type)"
    private static readonly Regex TypedThrowsRegex = new(
        @"throws\(([^)]+)\)",
        RegexOptions.Compiled);

    // Regex for any func declaration (public, open, or no access modifier in extension scope)
    // Captures the function name. Handles static, class, final, mutating modifiers.
    private static readonly Regex AnyFuncRegex = new(
        @"(?:(?:public|open|internal)\s+)?(?:final\s+)?(?:static\s+|class\s+)?(?:mutating\s+)?func\s+(\w+)\s*(?:<[^>]*>\s*)?\(",
        RegexOptions.Compiled);

    // Regex for init declarations
    private static readonly Regex AnyInitRegex = new(
        @"(?:(?:public|open|internal)\s+)?(?:convenience\s+)?init\s*(?:<[^>]*>\s*)?\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a .swiftinterface file and returns a dictionary mapping
    /// "TypeName.printedName" keys to lists of internal parameter names.
    /// For module-level free functions, the key is just "printedName".
    ///
    /// For example, for:
    ///   public func sumTwo(_ a: Int, _ b: Int) -> Int
    /// This produces: { "sumTwo(_:_:)": ["a", "b"] }
    ///
    /// Multi-line signatures are handled by detecting unmatched parentheses.
    /// </summary>
    /// <param name="swiftInterfacePath">Path to the .swiftinterface file.</param>
    /// <returns>Dictionary of parameter name lists keyed by "TypeName.printedName" or "printedName".</returns>
    public static Dictionary<string, List<string>> GetParameterNames(string swiftInterfacePath)
    {
        var result = new Dictionary<string, List<string>>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;
        string? continuationLine = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Handle multi-line signature continuation
            if (continuationLine != null)
            {
                continuationLine += " " + trimmed;
                // Check if parentheses are now balanced
                if (!HasUnmatchedOpenParen(continuationLine))
                {
                    var completeLine = continuationLine;
                    continuationLine = null;
                    ProcessFuncLineForParamNames(completeLine, typeStack, result);
                }
                // Don't process brace depth for continuation lines within signatures
                continue;
            }

            var (openBraces, closeBraces) = CountBraces(line);

            // Track type context (same logic as GetInternalMembers)
            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                pushedScope = true;
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    // Use last component (simple type name) for member key matching.
                    // ABI parser queries _parameterNames with parentDecl.Name (simple name).
                    var dotIdx = qualifiedName.LastIndexOf('.');
                    var typeName = dotIdx >= 0 ? qualifiedName.Substring(dotIdx + 1) : qualifiedName;
                    typeStack.Push((typeName, braceDepth));
                }
            }

            // Check for func/init declarations
            if (IsFuncOrInitLine(trimmed))
            {
                // Check for multi-line signature (unmatched open paren)
                if (HasUnmatchedOpenParen(trimmed))
                {
                    continuationLine = trimmed;
                }
                else
                {
                    ProcessFuncLineForParamNames(trimmed, typeStack, result);
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
            {
                typeStack.Pop();
            }
        }

        return result;
    }

    /// <summary>
    /// Parses a .swiftinterface file and returns a dictionary mapping
    /// "TypeName.printedName" keys to the fully-qualified error type string
    /// from typed throws declarations (e.g., "throws(Module.ErrorType)").
    /// For module-level free functions, the key is just "printedName".
    ///
    /// For example, for:
    ///   public func parseNumber(_ input: Swift.String) throws(SwiftBindingsTestLib.ParseError) -> Swift.Int32
    /// This produces: { "parseNumber(_:)": "SwiftBindingsTestLib.ParseError" }
    ///
    /// Only functions with typed throws are included; untyped "throws" and non-throwing
    /// functions are not present in the result.
    /// </summary>
    /// <param name="swiftInterfacePath">Path to the .swiftinterface file.</param>
    /// <returns>Dictionary mapping method keys to fully-qualified error type strings.</returns>
    public static Dictionary<string, string> GetTypedThrowsErrors(string swiftInterfacePath)
    {
        var result = new Dictionary<string, string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);

        var typeStack = new Stack<(string Name, int Depth)>();
        int braceDepth = 0;
        string? continuationLine = null;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Handle multi-line signature continuation
            if (continuationLine != null)
            {
                continuationLine += " " + trimmed;
                if (!HasUnmatchedOpenParen(continuationLine))
                {
                    var completeLine = continuationLine;
                    continuationLine = null;
                    ProcessFuncLineForTypedThrows(completeLine, typeStack, result);
                }
                continue;
            }

            var (openBraces, closeBraces) = CountBraces(line);

            // Track type context (same logic as GetInternalMembers/GetParameterNames)
            bool pushedScope = false;
            var typeMatch = TypeDeclRegex.Match(trimmed);
            if (typeMatch.Success && openBraces > 0)
            {
                typeStack.Push((typeMatch.Groups[1].Value, braceDepth));
                pushedScope = true;
            }

            if (!pushedScope)
            {
                var extMatch = ExtensionDeclRegex.Match(trimmed);
                if (extMatch.Success && openBraces > 0)
                {
                    var qualifiedName = extMatch.Groups[1].Value;
                    // Use last component (simple type name) for member key matching.
                    // ABI parser queries _typedThrowsErrors with parentDecl.Name (simple name).
                    var dotIdx = qualifiedName.LastIndexOf('.');
                    var typeName = dotIdx >= 0 ? qualifiedName.Substring(dotIdx + 1) : qualifiedName;
                    typeStack.Push((typeName, braceDepth));
                }
            }

            // Check for func/init declarations with typed throws
            if (IsFuncOrInitLine(trimmed))
            {
                if (HasUnmatchedOpenParen(trimmed))
                {
                    continuationLine = trimmed;
                }
                else
                {
                    ProcessFuncLineForTypedThrows(trimmed, typeStack, result);
                }
            }

            braceDepth += openBraces - closeBraces;

            while (typeStack.Count > 0 && braceDepth <= typeStack.Peek().Depth)
            {
                typeStack.Pop();
            }
        }

        return result;
    }

    /// <summary>
    /// Processes a complete function/init line to extract typed throws error type and add to result.
    /// </summary>
    private static void ProcessFuncLineForTypedThrows(
        string line,
        Stack<(string Name, int Depth)> typeStack,
        Dictionary<string, string> result)
    {
        // Check if this line has a typed throws pattern
        var throwsMatch = TypedThrowsRegex.Match(line);
        if (!throwsMatch.Success)
            return;

        var errorType = throwsMatch.Groups[1].Value.Trim();

        // Try func match
        var funcMatch = AnyFuncRegex.Match(line);
        if (funcMatch.Success)
        {
            var funcName = funcMatch.Groups[1].Value;
            var printedName = ExtractPrintedName(line, funcName);
            var key = typeStack.Count > 0
                ? $"{typeStack.Peek().Name}.{printedName}"
                : printedName;
            result[key] = errorType;
            return;
        }

        // Try init match
        var initMatch = AnyInitRegex.Match(line);
        if (initMatch.Success)
        {
            var printedName = ExtractPrintedName(line, "init");
            var key = typeStack.Count > 0
                ? $"{typeStack.Peek().Name}.{printedName}"
                : printedName;
            result[key] = errorType;
        }
    }

    /// <summary>
    /// Checks if a line contains a func or init declaration.
    /// </summary>
    private static bool IsFuncOrInitLine(string trimmed)
    {
        return AnyFuncRegex.IsMatch(trimmed) || AnyInitRegex.IsMatch(trimmed);
    }

    /// <summary>
    /// Checks if a line has unmatched open parentheses (multi-line signature).
    /// </summary>
    private static bool HasUnmatchedOpenParen(string line)
    {
        int depth = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '(') depth++;
            if (line[i] == ')') depth--;
        }
        return depth > 0;
    }

    /// <summary>
    /// Processes a complete function/init line to extract parameter names and add to result.
    /// </summary>
    private static void ProcessFuncLineForParamNames(
        string line,
        Stack<(string Name, int Depth)> typeStack,
        Dictionary<string, List<string>> result)
    {
        // Try func match
        var funcMatch = AnyFuncRegex.Match(line);
        if (funcMatch.Success)
        {
            var funcName = funcMatch.Groups[1].Value;
            var (printedName, internalNames) = ExtractParamNamesFromLine(line, funcName);

            if (internalNames.Count > 0)
            {
                var key = typeStack.Count > 0
                    ? $"{typeStack.Peek().Name}.{printedName}"
                    : printedName;
                result[key] = internalNames;
            }
            return;
        }

        // Try init match
        var initMatch = AnyInitRegex.Match(line);
        if (initMatch.Success)
        {
            var (printedName, internalNames) = ExtractParamNamesFromLine(line, "init");

            if (internalNames.Count > 0)
            {
                var key = typeStack.Count > 0
                    ? $"{typeStack.Peek().Name}.{printedName}"
                    : printedName;
                result[key] = internalNames;
            }
        }
    }

    /// <summary>
    /// Extracts both the printed name (ABI format) and internal parameter names from a function line.
    /// For "func sumTwo(_ a: Int, _ b: Int) -> Int", returns:
    ///   printedName = "sumTwo(_:_:)"
    ///   internalNames = ["a", "b"]
    /// </summary>
    private static (string PrintedName, List<string> InternalNames) ExtractParamNamesFromLine(string line, string funcName)
    {
        var printedName = ExtractPrintedName(line, funcName);
        var internalNames = new List<string>();

        // Find the opening parenthesis after the function name
        var funcNameIdx = line.IndexOf($" {funcName}(", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            funcNameIdx = line.IndexOf($" {funcName} (", StringComparison.Ordinal);
        // Also handle beginning of line (no space prefix)
        if (funcNameIdx < 0)
            funcNameIdx = line.IndexOf($"{funcName}(", StringComparison.Ordinal);
        // Also handle generic params: "func name<T>("
        if (funcNameIdx < 0)
            funcNameIdx = line.IndexOf($" {funcName}<", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            return (printedName, internalNames);

        var parenStart = line.IndexOf('(', funcNameIdx);
        if (parenStart < 0)
            return (printedName, internalNames);

        // Find matching close paren
        int depth = 0;
        int parenEnd = parenStart;
        for (int i = parenStart; i < line.Length; i++)
        {
            if (line[i] == '(') depth++;
            if (line[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    parenEnd = i;
                    break;
                }
            }
        }

        var paramStr = line.Substring(parenStart + 1, parenEnd - parenStart - 1);
        if (string.IsNullOrWhiteSpace(paramStr))
            return (printedName, internalNames);

        var parts = SplitParameters(paramStr);
        foreach (var part in parts)
        {
            var trimPart = part.Trim();
            var colonIdx = trimPart.IndexOf(':');
            if (colonIdx < 0) continue;
            var beforeColon = trimPart.Substring(0, colonIdx).Trim();
            var words = beforeColon.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (words.Length >= 2)
            {
                // "externalLabel internalName" -> internal name is second word
                internalNames.Add(words[1]);
            }
            else if (words.Length == 1)
            {
                // "name:" -> same name is both external and internal
                internalNames.Add(words[0]);
            }
        }

        return (printedName, internalNames);
    }

    /// <summary>
    /// Parses @available annotations from a .swiftinterface file.
    /// Returns a dictionary keyed by qualified path ("TypeName" for types,
    /// "TypeName.printedName" for members) to lists of availability annotations.
    /// </summary>
    public static Dictionary<string, List<AvailabilityAnnotation>> GetAvailabilityAnnotations(
        string swiftInterfacePath)
        => GetAvailabilityAnnotations(swiftInterfacePath, out _);

    /// <summary>
    /// Best-effort provenance overload of <see cref="GetAvailabilityAnnotations(string)"/>.
    /// <paramref name="positions"/> is keyed identically to the returned dictionary —
    /// qualified type path or "TypePath.printedName" — and points at the declaration line
    /// whose <c>@available</c> annotation produced the entry. Lines and columns are
    /// 1-based; the column advances past leading inline annotations
    /// (<c>@available(...) public func foo()</c>) so it lands on the declaration keyword
    /// (<c>public</c>) rather than the <c>@</c>. Multi-line member signatures point at
    /// the closing-line where the tracker observes the completion, not the opening
    /// keyword line — that imprecision is within the best-effort budget and tightens
    /// when SwiftSyntax replaces the regex parser post-1.0.
    /// </summary>
    public static Dictionary<string, List<AvailabilityAnnotation>> GetAvailabilityAnnotations(
        string swiftInterfacePath,
        out Dictionary<string, SourcePosition> positions)
    {
        var result = new Dictionary<string, List<AvailabilityAnnotation>>();
        positions = new Dictionary<string, SourcePosition>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);
        var tracker = new SwiftInterfaceContextTracker();

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var trimmed = line.TrimStart();
            int leading = line.Length - trimmed.Length;
            // Advance past inline `@xxx(...)` annotations on the decl line so the column
            // lands on the actual declaration keyword (`public`, `func`, etc.), matching
            // the @MainActor and @convention(c) parsers which use regex-match offsets.
            int column = leading + SkipLeadingAnnotations(trimmed) + 1;
            var pos = new SourcePosition(swiftInterfacePath, lineIndex + 1, column);
            var kind = tracker.ProcessLine(trimmed, line);

            switch (kind)
            {
                case SwiftInterfaceContextTracker.LineKind.TypeDeclaration:
                {
                    var annotations = CollectAvailabilityAnnotations(trimmed, tracker);
                    if (annotations.Count > 0)
                    {
                        var key = tracker.QualifiedTypePath;
                        AddAnnotations(result, key, annotations);
                        // First-position-wins: stacked @available clauses across multiple
                        // declarations of the same key (e.g. extension members) keep the
                        // earliest decl line so the diagnostic points at the first source
                        // the parser saw, not the last one.
                        if (!positions.ContainsKey(key))
                            positions[key] = pos;
                    }
                    tracker.ConsumePendingAnnotations();
                    break;
                }

                case SwiftInterfaceContextTracker.LineKind.ExtensionDeclaration:
                    // Extension-scope annotations are handled by the tracker
                    tracker.ConsumePendingAnnotations();
                    break;

                case SwiftInterfaceContextTracker.LineKind.MemberLine:
                {
                    // Use the completed multi-line text when available (multi-line continuations),
                    // otherwise use the current trimmed line.
                    var memberText = tracker.CompletedMultiLine ?? trimmed;

                    // Grouped enum case declarations (`case foo, bar(Int)`) expose each case as a
                    // separate Var node in the ABI JSON, so every name on the line needs the same
                    // availability metadata — not just the first.
                    var groupedCaseNames = SwiftInterfaceContextTracker.ExtractAllEnumCaseNames(memberText);
                    if (groupedCaseNames.Count > 0)
                    {
                        var annotations = CollectAvailabilityAnnotations(memberText, tracker);
                        if (annotations.Count > 0)
                        {
                            foreach (var caseName in groupedCaseNames)
                            {
                                var key = tracker.BuildMemberKey(caseName);
                                AddAnnotations(result, key, annotations);
                                if (!positions.ContainsKey(key))
                                    positions[key] = pos;
                            }
                        }
                    }
                    else
                    {
                        var printedName = SwiftInterfaceContextTracker.ExtractMemberPrintedName(memberText);
                        if (printedName != null)
                        {
                            var annotations = CollectAvailabilityAnnotations(memberText, tracker);
                            if (annotations.Count > 0)
                            {
                                var key = tracker.BuildMemberKey(printedName);
                                AddAnnotations(result, key, annotations);
                                if (!positions.ContainsKey(key))
                                    positions[key] = pos;
                            }
                        }
                    }
                    tracker.ConsumePendingAnnotations();
                    break;
                }

                case SwiftInterfaceContextTracker.LineKind.FreeFunctionLine:
                {
                    // Free functions at module level — collect annotations with bare printedName key
                    var freeFuncText = tracker.CompletedMultiLine ?? trimmed;
                    var freeFuncName = SwiftInterfaceContextTracker.ExtractMemberPrintedName(freeFuncText);
                    if (freeFuncName != null)
                    {
                        var annotations = CollectAvailabilityAnnotations(freeFuncText, tracker);
                        if (annotations.Count > 0)
                        {
                            AddAnnotations(result, freeFuncName, annotations);
                            if (!positions.ContainsKey(freeFuncName))
                                positions[freeFuncName] = pos;
                        }
                    }
                    tracker.ConsumePendingAnnotations();
                    break;
                }

                case SwiftInterfaceContextTracker.LineKind.AnnotationOnly:
                    // Accumulation handled by tracker
                    break;

                default:
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Collects availability annotations from pending annotation lines, extension scope, and inline.
    /// </summary>
    private static List<AvailabilityAnnotation> CollectAvailabilityAnnotations(
        string declarationLine, SwiftInterfaceContextTracker tracker)
    {
        var annotations = new List<AvailabilityAnnotation>();

        // 1. Pending annotation lines (preceding @available lines)
        foreach (var pendingLine in tracker.PendingAnnotationLines)
        {
            foreach (var clause in ExtractAvailableClauses(pendingLine))
            {
                annotations.AddRange(ParseAvailableClause(clause));
            }
        }

        // 2. Extension-scope annotations (inherited from @available on extension decl)
        if (tracker.ExtensionScopeAnnotations != null)
        {
            foreach (var extLine in tracker.ExtensionScopeAnnotations)
            {
                foreach (var clause in ExtractAvailableClauses(extLine))
                {
                    annotations.AddRange(ParseAvailableClause(clause));
                }
            }
        }

        // 3. Inline @available on the declaration line itself
        foreach (var clause in ExtractAvailableClauses(declarationLine))
        {
            annotations.AddRange(ParseAvailableClause(clause));
        }

        return annotations;
    }

    /// <summary>
    /// Finds all @available(...) clauses on a line using balanced-paren matching.
    /// Handles nested parens in messages (e.g., "Use init(config:) instead").
    /// </summary>
    internal static List<string> ExtractAvailableClauses(string line)
    {
        var results = new List<string>();
        int searchFrom = 0;
        while (true)
        {
            int idx = line.IndexOf("@available(", searchFrom, StringComparison.Ordinal);
            if (idx < 0) break;
            int openParen = idx + "@available".Length;
            int depth = 1, i = openParen + 1;
            while (i < line.Length && depth > 0)
            {
                if (line[i] == '(') depth++;
                else if (line[i] == ')') depth--;
                i++;
            }
            if (depth == 0)
                results.Add(line.Substring(openParen + 1, i - openParen - 2));
            searchFrom = i;
        }
        return results;
    }

    /// <summary>
    /// Parses a single @available clause content (without the @available( ) wrapper).
    /// Returns one or more AvailabilityAnnotation records.
    /// </summary>
    internal static List<AvailabilityAnnotation> ParseAvailableClause(string clause)
    {
        var annotations = new List<AvailabilityAnnotation>();
        var parts = SplitAvailableClause(clause);

        if (parts.Count == 0)
            return annotations;

        // Detect the form: @available(*, deprecated, ...) or @available(*, unavailable)
        // vs. platform-specific: @available(iOS 16.0, macOS 13, *)
        // vs. per-platform lifecycle: @available(iOS, introduced: 10, deprecated: 12)

        var first = parts[0].Trim();

        // Skip compiler-level: @available(swift, ...) or @available(SwiftStdlib, ...)
        if (first.Equals("swift", StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith("swift ", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("SwiftStdlib", StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith("SwiftStdlib ", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("_PackageDescription", StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith("_PackageDescription ", StringComparison.OrdinalIgnoreCase))
            return annotations;

        // Check for per-platform lifecycle form: @available(iOS, introduced: 10, deprecated: 12)
        if (parts.Count >= 2 && IsKnownPlatform(first) && !first.Contains(' '))
        {
            // Per-platform form with key-value pairs
            string? introduced = null, deprecated = null, obsoleted = null, message = null, renamed = null;
            bool isUnavailable = false, isDeprecated = false;
            foreach (var part in parts.Skip(1))
            {
                var kv = part.Trim();
                if (kv.StartsWith("introduced:"))
                    introduced = kv.Substring("introduced:".Length).Trim();
                else if (kv.StartsWith("deprecated:"))
                    deprecated = kv.Substring("deprecated:".Length).Trim();
                else if (kv.StartsWith("obsoleted:"))
                    obsoleted = kv.Substring("obsoleted:".Length).Trim();
                else if (kv.StartsWith("message:"))
                    message = ExtractQuotedString(kv.Substring("message:".Length).Trim());
                else if (kv.StartsWith("renamed:"))
                    renamed = ExtractQuotedString(kv.Substring("renamed:".Length).Trim());
                else if (kv == "unavailable")
                    isUnavailable = true;
                else if (kv == "deprecated")
                    isDeprecated = true;
            }
            annotations.Add(new AvailabilityAnnotation(
                NormalizePlatformName(first),
                introduced,
                deprecated,
                obsoleted,
                isDeprecated && deprecated == null,
                isUnavailable,
                message,
                renamed));
            return annotations;
        }

        // Unconditional form: @available(*, deprecated, ...) or @available(*, unavailable)
        if (first == "*" && parts.Count >= 2)
        {
            string? message = null, renamed = null;
            bool isDeprecated = false, isUnavailable = false;
            foreach (var part in parts.Skip(1))
            {
                var kv = part.Trim();
                if (kv == "deprecated")
                    isDeprecated = true;
                else if (kv == "unavailable")
                    isUnavailable = true;
                else if (kv.StartsWith("message:"))
                    message = ExtractQuotedString(kv.Substring("message:".Length).Trim());
                else if (kv.StartsWith("renamed:"))
                    renamed = ExtractQuotedString(kv.Substring("renamed:".Length).Trim());
            }
            annotations.Add(new AvailabilityAnnotation(
                null, null, null, null,
                isDeprecated, isUnavailable, message, renamed));
            return annotations;
        }

        // Shorthand platform form: @available(iOS 16.0, macOS 13, tvOS 13, watchOS 6, *)
        foreach (var part in parts)
        {
            var p = part.Trim();
            if (p == "*") continue;

            var spaceIdx = p.IndexOf(' ');
            if (spaceIdx > 0)
            {
                var platform = p.Substring(0, spaceIdx);
                var version = p.Substring(spaceIdx + 1).Trim();

                if (IsKnownPlatform(platform))
                {
                    annotations.Add(new AvailabilityAnnotation(
                        NormalizePlatformName(platform),
                        version, null, null, false, false, null, null));
                }
            }
        }

        return annotations;
    }

    /// <summary>
    /// Returns the number of characters at the start of a left-trimmed line that are
    /// occupied by leading <c>@xxx</c> or <c>@xxx(...)</c> attribute annotations and the
    /// whitespace separating them from the declaration keyword. Used to advance source
    /// positions past inline annotations so they land on the decl token.
    /// </summary>
    private static int SkipLeadingAnnotations(string trimmed)
    {
        int i = 0;
        while (i < trimmed.Length && trimmed[i] == '@')
        {
            int j = i + 1;
            // Attribute identifiers can be qualified (`@_Concurrency.MainActor`,
            // `@Module.Actor`), so the scanner accepts dot-separated components.
            while (j < trimmed.Length && (char.IsLetterOrDigit(trimmed[j]) || trimmed[j] == '_' || trimmed[j] == '.'))
                j++;
            if (j < trimmed.Length && trimmed[j] == '(')
            {
                int depth = 1;
                int p = j + 1;
                while (p < trimmed.Length && depth > 0)
                {
                    if (trimmed[p] == '(') depth++;
                    else if (trimmed[p] == ')') depth--;
                    p++;
                }
                // Unbalanced parens (shouldn't happen in well-formed swiftinterface):
                // bail out without advancing past this annotation.
                if (depth > 0)
                    return i;
                j = p;
            }
            while (j < trimmed.Length && char.IsWhiteSpace(trimmed[j]))
                j++;
            i = j;
        }
        return i;
    }

    /// <summary>
    /// Splits an @available clause by commas, respecting quoted strings.
    /// </summary>
    private static List<string> SplitAvailableClause(string clause)
    {
        var parts = new List<string>();
        int start = 0;
        bool inQuote = false;
        int parenDepth = 0;
        for (int i = 0; i < clause.Length; i++)
        {
            var c = clause[i];
            if (c == '"') inQuote = !inQuote;
            else if (!inQuote && c == '(') parenDepth++;
            else if (!inQuote && c == ')') parenDepth--;
            else if (!inQuote && parenDepth == 0 && c == ',')
            {
                parts.Add(clause.Substring(start, i - start));
                start = i + 1;
            }
        }
        parts.Add(clause.Substring(start));
        return parts;
    }

    private static bool IsKnownPlatform(string name)
    {
        return name switch
        {
            "iOS" or "macOS" or "tvOS" or "watchOS" or "visionOS" or
            "macCatalyst" or "iOSApplicationExtension" or "macOSApplicationExtension" or
            "tvOSApplicationExtension" or "watchOSApplicationExtension" => true,
            _ => false
        };
    }

    private static string? NormalizePlatformName(string name)
    {
        return name switch
        {
            "iOS" or "iOSApplicationExtension" => "iOS",
            "macOS" or "macOSApplicationExtension" => "macOS",
            "tvOS" or "tvOSApplicationExtension" => "tvOS",
            "watchOS" or "watchOSApplicationExtension" => "watchOS",
            "visionOS" => "visionOS",
            "macCatalyst" => "macCatalyst",
            _ => name
        };
    }

    private static string? ExtractQuotedString(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            return value.Substring(1, value.Length - 2);
        return value;
    }

    private static void AddAnnotations(
        Dictionary<string, List<AvailabilityAnnotation>> dict,
        string key,
        List<AvailabilityAnnotation> annotations)
    {
        if (!dict.TryGetValue(key, out var existing))
        {
            dict[key] = annotations;
        }
        else
        {
            existing.AddRange(annotations);
        }
    }

    /// <summary>
    /// Parses a .swiftinterface file and returns a dictionary mapping
    /// "QualifiedType.printedName" keys to lists of default value expressions.
    /// Each list is index-aligned with ABI parameters — null for params without defaults.
    /// Uses SwiftInterfaceContextTracker for type scope tracking and multi-line handling.
    /// </summary>
    public static Dictionary<string, List<string?>> GetDefaultParameterValues(string swiftInterfacePath)
    {
        var result = new Dictionary<string, List<string?>>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);
        var tracker = new SwiftInterfaceContextTracker();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var kind = tracker.ProcessLine(trimmed, line);

            // Process member lines (inside types) and free functions (top-level)
            bool isMember = kind == SwiftInterfaceContextTracker.LineKind.MemberLine;
            bool isFreeFunc = kind == SwiftInterfaceContextTracker.LineKind.FreeFunctionLine ||
                              (kind == SwiftInterfaceContextTracker.LineKind.Other &&
                               tracker.TypeDepth == 0 &&
                               SwiftInterfaceContextTracker.ExtractMemberPrintedName(trimmed) != null);

            if (isMember || isFreeFunc)
            {
                var memberText = tracker.CompletedMultiLine ?? trimmed;
                var printedName = SwiftInterfaceContextTracker.ExtractMemberPrintedName(memberText);
                if (printedName != null)
                {
                    var defaults = ExtractParameterDefaults(memberText);
                    if (defaults != null && defaults.Any(d => d != null))
                    {
                        var key = tracker.BuildMemberKey(printedName);
                        result[key] = defaults;
                    }
                }
                tracker.ConsumePendingAnnotations();
            }
            else if (kind == SwiftInterfaceContextTracker.LineKind.TypeDeclaration ||
                     kind == SwiftInterfaceContextTracker.LineKind.ExtensionDeclaration)
            {
                tracker.ConsumePendingAnnotations();
            }
        }

        return result;
    }

    /// <summary>
    /// Parses @autoclosure annotations from parameter declarations in a .swiftinterface file.
    /// Returns a dictionary mapping qualified member keys (e.g., "LottieLogger.assert(_:_:fileID:line:)")
    /// to index-aligned lists of booleans indicating which parameters have @autoclosure.
    /// </summary>
    public static Dictionary<string, List<bool>> GetAutoclosureParameters(string swiftInterfacePath)
    {
        var result = new Dictionary<string, List<bool>>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);
        var tracker = new SwiftInterfaceContextTracker();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var kind = tracker.ProcessLine(trimmed, line);

            bool isMember = kind == SwiftInterfaceContextTracker.LineKind.MemberLine;
            bool isFreeFunc = kind == SwiftInterfaceContextTracker.LineKind.FreeFunctionLine ||
                              (kind == SwiftInterfaceContextTracker.LineKind.Other &&
                               tracker.TypeDepth == 0 &&
                               SwiftInterfaceContextTracker.ExtractMemberPrintedName(trimmed) != null);

            if (isMember || isFreeFunc)
            {
                var memberText = tracker.CompletedMultiLine ?? trimmed;
                var printedName = SwiftInterfaceContextTracker.ExtractMemberPrintedName(memberText);
                if (printedName != null)
                {
                    var flags = ExtractAutoclosureFlags(memberText);
                    if (flags != null && flags.Any(f => f))
                    {
                        var key = tracker.BuildMemberKey(printedName);
                        result[key] = flags;
                    }
                }
                tracker.ConsumePendingAnnotations();
            }
            else if (kind == SwiftInterfaceContextTracker.LineKind.TypeDeclaration ||
                     kind == SwiftInterfaceContextTracker.LineKind.ExtensionDeclaration)
            {
                tracker.ConsumePendingAnnotations();
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts @autoclosure flags from a function/init declaration line.
    /// Returns a list index-aligned with parameters — true for params with @autoclosure.
    /// Returns null if the line has no parameter list.
    /// </summary>
    internal static List<bool>? ExtractAutoclosureFlags(string memberLine)
    {
        // Find the parameter list using the same approach as ExtractParameterDefaults
        string? funcName = null;
        var funcMatch = AnyFuncRegex.Match(memberLine);
        if (funcMatch.Success)
            funcName = funcMatch.Groups[1].Value;
        else if (AnyInitRegex.IsMatch(memberLine))
            funcName = "init";
        else
            return null;

        var funcNameIdx = memberLine.IndexOf($" {funcName}(", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            funcNameIdx = memberLine.IndexOf($" {funcName} (", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            funcNameIdx = memberLine.IndexOf($"{funcName}(", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            funcNameIdx = memberLine.IndexOf($" {funcName}<", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            return null;

        var parenStart = memberLine.IndexOf('(', funcNameIdx);
        if (parenStart < 0)
            return null;

        // Find matching close paren
        int depth = 0, parenEnd = parenStart;
        for (int i = parenStart; i < memberLine.Length; i++)
        {
            if (memberLine[i] == '(') depth++;
            if (memberLine[i] == ')')
            {
                depth--;
                if (depth == 0) { parenEnd = i; break; }
            }
        }

        // Guard: if the matching close paren was not found (e.g., multi-line signature
        // where only the first line was passed), bail out instead of crashing on Substring.
        if (parenEnd == parenStart)
            return null;

        var paramStr = memberLine.Substring(parenStart + 1, parenEnd - parenStart - 1);
        if (string.IsNullOrWhiteSpace(paramStr))
            return null;

        var parts = SplitParameters(paramStr);
        var flags = new List<bool>();
        bool hasAny = false;

        foreach (var part in parts)
        {
            // Check if the type portion (after the colon) contains @autoclosure
            var trimmedPart = part.Trim();
            var colonIdx = FindTopLevelColon(trimmedPart);
            if (colonIdx >= 0)
            {
                var typeStr = trimmedPart.Substring(colonIdx + 1).TrimStart();
                bool isAutoclosure = typeStr.Contains("@autoclosure");
                flags.Add(isAutoclosure);
                if (isAutoclosure) hasAny = true;
            }
            else
            {
                flags.Add(false);
            }
        }

        return hasAny ? flags : null;
    }

    /// <summary>
    /// Parses a .swiftinterface file and returns a set of "TypeName.printedName" keys
    /// for members that have variadic parameters (e.g., `_ prefixes: String...`).
    /// The ABI JSON represents variadic params as Array&lt;T&gt;, making them indistinguishable
    /// from regular array parameters. The swiftinterface is the only reliable source.
    /// </summary>
    public static HashSet<string> GetVariadicMembers(string swiftInterfacePath)
    {
        var result = new HashSet<string>();

        if (!File.Exists(swiftInterfacePath))
            return result;

        var lines = File.ReadAllLines(swiftInterfacePath);
        var tracker = new SwiftInterfaceContextTracker();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var kind = tracker.ProcessLine(trimmed, line);

            bool isMember = kind == SwiftInterfaceContextTracker.LineKind.MemberLine;
            bool isFreeFunc = kind == SwiftInterfaceContextTracker.LineKind.FreeFunctionLine ||
                              (kind == SwiftInterfaceContextTracker.LineKind.Other &&
                               tracker.TypeDepth == 0 &&
                               SwiftInterfaceContextTracker.ExtractMemberPrintedName(trimmed) != null);

            if (isMember || isFreeFunc)
            {
                // Use CompletedMultiLine for both members and free functions — it's set when
                // a multi-line continuation completes, regardless of nesting depth.
                var memberText = tracker.CompletedMultiLine ?? trimmed;
                var printedName = SwiftInterfaceContextTracker.ExtractMemberPrintedName(memberText);
                if (printedName != null && HasVariadicParameterInSignature(memberText))
                {
                    var key = tracker.BuildMemberKey(printedName);
                    result.Add(key);
                }
                tracker.ConsumePendingAnnotations();
            }
            else if (kind == SwiftInterfaceContextTracker.LineKind.TypeDeclaration ||
                     kind == SwiftInterfaceContextTracker.LineKind.ExtensionDeclaration)
            {
                tracker.ConsumePendingAnnotations();
            }
        }

        return result;
    }

    /// <summary>
    /// Checks if a function/init declaration line has any variadic parameter (type ending in "...").
    /// Scans the parameter list for "..." at depth-0 (not inside generics/closures).
    /// </summary>
    internal static bool HasVariadicParameterInSignature(string memberLine)
    {
        // Find the function/init name and opening paren
        string? funcName = null;
        var funcMatch = AnyFuncRegex.Match(memberLine);
        if (funcMatch.Success)
            funcName = funcMatch.Groups[1].Value;
        else if (AnyInitRegex.IsMatch(memberLine))
            funcName = "init";
        else
            return false;

        var funcNameIdx = memberLine.IndexOf($" {funcName}(", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            funcNameIdx = memberLine.IndexOf($" {funcName} (", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            funcNameIdx = memberLine.IndexOf($"{funcName}(", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            funcNameIdx = memberLine.IndexOf($" {funcName}<", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            return false;

        var parenStart = memberLine.IndexOf('(', funcNameIdx);
        if (parenStart < 0)
            return false;

        // Find matching close paren
        int depth = 0, parenEnd = parenStart;
        for (int i = parenStart; i < memberLine.Length; i++)
        {
            if (memberLine[i] == '(') depth++;
            if (memberLine[i] == ')')
            {
                depth--;
                if (depth == 0) { parenEnd = i; break; }
            }
        }

        if (parenEnd == parenStart)
            return false;

        var paramStr = memberLine.Substring(parenStart + 1, parenEnd - parenStart - 1);
        if (string.IsNullOrWhiteSpace(paramStr))
            return false;

        var parts = SplitParameters(paramStr);
        foreach (var part in parts)
        {
            var trimmedPart = part.Trim();
            // Find the colon separating label from type
            var colonIdx = FindTopLevelColon(trimmedPart);
            if (colonIdx >= 0)
            {
                var typeStr = trimmedPart.Substring(colonIdx + 1).Trim();
                // Remove default value (everything after " = " at depth 0)
                var eqIdx = FindTopLevelEquals(typeStr);
                if (eqIdx >= 0)
                    typeStr = typeStr.Substring(0, eqIdx).TrimEnd();
                // Variadic: type ends with "..."
                if (typeStr.EndsWith("..."))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the index of " = " at depth-0 in a type string.
    /// Returns -1 if not found.
    /// </summary>
    private static int FindTopLevelEquals(string s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<' || c == '(' || c == '[') depth++;
            else if (c == '>' || c == ')' || c == ']') depth--;
            else if (c == '=' && depth == 0 && i > 0 && s[i - 1] == ' ')
            {
                if (i + 1 < s.Length && s[i + 1] == ' ')
                    return i - 1; // Return start of " = "
            }
        }
        return -1;
    }

    /// <summary>
    /// Extracts default value expressions from a function/init declaration line.
    /// Returns a list index-aligned with parameters — null for params without defaults.
    /// Returns null if the line has no parameter list.
    /// </summary>
    internal static List<string?>? ExtractParameterDefaults(string memberLine)
    {
        // Find the function/init name and opening paren
        string? funcName = null;
        var funcMatch = AnyFuncRegex.Match(memberLine);
        if (funcMatch.Success)
            funcName = funcMatch.Groups[1].Value;
        else if (AnyInitRegex.IsMatch(memberLine))
            funcName = "init";
        else
            return null;

        // Locate the parameter list
        var funcNameIdx = memberLine.IndexOf($" {funcName}(", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            funcNameIdx = memberLine.IndexOf($" {funcName} (", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            funcNameIdx = memberLine.IndexOf($"{funcName}(", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            funcNameIdx = memberLine.IndexOf($" {funcName}<", StringComparison.Ordinal);
        if (funcNameIdx < 0)
            return null;

        var parenStart = memberLine.IndexOf('(', funcNameIdx);
        if (parenStart < 0)
            return null;

        // Find matching close paren
        int depth = 0, parenEnd = parenStart;
        for (int i = parenStart; i < memberLine.Length; i++)
        {
            if (memberLine[i] == '(') depth++;
            if (memberLine[i] == ')')
            {
                depth--;
                if (depth == 0) { parenEnd = i; break; }
            }
        }

        // Guard: if the matching close paren was not found (e.g., multi-line signature
        // where only the first line was passed), bail out instead of crashing on Substring.
        if (parenEnd == parenStart)
            return null;

        var paramStr = memberLine.Substring(parenStart + 1, parenEnd - parenStart - 1);
        if (string.IsNullOrWhiteSpace(paramStr))
            return null;

        var parts = SplitParameters(paramStr);
        var defaults = new List<string?>();
        bool hasAnyDefault = false;

        foreach (var part in parts)
        {
            var defaultExpr = ExtractDefaultFromParam(part.Trim());
            defaults.Add(defaultExpr);
            if (defaultExpr != null) hasAnyDefault = true;
        }

        return hasAnyDefault ? defaults : null;
    }

    /// <summary>
    /// Extracts the default value expression from a single parameter segment.
    /// Scans for " = " at depth-0 (outside nested parens/brackets/generics).
    /// Returns the expression after "= " or null if no default.
    /// </summary>
    private static string? ExtractDefaultFromParam(string paramSegment)
    {
        // Find the colon that separates "label: Type" first
        int colonIdx = -1;
        int depth = 0;
        for (int i = 0; i < paramSegment.Length; i++)
        {
            char c = paramSegment[i];
            if (c == '<' || c == '(' || c == '[') depth++;
            else if (c == '>' || c == ')' || c == ']') depth--;
            else if (c == ':' && depth == 0)
            {
                colonIdx = i;
                break;
            }
        }

        if (colonIdx < 0)
            return null;

        // Search for " = " after the type annotation, at depth 0
        depth = 0;
        var afterColon = paramSegment.Substring(colonIdx + 1);
        for (int i = 0; i < afterColon.Length; i++)
        {
            char c = afterColon[i];
            if (c == '<' || c == '(' || c == '[') depth++;
            else if (c == '>' || c == ')' || c == ']') depth--;
            else if (c == '=' && depth == 0 && i > 0 && afterColon[i - 1] == ' ')
            {
                // Verify there's a space after '=' too (or it's the last char)
                if (i + 1 < afterColon.Length && afterColon[i + 1] == ' ')
                {
                    return afterColon.Substring(i + 2).Trim();
                }
            }
        }

        return null;
    }
}
