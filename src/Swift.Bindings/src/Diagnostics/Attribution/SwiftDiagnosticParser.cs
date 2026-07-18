// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

using BindingsGeneration;

namespace BindingsGeneration.Diagnostics;

/// <summary>
/// Turns raw swiftc stderr into structured <see cref="DiagnosticGroup"/>s — primaries with their
/// notes, each carrying a file, line, UTF-8 column, severity, and message.
/// </summary>
/// <remarks>
/// <para>
/// This is the "structured capture" the attribution pipeline needs, produced from the textual
/// stream rather than swiftc's serialized <c>.dia</c> container. That choice is deliberate: under
/// <c>-emit-library</c> swiftc does not write a <c>.dia</c> without a per-primary output map, its
/// binary bitstream format has no managed reader, and its textual diagnostics already carry every
/// field attribution needs — <c>file:line:column: severity: message</c> with a distinct
/// <c>note:</c> severity for follow-ons. Parsing the text keeps the generator free of a native
/// dependency and composes with the raw-stderr preservation the compiler already does.
/// </para>
/// <para>
/// swiftc interleaves each diagnostic with source-context lines drawn with a <c>|</c> gutter (the
/// caret underline and a restated <c>`- severity:</c>). Those never match the positioned-diagnostic
/// shape — they carry no <c>:line:column:</c> prefix — so they are skipped structurally rather than
/// by a fragile "is this a gutter line" heuristic. Notes attach to the most recent primary as
/// evidence; only primaries are ever attributed.
/// </para>
/// </remarks>
public static class SwiftDiagnosticParser
{
    // file:line:col: severity: message.  The file segment is non-greedy up to the first
    // ":<digits>:<digits>: <severity>:" run, so a path containing a colon still parses.
    private static readonly Regex PositionedDiagnostic = new(
        @"^(?<file>.+?):(?<line>\d+):(?<col>\d+): (?<sev>error|warning|note|remark): (?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Driver/linker diagnostics that carry no source position (ld / clang front the message).
    private static readonly Regex ToolDiagnostic = new(
        @"^(?<tool>ld|clang|swiftc|swift-frontend): (?<sev>error|warning|fatal error): (?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A bare driver diagnostic with no tool prefix and no source position — swift-frontend emits
    // these for argument/configuration failures, e.g. "error: unknown argument: '-Xfoo'". Anchored
    // to the line start with no leading whitespace, so the indented caret restatement swiftc draws
    // under a positioned error ("      |  `- error: …") cannot match and be double-counted.
    private static readonly Regex BareDiagnostic = new(
        @"^(?<sev>error|warning|fatal error): (?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses <paramref name="stderr"/> into diagnostic groups in emitted order. Returns an empty
    /// list for null/empty input. Never throws on malformed input — unrecognized lines are context
    /// and are dropped.
    /// </summary>
    public static IReadOnlyList<DiagnosticGroup> Parse(string? stderr)
    {
        var groups = new List<DiagnosticGroup>();
        if (string.IsNullOrEmpty(stderr))
            return groups;

        CompilerDiagnostic? currentPrimary = null;
        var currentNotes = ImmutableArray.CreateBuilder<CompilerDiagnostic>();

        void Flush()
        {
            if (currentPrimary is { } primary)
            {
                groups.Add(new DiagnosticGroup { Primary = primary, Notes = currentNotes.ToImmutable() });
                currentPrimary = null;
                currentNotes.Clear();
            }
        }

        // The linker prints "Undefined symbols for architecture …:" then indented
        // "\"_sym\", referenced from:" lines. They belong together as one positionless error whose
        // message keeps the quoted symbols so symbol attribution can recover the owning unit.
        var linkerBlock = new List<string>();
        void FlushLinker()
        {
            if (linkerBlock.Count == 0)
                return;
            Flush();
            groups.Add(new DiagnosticGroup
            {
                Primary = CompilerDiagnostic.Global(DiagnosticSeverity.Error, string.Join("\n", linkerBlock)),
            });
            linkerBlock.Clear();
        }

        var lines = stderr.Replace("\r\n", "\n").Split('\n');
        foreach (var raw in lines)
        {
            var line = raw;

            // Inside an "Undefined symbols" block: keep gathering the symbol/reference lines.
            if (linkerBlock.Count > 0)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("\"", System.StringComparison.Ordinal)
                    || trimmed.StartsWith("_", System.StringComparison.Ordinal)
                    || trimmed.Contains("referenced from:", System.StringComparison.Ordinal)
                    || trimmed.StartsWith("(", System.StringComparison.Ordinal))
                {
                    linkerBlock.Add(trimmed);
                    continue;
                }
                FlushLinker();
                // fall through to classify this line normally
            }

            var posMatch = PositionedDiagnostic.Match(line);
            if (posMatch.Success)
            {
                var severity = ParseSeverity(posMatch.Groups["sev"].Value);
                var diag = new CompilerDiagnostic
                {
                    File = posMatch.Groups["file"].Value,
                    Line = int.Parse(posMatch.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Column = int.Parse(posMatch.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Severity = severity,
                    Message = posMatch.Groups["msg"].Value,
                };

                if (severity == DiagnosticSeverity.Note)
                {
                    // A note is evidence for a primary, never a failure in its own right. Attach it
                    // to the current primary; a note with no primary to attach to (a stray note after
                    // a flush, or a note-first stream) is dropped rather than promoted to a group —
                    // the invariant is that a group is only ever headed by a non-note diagnostic.
                    if (currentPrimary is not null)
                        currentNotes.Add(diag);
                    continue;
                }

                Flush();
                currentPrimary = diag;
                continue;
            }

            if (line.Contains("Undefined symbol", System.StringComparison.OrdinalIgnoreCase))
            {
                linkerBlock.Add(line.TrimStart());
                continue;
            }

            var toolMatch = ToolDiagnostic.Match(line);
            if (toolMatch.Success)
            {
                var severity = toolMatch.Groups["sev"].Value.StartsWith("warning", System.StringComparison.Ordinal)
                    ? DiagnosticSeverity.Warning
                    : DiagnosticSeverity.Error;
                Flush();
                groups.Add(new DiagnosticGroup
                {
                    Primary = CompilerDiagnostic.Global(severity, toolMatch.Groups["msg"].Value),
                });
                continue;
            }

            var bareMatch = BareDiagnostic.Match(line);
            if (bareMatch.Success)
            {
                var severity = bareMatch.Groups["sev"].Value.StartsWith("warning", System.StringComparison.Ordinal)
                    ? DiagnosticSeverity.Warning
                    : DiagnosticSeverity.Error;
                Flush();
                groups.Add(new DiagnosticGroup
                {
                    Primary = CompilerDiagnostic.Global(severity, bareMatch.Groups["msg"].Value),
                });
                continue;
            }

            // Anything else is source-context / gutter output: not a diagnostic.
        }

        FlushLinker();
        Flush();
        return groups;
    }

    private static DiagnosticSeverity ParseSeverity(string token) => token switch
    {
        "error" => DiagnosticSeverity.Error,
        "warning" => DiagnosticSeverity.Warning,
        "note" => DiagnosticSeverity.Note,
        "remark" => DiagnosticSeverity.Remark,
        _ => DiagnosticSeverity.Error,
    };
}
