// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[assembly: InternalsVisibleTo("Swift.Bindings.Unit.Tests")]

namespace BindingsGeneration
{
    /// <summary>
    /// Parses Swift symbol graph JSON files to extract doc comments keyed by USR.
    /// Uses streaming JSON reader with selective JObject.Load for memory efficiency.
    /// </summary>
    public static class SymbolGraphDocParser
    {
        private enum DirectiveTarget { None, Parameter, Returns, Throws, Remark, PluralParameters }
        /// <summary>
        /// Parses symbol graph file(s) and returns doc comments keyed by USR.
        /// Accepts a file path or directory path. If directory, merges all *.symbols.json files.
        /// Duplicate USR merge policy: first non-empty DocComment wins (files processed in sorted order).
        /// </summary>
        public static Dictionary<string, DocComment> ParseSymbolGraphs(string path)
        {
            var result = new Dictionary<string, DocComment>();

            if (File.Exists(path))
            {
                ParseSingleFile(path, result);
            }
            else if (Directory.Exists(path))
            {
                var files = Directory.GetFiles(path, "*.symbols.json", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.Ordinal);
                foreach (var file in files)
                {
                    ParseSingleFile(file, result);
                }
            }

            return result;
        }

        /// <summary>
        /// Parses a single symbol graph JSON file, extracting doc comments into the result dictionary.
        /// </summary>
        private static void ParseSingleFile(string filePath, Dictionary<string, DocComment> result)
        {
            using var reader = new StreamReader(filePath);
            using var jsonReader = new JsonTextReader(reader);

            // Navigate to the "symbols" array
            while (jsonReader.Read())
            {
                if (jsonReader.TokenType == JsonToken.PropertyName && (string?)jsonReader.Value == "symbols")
                {
                    if (!jsonReader.Read() || jsonReader.TokenType != JsonToken.StartArray)
                        return;

                    // Process each symbol in the array
                    while (jsonReader.Read())
                    {
                        if (jsonReader.TokenType == JsonToken.EndArray)
                            break;

                        if (jsonReader.TokenType == JsonToken.StartObject)
                        {
                            var symbol = JObject.Load(jsonReader);
                            ProcessSymbol(symbol, result);
                        }
                    }
                    return;
                }
            }
        }

        /// <summary>
        /// Extracts USR and doc comment from a single symbol JObject.
        /// </summary>
        private static void ProcessSymbol(JObject symbol, Dictionary<string, DocComment> result)
        {
            var usr = symbol.SelectToken("identifier.precise")?.Value<string>();
            if (string.IsNullOrEmpty(usr))
                return;

            // Skip if we already have a non-empty doc comment for this USR
            if (result.TryGetValue(usr, out var existing) && !existing.IsEmpty)
                return;

            var docCommentToken = symbol.SelectToken("docComment");
            if (docCommentToken == null)
                return;

            var linesToken = docCommentToken.SelectToken("lines");
            if (linesToken == null)
                return;

            var lines = new List<string>();
            foreach (var lineToken in linesToken)
            {
                var text = lineToken.Value<string>("text");
                if (text != null)
                    lines.Add(text);
            }

            if (lines.Count == 0)
                return;

            var docComment = ParseDocCommentLines(lines);
            if (!docComment.IsEmpty)
            {
                result[usr] = docComment;
            }
        }

        /// <summary>
        /// Converts Swift doc comment lines to a structured DocComment.
        /// Parses summary, - Parameter, - Parameters:, - Returns:, - Throws:,
        /// and remark directives (Note, Important, Warning, Remark, Precondition, Postcondition, Complexity).
        /// </summary>
        internal static DocComment ParseDocCommentLines(List<string> lines)
        {
            if (lines.Count == 0)
                return new DocComment();

            var summaryLines = new List<string>();
            var parameters = new Dictionary<string, string>();
            string? returns = null;
            string? throws = null;
            var remarks = new List<string>();

            // Track which directive we're currently appending multi-line content to
            var currentTarget = DirectiveTarget.None;
            string? currentParamName = null;
            bool pastSummary = false;
            bool inPluralParameterBlock = false;

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                // Before the first blank line or directive, everything is summary
                if (!pastSummary)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        pastSummary = true;
                        currentTarget = DirectiveTarget.None;
                        continue;
                    }

                    // Check if this line is a directive (starts with "- SomeDirective:")
                    if (IsDirective(trimmed))
                    {
                        pastSummary = true;
                        // Fall through to directive parsing below
                    }
                    else
                    {
                        summaryLines.Add(line.TrimEnd());
                        continue;
                    }
                }

                // Skip blank lines between directives
                if (string.IsNullOrWhiteSpace(line))
                {
                    currentTarget = DirectiveTarget.None;
                    inPluralParameterBlock = false;
                    continue;
                }

                // Check for singular parameter: "- Parameter name: description"
                if (TrySingularParameter(trimmed, out var paramName, out var paramDesc))
                {
                    parameters[paramName] = paramDesc;
                    currentTarget = DirectiveTarget.Parameter;
                    currentParamName = paramName;
                    inPluralParameterBlock = false;
                    continue;
                }

                // Check for plural parameters header: "- Parameters:"
                if (IsPluralParametersHeader(trimmed))
                {
                    inPluralParameterBlock = true;
                    currentTarget = DirectiveTarget.PluralParameters;
                    continue;
                }

                // Inside plural parameter block: "  - name: description"
                if (inPluralParameterBlock && TryPluralParameterEntry(trimmed, out var pluralName, out var pluralDesc))
                {
                    parameters[pluralName] = pluralDesc;
                    currentTarget = DirectiveTarget.Parameter;
                    currentParamName = pluralName;
                    continue;
                }

                // Check for Returns directive
                if (TryDirective(trimmed, "Returns", out var returnsDesc))
                {
                    returns = returnsDesc;
                    currentTarget = DirectiveTarget.Returns;
                    inPluralParameterBlock = false;
                    continue;
                }

                // Check for Throws directive
                if (TryDirective(trimmed, "Throws", out var throwsDesc))
                {
                    throws = throwsDesc;
                    currentTarget = DirectiveTarget.Throws;
                    inPluralParameterBlock = false;
                    continue;
                }

                // Check for remark directives
                if (TryRemarkDirective(trimmed, out var remarkText))
                {
                    remarks.Add(remarkText);
                    currentTarget = DirectiveTarget.Remark;
                    inPluralParameterBlock = false;
                    continue;
                }

                // Multi-line continuation: indented line continues previous directive
                if (currentTarget != DirectiveTarget.None && (line.StartsWith("  ") || line.StartsWith("\t")))
                {
                    var continuation = trimmed;
                    switch (currentTarget)
                    {
                        case DirectiveTarget.Parameter when currentParamName != null:
                            parameters[currentParamName] = parameters[currentParamName] + " " + continuation;
                            break;
                        case DirectiveTarget.Returns:
                            returns = returns + " " + continuation;
                            break;
                        case DirectiveTarget.Throws:
                            throws = throws + " " + continuation;
                            break;
                        case DirectiveTarget.Remark when remarks.Count > 0:
                            remarks[remarks.Count - 1] = remarks[remarks.Count - 1] + " " + continuation;
                            break;
                    }
                    continue;
                }

                // Non-indented, non-directive line after summary: treat as additional summary or ignore
                currentTarget = DirectiveTarget.None;
                inPluralParameterBlock = false;
            }

            return new DocComment
            {
                Summary = string.Join(" ", summaryLines).Trim(),
                Parameters = parameters,
                Returns = returns,
                Throws = throws,
                Remarks = remarks
            };
        }

        private static bool IsDirective(string trimmed)
        {
            return trimmed.StartsWith("- ") && trimmed.Contains(':');
        }

        private static bool TrySingularParameter(string trimmed, out string name, out string description)
        {
            name = string.Empty;
            description = string.Empty;

            // Match "- Parameter name: description"
            const string prefix = "- Parameter ";
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var rest = trimmed.Substring(prefix.Length);
            var colonIdx = rest.IndexOf(':');
            if (colonIdx < 0)
                return false;

            name = rest.Substring(0, colonIdx).Trim();
            description = rest.Substring(colonIdx + 1).Trim();
            return !string.IsNullOrEmpty(name);
        }

        private static bool IsPluralParametersHeader(string trimmed)
        {
            return trimmed.StartsWith("- Parameters:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("- Parameters :", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryPluralParameterEntry(string trimmed, out string name, out string description)
        {
            name = string.Empty;
            description = string.Empty;

            // Match "- name: description" (inside a plural parameters block)
            if (!trimmed.StartsWith("- "))
                return false;

            var rest = trimmed.Substring(2);
            var colonIdx = rest.IndexOf(':');
            if (colonIdx < 0)
                return false;

            name = rest.Substring(0, colonIdx).Trim();
            description = rest.Substring(colonIdx + 1).Trim();
            return !string.IsNullOrEmpty(name);
        }

        private static bool TryDirective(string trimmed, string directiveName, out string description)
        {
            description = string.Empty;
            var prefix = $"- {directiveName}:";
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            description = trimmed.Substring(prefix.Length).Trim();
            return true;
        }

        private static readonly string[] RemarkDirectiveNames = {
            "Note", "Important", "Warning", "Remark", "Precondition", "Postcondition", "Complexity"
        };

        private static bool TryRemarkDirective(string trimmed, out string remarkText)
        {
            remarkText = string.Empty;
            foreach (var name in RemarkDirectiveNames)
            {
                var prefix = $"- {name}:";
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    remarkText = $"{name}: {trimmed.Substring(prefix.Length).Trim()}";
                    return true;
                }
            }
            return false;
        }
    }
}
