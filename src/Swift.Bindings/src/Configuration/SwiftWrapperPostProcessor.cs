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
    }

    /// <summary>
    /// Strips known-broken patterns from generated Swift wrapper code.
    /// C# port of the Python post-processing in build-async-wrapper.sh.
    /// </summary>
    public static class SwiftWrapperPostProcessor
    {
        private static readonly Regex ClosureParamPattern = new(
            @",\s*\w+:\s*\([^)]*\)\s*->", RegexOptions.Compiled);

        /// <summary>
        /// Post-processes Swift source content, stripping known-broken wrapper patterns.
        /// </summary>
        public static PostProcessingResult Process(string sourceContent)
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

                // Pattern 2: @_silgen_name + function blocks with broken patterns
                if (stripped.StartsWith("@_silgen_name(", StringComparison.Ordinal))
                {
                    int end = FindBlockEnd(lines, i);
                    var body = ScanBlockBody(lines, i, end);

                    if (IsSilgenNameBroken(lines, i, end, body))
                    {
                        removedCount++;
                        i = end + 1;
                        continue;
                    }
                }

                // Pattern 3: Extension blocks with broken code (not EveryProtocol — already handled)
                if (stripped.StartsWith("extension ", StringComparison.Ordinal) &&
                    !stripped.StartsWith("extension EveryProtocol", StringComparison.Ordinal))
                {
                    int end = FindBlockEnd(lines, i);
                    var body = ScanBlockBody(lines, i, end);

                    if (IsExtensionBroken(lines, i, end, body))
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

                    if (IsStandaloneFuncBroken(body))
                    {
                        removedCount++;
                        i = end + 1;
                        continue;
                    }
                }

                outputLines.Add(lines[i]);
                i++;
            }

            return new PostProcessingResult
            {
                CleanedContent = string.Join("", outputLines),
                StrippedBlockCount = removedCount
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
        /// </summary>
        private static bool IsSilgenNameBroken(IReadOnlyList<string> lines, int start, int end, string body)
        {
            // (a) EveryProtocol() — protocol witness dispatch for unimplemented conformances
            if (body.Contains("EveryProtocol()"))
                return true;

            // (b) self.functionName() in free function (no _self: parameter)
            if (!body.Contains("_self:") && !body.Contains("_self :"))
            {
                for (int j = start; j <= end && j < lines.Count; j++)
                {
                    var s = lines[j].TrimStart();
                    if (s.StartsWith("self.", StringComparison.Ordinal) ||
                        s.Contains(" self.") || s.Contains("\tself."))
                        return true;
                }
            }

            // (c) __self.init( — async init wrapper (invalid Swift)
            if (body.Contains("__self.init("))
                return true;

            // (d) Mutating member on let existential
            if (body.Contains(".load(as: (any ") &&
                body.Contains("existential.") &&
                body.Contains("let existential"))
                return true;

            // (e) Non-escaping closure param passed to Task
            if (body.Contains("Task {"))
            {
                int sigEnd = body.IndexOf('{');
                if (sigEnd > 0)
                {
                    var sig = body.Substring(0, sigEnd);
                    if (ClosureParamPattern.IsMatch(sig))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if an extension block contains known-broken patterns.
        /// </summary>
        private static bool IsExtensionBroken(IReadOnlyList<string> lines, int start, int end, string body)
        {
            if (body.Contains("EveryProtocol()"))
                return true;

            if (body.Contains("__self.init("))
                return true;

            // Non-escaping closure in Task
            if (body.Contains("Task {"))
            {
                int taskIdx = body.IndexOf("Task {");
                // Search for closure params in the body before the Task block
                var bodyBeforeTask = body.Substring(0, taskIdx);
                if (ClosureParamPattern.IsMatch(bodyBeforeTask))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if a standalone public func block contains known-broken patterns.
        /// </summary>
        private static bool IsStandaloneFuncBroken(string body)
        {
            if (body.Contains("EveryProtocol()"))
                return true;

            if (body.Contains("let existential") &&
                body.Contains("existential.") &&
                body.Contains(".load(as: (any "))
                return true;

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
