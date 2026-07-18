// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace BindingsGeneration;

/// <summary>
/// Builds the interval map for an assembled output file from the tiling recorded while its text was
/// emitted.
/// </summary>
/// <remarks>
/// <para>
/// A written file is rarely a contiguous slice of the buffer it came from. The file-per-type split
/// concatenates the shared header, one type's byte range, and the namespace close — three disjoint
/// ranges of the same buffer — and the prelude is the buffer with every type range cut out. So the
/// map for a file is built by clipping the buffer's tiling to the ranges that file actually took and
/// renumbering them into file coordinates.
/// </para>
/// <para>
/// A leaf clipped by a range boundary is no longer a complete scope: the file holds only part of what
/// its owner emitted. That is recorded rather than glossed over, because a later pass that withdrew a
/// partial fragment believing it complete would remove less text than the artifact wrote and leave
/// the remainder orphaned.
/// </para>
/// </remarks>
public static class FragmentAssembly
{
    /// <summary>A half-open range of the source buffer that an assembled file copied verbatim.</summary>
    public readonly record struct SourceRange(int Start, int End);

    /// <summary>One leaf of a recorded buffer tiling.</summary>
    public readonly record struct TileLeaf(FragmentOwner Owner, int Start, int End, bool IsWholeScope, int Depth);

    /// <summary>
    /// Clips <paramref name="tiling"/> to <paramref name="ranges"/> and renumbers the result into the
    /// coordinates of the file those ranges were concatenated into, in the order given.
    /// </summary>
    /// <param name="source">The buffer the ranges index into.</param>
    /// <param name="tiling">The buffer's complete tiling.</param>
    /// <param name="ranges">The buffer ranges the file is the concatenation of, in file order.</param>
    /// <param name="plane">Which output language the file is written in.</param>
    public static IReadOnlyList<FragmentInterval> BuildIntervals(
        string source,
        IReadOnlyList<TileLeaf> tiling,
        IReadOnlyList<SourceRange> ranges,
        OutputPlane plane)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tiling);
        ArgumentNullException.ThrowIfNull(ranges);

        var intervals = new List<FragmentInterval>();
        var written = 0;

        foreach (var range in ranges)
        {
            foreach (var leaf in tiling)
            {
                // The tiling is ordered, so once a leaf starts past the range nothing later overlaps.
                if (leaf.Start >= range.End)
                    break;
                if (leaf.End <= range.Start)
                    continue;

                var start = Math.Max(leaf.Start, range.Start);
                var end = Math.Min(leaf.End, range.End);
                if (end <= start)
                    continue;

                var clipped = start != leaf.Start || end != leaf.End;
                var text = source[start..end];
                intervals.Add(new FragmentInterval(
                    new OutputFragment
                    {
                        Owner = leaf.Owner,
                        Plane = plane,
                        Text = text,
                        IsWholeScope = leaf.IsWholeScope && !clipped,
                        Depth = leaf.Depth,
                    },
                    written,
                    written + text.Length));
                written += text.Length;
            }
        }

        return intervals;
    }

    /// <summary>
    /// Verifies that <paramref name="intervals"/> tile <paramref name="text"/> completely and in
    /// order, and that each interval's recorded text is exactly the slice it claims.
    /// </summary>
    /// <remarks>
    /// This is the map's own correctness gate rather than a debug aid. Every consumer of a published
    /// map — a diagnostic attributed to an artifact, a fragment withdrawn and the file re-rendered —
    /// is only as sound as the claim that the interval boundaries line up with the bytes. Publishing
    /// a map that failed this would misattribute silently, so a failing map is withheld instead.
    /// </remarks>
    public static bool IsExactTiling(IReadOnlyList<FragmentInterval> intervals, string text)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentNullException.ThrowIfNull(text);

        var cursor = 0;
        foreach (var interval in intervals)
        {
            if (interval.Start != cursor || interval.End < interval.Start || interval.End > text.Length)
                return false;
            if (!string.Equals(interval.Fragment.Text, text[interval.Start..interval.End], StringComparison.Ordinal))
                return false;
            cursor = interval.End;
        }

        return cursor == text.Length;
    }
}
