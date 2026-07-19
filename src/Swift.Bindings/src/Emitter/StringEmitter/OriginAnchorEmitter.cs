// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;

namespace BindingsGeneration;

/// <summary>
/// Emits the <c>// SBW-ORIGIN: &lt;ArtifactId&gt;</c> provenance anchor that names the owner of a
/// symbol-less strippable wrapper block.
/// </summary>
/// <remarks>
/// <para>
/// Most wrapper blocks carry a <c>@_cdecl</c>/<c>@_silgen_name</c> symbol the wrapper-symbol
/// registry already maps to an artifact, so a diagnostic inside them attributes for free. The rest
/// is symbol-less scaffolding — a dispatch-protocol decl, its conformance extension, an
/// opaque-return extension, an <c>EveryProtocol</c> conformance — whose head line carries no symbol
/// at all. Without a marker a diagnostic landing in one of those blocks resolves only to the
/// coarse module scope (the fragment tiling's root for the Swift plane), which fails the whole
/// binding closed even when the fault is a single recoverable member. The anchor gives the block
/// an explicit, member-scoped identity that the verify-recover loop can withdraw as a leaf.
/// </para>
/// <para>
/// The anchor is emitted on its own comment line immediately ahead of the block head so
/// <see cref="Diagnostics.WrapperBlockIndex"/> — which scans a block's brace range forward from the
/// anchor line — spans exactly the block the anchor names. The identity is taken from
/// <see cref="FragmentOwners"/> so the anchor, the fragment interval map, and the recovery graph
/// cannot disagree about who owns the block.
/// </para>
/// </remarks>
public static class OriginAnchorEmitter
{
    /// <summary>The literal comment prefix an anchor line always starts with.</summary>
    public const string Prefix = "// SBW-ORIGIN: ";

    /// <summary>The anchor comment text for an already-resolved artifact identity.</summary>
    public static string Line(ArtifactId origin) => Prefix + origin.Canonical;

    /// <summary>
    /// The anchor comment text for the Swift wrapper a declaration emits — the same identity the
    /// fragment recorder brackets that wrapper's output with.
    /// </summary>
    public static string LineForWrapper(BaseDecl owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return Line(FragmentOwners.ForDeclWrapper(owner).Artifact);
    }

    /// <summary>Writes an anchor line for <paramref name="origin"/> at the writer's current indent.</summary>
    public static void Write(SwiftWriter writer, ArtifactId origin)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine(Line(origin));
    }

    /// <summary>
    /// Writes an anchor line naming the Swift wrapper of <paramref name="owner"/> at the writer's
    /// current indent.
    /// </summary>
    public static void WriteForWrapper(SwiftWriter writer, BaseDecl owner)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(owner);
        Write(writer, FragmentOwners.ForDeclWrapper(owner).Artifact);
    }
}
