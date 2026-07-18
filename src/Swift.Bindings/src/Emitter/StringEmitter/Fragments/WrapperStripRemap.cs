// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace BindingsGeneration;

/// <summary>
/// Recomputes a wrapper file's intervals for the bytes the pre-strip actually produced.
/// </summary>
/// <remarks>
/// <para>
/// The invariant the fragment subsystem has to hold is that a map describes the exact bytes a
/// compiler was handed. The Swift wrapper reaches swiftc only after
/// <see cref="SwiftWrapperPostProcessor"/> deletes whole blocks from it, so the map built at
/// emission describes a file that no longer exists by the time a diagnostic is reported against it.
/// </para>
/// <para>
/// The strip is line-based and keeps every surviving line verbatim, so its
/// <see cref="PostProcessingResult.CleanedLineSources"/> is enough to carry offsets across exactly —
/// no re-derivation, no guessing which block went where. Where that provenance is absent the remap
/// returns null and the file is recorded unmapped, because a map that is merely probably right is
/// indistinguishable downstream from one that is right.
/// </para>
/// <para>
/// This deliberately does not cover the simulator-guard pass, which inserts <c>#if</c> lines and
/// reports no provenance: a file it modified has no recoverable map and must be treated as
/// unmapped rather than remapped through this.
/// </para>
/// </remarks>
public static class WrapperStripRemap
{
    /// <summary>
    /// Maps <paramref name="intervals"/>, measured against <paramref name="sourceContent"/>, onto
    /// <paramref name="cleanedContent"/>. Returns null when the strip could not report which source
    /// line each cleaned line came from.
    /// </summary>
    /// <remarks>
    /// An interval whose text the strip deleted entirely is dropped; one that survives in part keeps
    /// the surviving span, with its text re-sliced from the cleaned bytes so the fragment can never
    /// claim text the file no longer holds. Callers publish through
    /// <see cref="ModuleFragmentSet.Add"/>, whose tiling check is the backstop: a remap that failed
    /// to stay total lands the file in <see cref="ModuleFragmentSet.UnmappedFiles"/> instead of
    /// being published.
    /// </remarks>
    public static IReadOnlyList<FragmentInterval>? Remap(
        IReadOnlyList<FragmentInterval> intervals,
        string sourceContent,
        string cleanedContent,
        IReadOnlyList<int>? cleanedLineSources)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentNullException.ThrowIfNull(sourceContent);
        ArgumentNullException.ThrowIfNull(cleanedContent);

        if (cleanedLineSources == null)
            return null;

        var offsets = BuildOffsetMap(sourceContent, cleanedContent, cleanedLineSources);
        if (offsets == null)
            return null;

        var mapped = new List<FragmentInterval>(intervals.Count);
        foreach (var interval in intervals)
        {
            if (interval.Start < 0 || interval.End > sourceContent.Length)
                return null;

            // The surviving span is bounded by the first and last source offsets in the interval
            // that still exist. Scanning inward from both ends keeps a fragment whose middle was
            // stripped attached to the text it still owns rather than dropping it wholesale.
            var start = -1;
            for (var i = interval.Start; i < interval.End; i++)
            {
                if (offsets[i] >= 0) { start = offsets[i]; break; }
            }
            if (start < 0)
                continue;   // every character this fragment owned was stripped

            var end = -1;
            for (var i = interval.End - 1; i >= interval.Start; i--)
            {
                if (offsets[i] >= 0) { end = offsets[i] + 1; break; }
            }
            if (end <= start)
                continue;

            // A fragment the strip only partly removed is no longer the complete body of its scope,
            // so it must stop claiming to be: withdrawing a clipped span as if it were the whole
            // artifact would leave the surviving remainder behind.
            var survivedWhole = end - start == interval.Length;
            mapped.Add(new FragmentInterval(
                interval.Fragment with
                {
                    Text = cleanedContent[start..end],
                    IsWholeScope = interval.Fragment.IsWholeScope && survivedWhole,
                },
                start,
                end));
        }

        return mapped;
    }

    /// <summary>
    /// Source character offset → cleaned character offset, with -1 for characters the strip removed.
    /// Returns null when the provenance does not describe <paramref name="cleanedContent"/> — a
    /// mismatch means the two came from different runs, and mapping through it would be fiction.
    /// </summary>
    private static int[]? BuildOffsetMap(
        string sourceContent, string cleanedContent, IReadOnlyList<int> cleanedLineSources)
    {
        var sourceLines = BuildLineStarts(sourceContent);
        var cleanedLines = BuildLineStarts(cleanedContent);

        if (cleanedLineSources.Count != cleanedLines.Count)
            return null;

        var offsets = new int[sourceContent.Length];
        Array.Fill(offsets, -1);

        for (var cleanedLine = 0; cleanedLine < cleanedLineSources.Count; cleanedLine++)
        {
            var sourceLine = cleanedLineSources[cleanedLine];
            if (sourceLine < 0 || sourceLine >= sourceLines.Count)
                return null;   // a synthesized line has no source to map back to

            var (sourceStart, sourceEnd) = sourceLines[sourceLine];
            var (cleanedStart, cleanedEnd) = cleanedLines[cleanedLine];
            if (sourceEnd - sourceStart != cleanedEnd - cleanedStart)
                return null;   // the strip is supposed to keep surviving lines verbatim

            for (var i = 0; i < sourceEnd - sourceStart; i++)
                offsets[sourceStart + i] = cleanedStart + i;
        }

        return offsets;
    }

    /// <summary>
    /// Line spans as half-open character ranges, each including its own trailing newline, matching
    /// how the strip splits and rejoins lines.
    /// </summary>
    private static List<(int Start, int End)> BuildLineStarts(string content)
    {
        var lines = new List<(int, int)>();
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n')
                continue;
            lines.Add((start, i + 1));
            start = i + 1;
        }
        if (start < content.Length)
            lines.Add((start, content.Length));
        return lines;
    }
}
