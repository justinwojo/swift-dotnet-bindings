// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace TbdParsing.Models
{
    /// <summary>
    /// Represents a TBD file
    /// </summary>
    public class TbdFile
    {
        /// <summary>
        /// Version of the TBD format
        /// </summary>
        public int Version { get; set; }

        /// <summary>
        /// List of targets supported by the library
        /// </summary>
        public List<string> Targets { get; set; } = new List<string>();

        /// <summary>
        /// Installation path for the library. A `.tbd` may hold several YAML documents (the
        /// framework's own library followed by the private libraries it re-exports); this is the
        /// FIRST document's install name, i.e. the library the file is named for.
        /// </summary>
        public string InstallName { get; set; } = string.Empty;

        /// <summary>
        /// Install name of every document in the file, in document order. Single-document files
        /// hold exactly one entry, equal to <see cref="InstallName"/>. Documents after the first
        /// are the re-exported libraries whose symbols also resolve through this file.
        /// </summary>
        public List<string> InstallNames { get; set; } = new List<string>();

        /// <summary>
        /// Number of YAML/JSON documents the file was parsed from. 1 for an ordinary single-library
        /// `.tbd`; greater when the library re-exports others (e.g. VisionKit re-exports the private
        /// DocumentCamera framework as a second document).
        /// </summary>
        public int DocumentCount { get; set; }

        /// <summary>
        /// Swift ABI version
        /// </summary>
        public int SwiftAbiVersion { get; set; }

        /// <summary>
        /// List of export entries, accumulated across every document in the file.
        /// </summary>
        public List<ExportEntry> Exports { get; set; } = new List<ExportEntry>();
    }

    /// <summary>
    /// Represents an export entry in a TBD file
    /// </summary>
    public class ExportEntry
    {
        /// <summary>
        /// List of targets for the export entry
        /// </summary>
        public List<string> Targets { get; set; } = new List<string>();

        /// <summary>
        /// List of categorized symbols in the export entry
        /// </summary>
        public List<Symbol> Symbols { get; set; } = new List<Symbol>();

        /// <summary>
        /// List of Objective-C classes in the export entry
        /// </summary>
        public List<string> ObjcClasses { get; set; } = new List<string>();

        /// <summary>
        /// List of Objective-C ivars in the export entry
        /// </summary>
        public List<string> ObjcIvars { get; set; } = new List<string>();

        /// <summary>
        /// Get Swift symbols only
        /// </summary>
        public IEnumerable<Symbol> SwiftSymbols => Symbols.Where(s => s.Type == SymbolType.Swift);

        /// <summary>
        /// Get Objective-C symbols only (excluding ObjcClasses)
        /// </summary>
        public IEnumerable<Symbol> ObjectiveCSymbols => Symbols.Where(s => s.Type == SymbolType.ObjectiveC);

        /// <summary>
        /// Get other symbols that are neither Swift nor Objective-C
        /// </summary>
        public IEnumerable<Symbol> OtherSymbols => Symbols.Where(s => s.Type == SymbolType.Other);
    }
}
