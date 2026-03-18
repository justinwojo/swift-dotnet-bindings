// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace BindingsGeneration
{
    /// <summary>
    /// Result of post-processing a Swift wrapper file.
    /// </summary>
    public sealed class PostProcessingResult
    {
        public required string CleanedContent { get; init; }
        public required int StrippedBlockCount { get; init; }
        public int ModuleNameCollisionReplacements { get; init; }
    }

    /// <summary>
    /// Strips known-broken patterns from generated Swift wrapper code.
    /// C# port of the Python post-processing in build-async-wrapper.sh.
    /// </summary>
    public static class SwiftWrapperPostProcessor
    {
        // NOTE: Safety-net patterns (b)-(f) were removed in Phase 1 of the architecture refactoring.
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
            return Process(sourceContent, internalTypeNames: null, onSafetyNetWarning: null, moduleNameForCollision: null);
        }

        /// <summary>
        /// Post-processes Swift source content, stripping known-broken wrapper patterns
        /// and functions that reference internal (non-public) types.
        /// </summary>
        /// <param name="sourceContent">Swift source code to process.</param>
        /// <param name="internalTypeNames">
        /// Set of internal type names to strip. Contains both short names ("SkeletonLayer")
        /// and qualified names ("SkeletonView.SkeletonLayer"). Null to skip internal type stripping.
        /// </param>
        /// <param name="onSafetyNetWarning">
        /// Optional callback invoked when a safety-net pattern fires. These patterns should no longer
        /// match in normal operation (they are now prevented at emission time), so a warning indicates
        /// a regression in the emitter.
        /// </param>
        /// <param name="moduleNameForCollision">
        /// When non-null, indicates that the module has a public type with the same name as the module
        /// (e.g., module "Reachability" containing class "Reachability"). All module-qualified type
        /// references are rewritten to strip the module prefix, since the wrapper file already imports
        /// the module and Swift resolves the bare module name as the type, not the module.
        /// </param>
        /// <param name="nestedTypesInCollidingClass">
        /// Types nested inside the colliding class (e.g., {"Level"} for SwiftyBeaver.Level).
        /// When collision regex would strip "SwiftyBeaver.Level" → "Level", but "Level" is
        /// nested in class "SwiftyBeaver", the reference must stay qualified. Null to skip.
        /// </param>
        public static PostProcessingResult Process(string sourceContent, HashSet<string>? internalTypeNames, Action<string>? onSafetyNetWarning = null, string? moduleNameForCollision = null, HashSet<string>? nestedTypesInCollidingClass = null)
        {
            if (string.IsNullOrEmpty(sourceContent))
                return new PostProcessingResult { CleanedContent = sourceContent, StrippedBlockCount = 0 };

            var lines = SplitLines(sourceContent);
            var outputLines = new List<string>();
            int removedCount = 0;
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
                        // strip if the body references an internal type
                        var body = ScanBlockBody(lines, i, end);
                        if (ReferencesInternalType(body, internalTypeNames))
                        {
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

                    if (IsSilgenNameBroken(lines, i, end, body, onSafetyNetWarning) ||
                        ReferencesInternalType(body, internalTypeNames))
                    {
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

                        if (IsSilgenNameBroken(lines, i + 1, end, body, onSafetyNetWarning) ||
                            ReferencesInternalType(body, internalTypeNames))
                        {
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

                    if (IsExtensionBroken(lines, i, end, body, onSafetyNetWarning) ||
                        ReferencesInternalType(body, internalTypeNames))
                    {
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

                    if (IsStandaloneFuncBroken(body, i, onSafetyNetWarning) ||
                        ReferencesInternalType(body, internalTypeNames))
                    {
                        removedCount++;
                        i = end + 1;
                        continue;
                    }
                }

                outputLines.Add(lines[i]);
                i++;
            }

            // Module/type name collision fix: strip module prefix from type references.
            // When a module has a public type with the same name (e.g., module "Reachability"
            // containing class "Reachability"), Swift resolves bare "Reachability" as the type,
            // not the module. "Reachability.X" fails because it looks for "X" nested in the class.
            // Fix: strip the module prefix. The wrapper already imports the module, so unqualified
            // names resolve correctly. The regex captures the rest of the type path (including
            // nested components) so "Reachability.Reachability.Nested" → "Reachability.Nested".
            int collisionReplacements = 0;
            if (!string.IsNullOrEmpty(moduleNameForCollision))
            {
                var collisionPattern = new Regex(
                    @"\b" + Regex.Escape(moduleNameForCollision) + @"\.(\w+(?:\.\w+)*)",
                    RegexOptions.Compiled);

                for (int j = 0; j < outputLines.Count; j++)
                {
                    // Don't modify import lines
                    if (outputLines[j].TrimStart().StartsWith("import ", StringComparison.Ordinal))
                        continue;

                    var replaced = collisionPattern.Replace(outputLines[j], match =>
                    {
                        // The first captured group is the type name after the module prefix.
                        // If it's a nested type of the colliding class, preserve the qualification.
                        // E.g., SwiftyBeaver.Level → keep as SwiftyBeaver.Level (Level is nested in class SwiftyBeaver)
                        var firstComponent = match.Groups[1].Value;
                        var dotIdx = firstComponent.IndexOf('.');
                        var topLevelName = dotIdx >= 0 ? firstComponent.Substring(0, dotIdx) : firstComponent;

                        if (nestedTypesInCollidingClass != null &&
                            nestedTypesInCollidingClass.Contains(topLevelName))
                            return match.Value; // Keep the full qualified name

                        return match.Groups[1].Value;
                    });
                    if (replaced != outputLines[j])
                    {
                        collisionReplacements++;
                        outputLines[j] = replaced;
                    }
                }
            }

            return new PostProcessingResult
            {
                CleanedContent = string.Join("", outputLines),
                StrippedBlockCount = removedCount,
                ModuleNameCollisionReplacements = collisionReplacements
            };
        }

        /// <summary>
        /// Finds the end of a brace-delimited block starting at the given index.
        /// </summary>
        internal static int FindBlockEnd(IReadOnlyList<string> lines, int start)
        {
            int depth = 0;
            for (int j = start; j < lines.Count; j++)
            {
                foreach (char c in lines[j])
                {
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                }
                if (depth <= 0 && j > start)
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
            // See architecture-refactoring-plan.md Session 2 log.

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
        /// Checks if a block body references any internal (non-public) type names.
        /// Uses word-boundary matching to avoid false positives (e.g., "Layer" won't match "Player").
        /// </summary>
        private static bool ReferencesInternalType(string body, HashSet<string>? internalTypeNames)
        {
            if (internalTypeNames == null || internalTypeNames.Count == 0)
                return false;

            foreach (var typeName in internalTypeNames)
            {
                // Use word-boundary regex to avoid false positives
                var pattern = @"\b" + Regex.Escape(typeName) + @"\b";
                if (Regex.IsMatch(body, pattern))
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
