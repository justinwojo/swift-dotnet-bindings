// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BindingsGeneration
{
    /// <summary>
    /// Pure repackaging of a module's combined C# output into one file per top-level type.
    /// Given the pre-qualify output plus the boundary offsets and top-level-type spans that
    /// <see cref="StringEmitter"/> recorded during emission, produces the file set:
    /// <list type="bullet">
    /// <item>the prelude <c>{namespace}.cs</c> — the combined output with every type span cut
    /// out (shared header, free functions, module-level trailers, namespace close, and the
    /// <c>SwiftInterop</c> companion);</item>
    /// <item>one <c>{namespace}.Types.{Leaf}.cs</c> per top-level type — the shared header,
    /// that type's own byte-range, and the namespace-closing brace.</item>
    /// </list>
    /// The distinct content across all files is exactly the combined output, so the split is a
    /// pure repackaging with zero public-API change. Kept side-effect-free (no I/O) so it can
    /// be unit-tested directly for reassembly identity, per-type isolation, determinism, and
    /// case-insensitive filename disambiguation.
    /// </summary>
    internal static class ModuleFileSplitter
    {
        /// <summary>A single output file: its leaf name (no directory) and full text.</summary>
        public readonly record struct SplitFile(string FileName, string Content);

        /// <summary>
        /// Builds the file set. Returns <c>null</c> when the recorded spans are not sliceable
        /// (e.g. a module with only free functions, or missing/inconsistent offsets) — the
        /// caller then writes the single combined file, identical to the pre-split behavior.
        /// </summary>
        public static IReadOnlyList<SplitFile>? BuildFileSet(
            string preQualifyOutput,
            string @namespace,
            int? namespaceBodyStart,
            int? namespaceBodyEnd,
            int? namespaceCloseEnd,
            IReadOnlyList<(string TypeName, int Start, int End)> spans,
            Func<string, string> qualify)
        {
            if (namespaceBodyStart is not int bodyStart
                || namespaceBodyEnd is not int bodyEnd
                || namespaceCloseEnd is not int closeEnd
                || spans.Count == 0
                || !AreSpansSliceable(spans, bodyStart, bodyEnd, closeEnd, preQualifyOutput.Length))
            {
                return null;
            }

            var ordered = spans.OrderBy(s => s.Start).ToList();
            var header = preQualifyOutput.Substring(0, bodyStart);
            var namespaceClose = preQualifyOutput.Substring(bodyEnd, closeEnd - bodyEnd);

            var files = new List<SplitFile>(ordered.Count + 1);

            // Prelude = the combined output with every type span cut out.
            var prelude = new StringBuilder(preQualifyOutput.Length);
            var cursor = 0;
            foreach (var s in ordered)
            {
                prelude.Append(preQualifyOutput, cursor, s.Start - cursor);
                cursor = s.End;
            }
            prelude.Append(preQualifyOutput, cursor, preQualifyOutput.Length - cursor);
            files.Add(new SplitFile($"{@namespace}.cs", qualify(prelude.ToString())));

            // One file per top-level type; disambiguate case-insensitively (macOS/APFS) in
            // deterministic emission order.
            var takenLeaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in ordered)
            {
                var baseLeaf = SplitFileNaming.SanitizeLeaf(s.TypeName);
                var leaf = baseLeaf;
                var n = 1;
                while (!takenLeaves.Add(leaf))
                {
                    n++;
                    leaf = $"{baseLeaf}_{n}";
                }

                var body = header
                    + preQualifyOutput.Substring(s.Start, s.End - s.Start)
                    + namespaceClose;
                files.Add(new SplitFile(SplitFileNaming.TypeFileName(@namespace, leaf), qualify(body)));
            }

            return files;
        }

        /// <summary>
        /// Validates that the recorded spans are sliceable: each lies within the namespace
        /// body, they are ordered and non-overlapping, and the boundary offsets are in range.
        /// </summary>
        private static bool AreSpansSliceable(
            IReadOnlyList<(string TypeName, int Start, int End)> spans,
            int bodyStart, int bodyEnd, int closeEnd, int length)
        {
            if (bodyStart < 0 || bodyEnd < bodyStart || closeEnd < bodyEnd || closeEnd > length)
                return false;

            var prevEnd = bodyStart;
            foreach (var s in spans.OrderBy(s => s.Start))
            {
                if (s.Start < prevEnd || s.End < s.Start || s.End > bodyEnd)
                    return false;
                prevEnd = s.End;
            }
            return true;
        }
    }
}
