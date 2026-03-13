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
        private static readonly Regex ClosureParamPattern = new(
            @",\s*\w+:\s*\([^)]*\)\s*->", RegexOptions.Compiled);

        // Matches raw Swift generic type parameters: τ_0_0, τ_1_0, τ_0_1, etc.
        // These are ABI-level names that should never appear in emitted Swift source.
        private static readonly Regex RawGenericParamPattern = new(
            @"τ_\d+_\d+", RegexOptions.Compiled);

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
        public static PostProcessingResult Process(string sourceContent, HashSet<string>? internalTypeNames, Action<string>? onSafetyNetWarning = null, string? moduleNameForCollision = null)
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

                // Pattern 1: EveryProtocol conformance extensions and class definition
                if (stripped.StartsWith("extension EveryProtocol", StringComparison.Ordinal) ||
                    stripped.StartsWith("class EveryProtocol", StringComparison.Ordinal))
                {
                    int end = FindBlockEnd(lines, i);
                    removedCount++;
                    i = end + 1;
                    continue;
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

                    var replaced = collisionPattern.Replace(outputLines[j], "$1");
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
            // (a) EveryProtocol() — protocol witness dispatch for unimplemented conformances
            // This is unconditional by design (not a safety net).
            if (body.Contains("EveryProtocol()"))
                return true;

            // (b) self.functionName() in free function (no _self: parameter)
            // Safety net: now prevented by async wrapper fix at emission time.
            // Skip for indented @_silgen_name blocks — these are inside extension { }
            // scopes where `self` is legitimate (e.g., default-parameter-overload wrappers).
            // Top-level functions in generated code always start at column 0; indented = nested.
            var rawStartLine = start < lines.Count ? lines[start] : "";
            bool isInsideExtension = rawStartLine.Length > 0 && char.IsWhiteSpace(rawStartLine[0]);
            if (!isInsideExtension && !body.Contains("_self:") && !body.Contains("_self :"))
            {
                for (int j = start; j <= end && j < lines.Count; j++)
                {
                    var s = lines[j].TrimStart();
                    if (s.StartsWith("self.", StringComparison.Ordinal) ||
                        s.Contains(" self.") || s.Contains("\tself."))
                    {
                        onSafetyNetWarning?.Invoke($"Post-processor safety net: stripped self-without-_self at line {j}");
                        return true;
                    }
                }
            }

            // (c) __self.init( — async init wrapper (invalid Swift)
            // Safety net: now prevented at emission time.
            if (body.Contains("__self.init("))
            {
                onSafetyNetWarning?.Invoke($"Post-processor safety net: stripped __self.init at line {start}");
                return true;
            }

            // Pattern (d) REMOVED: Mutating member on let existential.
            // All existentials now use `var`, so this pattern can never match.

            // (e) Non-escaping closure param passed to Task
            // Safety net: now prevented at emission time.
            if (body.Contains("Task {"))
            {
                int sigEnd = body.IndexOf('{');
                if (sigEnd > 0)
                {
                    var sig = body.Substring(0, sigEnd);
                    if (ClosureParamPattern.IsMatch(sig))
                    {
                        onSafetyNetWarning?.Invoke($"Post-processor safety net: stripped non-escaping-closure-in-Task at line {start}");
                        return true;
                    }
                }
            }

            // (f) Raw generic type parameters (τ_0_0, τ_1_0, etc.) — never valid in emitted Swift
            // Safety net: now gated at emission time in WrapperValidation.
            if (ContainsRawGenericParam(body))
            {
                onSafetyNetWarning?.Invoke($"Post-processor safety net: stripped raw-generic-param at line {start}");
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
            // (a) EveryProtocol() — unconditional by design
            if (body.Contains("EveryProtocol()"))
                return true;

            // (c) __self.init( — safety net
            if (body.Contains("__self.init("))
            {
                onSafetyNetWarning?.Invoke($"Post-processor safety net: stripped __self.init at line {start}");
                return true;
            }

            // (f) Raw generic type parameters (τ_0_0, τ_1_0, etc.) — safety net
            if (ContainsRawGenericParam(body))
            {
                onSafetyNetWarning?.Invoke($"Post-processor safety net: stripped raw-generic-param at line {start}");
                return true;
            }

            // (e) Non-escaping closure in Task — safety net
            if (body.Contains("Task {"))
            {
                int taskIdx = body.IndexOf("Task {");
                // Search for closure params in the body before the Task block
                var bodyBeforeTask = body.Substring(0, taskIdx);
                if (ClosureParamPattern.IsMatch(bodyBeforeTask))
                {
                    onSafetyNetWarning?.Invoke($"Post-processor safety net: stripped non-escaping-closure-in-Task at line {start}");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if a standalone public func block contains known-broken patterns.
        /// Safety-net patterns fire a warning callback when they match.
        /// </summary>
        private static bool IsStandaloneFuncBroken(string body, int start, Action<string>? onSafetyNetWarning)
        {
            // (a) EveryProtocol() — unconditional by design
            if (body.Contains("EveryProtocol()"))
                return true;

            // Pattern (d) REMOVED: let existential mutating pattern.
            // All existentials now use `var`, so this pattern can never match.

            // (f) Raw generic type parameters (τ_0_0, τ_1_0, etc.) — safety net
            if (ContainsRawGenericParam(body))
            {
                onSafetyNetWarning?.Invoke($"Post-processor safety net: stripped raw-generic-param at line {start}");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if a block body contains raw ABI generic type parameters (e.g., τ_0_0).
        /// These appear when the wrapper emitter fails to resolve generic types and emits
        /// the raw Swift ABI parameter names, which are not valid Swift identifiers.
        /// </summary>
        private static bool ContainsRawGenericParam(string body) => RawGenericParamPattern.IsMatch(body);

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
