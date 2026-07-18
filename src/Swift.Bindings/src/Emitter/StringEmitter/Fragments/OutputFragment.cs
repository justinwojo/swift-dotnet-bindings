// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;

namespace BindingsGeneration;

/// <summary>
/// The output plane a fragment was written into. One emitted member contributes to both: a C#
/// public surface plus its P/Invoke, and the Swift <c>@_cdecl</c> wrapper the P/Invoke binds. They
/// are separate planes rather than separate files because the C# plane is later repackaged into
/// several files while the Swift plane stays one.
/// </summary>
public enum OutputPlane
{
    /// <summary>Generated C# source.</summary>
    CSharp,

    /// <summary>The generated Swift wrapper source.</summary>
    Swift,
}

/// <summary>
/// Who owns a region of generated output: the artifact it renders and the recovery unit that
/// artifact belongs to. Both are needed — the artifact is what a diagnostic names, the unit is the
/// granularity at which the artifact can actually be withdrawn.
/// </summary>
public readonly record struct FragmentOwner(ArtifactId Artifact, RecoveryUnitId Unit)
{
    /// <inheritdoc />
    public override string ToString() => $"{Artifact.Canonical} @ {Unit.Canonical}";
}

/// <summary>
/// One owned, immutable piece of generated output: who owns it, which plane and file it landed in,
/// and the exact text. Produced only at assembly, from the boundaries recorded while emitting; the
/// text is a slice of the buffer that was actually written, never a re-render.
/// </summary>
/// <remarks>
/// The point of holding text rather than positions is that positions do not survive the passes that
/// run between emission and compilation (namespace qualification rewrites the C# buffer, the
/// file-per-type split reslices it, the wrapper pre-strip deletes blocks from the Swift file). A
/// fragment set can be re-assembled after any of those and the interval map recomputed from the
/// resulting lengths, so the map always describes the exact bytes a compiler was handed.
/// </remarks>
public sealed record OutputFragment
{
    /// <summary>The artifact and recovery unit that own this text.</summary>
    public required FragmentOwner Owner { get; init; }

    /// <summary>Which output plane this text was written into.</summary>
    public required OutputPlane Plane { get; init; }

    /// <summary>The exact generated text.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// True when this fragment is the complete body of a recorded scope, false when it is a slice of
    /// an enclosing scope that lies between (or around) that scope's children — a type's declaration
    /// header, the blank line a member dispatch loop writes after each member, the namespace close.
    /// </summary>
    /// <remarks>
    /// Interstitial text still has a real owner (the innermost scope that was open when it was
    /// written), which is why it is a fragment and not a gap. Distinguishing the two matters for
    /// withdrawal: dropping a whole-scope fragment removes an artifact, while dropping an
    /// interstitial one would remove punctuation the surrounding scope still needs.
    /// </remarks>
    public required bool IsWholeScope { get; init; }

    /// <summary>Nesting depth of the owning scope; 0 is the module root.</summary>
    public required int Depth { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Plane} {Owner.Artifact.Canonical} ({Text.Length} chars{(IsWholeScope ? "" : ", interstitial")})";
}

/// <summary>
/// A half-open character interval in one assembled file, and the fragment that produced it.
/// </summary>
/// <remarks>
/// The unit is UTF-16 characters, because that is what the emitter's <see cref="System.Text.StringBuilder"/>
/// buffers count in and therefore the only unit an offset recorded during emission can mean. It is
/// deliberately not the unit either compiler reports in — swiftc columns are UTF-8 byte counts — so a
/// caller holding a compiler position must convert rather than index straight in; see
/// <see cref="FileIntervalMap.TryResolveUtf8Column"/>. Every offset in this subsystem is UTF-16 unless
/// its name says otherwise.
/// </remarks>
public readonly record struct FragmentInterval(OutputFragment Fragment, int Start, int End)
{
    /// <summary>Number of characters covered.</summary>
    public int Length => End - Start;

    /// <summary>True when <paramref name="offset"/> falls inside this interval.</summary>
    public bool Contains(int offset) => offset >= Start && offset < End;
}
