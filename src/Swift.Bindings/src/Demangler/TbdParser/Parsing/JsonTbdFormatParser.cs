// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TbdParsing.Models;

namespace TbdParsing.Parsing
{
    /// <summary>
    /// Parser for JSON-based TBD format (version 5+)
    /// </summary>
    public class JsonTbdFormatParser : TbdFormatParserBase
    {
        /// <summary>
        /// Creates a new JSON TBD format parser
        /// </summary>
        public JsonTbdFormatParser(ILogger logger) : base(logger)
        {
        }

        public override bool CanParse(string[] lines)
        {
            if (lines == null || lines.Length == 0)
                return false;

            try
            {
                var joined = string.Join("\n", lines).TrimStart('\uFEFF').Trim();

                using var doc = JsonDocument.Parse(joined);
                var root = doc.RootElement;

                if (root.TryGetProperty("tapi_tbd_version", out _))
                {
                    _logger.LogDebug("Detected JSON TBD format");
                    return true;
                }

                _logger.LogDebug("JSON parsed but missing tapi_tbd_version property");
                return false;
            }
            catch
            {
                return false;
            }
        }

        public override TbdFile Parse(string[] lines)
        {
            var joined = string.Join("\n", lines).TrimStart('\uFEFF').Trim();

            using var doc = JsonDocument.Parse(joined);
            var root = doc.RootElement;

            if (!root.TryGetProperty("main_library", out var mainLibrary))
                throw new ParsingException("Invalid JSON TBD file: missing main_library");

            var tbdFile = new TbdFile
            {
                Version = root.TryGetProperty("tapi_tbd_version", out var versionProp)
                    ? versionProp.GetInt32() : 0,
                InstallName = GetFirstStringFromArray(mainLibrary, "install_names", "name"),
                SwiftAbiVersion = GetFirstIntFromArray(mainLibrary, "swift_abi", "abi"),
            };

            // Parse target_info
            if (mainLibrary.TryGetProperty("target_info", out var targetInfo)
                && targetInfo.ValueKind == JsonValueKind.Array)
            {
                foreach (var ti in targetInfo.EnumerateArray())
                {
                    if (ti.TryGetProperty("target", out var target))
                    {
                        var targetStr = target.GetString();
                        if (!string.IsNullOrEmpty(targetStr))
                            tbdFile.Targets.Add(targetStr);
                    }
                }
            }

            _logger.LogDebug("Parsing JSON TBD version {Version}", tbdFile.Version);

            AddLibrary(tbdFile, mainLibrary);

            // Like the YAML stream's second and later `--- !tapi-tbd` documents, a JSON TBD carries
            // the libraries this one re-exports as sibling library objects. Their symbols resolve
            // through this same file, so they accumulate into the one export list while the file's
            // scalar metadata stays that of `main_library`.
            if (root.TryGetProperty("libraries", out var libraries)
                && libraries.ValueKind == JsonValueKind.Array)
            {
                foreach (var library in libraries.EnumerateArray())
                {
                    AddLibrary(tbdFile, library);
                }
            }

            _logger.LogDebug("Parsed {DocumentCount} librar(ies) with {ExportCount} export entries and {SymbolCount} total symbols",
                tbdFile.DocumentCount, tbdFile.Exports.Count, tbdFile.Exports.Sum(e => e.Symbols.Count));

            return tbdFile;
        }

        /// <summary>
        /// Fold one library object (`main_library` or one of the re-exported entries under
        /// `libraries`) into the accumulating <see cref="TbdFile"/>: record its install name and
        /// append its exported and re-exported symbol groups.
        /// </summary>
        private static void AddLibrary(TbdFile tbdFile, JsonElement library)
        {
            tbdFile.DocumentCount++;
            tbdFile.InstallNames.Add(GetFirstStringFromArray(library, "install_names", "name"));

            AddSymbolGroups(tbdFile, library, "exported_symbols");
            AddSymbolGroups(tbdFile, library, "reexported_symbols");
        }

        private static void AddSymbolGroups(TbdFile tbdFile, JsonElement library, string groupProperty)
        {
            if (!library.TryGetProperty(groupProperty, out var groups)
                || groups.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var exportGroup in groups.EnumerateArray())
            {
                var exportEntry = new ExportEntry
                {
                    // JSON TBD format doesn't have per-export targets;
                    // inherit from top-level target_info
                    Targets = new List<string>(tbdFile.Targets),
                };

                if (exportGroup.TryGetProperty("data", out var data))
                    AddSymbolsFromData(exportEntry, data);

                if (exportGroup.TryGetProperty("text", out var text))
                    AddSymbolsFromData(exportEntry, text);

                tbdFile.Exports.Add(exportEntry);
            }
        }

        private static void AddSymbolsFromData(ExportEntry exportEntry, JsonElement data)
        {
            if (data.TryGetProperty("global", out var global)
                && global.ValueKind == JsonValueKind.Array)
            {
                foreach (var sym in global.EnumerateArray())
                {
                    var name = sym.GetString();
                    if (name != null)
                        exportEntry.Symbols.Add(new Symbol(name));
                }
            }

            if (data.TryGetProperty("objc_class", out var objcClass)
                && objcClass.ValueKind == JsonValueKind.Array)
            {
                foreach (var cls in objcClass.EnumerateArray())
                {
                    var name = cls.GetString();
                    if (name != null)
                        exportEntry.ObjcClasses.Add(name);
                }
            }

            if (data.TryGetProperty("objc_ivar", out var objcIvar)
                && objcIvar.ValueKind == JsonValueKind.Array)
            {
                foreach (var ivar in objcIvar.EnumerateArray())
                {
                    var name = ivar.GetString();
                    if (name != null)
                        exportEntry.ObjcIvars.Add(name);
                }
            }
        }

        private static string GetFirstStringFromArray(JsonElement parent, string arrayProp, string valueProp)
        {
            if (parent.TryGetProperty(arrayProp, out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty(valueProp, out var val))
                        return val.GetString() ?? string.Empty;
                }
            }
            return string.Empty;
        }

        private static int GetFirstIntFromArray(JsonElement parent, string arrayProp, string valueProp)
        {
            if (parent.TryGetProperty(arrayProp, out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty(valueProp, out var val))
                        return val.GetInt32();
                }
            }
            return 0;
        }
    }
}
