// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

using BindingsGeneration;

namespace BindingsGeneration.Diagnostics;

/// <summary>
/// Priority 1: resolve a positioned diagnostic through the per-render interval map over immutable
/// fragments. This is the authoritative mechanism — the fragment owner carries both the artifact
/// and the recovery unit, and the map describes the exact bytes compiled — so it is tried first
/// whenever a fragment set is in hand.
/// </summary>
/// <remarks>
/// The map must be the one for the source version that actually reached swiftc. When the
/// pre-compile wrapper strip ran, that is the post-strip render's map (its intervals were recomputed
/// against the cleaned bytes); a caller passing a stale pre-strip map would resolve columns against
/// text swiftc never saw. This step trusts the set it is given and does not re-derive that
/// invariant — it is established where the set is built.
/// </remarks>
public sealed class IntervalMapProvenanceStep : IProvenanceStep
{
    private readonly ModuleFragmentSet _fragments;

    /// <summary>Wraps the fragment set for the compiled module.</summary>
    public IntervalMapProvenanceStep(ModuleFragmentSet fragments) =>
        _fragments = fragments ?? throw new ArgumentNullException(nameof(fragments));

    /// <inheritdoc />
    public bool TryResolve(CompilerDiagnostic diagnostic, out ProvenanceHit hit)
    {
        hit = default;
        if (!diagnostic.HasPosition || string.IsNullOrEmpty(diagnostic.File))
            return false;

        var leaf = Path.GetFileName(diagnostic.File);
        if (!_fragments.Files.TryGetValue(leaf, out var map))
            return false;

        if (!map.TryResolveUtf8Column(diagnostic.Line, diagnostic.Column, out var fragment))
            return false;

        // The wrapper compile only produces Swift-plane diagnostics; a C#-plane hit here would mean
        // the wrong file matched by leaf name, so it is not a wrapper attribution.
        if (fragment.Plane != OutputPlane.Swift)
            return false;

        hit = new ProvenanceHit(fragment.Owner.Artifact, fragment.Owner.Unit, ProvenanceSource.IntervalMap);
        return true;
    }
}

/// <summary>
/// The C#-plane counterpart of <see cref="IntervalMapProvenanceStep"/>: resolves a positioned C#
/// compiler diagnostic (from the MSBuild+SARIF verifier) through the same per-render interval map,
/// selecting the C# plane. This is the tree-based attribution the C# verify-recover loop needs — the
/// interval map is the emitted syntax tiling, so a diagnostic's line/column lands on the exact
/// member fragment, and the fragment owner carries the recovery unit that member belongs to (the
/// same union-compatible id the Swift loop withdraws, by <see cref="RecoveryUnitClassifier"/>
/// role-collapse).
/// </summary>
/// <remarks>
/// Two differences from the Swift step, both load-bearing. The column is counted in UTF-16
/// characters — Roslyn/SARIF report character columns, not swiftc's UTF-8 byte columns — so this
/// resolves through <see cref="FileIntervalMap.TryResolve(int,int,out OutputFragment)"/>, never
/// <c>TryResolveUtf8Column</c>. And a hit whose fragment is on the Swift plane is rejected: a C#
/// diagnostic can only be about C#, so a Swift-plane match means the wrong file matched by leaf name.
/// The map used here is the current render's <c>_context.FragmentSet</c>, which describes the exact
/// bytes on disk that MSBuild compiled — no post-publish rewrite intervenes on the loop path.
/// </remarks>
public sealed class CSharpIntervalMapProvenanceStep : IProvenanceStep
{
    private readonly ModuleFragmentSet _fragments;

    /// <summary>Wraps the fragment set for the compiled module.</summary>
    public CSharpIntervalMapProvenanceStep(ModuleFragmentSet fragments) =>
        _fragments = fragments ?? throw new ArgumentNullException(nameof(fragments));

    /// <inheritdoc />
    public bool TryResolve(CompilerDiagnostic diagnostic, out ProvenanceHit hit)
    {
        hit = default;
        if (!diagnostic.HasPosition || string.IsNullOrEmpty(diagnostic.File))
            return false;

        var leaf = Path.GetFileName(diagnostic.File);
        if (!_fragments.Files.TryGetValue(leaf, out var map))
            return false;

        if (!map.TryResolve(diagnostic.Line, diagnostic.Column, out var fragment))
            return false;

        if (fragment.Plane != OutputPlane.CSharp)
            return false;

        hit = new ProvenanceHit(fragment.Owner.Artifact, fragment.Owner.Unit, ProvenanceSource.IntervalMap);
        return true;
    }
}

/// <summary>
/// Priority 2/3: resolve a positioned diagnostic to the enclosing wrapper block, then to its owner
/// via the block's <c>@_cdecl</c>/<c>@_silgen_name</c> symbol (registry lookup) or its
/// <c>// SBW-ORIGIN:</c> anchor (a serialized <see cref="ArtifactId"/>).
/// </summary>
/// <remarks>
/// This is the fallback that needs no stored map — it reads ownership straight out of the compiled
/// bytes — and it is what makes attribution robust to the passes that rewrite positions. The symbol
/// path resolves through a caller-supplied registry (symbol → artifact) because symbol promotion
/// details live on a side table the block text does not carry; the anchor path is self-describing
/// and parses directly. Both then map artifact → unit through the caller's resolver, which in the
/// live loop reads the recovery graph.
/// </remarks>
public sealed class SymbolAnchorProvenanceStep : IProvenanceStep
{
    private readonly WrapperBlockIndex _index;
    private readonly Func<string, ArtifactId?> _symbolLookup;
    private readonly Func<ArtifactId, RecoveryUnitId?> _unitLookup;

    /// <summary>Wraps the block index and the symbol/unit resolvers.</summary>
    public SymbolAnchorProvenanceStep(
        WrapperBlockIndex index,
        Func<string, ArtifactId?> symbolLookup,
        Func<ArtifactId, RecoveryUnitId?> unitLookup)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _symbolLookup = symbolLookup ?? throw new ArgumentNullException(nameof(symbolLookup));
        _unitLookup = unitLookup ?? throw new ArgumentNullException(nameof(unitLookup));
    }

    /// <inheritdoc />
    public bool TryResolve(CompilerDiagnostic diagnostic, out ProvenanceHit hit)
    {
        hit = default;
        if (!diagnostic.HasPosition)
            return false;

        // Walk containing blocks innermost-first. The innermost block owns the line when it resolves,
        // but a nested @_silgen_name whose promoted symbol isn't in the registry must NOT sink the
        // whole attribution to coarse scope — fall back to the enclosing anchored extension header,
        // which self-describes its owner. Stop at the first block that names a resolvable unit.
        foreach (var block in _index.ResolveChain(diagnostic.Line))
        {
            ArtifactId? artifact = null;
            var source = ProvenanceSource.None;

            if (!string.IsNullOrEmpty(block.Symbol))
            {
                artifact = _symbolLookup(block.Symbol!);
                source = ProvenanceSource.SymbolAnchor;
            }
            else if (!string.IsNullOrEmpty(block.OriginAnchor) && ArtifactId.TryParse(block.OriginAnchor, out var parsed))
            {
                artifact = parsed;
                source = ProvenanceSource.OriginAnchor;
            }

            if (artifact is not { } resolvedArtifact)
                continue;

            if (_unitLookup(resolvedArtifact) is not { } unit)
                continue;

            hit = new ProvenanceHit(resolvedArtifact, unit, source);
            return true;
        }

        return false;
    }
}

/// <summary>
/// Priority 4: resolve a positionless linker diagnostic by matching the wrapper symbol(s) it names
/// against the registry. Undefined-symbol errors carry no source location, so the referenced symbol
/// is the only handle on the owning artifact.
/// </summary>
public sealed class LinkerSymbolProvenanceStep : IProvenanceStep
{
    // Wrapper-symbol shapes the generator emits (bare or with the linker's leading underscore), plus
    // mangled Swift symbols the reverse-interop plane declares via @_silgen_name.
    private static readonly Regex SymbolToken = new(
        @"_?(?:SBW_|SBSW_|DBW_|DBSW_)[A-Za-z0-9_]+|\$s[A-Za-z0-9_]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Func<string, ArtifactId?> _symbolLookup;
    private readonly Func<ArtifactId, RecoveryUnitId?> _unitLookup;

    /// <summary>Wraps the symbol/unit resolvers.</summary>
    public LinkerSymbolProvenanceStep(
        Func<string, ArtifactId?> symbolLookup,
        Func<ArtifactId, RecoveryUnitId?> unitLookup)
    {
        _symbolLookup = symbolLookup ?? throw new ArgumentNullException(nameof(symbolLookup));
        _unitLookup = unitLookup ?? throw new ArgumentNullException(nameof(unitLookup));
    }

    /// <inheritdoc />
    public bool TryResolve(CompilerDiagnostic diagnostic, out ProvenanceHit hit)
    {
        hit = default;
        if (diagnostic.HasPosition)
            return false;

        foreach (Match match in SymbolToken.Matches(diagnostic.Message))
        {
            foreach (var candidate in Candidates(match.Value))
            {
                if (_symbolLookup(candidate) is not { } artifact)
                    continue;
                if (_unitLookup(artifact) is not { } unit)
                    continue;
                hit = new ProvenanceHit(artifact, unit, ProvenanceSource.LinkerSymbol);
                return true;
            }
        }

        return false;
    }

    // The linker mangles a C symbol with a leading underscore ("_SBW_x"); the registry keys the bare
    // exported name ("SBW_x"). Try the token as-is and with one leading underscore removed.
    private static IEnumerable<string> Candidates(string token)
    {
        yield return token;
        if (token.StartsWith("_", StringComparison.Ordinal))
            yield return token.Substring(1);
    }
}
