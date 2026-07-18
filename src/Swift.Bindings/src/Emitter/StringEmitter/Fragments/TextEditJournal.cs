// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

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
