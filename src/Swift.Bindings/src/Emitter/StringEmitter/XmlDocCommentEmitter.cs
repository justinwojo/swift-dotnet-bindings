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
        /// Emits XML doc comments for a method, constructor, or failable factory: summary, param
        /// tags, returns, and remarks.
        ///
        /// <para>The param tags follow the declaration the caller writes, never the Swift
        /// signature. The two differ often: a bridge drops defaulted or closure parameters, a
        /// projection renames one, an async member appends a token and a failable init an
        /// <c>out</c> result. A tag naming a parameter the declaration lacks is CS1572, and once one
        /// parameter is tagged every other untagged one is CS1573, both of them errors for a
        /// consumer building with a documentation file under warnings-as-errors. So a Swift
        /// description is written only for a parameter the declaration has, and as soon as one is,
        /// every other parameter is tagged too, with an empty description when Swift gave none.
        /// With no description to write, no parameter is tagged.</para>
        /// </summary>
        /// <param name="csWriter">The C# writer.</param>
        /// <param name="methodDecl">The method declaration.</param>
        /// <param name="emittedParameterNames">
        /// The C# names of the parameters the declaration writes for the Swift ones, in declaration
        /// order. Leave out the synthetic trailing parameters named by
        /// <paramref name="cancellationTokenName"/> and <paramref name="failableResultName"/>.
        /// </param>
        /// <param name="isConstructor">True for constructors and factories built from an
        /// initializer (suppresses the returns tag).</param>
        /// <param name="cancellationTokenName">
        /// The C# name of the trailing <c>CancellationToken</c> an async projection appends, or
        /// <see langword="null"/> when the emitted signature has none.
        /// </param>
        /// <param name="failableResultName">
        /// The C# name of the trailing <c>out</c> result a failable initializer's <c>TryCreate</c>
        /// factory appends, or <see langword="null"/> for every other member. The Swift
        /// <c>Returns:</c> text describes it.
        /// </param>
        public static void EmitMethodDocComment(
            CSharpWriter csWriter,
            MethodDecl methodDecl,
            IReadOnlyList<string> emittedParameterNames,
            bool isConstructor = false,
            string? cancellationTokenName = null,
            string? failableResultName = null)
        {
            if (methodDecl.Documentation == null || methodDecl.Documentation.IsEmpty)
                return;

            var doc = methodDecl.Documentation;
            bool hasReturns = !string.IsNullOrWhiteSpace(doc.Returns);

            EmitSummary(csWriter, doc.Summary);

            var described = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (csName, text) in MapParameterDocs(methodDecl, doc))
                described.TryAdd(NameProvider.StripVerbatimPrefix(csName), text);

            var tags = new List<(string Name, string Text)>();
            bool anyDescribed = false;
            foreach (var name in emittedParameterNames)
            {
                if (described.TryGetValue(NameProvider.StripVerbatimPrefix(name), out var text))
                {
                    anyDescribed = true;
                    tags.Add((name, text));
                }
                else
                {
                    tags.Add((name, string.Empty));
                }
            }
            if (cancellationTokenName != null)
                tags.Add((cancellationTokenName, CancellationTokenParamText));
            if (failableResultName != null)
            {
                anyDescribed |= hasReturns;
                tags.Add((failableResultName, hasReturns ? FormatDocText(doc.Returns!) : FailableResultParamText));
            }

            if (anyDescribed)
            {
                foreach (var (name, text) in tags)
                    csWriter.WriteLine(ParamTag(name, text));
            }

            // A constructor returns nothing to describe, and a failable factory's result is the
            // out parameter tagged above.
            if (hasReturns && !isConstructor && failableResultName == null)
                csWriter.WriteLine($"/// <returns>{FormatDocText(doc.Returns!)}</returns>");

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

        /// <summary>The <c>&lt;param&gt;</c> text for an async projection's trailing token.</summary>
        internal const string CancellationTokenParamText = "Requests cancellation of the asynchronous Swift call.";

        /// <summary>
        /// The <c>&lt;param&gt;</c> text for a failable factory's <c>out</c> result when the Swift
        /// comment has no <c>Returns:</c> to describe it.
        /// </summary>
        internal const string FailableResultParamText = "When this method returns true, the initialized instance.";

        /// <summary>
        /// A <c>&lt;param&gt;</c> line for a C# parameter. The tag names the parameter without a
        /// verbatim-identifier <c>@</c> (a Swift <c>base:</c> label becomes the C# <c>@base</c>), as
        /// C# does: <c>name="@base"</c> names no parameter (CS1572). <paramref name="xmlText"/> is
        /// already doc XML.
        /// </summary>
        internal static string ParamTag(string csParamName, string xmlText)
            => $"/// <param name=\"{XmlEscape(NameProvider.StripVerbatimPrefix(csParamName))}\">{xmlText}</param>";

        /// <summary>
        /// The method's Swift parameter documentation keyed by C# parameter name, as doc XML, in
        /// doc-comment order. Parameters the Swift comment does not describe are absent.
        /// </summary>
        internal static IReadOnlyList<(string CsName, string XmlText)> MapParameterDocs(MethodDecl methodDecl)
            => methodDecl.Documentation is { IsEmpty: false } doc
                ? MapParameterDocs(methodDecl, doc)
                : Array.Empty<(string, string)>();

        private static List<(string CsName, string XmlText)> MapParameterDocs(MethodDecl methodDecl, DocComment doc)
        {
            var mapped = new List<(string CsName, string XmlText)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (doc.Parameters.Count == 0)
                return mapped;

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
                if (csParamName != null && seen.Add(csParamName))
                    mapped.Add((csParamName, FormatDocText(description)));
            }

            return mapped;
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
