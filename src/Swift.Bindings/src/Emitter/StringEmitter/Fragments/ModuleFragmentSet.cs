// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BindingsGeneration;

/// <summary>
/// The interval map for one rendered file: which artifact produced each character range, plus
/// line-start offsets so a compiler diagnostic's line/column can be resolved without re-scanning.
/// </summary>
/// <remarks>
/// Rebuilt from the fragments on every render and never carried across one. A map that outlived the
/// text it describes is exactly the drift this replaces.
/// </remarks>
public sealed class FileIntervalMap
{
    private readonly IReadOnlyList<FragmentInterval> _intervals;
    private readonly int[] _lineStarts;
    private readonly string _content;

    internal FileIntervalMap(string fileName, string content, IReadOnlyList<FragmentInterval> intervals)
    {
        FileName = fileName;
        _content = content;
        _intervals = intervals;
        _lineStarts = BuildLineStarts(content);
    }

    /// <summary>The file leaf name this map describes.</summary>
    public string FileName { get; }

    /// <summary>Character length of the rendered file.</summary>
    public int Length => _content.Length;

    /// <summary>Number of lines in the rendered file.</summary>
    public int LineCount => _lineStarts.Length;

    /// <summary>Every interval, in file order.</summary>
    public IReadOnlyList<FragmentInterval> Intervals => _intervals;

    /// <summary>Resolves the fragment covering <paramref name="offset"/>.</summary>
    public bool TryResolve(int offset, out OutputFragment fragment)
    {
        fragment = null!;
        if (offset < 0 || offset >= Length || _intervals.Count == 0)
            return false;

        // Intervals tile the file in order, so a binary search on Start lands on the owner.
        var lo = 0;
        var hi = _intervals.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var candidate = _intervals[mid];
            if (offset < candidate.Start)
                hi = mid - 1;
            else if (offset >= candidate.End)
                lo = mid + 1;
            else
            {
                fragment = candidate.Fragment;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Resolves the fragment at a compiler-reported line and column, both 1-based, with the column
    /// counted in UTF-16 characters.
    /// </summary>
    /// <remarks>
    /// A column past the end of its line clamps to the line's last character rather than failing: a
    /// diagnostic pointing just past a token is still about that token's line, and refusing to
    /// resolve it would drop the attribution entirely.
    /// </remarks>
    public bool TryResolve(int line, int column, out OutputFragment fragment)
    {
        fragment = null!;
        if (line < 1 || line > _lineStarts.Length)
            return false;

        var lineStart = _lineStarts[line - 1];
        var lineEnd = line < _lineStarts.Length ? _lineStarts[line] : Length;
        var offset = lineStart + Math.Max(0, column - 1);
        if (offset >= lineEnd)
            offset = Math.Max(lineStart, lineEnd - 1);

        return TryResolve(offset, out fragment);
    }

    /// <summary>
    /// Resolves the fragment at a compiler-reported line and a column counted in UTF-8 <em>bytes</em>,
    /// which is what swiftc reports.
    /// </summary>
    /// <remarks>
    /// The two units agree only while a line is pure ASCII. Generated source is mostly ASCII, so
    /// treating a byte column as a character column looks correct almost always and then silently
    /// mis-resolves on the lines that carry a non-ASCII identifier or a doc comment lifted from Swift
    /// — precisely the lines a diagnostic is most likely to land on. Converting explicitly costs one
    /// scan of the line and removes the class of error.
    /// </remarks>
    public bool TryResolveUtf8Column(int line, int utf8Column, out OutputFragment fragment)
    {
        fragment = null!;
        if (line < 1 || line > _lineStarts.Length)
            return false;

        var lineStart = _lineStarts[line - 1];
        var lineEnd = line < _lineStarts.Length ? _lineStarts[line] : Length;

        var remainingBytes = Math.Max(0, utf8Column - 1);
        var offset = lineStart;
        while (offset < lineEnd && remainingBytes > 0)
        {
            var runeLength = char.IsHighSurrogate(_content[offset]) && offset + 1 < lineEnd ? 2 : 1;
            var byteLength = Encoding.UTF8.GetByteCount(_content.AsSpan(offset, runeLength));
            if (byteLength > remainingBytes)
                break;
            remainingBytes -= byteLength;
            offset += runeLength;
        }

        if (offset >= lineEnd)
            offset = Math.Max(lineStart, lineEnd - 1);

        return TryResolve(offset, out fragment);
    }

    private static int[] BuildLineStarts(string content)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
                starts.Add(i + 1);
        }
        return starts.ToArray();
    }
}

/// <summary>
/// Everything one render produced: the interval map for each rendered file, keyed by leaf name.
/// </summary>
/// <remarks>
/// Deliberately per-render state — built as each file reaches its final form, discarded when the next
/// render starts — so a diagnostic can only ever be interpreted against the map for the source
/// version that produced it.
/// </remarks>
public sealed class ModuleFragmentSet
{
    private readonly Dictionary<string, FileIntervalMap> _maps = new(StringComparer.Ordinal);
    private readonly List<string> _unmappedFiles = new();

    /// <summary>The module this render belongs to.</summary>
    public required string ModuleName { get; init; }

    /// <summary>
    /// Files whose map could not be established exactly. A file lands here rather than carrying an
    /// approximate map: an attribution that is merely probably right is worse than none, because
    /// nothing downstream can tell the two apart.
    /// </summary>
    public IReadOnlyList<string> UnmappedFiles => _unmappedFiles;

    /// <summary>Interval maps by file leaf name.</summary>
    public IReadOnlyDictionary<string, FileIntervalMap> Files => _maps;

    /// <summary>Every fragment in every mapped file, in file-name then file order.</summary>
    public IEnumerable<OutputFragment> AllFragments =>
        _maps.OrderBy(kv => kv.Key, StringComparer.Ordinal)
             .SelectMany(kv => kv.Value.Intervals.Select(i => i.Fragment));

    /// <summary>
    /// Records the map for a rendered file, verifying first that the intervals tile the text exactly.
    /// A map that does not is recorded as unmapped and dropped.
    /// </summary>
    public void Add(string fileName, string content, IReadOnlyList<FragmentInterval>? intervals)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(content);

        if (intervals == null || !FragmentAssembly.IsExactTiling(intervals, content))
        {
            // Drop any map already held for this name. A later pass re-adding a file it could not
            // map exactly is replacing that file's text, so a map recorded for the earlier version
            // now describes bytes that no longer exist — keeping it would leave the set publishing
            // an attribution that is not merely coarse but wrong.
            _maps.Remove(fileName);
            if (!_unmappedFiles.Contains(fileName))
                _unmappedFiles.Add(fileName);
            return;
        }
        _maps[fileName] = new FileIntervalMap(fileName, content, intervals);
        _unmappedFiles.Remove(fileName);
    }

    /// <summary>Resolves a diagnostic position in a known file to the artifact that produced it.</summary>
    public bool TryResolve(string fileName, int line, int column, out OutputFragment fragment)
    {
        fragment = null!;
        return _maps.TryGetValue(fileName, out var map) && map.TryResolve(line, column, out fragment);
    }

    /// <summary>
    /// The distinct artifacts that own at least one complete-scope fragment. Interstitial text is
    /// excluded: a type's declaration header carries its owner's id but is not the owner's artifact,
    /// and counting it would claim an artifact was emitted when only its container's punctuation was.
    /// </summary>
    public IReadOnlyCollection<ArtifactId> EmittedArtifacts =>
        AllFragments.Where(f => f.IsWholeScope).Select(f => f.Owner.Artifact).Distinct().ToList();
}
