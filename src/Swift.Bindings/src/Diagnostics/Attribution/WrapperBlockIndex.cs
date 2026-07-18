// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Text.RegularExpressions;

using BindingsGeneration;

namespace BindingsGeneration.Diagnostics;

/// <summary>
/// One strippable wrapper block, identified by the symbol it carries or the origin anchor that
/// names its owner, spanning a half-open-inclusive line range of the compiled source.
/// </summary>
/// <remarks>
/// A block is identified two ways because two kinds of block exist. Most carry a
/// <c>@_cdecl</c>/<c>@_silgen_name</c> <see cref="Symbol"/> — a globally unique per-member key the
/// wrapper-symbol registry already maps to an artifact. The rest are symbol-less strippable
/// scaffolding (an <c>extension</c> header, a dispatch-protocol decl, a shared-helper bundle) that
/// instead carries a <c>// SBW-ORIGIN: &lt;ArtifactId&gt;</c> <see cref="OriginAnchor"/> emitted at
/// its head. Either way the block names its owner, so a diagnostic landing inside it attributes to
/// a declaration rather than to whatever member happened to be emitted nearby.
/// </remarks>
public readonly record struct WrapperBlock(string? Symbol, string? OriginAnchor, int StartLine, int EndLine)
{
    /// <summary>Number of source lines the block spans.</summary>
    public int LineSpan => EndLine - StartLine + 1;

    /// <summary>True when <paramref name="line"/> (1-based) falls inside the block.</summary>
    public bool Contains(int line) => line >= StartLine && line <= EndLine;
}

/// <summary>
/// An index over the exact bytes a wrapper compile was handed, mapping a diagnostic line to the
/// innermost strippable block that owns it.
/// </summary>
/// <remarks>
/// <para>
/// This is the symbol/anchor half of attribution — the priority-2 and priority-3 mechanisms — and
/// it works directly off the compiled source rather than any stored map, so it is immune to the
/// line drift that invalidates a persisted span table. Blocks nest (a symbol-bearing function
/// inside a symbol-less extension), so resolution returns the <em>smallest</em> block containing a
/// line: an error on a function's signature attributes to that function, not to the extension that
/// encloses it.
/// </para>
/// <para>
/// The brace matching reuses <see cref="StructuralBraceScanner"/>, the same literal/comment-aware
/// scanner the post-processor's block surgery uses, so the block boundaries this index computes are
/// exactly the boundaries the strip machinery would compute for the same text.
/// </para>
/// </remarks>
public sealed class WrapperBlockIndex
{
    private static readonly Regex SymbolAttribute = new(
        @"@_(?:cdecl|silgen_name)\(\s*""([^""]+)""\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OriginAnchorComment = new(
        @"//\s*SBW-ORIGIN:\s*(\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReadOnlyList<WrapperBlock> _blocks;

    private WrapperBlockIndex(IReadOnlyList<WrapperBlock> blocks) => _blocks = blocks;

    /// <summary>Every indexed block, in source order.</summary>
    public IReadOnlyList<WrapperBlock> Blocks => _blocks;

    /// <summary>Builds an index from the compiled wrapper source text.</summary>
    public static WrapperBlockIndex Build(string source)
    {
        var lines = (source ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        var blocks = new List<WrapperBlock>();

        for (int i = 0; i < lines.Length; i++)
        {
            var symbolMatch = SymbolAttribute.Match(lines[i]);
            if (symbolMatch.Success)
            {
                var end = FindBlockEnd(lines, i);
                blocks.Add(new WrapperBlock(symbolMatch.Groups[1].Value, null, i + 1, end + 1));
                continue;
            }

            var anchorMatch = OriginAnchorComment.Match(lines[i]);
            if (anchorMatch.Success)
            {
                var end = FindBlockEnd(lines, i);
                blocks.Add(new WrapperBlock(null, anchorMatch.Groups[1].Value, i + 1, end + 1));
            }
        }

        return new WrapperBlockIndex(blocks);
    }

    /// <summary>
    /// Resolves a 1-based <paramref name="line"/> to the innermost block that contains it. Returns
    /// false when the line falls in no indexed block (interstitial prelude, an un-anchored header).
    /// </summary>
    public bool TryResolve(int line, out WrapperBlock block)
    {
        block = default;
        var best = -1;
        for (int b = 0; b < _blocks.Count; b++)
        {
            if (!_blocks[b].Contains(line))
                continue;
            if (best < 0 || _blocks[b].LineSpan < _blocks[best].LineSpan)
                best = b;
        }

        if (best < 0)
            return false;
        block = _blocks[best];
        return true;
    }

    // Scans forward from a block head to the line that closes it, honoring Swift string/comment
    // rules via the shared structural scanner. Mirrors SwiftWrapperPostProcessor.FindBlockEnd: the
    // first line whose running structural depth returns to zero after an open brace was seen ends
    // the block. Returns the last line index when no matching close is found.
    private static int FindBlockEnd(IReadOnlyList<string> lines, int start)
    {
        int depth = 0;
        bool sawOpen = false;
        int blockCommentDepth = 0;
        for (int j = start; j < lines.Count; j++)
        {
            depth += StructuralBraceScanner.NetLineDelta(lines[j], ref blockCommentDepth, ref sawOpen);
            if (sawOpen && depth <= 0 && j > start)
                return j;
        }

        return lines.Count - 1;
    }
}
