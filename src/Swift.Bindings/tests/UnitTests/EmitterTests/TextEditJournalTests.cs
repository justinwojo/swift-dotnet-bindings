// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The offset-carrying core the whole fragment subsystem rests on. Every text pass between emission
/// and compilation reports its edits here instead of being re-run per fragment, so if this mapping
/// is wrong the interval maps are wrong everywhere at once — and wrong in the silent way, where
/// output bytes are still correct and only the attribution has drifted.
/// </summary>
public class TextEditJournalTests
{
    [Fact]
    public void MapOffset_WithNoEdits_IsIdentity()
    {
        var journal = new TextEditJournal();

        Assert.True(journal.IsIdentity);
        foreach (var offset in new[] { 0, 1, 17, 4096 })
            Assert.Equal(offset, journal.MapOffset(offset));
    }

    [Fact]
    public void MapOffset_BeforeAnyEdit_IsUnchanged()
    {
        var journal = new TextEditJournal();
        journal.Record(start: 10, oldLength: 5, newLength: 9);

        Assert.Equal(0, journal.MapOffset(0));
        Assert.Equal(9, journal.MapOffset(9));
        Assert.Equal(10, journal.MapOffset(10));
    }

    [Fact]
    public void MapOffset_AfterAnEdit_ShiftsByTheLengthDelta()
    {
        var journal = new TextEditJournal();
        journal.Record(start: 10, oldLength: 5, newLength: 9);   // +4

        Assert.Equal(19, journal.MapOffset(15));
        Assert.Equal(24, journal.MapOffset(20));
    }

    [Fact]
    public void MapOffset_AcrossSeveralEdits_AccumulatesEveryDelta()
    {
        var journal = new TextEditJournal();
        journal.Record(start: 5, oldLength: 3, newLength: 1);     // -2
        journal.Record(start: 20, oldLength: 2, newLength: 8);    // +6
        journal.Record(start: 40, oldLength: 4, newLength: 4);    // 0

        Assert.Equal(4, journal.MapOffset(4));
        Assert.Equal(8, journal.MapOffset(10));
        Assert.Equal(28, journal.MapOffset(24));
        Assert.Equal(54, journal.MapOffset(50));
    }

    /// <summary>
    /// An offset strictly inside replaced text has no image — the characters it pointed at no longer
    /// exist. Pinning it to the replacement's start is the only choice that stays monotonic; letting
    /// it drift past the replacement would put a fragment boundary inside the *next* fragment's text.
    /// </summary>
    [Fact]
    public void MapOffset_InsideReplacedText_PinsToTheReplacementStart()
    {
        var journal = new TextEditJournal();
        journal.Record(start: 10, oldLength: 6, newLength: 2);

        Assert.Equal(10, journal.MapOffset(11));
        Assert.Equal(10, journal.MapOffset(15));
        Assert.Equal(12, journal.MapOffset(16));   // first char past the edit
    }

    [Fact]
    public void MapOffset_IsMonotonicAcrossEveryOffsetOfAnEditedText()
    {
        var journal = new TextEditJournal();
        journal.Record(start: 3, oldLength: 4, newLength: 1);
        journal.Record(start: 12, oldLength: 1, newLength: 7);
        journal.Record(start: 20, oldLength: 5, newLength: 0);

        var previous = -1;
        for (var offset = 0; offset <= 40; offset++)
        {
            var mapped = journal.MapOffset(offset);
            Assert.True(mapped >= previous, $"offset {offset} mapped to {mapped}, behind {previous}");
            previous = mapped;
        }
    }

    [Theory]
    [InlineData(9)]    // starts before the previous edit
    [InlineData(12)]   // starts inside the previous edit's replaced range
    public void Record_OutOfAscendingOrder_Throws(int start)
    {
        var journal = new TextEditJournal();
        journal.Record(start: 10, oldLength: 5, newLength: 5);

        Assert.Throws<InvalidOperationException>(() => journal.Record(start, oldLength: 1, newLength: 1));
    }

    [Fact]
    public void Record_AdjacentToThePreviousEdit_IsAccepted()
    {
        var journal = new TextEditJournal();
        journal.Record(start: 10, oldLength: 5, newLength: 5);

        journal.Record(start: 15, oldLength: 2, newLength: 3);

        Assert.Equal(2, journal.Edits.Count);
    }

    [Theory]
    [InlineData(-1, 1, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 1, -1)]
    public void Record_WithNegativeBounds_Throws(int start, int oldLength, int newLength)
    {
        var journal = new TextEditJournal();

        Assert.Throws<ArgumentOutOfRangeException>(() => journal.Record(start, oldLength, newLength));
    }

    /// <summary>
    /// The property that actually matters, exercised against a real rewrite rather than arithmetic:
    /// apply a set of replacements to a string while recording them, then check that every source
    /// offset maps to the offset holding the same character in the rewritten string. Offsets inside
    /// replaced regions are excluded — those characters are gone by construction.
    /// </summary>
    [Fact]
    public void MapOffset_OverARealRewrite_LandsOnTheSameCharacter()
    {
        const string source = "alpha BETA gamma DELTA epsilon BETA omega";
        var replacements = new List<(int Start, int Length, string Text)>();
        for (var i = source.IndexOf("BETA", StringComparison.Ordinal); i >= 0;
             i = source.IndexOf("BETA", i + 4, StringComparison.Ordinal))
        {
            replacements.Add((i, 4, "b"));
        }
        var delta = source.IndexOf("DELTA", StringComparison.Ordinal);
        replacements.Add((delta, 5, "d_e_l_t_a"));
        replacements.Sort((x, y) => x.Start.CompareTo(y.Start));

        var journal = new TextEditJournal();
        var rewritten = new StringBuilder();
        var cursor = 0;
        foreach (var (start, length, text) in replacements)
        {
            rewritten.Append(source, cursor, start - cursor).Append(text);
            journal.Record(start, length, text.Length);
            cursor = start + length;
        }
        rewritten.Append(source, cursor, source.Length - cursor);
        var result = rewritten.ToString();

        Assert.Equal("alpha b gamma d_e_l_t_a epsilon b omega", result);

        for (var offset = 0; offset < source.Length; offset++)
        {
            // Offsets from an edit's start through its replaced range have no surviving character:
            // the start maps onto the replacement's first character, the interior pins back to it.
            // Both are covered by the pinning and monotonicity tests; only survivors belong here.
            var consumedByEdit = replacements.Exists(r => offset >= r.Start && offset < r.Start + r.Length);
            if (consumedByEdit)
                continue;

            var mapped = journal.MapOffset(offset);
            Assert.True(mapped < result.Length, $"source offset {offset} mapped past the rewritten text");
            Assert.Equal(source[offset], result[mapped]);
        }

        // An offset at an edit's start lands on the replacement's first character, not the
        // original's — the mapping is monotonic, so it cannot also preserve the character.
        var betaStart = replacements[0].Start;
        Assert.Equal('b', result[journal.MapOffset(betaStart)]);

        // The end offset must land exactly on the rewritten length, or the last interval of a
        // mapped file would stop short of the text it is supposed to cover.
        Assert.Equal(result.Length, journal.MapOffset(source.Length));
    }

    [Fact]
    public void MapIntervals_OverARewrite_StillTilesTheMappedTextExactly()
    {
        const string source = "0123456789ABCDEFGHIJ";
        var journal = new TextEditJournal();
        journal.Record(start: 4, oldLength: 3, newLength: 6);
        var mappedText = source[..4] + "xxxxxx" + source[7..];

        var intervals = new List<FragmentInterval>
        {
            Interval("a", source, 0, 4),
            Interval("b", source, 4, 12),
            Interval("c", source, 12, source.Length),
        };

        var mapped = journal.MapIntervals(intervals, mappedText);

        Assert.Equal(3, mapped.Count);
        Assert.True(FragmentAssembly.IsExactTiling(mapped, mappedText));
        foreach (var interval in mapped)
            Assert.Equal(interval.Fragment.Text, mappedText[interval.Start..interval.End]);
    }

    /// <summary>
    /// A pass that deletes everything an interval covered leaves no text to attribute, so the
    /// interval is dropped rather than kept as an empty one. The resulting gap is what makes the
    /// file fail its tiling check and be recorded unmapped — degrading to "no attribution" instead
    /// of publishing a map with a zero-width hole in it.
    /// </summary>
    [Fact]
    public void MapIntervals_WhenAPassDeletesAnIntervalEntirely_DropsIt()
    {
        const string source = "keepDELETEkeep";
        var journal = new TextEditJournal();
        journal.Record(start: 4, oldLength: 6, newLength: 0);
        var mappedText = "keepkeep";

        var intervals = new List<FragmentInterval>
        {
            Interval("a", source, 0, 4),
            Interval("b", source, 4, 10),
            Interval("c", source, 10, source.Length),
        };

        var mapped = journal.MapIntervals(intervals, mappedText);

        Assert.Equal(2, mapped.Count);
        Assert.DoesNotContain(mapped, i => i.Fragment.Text.Contains("DELETE", StringComparison.Ordinal));
    }

    private static FragmentInterval Interval(string name, string source, int start, int end) =>
        new(
            new OutputFragment
            {
                Owner = FragmentOwners.ForModule(DeclIdFactory.ForModule(name)),
                Plane = OutputPlane.CSharp,
                Text = source[start..end],
                IsWholeScope = true,
                Depth = 0,
            },
            start,
            end);
}
