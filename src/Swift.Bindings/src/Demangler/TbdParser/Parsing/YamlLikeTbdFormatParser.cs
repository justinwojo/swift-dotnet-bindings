// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using TbdParsing.Models;

namespace TbdParsing.Parsing
{
    /// <summary>
    /// Parser for YAML-like TBD format (versions 1-4)
    /// </summary>
    public class YamlLikeTbdFormatParser : TbdFormatParserBase
    {
        /// <summary>
        /// Creates a new YAML-like TBD format parser
        /// </summary>
        public YamlLikeTbdFormatParser(ILogger logger) : base(logger)
        {
        }
        public override bool CanParse(string[] lines)
        {
            if (lines == null || lines.Length == 0)
            {
                _logger.LogDebug("Cannot parse empty file");
                return false;
            }

            // YAML-like format typically starts with "--- !tapi-tbd"
            string firstLine = lines[0].Trim();
            if (firstLine != DocumentMarker)
            {
                _logger.LogDebug("First line does not contain \"--- !tapi-tbd\"");
                return false;
            }

            return true;
        }

        public override TbdFile Parse(string[] lines)
        {
            _logger.LogDebug("Starting YAML-like TBD format parsing");
            var tbdFile = new TbdFile();
            int lineIndex = 0;

            // A .tbd file is a YAML *stream*: every `--- !tapi-tbd` marker opens a new document,
            // and a library that re-exports others emits one document per library (the framework's
            // own first, then each re-exported private library). All documents describe symbols
            // that resolve through this one file, so symbol-bearing lists ACCUMULATE across the
            // stream while the scalar metadata (install-name, targets, tbd-version,
            // swift-abi-version) is taken from the FIRST document — the library the file is named
            // for. Treating a later document's metadata as the file's would attribute the file to a
            // re-exported private library, and overwriting rather than accumulating its exports
            // would discard the framework's own symbols entirely.
            int documentIndex = -1;

            while (lineIndex < lines.Length)
            {
                string line = lines[lineIndex].Trim();

                if (IsDocumentMarker(line))
                {
                    documentIndex++;
                    lineIndex++;
                    _logger.LogDebug($"Starting TBD document {documentIndex} at line {lineIndex}");
                    continue;
                }

                lineIndex++;

                // Skip blank lines and comments
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                {
                    continue;
                }

                if (line == DocumentEndMarker)
                {
                    // `...` closes the current document, not necessarily the stream: a following
                    // `---` marker legitimately opens another one.
                    _logger.LogDebug($"TBD document end marker found ({DocumentEndMarker}) at line {lineIndex}");
                    continue;
                }

                // Metadata is first-document-wins; a stream whose first document is implicit
                // (no leading marker) is still treated as document 0.
                bool isFirstDocument = documentIndex <= 0;

                // Parse key-value pairs
                KeyValuePair<string, string> kvp;
                try
                {
                    kvp = ParseKeyValuePair(line);
                }
                catch (FormatException ex)
                {
                    _logger.LogWarning($"Line {lineIndex}: {ex.Message}");
                    continue;
                }
                _logger.LogDebug($"Found top-level key-value pair: {kvp.Key} = {kvp.Value}");

                switch (kvp.Key)
                {
                    case "tbd-version":
                        try
                        {
                            int version = int.Parse(kvp.Value);
                            if (isFirstDocument)
                            {
                                tbdFile.Version = version;
                                _logger.LogDebug($"Parsed tbd-version = {tbdFile.Version}");
                            }
                        }
                        catch (FormatException)
                        {
                            _logger.LogWarning($"Failed to parse tbd-version: {kvp.Value}");
                        }
                        break;

                    case "install-name":
                        string installName = kvp.Value.Trim('\'', '"');
                        tbdFile.InstallNames.Add(installName);
                        if (isFirstDocument)
                        {
                            tbdFile.InstallName = installName;
                        }
                        _logger.LogDebug($"Parsed install-name = {installName} (document {Math.Max(documentIndex, 0)})");
                        break;

                    case "swift-abi-version":
                        try
                        {
                            int swiftAbiVersion = int.Parse(kvp.Value);
                            if (isFirstDocument)
                            {
                                tbdFile.SwiftAbiVersion = swiftAbiVersion;
                                _logger.LogDebug($"Parsed swift-abi-version = {tbdFile.SwiftAbiVersion}");
                            }
                        }
                        catch (FormatException)
                        {
                            _logger.LogWarning($"Failed to parse swift-abi-version: {kvp.Value}");
                        }
                        break;

                    case "targets":
                        // Always parse, so the continuation lines are consumed either way; only the
                        // first document's list becomes the file's target list.
                        var targets = ParseMultiLineArray(lines, ref lineIndex, kvp.Value);
                        if (isFirstDocument)
                        {
                            tbdFile.Targets = targets;
                            _logger.LogDebug($"Parsed {tbdFile.Targets.Count} targets: [{string.Join(", ", tbdFile.Targets)}]");
                        }
                        break;

                    // `exports` are this document's own symbols; `reexports` are symbols another
                    // library defines that still resolve through this one at link time. Both are
                    // reachable from the image being bound, so both feed the symbol set.
                    case "exports":
                    case "reexports":
                        _logger.LogDebug($"Starting {kvp.Key} section parsing at line {lineIndex}");
                        var entries = ParseExports(lines, ref lineIndex);
                        tbdFile.Exports.AddRange(entries);
                        _logger.LogDebug($"Parsed {entries.Count} {kvp.Key} entries ({tbdFile.Exports.Count} total)");
                        break;

                    // These keys are valid TBD fields but not needed for binding generation.
                    // The nested-block ones carry indented children that must be consumed so the
                    // next iteration doesn't read them as top-level keys.
                    case "flags":
                    case "current-version":
                    case "compatibility-version":
                    case "objc-constraint":
                    case "platform":
                    case "uuids":
                        _logger.LogDebug($"Ignoring optional TBD field: {kvp.Key}");
                        ConsumeNestedValue(lines, ref lineIndex, kvp.Value);
                        break;

                    // `reexported-libraries` names the libraries whose documents follow in this same
                    // stream; the documents themselves carry the symbols, so the declaration block is
                    // only consumed. `allowable-clients` is a link-time restriction, irrelevant here.
                    case "reexported-libraries":
                    case "allowable-clients":
                        _logger.LogDebug($"Skipping TBD block not needed for bindings: {kvp.Key}");
                        ConsumeNestedValue(lines, ref lineIndex, kvp.Value);
                        break;

                    default:
                        _logger.LogWarning($"Unknown top-level key: {kvp.Key}");
                        // If the unknown key's value opens a multi-line array or an indented nested
                        // block, consume the continuation lines so the next iteration doesn't try to
                        // parse them as new key-value pairs. The result is intentionally discarded.
                        ConsumeNestedValue(lines, ref lineIndex, kvp.Value);
                        break;
                }
            }

            tbdFile.DocumentCount = Math.Max(documentIndex + 1, 1);
            _logger.LogDebug(
                $"Completed YAML-like TBD format parsing: {tbdFile.DocumentCount} document(s), " +
                $"{tbdFile.Exports.Count} export entries, install-names [{string.Join(", ", tbdFile.InstallNames)}]");
            return tbdFile;
        }

        /// <summary>
        /// The YAML document-start marker that opens each library in a `.tbd` stream. Apple writes
        /// it with the tapi tag (`--- !tapi-tbd`); a bare `---` is the same marker in YAML.
        /// </summary>
        private const string DocumentMarker = "--- !tapi-tbd";

        /// <summary>The YAML document-end marker.</summary>
        private const string DocumentEndMarker = "...";

        /// <summary>
        /// True when the (trimmed) line opens a new YAML document.
        /// </summary>
        private static bool IsDocumentMarker(string trimmedLine) =>
            trimmedLine == DocumentMarker || trimmedLine == "---";

        /// <summary>
        /// Parse an array of strings in the format [ item1, item2, item3 ]
        /// </summary>
        private List<string> ParseArray(string value)
        {
            var items = new List<string>();

            // If the array is empty
            if (string.IsNullOrWhiteSpace(value) || value == "[]")
            {
                return items;
            }

            // Handle array format like [ item1, item2, item3 ]
            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                string content = value[1..^1].Trim();

                // Split by comma, but handle commas within quoted strings
                var splitItems = SplitArrayItems(content);
                foreach (var item in splitItems)
                {
                    string trimmedItem = item.Trim().Trim('\'', '"');
                    if (!string.IsNullOrWhiteSpace(trimmedItem))
                    {
                        items.Add(trimmedItem);
                    }
                }
            }
            else
            {
                _logger.LogWarning($"Invalid array format: {value}");
            }

            return items;
        }

        /// <summary>
        /// Parse an array of strings spanning over multiple lines
        /// </summary>
        private List<string> ParseMultiLineArray(string[] lines, ref int lineIndex, string initialValue)
        {
            var items = new List<string>();

            // If the array is empty
            if (string.IsNullOrWhiteSpace(initialValue) || initialValue == "[]")
            {
                return items;
            }

            // If the array is already complete on the first line
            if (initialValue.StartsWith('[') && initialValue.EndsWith(']'))
            {
                _logger.LogDebug($"Single line array encountered, falling back to ParseArray");
                return ParseArray(initialValue);
            }

            // If the array starts but doesn't end on the first line
            if (initialValue.StartsWith('[') && !initialValue.EndsWith(']'))
            {
                StringBuilder arrayBuilder = new StringBuilder(initialValue);

                // Keep reading lines until we find the closing bracket
                while (lineIndex < lines.Length)
                {
                    string nextLine = lines[lineIndex].Trim();

                    // If this is a new section or entry, we probably encountered a malformed array.
                    // Leave lineIndex on that line so the caller still sees it — swallowing it here
                    // would drop a following key or a `--- !tapi-tbd` document marker (which starts
                    // with '-' and so lands on exactly this branch).
                    if (nextLine.StartsWith('-') || nextLine.Contains(':'))
                    {
                        _logger.LogWarning($"Array does not have a closing bracket before new section at line {lineIndex} with content: {nextLine}");
                        break;
                    }

                    lineIndex++;
                    arrayBuilder.Append(' ').Append(nextLine);

                    // Check if this line contains the closing bracket
                    if (nextLine.Contains(']'))
                    {
                        break;
                    }
                }

                // Parse the complete array string
                return ParseArray(arrayBuilder.ToString());
            }

            // If we got here, the input wasn't an array
            _logger.LogWarning($"Expected array format but found: {initialValue}");
            return items;
        }

        /// <summary>
        /// Parse the exports section which has a nested structure
        /// </summary>
        private List<ExportEntry> ParseExports(string[] lines, ref int lineIndex)
        {
            var exports = new List<ExportEntry>();
            ExportEntry? currentExport = null;
            int baseIndentation = -1;

            _logger.LogDebug("Parsing exports section");
            while (lineIndex < lines.Length)
            {
                string rawLine = lines[lineIndex];
                // Get indentation level before trimming
                int indentation = GetIndentation(rawLine);
                string line = rawLine.Trim();

                // A blank line inside the section is not a section terminator — skip it rather
                // than let its zero indentation end the section early.
                if (string.IsNullOrWhiteSpace(line))
                {
                    lineIndex++;
                    continue;
                }

                // A document boundary always ends the section, whatever the indentation looks like.
                if (IsDocumentMarker(line) || line == DocumentEndMarker)
                {
                    _logger.LogDebug($"Exiting exports section at line {lineIndex} on document boundary");
                    break;
                }

                // If we haven't determined base indentation yet, set it now
                if (baseIndentation == -1)
                {
                    baseIndentation = indentation;
                    _logger.LogDebug($"Base indentation set to {baseIndentation}");
                }

                // If we're back at a lower indentation than the exports level,
                // we've exited the exports section. The terminating line belongs to the caller —
                // leave lineIndex on it so the top-level loop still sees the next key or the next
                // document's marker instead of silently dropping it.
                if (indentation < baseIndentation)
                {
                    _logger.LogDebug($"Exiting exports section at line {lineIndex}, indentation {indentation} < base {baseIndentation}");
                    break;
                }

                lineIndex++;

                // Parse key-value pairs
                KeyValuePair<string, string> kvp;
                kvp = ParseKeyValuePair(line);
                _logger.LogDebug($"Found export key-value pair: {kvp.Key} = {kvp.Value}");

                switch (kvp.Key)
                {
                    case "- targets":
                        // Start a new export entry
                        currentExport = new ExportEntry();
                        exports.Add(currentExport);

                        // Parse the export targets
                        currentExport.Targets = ParseMultiLineArray(lines, ref lineIndex, kvp.Value);
                        _logger.LogDebug($"Found new export entry at line {lineIndex} with [{string.Join(", ", currentExport.Targets)}] targets.");

                        break;
                    case "symbols":
                        if (currentExport == null)
                        {
                            _logger.LogWarning($"Unexpected symbols entry at line {lineIndex} without a current export entry");
                            break;
                        }

                        // Parse symbols list
                        currentExport.Symbols = ConvertToSymbols(ParseMultiLineArray(lines, ref lineIndex, kvp.Value));
                        _logger.LogDebug($"Parsed {currentExport.Symbols.Count} symbols");
                        break;

                    case "objc-classes":
                        if (currentExport == null)
                        {
                            _logger.LogWarning($"Unexpected objc-classes entry at line {lineIndex} without a current export entry");
                            break;
                        }

                        // Parse objc-classes list
                        currentExport.ObjcClasses = ParseMultiLineArray(lines, ref lineIndex, kvp.Value);
                        _logger.LogDebug($"Parsed {currentExport.ObjcClasses.Count} objc-classes");
                        break;
                    case "objc-ivars":
                        if (currentExport == null)
                        {
                            _logger.LogWarning($"Unexpected objc-ivars entry at line {lineIndex} without a current export entry");
                            break;
                        }

                        // Parse objc-ivars list
                        currentExport.ObjcIvars = ParseMultiLineArray(lines, ref lineIndex, kvp.Value);
                        _logger.LogDebug($"Parsed {currentExport.ObjcIvars.Count} objc-ivars");
                        break;
                    case "weak-symbols":
                        // Weak symbols are not needed for binding generation, but we need to
                        // consume the array to continue parsing properly
                        var weakSymbols = ParseMultiLineArray(lines, ref lineIndex, kvp.Value);
                        _logger.LogDebug($"Skipped {weakSymbols.Count} weak-symbols (not needed for bindings)");
                        break;
                    default:
                        _logger.LogWarning($"Unknown export property at line {lineIndex}: {kvp.Key}");
                        // Real-world TBDs (e.g. SDKs that import ObjC exception
                        // types from a sibling module) include export properties such as
                        // `objc-eh-types: [ ... ]` that span multiple lines. The parser
                        // doesn't need the values, but it MUST consume the continuation
                        // lines — otherwise the next iteration tries to parse the array
                        // tail (e.g. "STDSRuntimeException ]") as a key-value pair and
                        // throws. The result is intentionally discarded.
                        ConsumeIfMultiLineArray(lines, ref lineIndex, kvp.Value);
                        break;
                }
            }

            return exports;
        }

        /// <summary>
        /// If the given value opens a multi-line YAML-like array (`[ ...` without a closing
        /// `]` on the same line), consume continuation lines through the closing bracket and
        /// discard the result. Used by the `default` arms of the top-level and exports
        /// switches so that unknown properties whose values span multiple lines don't break
        /// parsing on the continuation tail.
        /// </summary>
        private void ConsumeIfMultiLineArray(string[] lines, ref int lineIndex, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            if (value.StartsWith('[') && !value.EndsWith(']'))
            {
                _ = ParseMultiLineArray(lines, ref lineIndex, value);
            }
        }

        /// <summary>
        /// Consume whatever the value of a top-level key spans, without interpreting it: either a
        /// multi-line `[ ... ]` array (see <see cref="ConsumeIfMultiLineArray"/>) or — when the key
        /// carries no inline value — the indented block beneath it, e.g.
        /// <code>
        /// reexported-libraries:
        ///   - targets:   [ arm64-ios-simulator ]
        ///     libraries: [ '/System/Library/PrivateFrameworks/Foo.framework/Foo' ]
        /// </code>
        /// Top-level keys sit at column 0, so any indented or blank line after the key belongs to
        /// its block. Without this, those children are read back as top-level keys and each one
        /// produces a spurious "unknown key" warning.
        /// </summary>
        private void ConsumeNestedValue(string[] lines, ref int lineIndex, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                ConsumeIfMultiLineArray(lines, ref lineIndex, value);
                return;
            }

            int start = lineIndex;
            while (lineIndex < lines.Length)
            {
                string rawLine = lines[lineIndex];
                if (!string.IsNullOrWhiteSpace(rawLine) && GetIndentation(rawLine) == 0)
                {
                    break;
                }
                lineIndex++;
            }

            if (lineIndex > start)
            {
                _logger.LogDebug($"Consumed {lineIndex - start} nested line(s) below a top-level key");
            }
        }

        /// <summary>
        /// Split array items respecting quotes
        /// </summary>
        private static List<string> SplitArrayItems(string content)
        {
            var result = new List<string>();
            bool inQuote = false;
            int start = 0;

            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];

                if (c == '\'' || c == '"')
                    inQuote = !inQuote;

                else if (c == ',' && !inQuote)
                {
                    result.Add(content[start..i].Trim());
                    start = i + 1;
                }
            }

            // Add the last item
            if (start < content.Length)
                result.Add(content[start..].Trim());

            return result;
        }


        /// <summary>
        /// Parse key-value pairs in the format "key: value"
        /// </summary>
        private static KeyValuePair<string, string> ParseKeyValuePair(string line)
        {
            int colonPos = line.IndexOf(':');
            if (colonPos == -1)
            {
                throw new FormatException($"Invalid key-value pair format: {line}");
            }

            string key = line[..colonPos].Trim();
            string value = line[(colonPos + 1)..].Trim();
            return new KeyValuePair<string, string>(key, value);
        }

        /// <summary>
        /// Convert a list of strings to a list of Symbol objects
        /// </summary>
        private static List<Symbol> ConvertToSymbols(List<string> arr)
        {
            var symbols = new List<Symbol>();
            foreach (var item in arr)
            {
                symbols.Add(new Symbol(item));
            }
            return symbols;
        }

        /// <summary>
        /// Get the indentation level (number of leading spaces)
        /// </summary>
        private static int GetIndentation(string line)
        {
            if (string.IsNullOrEmpty(line))
                return 0;

            ReadOnlySpan<char> span = line.AsSpan();
            int i = 0;
            while (i < span.Length && span[i] == ' ')
                i++;
            return i;
        }
    }
}
