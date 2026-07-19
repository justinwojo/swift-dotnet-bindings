// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BindingsGeneration
{
    /// <summary>
    /// Sub-cause classification for stripped blocks. Used to distinguish "the new emission-time
    /// gate caught the dominant case" from "the gate missed, and the post-processor swept up the
    /// residue." Each stripped block is bucketed by the trigger that fired in the post-processor
    /// (priority follows the post-processor's short-circuit OR order).
    /// </summary>
    public enum StripSubCause
    {
        /// <summary>
        /// Block body referenced an <c>@usableFromInline internal</c> type (or any name in
        /// <c>InternalTypeNames</c>). Dominant Pattern 2 case — should drop near-zero once the
        /// <c>Pattern2InternalTypeReach</c> emission gate is in place.
        /// </summary>
        InternalType,

        /// <summary>
        /// Block body referenced an ObjC type that is explicitly unavailable in Swift
        /// (e.g. <c>NSInvocation</c>). Not addressed by the emission-time gate.
        /// </summary>
        NSInvocation,

        /// <summary>
        /// Pattern-specific broken-shape trigger (<c>EveryProtocol()</c> placeholder,
        /// <c>.load(as: @escaping)</c>, etc.) — the catch-all bucket for safety-net strips
        /// that aren't internal-type or NSInvocation reaches.
        /// </summary>
        Other,
    }

    /// <summary>
    /// Result of post-processing a Swift wrapper file.
    /// </summary>
    public sealed class PostProcessingResult
    {
        public required string CleanedContent { get; init; }
        public required int StrippedBlockCount { get; init; }

        /// <summary>
        /// Per-sub-cause counts for the blocks counted in <see cref="StrippedBlockCount"/>.
        /// Sums to <see cref="StrippedBlockCount"/>. Used by validation reporting to track
        /// whether the <c>Pattern2InternalTypeReach</c> emission-time gate is taking the
        /// load expected of it (the <see cref="StripSubCause.InternalType"/> bucket should
        /// drop to a small documented residue).
        /// </summary>
        public IReadOnlyDictionary<StripSubCause, int> StrippedBlocksBySubCause { get; init; }
            = new Dictionary<StripSubCause, int>();

        /// <summary>
        /// Set of @_cdecl / @_silgen_name symbol names that were stripped from the wrapper.
        /// Used by the C# stripped-symbol reconciler to suppress P/Invokes targeting these symbols.
        /// </summary>
        public IReadOnlySet<string> StrippedSymbols { get; init; } = new HashSet<string>();

        /// <summary>
        /// For each line of <see cref="CleanedContent"/>, the 0-based index of the line in the
        /// original source it came from; null when that mapping could not be established.
        /// </summary>
        public IReadOnlyList<int>? CleanedLineSources { get; init; }
    }

    /// <summary>
    /// Strips known-broken patterns from generated Swift wrapper code.
    /// C# port of the Python post-processing in build-async-wrapper.sh.
    /// </summary>
    public static class SwiftWrapperPostProcessor
    {
        /// <summary>
        /// ObjC types that are explicitly unavailable in Swift. Wrapper functions that reference
        /// these types must be stripped because they cannot compile in a Swift source file.
        /// </summary>
        private static readonly HashSet<string> SwiftUnavailableTypes = new(StringComparer.Ordinal)
        {
            "NSInvocation",
        };

        // NOTE: Safety-net patterns (b)-(f) were removed during the architecture refactoring.
        // Pattern (b) self-without-_self: prevented by extension scoping in emitters.
        // Pattern (c) __self.init: prevented at emission time.
        // Pattern (e) non-escaping closure in Task: prevented at emission time.
        // Pattern (f) raw generic params: prevented by WrapperValidation.HasRawGenericTypeParams
        //   gate in DefaultParameterOverloadEmitter + wrapper emitters.

        /// <summary>
        /// Post-processes Swift source content, stripping known-broken wrapper patterns.
        /// </summary>
        public static PostProcessingResult Process(string sourceContent)
        {
            return Process(sourceContent, internalTypeNames: null, onSafetyNetWarning: null);
        }

        /// <summary>
        /// Post-processes Swift source content, stripping known-broken wrapper patterns
        /// and functions that reference internal (non-public) types.
        /// </summary>
        /// <param name="sourceContent">Swift source code to process.</param>
        /// <param name="internalTypeNames">
        /// Set of internal type names to strip. Contains both short names ("InternalType")
        /// and qualified names ("Module.InternalType"). Null to skip internal type stripping.
        /// </param>
        /// <param name="onSafetyNetWarning">
        /// Optional callback invoked when a safety-net pattern fires. These patterns should no longer
        /// match in normal operation (they are now prevented at emission time), so a warning indicates
        /// a regression in the emitter.
        /// </param>
        /// <param name="currentModuleName">
        /// The Swift module currently being generated. Lets the internal-type matcher tell a
        /// current-module-qualified reference (<c>&lt;currentModule&gt;.X</c>, strip) apart from a
        /// foreign-module reference (<c>Foundation.Data</c>, keep) when only a short name is known.
        /// Null falls back to bare-only short-name matching.
        /// </param>
        public static PostProcessingResult Process(string sourceContent, HashSet<string>? internalTypeNames, Action<string>? onSafetyNetWarning = null, string? currentModuleName = null)
        {
            if (string.IsNullOrEmpty(sourceContent))
                return new PostProcessingResult { CleanedContent = sourceContent, StrippedBlockCount = 0 };

            var lines = SplitLines(sourceContent);
            var outputLines = new List<string>();
            // Parallel to outputLines: source line index for each kept line. Kept in lockstep so
            // callers can attribute cleaned positions; length mismatch → null (never a wrong map).
            var cleanedLineSources = new List<int>();
            int removedCount = 0;
            var strippedSymbols = new HashSet<string>();
            var subCauseCounts = new Dictionary<StripSubCause, int>
            {
                [StripSubCause.InternalType] = 0,
                [StripSubCause.NSInvocation] = 0,
                [StripSubCause.Other] = 0,
            };
            int i = 0;

            while (i < lines.Count)
            {
                var stripped = lines[i].TrimStart();

                // Pattern 1: EveryProtocol blocks that reference internal types.
                // Valid EveryProtocol conformances and the class definition are preserved.
                // Codable/Error stub conformances are always preserved (they only use stdlib types).
                if (stripped.StartsWith("extension EveryProtocol", StringComparison.Ordinal) ||
                    stripped.StartsWith("class EveryProtocol", StringComparison.Ordinal) ||
                    stripped.StartsWith("public final class EveryProtocol", StringComparison.Ordinal))
                {
                    int end = FindBlockEnd(lines, i);

                    // Always preserve: class definition, Codable/Error stubs, composition protocols
                    if (stripped.StartsWith("class EveryProtocol", StringComparison.Ordinal) ||
                        stripped.StartsWith("public final class EveryProtocol", StringComparison.Ordinal) ||
                        IsEveryProtocolCodableStub(stripped) ||
                        IsEveryProtocolCompositionConformance(lines, i, end))
                    {
                        // Don't strip — these are valid EveryProtocol system blocks
                    }
                    else
                    {
                        // For protocol conformance extensions with method/property bodies,
                        // strip if the body references an internal or Swift-unavailable type
                        var body = ScanBlockBody(lines, i, end);
                        bool refsInternal = ReferencesInternalType(body, internalTypeNames, currentModuleName);
                        bool refsUnavail = !refsInternal && ReferencesSwiftUnavailableType(body);
                        if (refsInternal || refsUnavail)
                        {
                            ExtractSymbolsFromBlock(lines, i, end, strippedSymbols);
                            subCauseCounts[refsInternal ? StripSubCause.InternalType : StripSubCause.NSInvocation]++;
                            RemoveTrailingOriginAnchor(outputLines, cleanedLineSources);
                            removedCount++;
                            i = end + 1;
                            continue;
                        }
                    }
                }

                // Pattern 2: @_silgen_name / @_cdecl + function blocks with broken patterns
                // Also match when prefixed with @MainActor (same line or preceding line)
                if (stripped.StartsWith("@_silgen_name(", StringComparison.Ordinal) ||
                    stripped.StartsWith("@_cdecl(", StringComparison.Ordinal) ||
                    stripped.StartsWith("@MainActor @_silgen_name(", StringComparison.Ordinal) ||
                    stripped.StartsWith("@MainActor @_cdecl(", StringComparison.Ordinal))
                {
                    int end = FindBlockEnd(lines, i);
                    var body = ScanBlockBody(lines, i, end);

                    bool brokenPat = IsSilgenNameBroken(lines, i, end, body, onSafetyNetWarning);
                    bool refsInternal = !brokenPat && ReferencesInternalType(body, internalTypeNames, currentModuleName);
                    bool refsUnavail = !brokenPat && !refsInternal && ReferencesSwiftUnavailableType(body);
                    if (brokenPat || refsInternal || refsUnavail)
                    {
                        ExtractSymbolsFromBlock(lines, i, end, strippedSymbols);
                        subCauseCounts[ClassifySubCause(brokenPat, refsInternal, refsUnavail)]++;
                        // The wrapper emitters write a "// Comment\n@available(...)\n" preamble
                        // BEFORE the @_cdecl line. Pop those preamble lines from outputLines so they
                        // don't end up dangling — `@available` annotations on a missing declaration
                        // produce "expected declaration" errors at swiftc time.
                        RemoveTrailingWrapperPreamble(outputLines, cleanedLineSources);
                        removedCount++;
                        i = end + 1;
                        continue;
                    }
                }

                // Pattern 2b: Standalone @MainActor on its own line, followed by @_cdecl / @_silgen_name
                // ConstructorWrapperEmitter emits @MainActor and @_cdecl on separate lines.
                // If the block is broken, strip the @MainActor line along with the function block.
                if (stripped.TrimEnd() == "@MainActor" && i + 1 < lines.Count)
                {
                    var nextStripped = lines[i + 1].TrimStart();
                    if (nextStripped.StartsWith("@_silgen_name(", StringComparison.Ordinal) ||
                        nextStripped.StartsWith("@_cdecl(", StringComparison.Ordinal))
                    {
                        int end = FindBlockEnd(lines, i + 1);
                        var body = ScanBlockBody(lines, i + 1, end);

                        bool brokenPat = IsSilgenNameBroken(lines, i + 1, end, body, onSafetyNetWarning);
                        bool refsInternal = !brokenPat && ReferencesInternalType(body, internalTypeNames, currentModuleName);
                        bool refsUnavail = !brokenPat && !refsInternal && ReferencesSwiftUnavailableType(body);
                        if (brokenPat || refsInternal || refsUnavail)
                        {
                            ExtractSymbolsFromBlock(lines, i, end, strippedSymbols);
                            subCauseCounts[ClassifySubCause(brokenPat, refsInternal, refsUnavail)]++;
                            RemoveTrailingWrapperPreamble(outputLines, cleanedLineSources);
                            removedCount++;
                            i = end + 1;
                            continue;
                        }
                    }
                }

                // Pattern 3: Extension blocks with broken code (not EveryProtocol — already handled)
                if (stripped.StartsWith("extension ", StringComparison.Ordinal) &&
                    !stripped.StartsWith("extension EveryProtocol", StringComparison.Ordinal))
                {
                    int end = FindBlockEnd(lines, i);
                    var body = ScanBlockBody(lines, i, end);

                    // Check both the extension header and body for internal type references.
                    // The header (e.g., "extension Module.TypeName: _SBW_...") names the type
                    // being extended, which may be internal even when the body uses Self.
                    bool brokenPat = IsExtensionBroken(lines, i, end, body, onSafetyNetWarning);
                    bool refsInternal = !brokenPat && (
                        ReferencesInternalType(body, internalTypeNames, currentModuleName) ||
                        ReferencesInternalType(stripped, internalTypeNames, currentModuleName));
                    bool refsUnavail = !brokenPat && !refsInternal && ReferencesSwiftUnavailableType(body);
                    if (brokenPat || refsInternal || refsUnavail)
                    {
                        ExtractSymbolsFromBlock(lines, i, end, strippedSymbols);
                        subCauseCounts[ClassifySubCause(brokenPat, refsInternal, refsUnavail)]++;
                        RemoveTrailingOriginAnchor(outputLines, cleanedLineSources);
                        removedCount++;
                        i = end + 1;
                        continue;
                    }
                }

                // Pattern 3c: Private protocol _SBW_ declarations referencing internal types.
                // These are dispatch protocols for the generic factory pattern. When the protocol
                // signature references an internal type (e.g., SharedBox<T>), the wrapper can't compile.
                if (stripped.StartsWith("private protocol _SBW_", StringComparison.Ordinal))
                {
                    int end = FindBlockEnd(lines, i);
                    var body = ScanBlockBody(lines, i, end);

                    bool refsInternal =
                        ReferencesInternalType(body, internalTypeNames, currentModuleName) ||
                        ReferencesInternalType(stripped, internalTypeNames, currentModuleName);
                    bool refsUnavail = !refsInternal && ReferencesSwiftUnavailableType(body);
                    if (refsInternal || refsUnavail)
                    {
                        ExtractSymbolsFromBlock(lines, i, end, strippedSymbols);
                        subCauseCounts[refsInternal ? StripSubCause.InternalType : StripSubCause.NSInvocation]++;
                        RemoveTrailingOriginAnchor(outputLines, cleanedLineSources);
                        removedCount++;
                        i = end + 1;
                        continue;
                    }
                }

                // Pattern 4: Standalone public func blocks (without @_silgen_name prefix)
                if (stripped.StartsWith("public func SBW_", StringComparison.Ordinal) ||
                    stripped.StartsWith("public func PInvoke_", StringComparison.Ordinal))
                {
                    int end = FindBlockEnd(lines, i);
                    var body = ScanBlockBody(lines, i, end);

                    bool brokenPat = IsStandaloneFuncBroken(body, i, onSafetyNetWarning);
                    bool refsInternal = !brokenPat && ReferencesInternalType(body, internalTypeNames, currentModuleName);
                    bool refsUnavail = !brokenPat && !refsInternal && ReferencesSwiftUnavailableType(body);
                    if (brokenPat || refsInternal || refsUnavail)
                    {
                        ExtractSymbolsFromBlock(lines, i, end, strippedSymbols);
                        subCauseCounts[ClassifySubCause(brokenPat, refsInternal, refsUnavail)]++;
                        RemoveTrailingWrapperPreamble(outputLines, cleanedLineSources);
                        removedCount++;
                        i = end + 1;
                        continue;
                    }
                }

                outputLines.Add(lines[i]);
                cleanedLineSources.Add(i);
                i++;
            }

            // Fail closed: a mismatched map would misattribute silently; absent mapping only
            // degrades attribution.
            IReadOnlyList<int>? lineSources = cleanedLineSources.Count == outputLines.Count
                ? cleanedLineSources
                : null;

            return new PostProcessingResult
            {
                CleanedContent = string.Join("", outputLines),
                StrippedBlockCount = removedCount,
                StrippedBlocksBySubCause = subCauseCounts,
                StrippedSymbols = strippedSymbols,
                CleanedLineSources = lineSources,
            };
        }

        /// <summary>
        /// Picks the highest-priority sub-cause for a stripped block. Priority follows the
        /// post-processor's short-circuit OR order: pattern-specific broken &gt; internal-type
        /// reference &gt; Swift-unavailable type reference.
        /// </summary>
        private static StripSubCause ClassifySubCause(bool brokenPat, bool refsInternal, bool refsUnavail)
        {
            if (brokenPat) return StripSubCause.Other;
            if (refsInternal) return StripSubCause.InternalType;
            if (refsUnavail) return StripSubCause.NSInvocation;
            // Caller guarantees at least one trigger; defensive fallback only.
            return StripSubCause.Other;
        }

        /// <summary>
        /// Finds the end of a brace-delimited block starting at the given index. Braces inside string
        /// literals and comments are ignored so a default value like <c>= "}"</c> does not close the
        /// block early.
        /// </summary>
        internal static int FindBlockEnd(IReadOnlyList<string> lines, int start)
        {
            int depth = 0;
            bool sawOpenBrace = false;
            int blockCommentDepth = 0;
            for (int j = start; j < lines.Count; j++)
            {
                depth += StructuralBraceScanner.NetLineDelta(lines[j], ref blockCommentDepth, ref sawOpenBrace);
                if (sawOpenBrace && depth <= 0 && j > start)
                    return j;
            }
            return lines.Count - 1;
        }

        /// <summary>
        /// Returns concatenated text of lines[start..end] inclusive.
        /// </summary>
        internal static string ScanBlockBody(IReadOnlyList<string> lines, int start, int end)
        {
            int totalLen = 0;
            for (int j = start; j <= end && j < lines.Count; j++)
                totalLen += lines[j].Length;

            var sb = new System.Text.StringBuilder(totalLen);
            for (int j = start; j <= end && j < lines.Count; j++)
                sb.Append(lines[j]);
            return sb.ToString();
        }

        /// <summary>
        /// Checks if a @_silgen_name function block contains known-broken patterns.
        /// Safety-net patterns (b)-(f) now fire a warning callback when they match,
        /// since these patterns should be prevented at emission time.
        /// </summary>
        private static bool IsSilgenNameBroken(IReadOnlyList<string> lines, int start, int end, string body, Action<string>? onSafetyNetWarning)
        {
            // (a) EveryProtocol() — strip wrapper functions that use EveryProtocol() as a
            // placeholder for unimplemented conformances. But PRESERVE witness table getter
            // functions (Get_EveryProtocol_*), SetVtable functions, and EveryProtocol lifecycle
            // helpers (SBW_CreateEveryProtocol, etc.) — these are valid code.
            if (body.Contains("EveryProtocol()"))
            {
                // Check if this is a valid EveryProtocol system function
                if (body.Contains("Get_EveryProtocol_") || body.Contains("SetVtable") ||
                    body.Contains("Set_vtable") || body.Contains("_vtable") ||
                    body.Contains("SBW_CreateEveryProtocol") || body.Contains("SBW_ReleaseEveryProtocol") ||
                    body.Contains("SBW_GetMetadata_EveryProtocol"))
                    return false;
                return true;
            }

            // Safety-net patterns (b)-(f) removed — now prevented at emission time.

            // (g) .load(as: @escaping — closure types in .load(as:) metatype context.
            // @escaping is a storage qualifier not valid in metatype position, and .self
            // binds to the return type instead of the full function type. Prevented at
            // emission time by CanConvertToCdecl rejecting closure params.
            if (body.Contains(".load(as: @escaping") || body.Contains(".load(as: @Sendable"))
            {
                onSafetyNetWarning?.Invoke($"Line ~{start}: .load(as: @escaping) closure in metatype context (should be prevented by CanConvertToCdecl)");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if an extension block contains known-broken patterns.
        /// Safety-net patterns fire a warning callback when they match.
        /// </summary>
        private static bool IsExtensionBroken(IReadOnlyList<string> lines, int start, int end, string body, Action<string>? onSafetyNetWarning)
        {
            // (a) EveryProtocol() — strip extension blocks that use EveryProtocol() as a
            // placeholder. But EveryProtocol extensions are handled by Pattern 1 (above),
            // so this only fires for non-EveryProtocol extensions that somehow reference it.
            if (body.Contains("EveryProtocol()"))
            {
                // Valid EveryProtocol system functions use EveryProtocol() for witness table extraction
                if (body.Contains("Get_EveryProtocol_") || body.Contains("SetVtable") ||
                    body.Contains("Set_vtable") || body.Contains("_vtable"))
                    return false;
                return true;
            }

            // Safety-net patterns (c), (e), (f) removed — now prevented at emission time.

            return false;
        }

        /// <summary>
        /// Checks if a standalone public func block contains known-broken patterns.
        /// Safety-net patterns fire a warning callback when they match.
        /// </summary>
        private static bool IsStandaloneFuncBroken(string body, int start, Action<string>? onSafetyNetWarning)
        {
            // (a) EveryProtocol() — strip standalone functions that use EveryProtocol() as a
            // placeholder. Preserve valid EveryProtocol system functions.
            if (body.Contains("EveryProtocol()"))
            {
                if (body.Contains("Get_EveryProtocol_") || body.Contains("SetVtable") ||
                    body.Contains("Set_vtable") || body.Contains("_vtable"))
                    return false;
                return true;
            }

            // Safety-net pattern (f) removed — now prevented at emission time.

            return false;
        }


        /// <summary>
        /// Pops trailing wrapper-preamble lines (`@available`, `@MainActor`, blank lines, and the
        /// "// Property getter @_cdecl wrapper for ..." style comments emitted by the wrapper
        /// emitters) from <paramref name="outputLines"/>. Called after a `@_cdecl` /
        /// `@_silgen_name` block has been stripped, to prevent dangling annotations on a missing
        /// declaration (which produces "expected declaration" errors at swiftc time).
        ///
        /// The walk stops at the first line that doesn't match a known preamble pattern, so it
        /// will never run into the body of an unrelated previous declaration.
        /// </summary>
        /// <param name="cleanedLineSources">
        /// Parallel source-index list for <paramref name="outputLines"/>; each removal must
        /// pop the matching entry so length parity (and fail-closed mapping) is preserved.
        /// </param>
        internal static void RemoveTrailingWrapperPreamble(List<string> outputLines, List<int> cleanedLineSources)
        {
            while (outputLines.Count > 0)
            {
                var trimmed = outputLines[^1].TrimStart();
                var trimmedEnd = trimmed.TrimEnd();

                // Blank line — part of preamble spacing
                if (trimmedEnd.Length == 0)
                {
                    outputLines.RemoveAt(outputLines.Count - 1);
                    if (cleanedLineSources.Count > 0)
                        cleanedLineSources.RemoveAt(cleanedLineSources.Count - 1);
                    continue;
                }

                // Annotation lines that wrappers can be preceded by
                if (trimmedEnd.StartsWith("@available(", StringComparison.Ordinal) ||
                    trimmedEnd == "@MainActor")
                {
                    outputLines.RemoveAt(outputLines.Count - 1);
                    if (cleanedLineSources.Count > 0)
                        cleanedLineSources.RemoveAt(cleanedLineSources.Count - 1);
                    continue;
                }

                // Wrapper-preamble comment lines emitted by the wrapper emitters. Match
                // narrowly to avoid eating unrelated comments from neighbouring declarations.
                if (trimmedEnd.StartsWith("//", StringComparison.Ordinal) &&
                    IsWrapperPreambleComment(trimmedEnd))
                {
                    outputLines.RemoveAt(outputLines.Count - 1);
                    if (cleanedLineSources.Count > 0)
                        cleanedLineSources.RemoveAt(cleanedLineSources.Count - 1);
                    continue;
                }

                break;
            }
        }

        /// <summary>
        /// Removes the <c>// SBW-ORIGIN:</c> provenance anchor a symbol-less block was emitted with,
        /// along with any availability preamble between the anchor and the (now-stripped) block head.
        /// </summary>
        /// <remarks>
        /// The symbol-less patterns (EveryProtocol conformance, plain extension, <c>_SBW_</c> dispatch
        /// protocol) emit their anchor as the first line of a <c>[anchor, @available?]</c> preamble
        /// ahead of the head. When the block is stripped the head and body go, but that preamble is
        /// already in <paramref name="outputLines"/>; left behind, the dangling anchor would make
        /// <see cref="Diagnostics.WrapperBlockIndex"/> span the <em>next</em> block and misattribute a
        /// diagnostic to it. The removal is committed only when the anchor is actually found within the
        /// trailing preamble window, so an un-anchored block's output is left exactly as before.
        /// </remarks>
        internal static void RemoveTrailingOriginAnchor(List<string> outputLines, List<int> cleanedLineSources)
        {
            int scan = outputLines.Count - 1;
            while (scan >= 0)
            {
                var trimmed = outputLines[scan].TrimStart().TrimEnd();
                if (IsOriginAnchor(trimmed))
                    break;
                // Only the block's own availability / spacing may sit between its anchor and its head.
                if (trimmed.Length == 0 ||
                    trimmed.StartsWith("@available(", StringComparison.Ordinal) ||
                    trimmed == "@MainActor")
                {
                    scan--;
                    continue;
                }
                // Hit real content before any anchor — this block carried none; leave everything.
                return;
            }

            if (scan < 0)
                return;

            int removeCount = outputLines.Count - scan;
            outputLines.RemoveRange(scan, removeCount);
            // outputLines and cleanedLineSources are maintained in lockstep, so the same range clears.
            if (cleanedLineSources.Count >= scan + removeCount)
                cleanedLineSources.RemoveRange(scan, removeCount);
        }

        /// <summary>True when a trimmed line is a <c>// SBW-ORIGIN:</c> provenance anchor.</summary>
        private static bool IsOriginAnchor(string trimmedLine) =>
            trimmedLine.StartsWith("// SBW-ORIGIN:", StringComparison.Ordinal);

        /// <summary>
        /// Returns true if the comment line matches one of the known wrapper-emitter preamble
        /// comments — these are safe to remove together with the wrapper they describe.
        /// Anything else (e.g., a code comment from a previous declaration) is preserved.
        /// </summary>
        private static bool IsWrapperPreambleComment(string trimmedComment)
        {
            return trimmedComment.Contains("@_cdecl wrapper for", StringComparison.Ordinal)
                || trimmedComment.Contains("@_silgen_name wrapper for", StringComparison.Ordinal)
                || trimmedComment.Contains("Routes through C calling convention", StringComparison.Ordinal)
                || trimmedComment.Contains("Routes method through C calling convention", StringComparison.Ordinal);
        }

        private static readonly Regex CdeclSymbolRegex = new(
            @"@_(?:cdecl|silgen_name)\(""([^""]+)""\)",
            RegexOptions.Compiled);

        /// <summary>
        /// Extracts @_cdecl / @_silgen_name symbol names from a block being stripped.
        /// Scans the entire block range since extension blocks may contain multiple functions.
        /// </summary>
        internal static void ExtractSymbolsFromBlock(IReadOnlyList<string> lines, int start, int end, HashSet<string> symbols)
        {
            for (int j = start; j <= end && j < lines.Count; j++)
            {
                var matches = CdeclSymbolRegex.Matches(lines[j]);
                foreach (Match match in matches)
                {
                    symbols.Add(match.Groups[1].Value);
                }
            }
        }

        /// <summary>
        /// Checks if a block body references any internal (non-public) type names.
        /// Uses word-boundary matching to avoid false positives (e.g., "Layer" won't match "Player").
        ///
        /// Module-aware matching: the internal-type set carries each current-module internal type
        /// under BOTH its short name (<c>Data</c>) and its module-qualified name (<c>MyModule.Data</c>).
        /// Generated wrappers, however, spell BOTH current-module internal types AND cross-module
        /// public types module-qualified — e.g. <c>SwiftBindingsTestLib.InternalHolder</c> (current,
        /// must strip) and <c>Foundation.Data</c> (foreign, must survive) are syntactically identical.
        /// A naive <c>\b&lt;short&gt;\b</c> over-strips the foreign one (the <c>.Data</c> suffix matches
        /// internal <c>Data</c> → silent public-API loss); a blanket negative lookbehind under-strips the
        /// current one (it also suppresses the <c>.InternalHolder</c> match → uncompilable wrapper). The
        /// only correct discriminator is the module prefix, so this mirrors the emission-time gate
        /// <see cref="InternalTypeReferenceWalker"/>'s rule: a SHORT internal name matches a reference
        /// only when that reference denotes the CURRENT module's type — written bare (not preceded by a
        /// <c>.</c>) OR qualified with the current module (<c>&lt;currentModule&gt;.X</c>) — and never
        /// when qualified with a different module. QUALIFIED set entries (<c>Module.Type[.Nested]</c>)
        /// already carry their own module prefix and use a plain word-boundary match; they also cover
        /// nested current-module types whose bare/short form the current-module-qualified pattern can't
        /// reach. When <paramref name="currentModuleName"/> is unknown (null), only the bare form is
        /// matched for short names (the legacy behavior for callers that don't supply a module).
        /// </summary>
        private static bool ReferencesInternalType(string body, HashSet<string>? internalTypeNames, string? currentModuleName)
        {
            if (internalTypeNames == null || internalTypeNames.Count == 0)
                return false;

            foreach (var typeName in internalTypeNames)
            {
                var escaped = Regex.Escape(typeName);

                if (typeName.Contains('.'))
                {
                    // Qualified entry — the module prefix disambiguates it; plain match.
                    if (Regex.IsMatch(body, @"\b" + escaped + @"\b"))
                        return true;
                    continue;
                }

                // Short (unqualified) entry. Match a bare occurrence (not preceded by '.') — this
                // catches a genuine current-module reference and never the '.Data' tail of a
                // qualified foreign type like Foundation.Data.
                if (Regex.IsMatch(body, @"(?<!\.)\b" + escaped + @"\b"))
                    return true;

                // Also match the current-module-qualified spelling (<currentModule>.X) that wrappers
                // emit for current-module types, while still rejecting <foreignModule>.X.
                if (!string.IsNullOrEmpty(currentModuleName) &&
                    Regex.IsMatch(body, @"\b" + Regex.Escape(currentModuleName) + @"\." + escaped + @"\b"))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true if the body references an ObjC type that is explicitly unavailable in Swift.
        /// These types exist in ObjC headers but are annotated with NS_SWIFT_UNAVAILABLE.
        /// </summary>
        private static bool ReferencesSwiftUnavailableType(string body)
        {
            foreach (var typeName in SwiftUnavailableTypes)
            {
                if (body.Contains(typeName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true if the extension line is a Codable/Error stub conformance for EveryProtocol.
        /// These stubs use only Swift stdlib types and should never be stripped.
        /// </summary>
        private static bool IsEveryProtocolCodableStub(string strippedLine)
        {
            // Match: "extension EveryProtocol: Decodable {", "extension EveryProtocol: Encodable {",
            //        "extension EveryProtocol: Swift.Error {}", "extension EveryProtocol: Error {}"
            return strippedLine.StartsWith("extension EveryProtocol: Decodable", StringComparison.Ordinal) ||
                   strippedLine.StartsWith("extension EveryProtocol: Encodable", StringComparison.Ordinal) ||
                   strippedLine.StartsWith("extension EveryProtocol: Error", StringComparison.Ordinal) ||
                   strippedLine.StartsWith("extension EveryProtocol: Swift.Error", StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns true if the EveryProtocol extension is a composition conformance (empty body).
        /// Composition protocols have no own members — the extension is just "{}" or has only comments.
        /// </summary>
        private static bool IsEveryProtocolCompositionConformance(IReadOnlyList<string> lines, int start, int end)
        {
            // A composition conformance is a single-line or two-line block like:
            // "extension EveryProtocol: Module.Protocol {}"
            // or "extension EveryProtocol: Module.Protocol {\n}"
            for (int j = start; j <= end && j < lines.Count; j++)
            {
                var line = lines[j].Trim();
                // Skip empty lines, comments, and braces
                if (string.IsNullOrEmpty(line) || line.StartsWith("//") ||
                    line == "{" || line == "}" || line == "{}")
                    continue;
                // Skip the extension declaration line itself
                if (line.StartsWith("extension EveryProtocol"))
                    continue;
                // If any other content exists, it's not a composition conformance
                return false;
            }
            return true;
        }

        /// <summary>
        /// Splits content into lines, preserving line endings.
        /// </summary>
        private static List<string> SplitLines(string content)
        {
            var lines = new List<string>();
            int start = 0;
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == '\n')
                {
                    lines.Add(content.Substring(start, i - start + 1));
                    start = i + 1;
                }
                else if (content[i] == '\r')
                {
                    if (i + 1 < content.Length && content[i + 1] == '\n')
                    {
                        lines.Add(content.Substring(start, i - start + 2));
                        start = i + 2;
                        i++;
                    }
                    else
                    {
                        lines.Add(content.Substring(start, i - start + 1));
                        start = i + 1;
                    }
                }
            }
            if (start < content.Length)
                lines.Add(content.Substring(start));
            return lines;
        }
    }
}
