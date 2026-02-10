// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;

namespace BindingsGeneration
{
    /// <summary>
    /// Emits C# XML doc comments from structured DocComment data parsed from Swift symbol graphs.
    /// </summary>
    internal static class XmlDocCommentEmitter
    {
        /// <summary>
        /// Emits XML doc comments for a type, property, or enum case declaration.
        /// Only emits summary and remarks (no parameters or returns).
        /// </summary>
        public static void EmitDocComment(CSharpWriter csWriter, BaseDecl decl)
        {
            if (decl.Documentation == null || decl.Documentation.IsEmpty)
                return;

            var doc = decl.Documentation;

            EmitSummary(csWriter, doc.Summary);
            EmitRemarks(csWriter, doc);
        }

        /// <summary>
        /// Emits XML doc comments for a method, constructor, or failable factory.
        /// Includes summary, param tags, returns, and remarks.
        /// </summary>
        /// <param name="csWriter">The C# writer.</param>
        /// <param name="methodDecl">The method declaration.</param>
        /// <param name="isConstructor">True for constructors (suppresses returns tag).</param>
        /// <param name="isFailableFactory">True for failable init? → TryCreate (maps Returns to param "result").</param>
        public static void EmitMethodDocComment(CSharpWriter csWriter, MethodDecl methodDecl, bool isConstructor = false, bool isFailableFactory = false)
        {
            if (methodDecl.Documentation == null || methodDecl.Documentation.IsEmpty)
                return;

            var doc = methodDecl.Documentation;

            EmitSummary(csWriter, doc.Summary);
            var emittedParamNames = EmitParameterTags(csWriter, methodDecl, doc);

            // Returns handling depends on context
            if (!string.IsNullOrWhiteSpace(doc.Returns))
            {
                if (isFailableFactory && !emittedParamNames.Contains("result"))
                {
                    // Failable factory: Swift "Returns:" maps to <param name="result"> on TryCreate
                    // Skip if a mapped parameter already emitted <param name="result">
                    csWriter.WriteLine($"/// <param name=\"result\">{FormatDocText(doc.Returns)}</param>");
                }
                else if (!isConstructor && !isFailableFactory)
                {
                    // Regular methods get a <returns> tag
                    csWriter.WriteLine($"/// <returns>{FormatDocText(doc.Returns)}</returns>");
                }
                // Constructors: suppress <returns> entirely
            }

            EmitRemarks(csWriter, doc);
        }

        /// <summary>
        /// Emits the summary tag.
        /// </summary>
        private static void EmitSummary(CSharpWriter csWriter, string summary)
        {
            if (string.IsNullOrWhiteSpace(summary))
                return;

            csWriter.WriteLine($"/// <summary>");
            csWriter.WriteLine($"/// {FormatDocText(summary)}");
            csWriter.WriteLine($"/// </summary>");
        }

        /// <summary>
        /// Emits param tags by mapping Swift public labels to C# parameter names.
        /// Returns the set of emitted C# parameter names (for duplicate detection).
        /// </summary>
        private static HashSet<string> EmitParameterTags(CSharpWriter csWriter, MethodDecl methodDecl, DocComment doc)
        {
            var emittedNames = new HashSet<string>(StringComparer.Ordinal);
            if (doc.Parameters.Count == 0)
                return emittedNames;

            // Build Swift label → C# parameter name mapping from CSSignature
            // CSSignature[0] is the return type, actual parameters start at index 1
            var labelToCSName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var privateNameToCSName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 1; i < methodDecl.CSSignature.Count; i++)
            {
                var arg = methodDecl.CSSignature[i];
                var csName = NameProvider.GetCSharpParameterName(arg);

                // Primary mapping: Swift public label (Name) → C# name
                if (!string.IsNullOrEmpty(arg.Name) && arg.Name != "_")
                {
                    labelToCSName.TryAdd(arg.Name, csName);
                }

                // Fallback mapping: Swift private name → C# name (for unlabeled args)
                if (!string.IsNullOrEmpty(arg.PrivateName))
                {
                    privateNameToCSName.TryAdd(arg.PrivateName, csName);
                }
            }

            // Emit param tags in doc comment order
            foreach (var (swiftLabel, description) in doc.Parameters)
            {
                string? csParamName = null;

                // Try primary: Swift public label
                if (!labelToCSName.TryGetValue(swiftLabel, out csParamName))
                {
                    // Fallback: try private name
                    privateNameToCSName.TryGetValue(swiftLabel, out csParamName);
                }

                // Only emit if we found a matching C# parameter name
                if (csParamName != null)
                {
                    csWriter.WriteLine($"/// <param name=\"{XmlEscape(csParamName)}\">{FormatDocText(description)}</param>");
                    emittedNames.Add(csParamName);
                }
            }

            return emittedNames;
        }

        /// <summary>
        /// Emits remarks tag combining Throws and remark directives.
        /// Uses para tags when there are multiple items for better visual structure.
        /// </summary>
        private static void EmitRemarks(CSharpWriter csWriter, DocComment doc)
        {
            var hasThrows = !string.IsNullOrWhiteSpace(doc.Throws);
            var hasRemarks = doc.Remarks.Count > 0;

            if (!hasThrows && !hasRemarks)
                return;

            int totalItems = (hasThrows ? 1 : 0) + doc.Remarks.Count;
            bool usePara = totalItems > 1;

            csWriter.WriteLine("/// <remarks>");
            if (hasThrows)
            {
                var text = $"Throws: {FormatDocText(doc.Throws!)}";
                csWriter.WriteLine(usePara ? $"/// <para>{text}</para>" : $"/// {text}");
            }
            foreach (var remark in doc.Remarks)
            {
                var text = FormatDocText(remark);
                csWriter.WriteLine(usePara ? $"/// <para>{text}</para>" : $"/// {text}");
            }
            csWriter.WriteLine("/// </remarks>");
        }

        /// <summary>
        /// Formats doc comment text for XML output: escapes XML characters and converts
        /// backtick code spans to &lt;c&gt;code&lt;/c&gt; tags.
        /// </summary>
        internal static string FormatDocText(string text)
        {
            // Strategy: split on backtick code spans, XML-escape non-code parts,
            // and wrap code parts in <c> tags (with their own XML escaping).
            var result = new StringBuilder();
            int pos = 0;

            foreach (Match match in Regex.Matches(text, "`([^`]+)`"))
            {
                // Append XML-escaped text before this code span
                if (match.Index > pos)
                {
                    result.Append(XmlEscape(text.Substring(pos, match.Index - pos)));
                }

                // Append <c>escaped_code</c>
                result.Append("<c>");
                result.Append(XmlEscape(match.Groups[1].Value));
                result.Append("</c>");

                pos = match.Index + match.Length;
            }

            // Append remaining text after last code span
            if (pos < text.Length)
            {
                result.Append(XmlEscape(text.Substring(pos)));
            }

            return result.ToString();
        }

        /// <summary>
        /// Escapes special XML characters in doc comment text.
        /// </summary>
        internal static string XmlEscape(string text)
        {
            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }
    }
}
