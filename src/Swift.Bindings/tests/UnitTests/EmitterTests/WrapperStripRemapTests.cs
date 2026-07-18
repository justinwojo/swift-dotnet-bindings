// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The wrapper plane's last coordinate hop. Emission maps the wrapper it wrote, but swiftc is handed
/// the file <see cref="SwiftWrapperPostProcessor"/> produced after deleting broken blocks — so
/// unless the map is carried across that strip, every diagnostic on a stripped wrapper is attributed
/// against text at the wrong offset.
///
/// <para>These drive the real strip rather than a stand-in, because the property that matters is
/// that the remap agrees with what that pass actually did, not with a model of it.</para>
/// </summary>
public class WrapperStripRemapTests
{
    [Fact]
    public void Remap_WithoutLineProvenance_ReturnsNull()
    {
        const string source = "a\nb\n";
        var intervals = new List<FragmentInterval> { Interval(source, 0, source.Length, "whole") };

        Assert.Null(WrapperStripRemap.Remap(intervals, source, "a\n", cleanedLineSources: null));
    }

    [Fact]
    public void Remap_WhenTheStripRemovedNothing_ReproducesTheOriginalIntervals()
    {
        const string source = "// alpha\n// beta\n// gamma\n";
        var result = SwiftWrapperPostProcessor.Process(source);
        Assert.Equal(0, result.StrippedBlockCount);

        var intervals = new List<FragmentInterval>
        {
            Interval(source, 0, 9, "a"),
            Interval(source, 9, source.Length, "b"),
        };

        var mapped = WrapperStripRemap.Remap(
            intervals, source, result.CleanedContent, result.CleanedLineSources);

        Assert.NotNull(mapped);
        Assert.Equal(intervals.Count, mapped!.Count);
        for (var i = 0; i < intervals.Count; i++)
        {
            Assert.Equal(intervals[i].Start, mapped[i].Start);
            Assert.Equal(intervals[i].End, mapped[i].End);
            Assert.Equal(intervals[i].Fragment.Text, mapped[i].Fragment.Text);
        }
    }

    /// <summary>
    /// The shape the remap exists for: a fragment in the middle is stripped away entirely, so every
    /// fragment after it sits at a different offset in the bytes swiftc receives.
    /// </summary>
    [Fact]
    public void Remap_AcrossAStrippedBlock_DropsItAndRebasesEverythingAfterIt()
    {
        var internalTypes = new HashSet<string> { "InternalType" };
        const string source =
            "// keep-before\n" +
            "extension EveryProtocol: SomeProtocol {\n" +
            "    var prop: InternalType { fatalError() }\n" +
            "}\n" +
            "// keep-after\n";

        var result = SwiftWrapperPostProcessor.Process(source, internalTypes);
        Assert.Equal(1, result.StrippedBlockCount);

        var strippedStart = source.IndexOf("extension", StringComparison.Ordinal);
        var strippedEnd = source.IndexOf("// keep-after", StringComparison.Ordinal);
        var intervals = new List<FragmentInterval>
        {
            Interval(source, 0, strippedStart, "before"),
            Interval(source, strippedStart, strippedEnd, "stripped"),
            Interval(source, strippedEnd, source.Length, "after"),
        };

        var mapped = WrapperStripRemap.Remap(
            intervals, source, result.CleanedContent, result.CleanedLineSources);

        Assert.NotNull(mapped);
        Assert.DoesNotContain(mapped!, i => i.Fragment.Text.Contains("EveryProtocol", StringComparison.Ordinal));

        // Whatever survived must tile the cleaned bytes exactly, and each fragment's text must be
        // the slice it claims — the same contract a published map is held to.
        AssertTilesExactly(mapped!, result.CleanedContent);
    }

    /// <summary>
    /// End to end against the real strip over a wrapper-shaped source with several stripped blocks:
    /// remapping a complete tiling of the original must yield a complete tiling of the cleaned bytes.
    /// This is the property a published map depends on, so it is asserted over the whole file rather
    /// than a chosen fragment.
    /// </summary>
    [Fact]
    public void Remap_OverAWholeStrippedWrapper_StillTilesTheCleanedBytesExactly()
    {
        var internalTypes = new HashSet<string> { "InternalType" };
        const string source =
            "// header\n" +
            "extension EveryProtocol: A {\n" +
            "    var one: InternalType { fatalError() }\n" +
            "}\n" +
            "// between\n" +
            "extension EveryProtocol: B {\n" +
            "    var two: InternalType { fatalError() }\n" +
            "}\n" +
            "// trailer\n";

        var result = SwiftWrapperPostProcessor.Process(source, internalTypes);
        Assert.Equal(2, result.StrippedBlockCount);

        // A per-line tiling is the harshest input: every stripped line is its own fragment, so any
        // off-by-one in the offset map shows up as a gap rather than being absorbed by a neighbour.
        var intervals = new List<FragmentInterval>();
        var cursor = 0;
        var lineNumber = 0;
        while (cursor < source.Length)
        {
            var newline = source.IndexOf('\n', cursor);
            var end = newline < 0 ? source.Length : newline + 1;
            intervals.Add(Interval(source, cursor, end, $"line{lineNumber++}"));
            cursor = end;
        }

        var mapped = WrapperStripRemap.Remap(
            intervals, source, result.CleanedContent, result.CleanedLineSources);

        Assert.NotNull(mapped);
        AssertTilesExactly(mapped!, result.CleanedContent);
        Assert.Equal(
            new[] { "// header\n", "// between\n", "// trailer\n" },
            mapped!.Select(i => i.Fragment.Text).ToArray());

        // And the remapped intervals must survive publication, which is where the tiling check
        // actually gates in production.
        var set = new ModuleFragmentSet { ModuleName = "Fixture" };
        set.Add("M.Wrapper.swift", result.CleanedContent, mapped);
        Assert.Empty(set.UnmappedFiles);
    }

    [Fact]
    public void Remap_WhenTheProvenanceDoesNotDescribeTheCleanedText_ReturnsNull()
    {
        const string source = "a\nb\nc\n";
        var intervals = new List<FragmentInterval> { Interval(source, 0, source.Length, "whole") };

        // Two cleaned lines claimed, one actually present.
        Assert.Null(WrapperStripRemap.Remap(intervals, source, "a\n", new[] { 0, 1 }));

        // A source line index that does not exist.
        Assert.Null(WrapperStripRemap.Remap(intervals, source, "a\n", new[] { 99 }));
    }

    private static void AssertTilesExactly(IReadOnlyList<FragmentInterval> intervals, string content)
    {
        var expectedStart = 0;
        foreach (var interval in intervals)
        {
            Assert.Equal(expectedStart, interval.Start);
            Assert.Equal(interval.Fragment.Text, content[interval.Start..interval.End]);
            expectedStart = interval.End;
        }
        Assert.Equal(content.Length, expectedStart);
    }

    private static FragmentInterval Interval(string source, int start, int end, string name) =>
        new(
            new OutputFragment
            {
                Owner = FragmentOwners.ForModule(DeclIdFactory.ForModule(name)),
                Plane = OutputPlane.Swift,
                Text = source[start..end],
                IsWholeScope = true,
                Depth = 0,
            },
            start,
            end);
}
