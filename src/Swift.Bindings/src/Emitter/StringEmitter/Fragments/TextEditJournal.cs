// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BindingsGeneration;

/// <summary>
/// The edits one text pass made, in source order, so offsets measured against its input can be
/// carried over to its output exactly.
/// </summary>
/// <remarks>
/// <para>
/// Generated source is rewritten several times between emission and compilation — namespace
/// qualification, wrapper-source qualification, the wrapper pre-strip — and every one of them shifts
/// the offsets recorded while emitting. The obvious repair is to run each pass over each fragment
/// separately and concatenate, but those passes are not fragment-local: the qualifier's negative
/// lookbehinds read text a cut may have moved into another fragment, and the wrapper strip walks
/// whole brace blocks and retroactively removes a stripped block's preamble from lines it has
/// already emitted. Splitting the input changes what they do, so that repair trades an offset bug
/// for an output bug.
/// </para>
/// <para>
/// Recording the edits instead leaves the pass exactly as it was — same input, same call, same
/// output bytes — and makes the offset mapping a consequence of what the pass actually did rather
/// than an assumption about how it behaves. Byte-identity is then structural rather than something
/// a cross-check has to defend after the fact.
/// </para>
/// <para>Offsets are UTF-16 character offsets, matching the rest of the fragment subsystem.</para>
/// </remarks>
public sealed class TextEditJournal
{
    private readonly List<Edit> _edits = new();

    /// <summary>A replacement of <c>[Start, Start + OldLength)</c> by <c>NewLength</c> characters.</summary>
    public readonly record struct Edit(int Start, int OldLength, int NewLength);

    /// <summary>Every recorded edit, in ascending source order.</summary>
    public IReadOnlyList<Edit> Edits => _edits;

    /// <summary>True when the pass left its input untouched, so offsets carry over unchanged.</summary>
    public bool IsIdentity => _edits.Count == 0;

    /// <summary>
    /// Records that <paramref name="oldLength"/> characters at <paramref name="start"/> became
    /// <paramref name="newLength"/> characters.
    /// </summary>
    /// <remarks>
    /// Edits must arrive in ascending, non-overlapping source order — the order a single left-to-right
    /// scan produces. An out-of-order edit means the caller is not recording what it thinks it is, and
    /// silently accepting it would produce a mapping that is wrong in a way nothing downstream could
    /// detect.
    /// </remarks>
    public void Record(int start, int oldLength, int newLength)
    {
        if (start < 0 || oldLength < 0 || newLength < 0)
            throw new ArgumentOutOfRangeException(nameof(start), "Edit bounds must be non-negative.");

        if (_edits.Count > 0)
        {
            var previous = _edits[^1];
            if (start < previous.Start + previous.OldLength)
                throw new InvalidOperationException(
                    $"TextEditJournal edit at {start} overlaps or precedes the previous edit ending at "
                    + $"{previous.Start + previous.OldLength}; edits must be recorded in ascending source order.");
        }

        _edits.Add(new Edit(start, oldLength, newLength));
    }

    /// <summary>
    /// Records into <paramref name="target"/> the single pass equivalent to <paramref name="first"/>
    /// followed by <paramref name="second"/>, whose edits are measured against the first pass's output.
    /// </summary>
    /// <remarks>
    /// An edit of the second pass that overlaps or touches an edit of the first becomes one edit
    /// covering both, measured in the first pass's input (split in two where the first pass inserted
    /// text at its end); edits that touch nothing of the other pass carry over unchanged. Every offset outside the composed edits maps exactly as the two passes
    /// map it in turn, and an offset inside one pins to the replacement's start, as
    /// <see cref="MapOffset"/> does for a single pass.
    /// </remarks>
    public static void Compose(TextEditJournal first, TextEditJournal second, TextEditJournal target)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsIdentity)
            throw new InvalidOperationException("A composed journal is recorded into an empty journal.");

        // The first pass's edits as the second pass sees them: where each replacement sits in the
        // first pass's output.
        var images = new List<(int Start, int End, Edit Edit)>(first._edits.Count);
        var delta = 0;
        foreach (var edit in first._edits)
        {
            images.Add((edit.Start + delta, edit.Start + delta + edit.NewLength, edit));
            delta += edit.NewLength - edit.OldLength;
        }

        // A position of the first pass's output outside every replacement, back in its input: before
        // a deletion at that position, or after one.
        int Before(int position)
        {
            var shift = 0;
            foreach (var image in images)
            {
                if (image.End <= position && image.Start < position)
                    shift += image.Edit.NewLength - image.Edit.OldLength;
            }
            return position - shift;
        }
        int After(int position)
        {
            var shift = 0;
            foreach (var image in images)
            {
                if (image.End <= position)
                    shift += image.Edit.NewLength - image.Edit.OldLength;
            }
            return position - shift;
        }

        // Clusters, in the first pass's output, of second-pass edits and the first-pass replacements
        // they overlap or touch.
        var composed = new List<Edit>();
        var used = new bool[images.Count];
        var index = 0;
        var edits = second._edits;
        for (int g = 0; g < edits.Count;)
        {
            int start = edits[g].Start, end = edits[g].Start + edits[g].OldLength;
            var growth = edits[g].NewLength - edits[g].OldLength;
            g++;
            bool grew;
            do
            {
                grew = false;
                for (int i = index; i < images.Count && images[i].Start <= end; i++)
                {
                    if (used[i] || images[i].End < start)
                        continue;
                    used[i] = true;
                    grew = true;
                    start = Math.Min(start, images[i].Start);
                    end = Math.Max(end, images[i].End);
                }
                while (g < edits.Count && edits[g].Start <= end)
                {
                    grew = true;
                    end = Math.Max(end, edits[g].Start + edits[g].OldLength);
                    growth += edits[g].NewLength - edits[g].OldLength;
                    g++;
                }
            }
            while (grew);
            while (index < images.Count && used[index])
                index++;
            // Every replacement the cluster touches is inside it, so its bounds sit outside all of them.
            int sourceStart = Before(start), sourceEnd = After(end);
            var newLength = end - start + growth;
            // The cluster's end in the input maps through the passes in turn to the end of its
            // replacement, unless the first pass inserted text there: an offset at an insertion maps
            // to the insertion's start, which the second pass may have moved anywhere inside the
            // replacement. The replacement is then split at that image, the rest of it an insertion
            // at the cluster's end, so the end still maps as the passes map it.
            var head = second.MapOffset(first.MapOffset(sourceEnd)) - second.MapOffset(start);
            if (head < newLength && sourceEnd > sourceStart)
            {
                composed.Add(new Edit(sourceStart, sourceEnd - sourceStart, head));
                composed.Add(new Edit(sourceEnd, 0, newLength - head));
            }
            else
            {
                composed.Add(new Edit(sourceStart, sourceEnd - sourceStart, newLength));
            }
        }
        for (int i = 0; i < images.Count; i++)
        {
            if (!used[i])
                composed.Add(images[i].Edit);
        }

        foreach (var edit in composed.OrderBy(e => e.Start).ThenBy(e => e.OldLength))
            target.Record(edit.Start, edit.OldLength, edit.NewLength);
    }

    /// <summary>
    /// Maps an offset in the pass's input to the corresponding offset in its output.
    /// </summary>
    /// <remarks>
    /// An offset that falls strictly inside replaced text has no exact image — the characters it
    /// pointed at are gone. It maps to the start of the replacement, which is the only choice that
    /// keeps the mapping monotonic and keeps a fragment boundary attached to the edit that consumed
    /// it rather than letting it drift past unrelated text.
    /// </remarks>
    public int MapOffset(int sourceOffset)
    {
        if (sourceOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceOffset));

        var delta = 0;
        foreach (var edit in _edits)
        {
            if (edit.Start >= sourceOffset)
                break;

            if (edit.Start + edit.OldLength <= sourceOffset)
            {
                delta += edit.NewLength - edit.OldLength;
                continue;
            }

            // The offset lands inside this edit's replaced range; pin it to the replacement's start.
            return edit.Start + delta;
        }

        return sourceOffset + delta;
    }

    /// <summary>
    /// Maps every interval boundary through this journal, dropping intervals that collapse to
    /// nothing because the pass deleted everything they covered.
    /// </summary>
    public IReadOnlyList<FragmentInterval> MapIntervals(
        IReadOnlyList<FragmentInterval> intervals, string mappedText)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentNullException.ThrowIfNull(mappedText);

        var mapped = new List<FragmentInterval>(intervals.Count);
        foreach (var interval in intervals)
        {
            var start = MapOffset(interval.Start);
            var end = MapOffset(interval.End);
            if (end <= start)
                continue;

            mapped.Add(new FragmentInterval(
                interval.Fragment with { Text = mappedText[start..end] },
                start,
                end));
        }
        return mapped;
    }
}
