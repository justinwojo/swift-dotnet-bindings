// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Marker for an <see cref="ISwiftObject"/> wrapper whose from-handle constructor
/// (<c>NewFromPayload</c>) allocates its own buffer and fills it with a <b>raw bitwise copy</b> of
/// the source handle's bytes rather than a value-witness <c>InitializeWithCopy</c> — i.e. it
/// <i>moves</i> the source's reference(s) into its buffer instead of taking an independent
/// value-witness <c>+1</c>. <see cref="SwiftString"/> is the sole such type (its
/// <c>SwiftString.Buffer</c> is bitwise-copied, transferring the bridge-object ownership).
/// <para>
/// This distinguishes the MOVE-bitwise shape from the COPY shape used by frozen-projected-as-class
/// structs and <c>SwiftArray</c>/<c>SwiftDictionary</c>/<c>SwiftSet</c> (which DO run
/// <c>InitializeWithCopy</c> and so take their own <c>+1</c>). Payload extraction
/// (<c>SwiftMarshal.MarshalExtractedPayloadValue</c>) needs the distinction: when its temporary
/// extraction buffer is not adopted by the wrapper, a COPY wrapper leaves the temporary's <c>+1</c>
/// orphaned (so the temporary must be value-witness-<c>Destroy</c>ed), whereas a MOVE wrapper has
/// already transferred that <c>+1</c> into its own buffer (so destroying the temporary would
/// over-release). Generated bindings never emit this shape — non-frozen structs and complex enums
/// adopt the handle directly, frozen structs copy — so this marker is internal to the runtime.
/// </para>
/// </summary>
internal interface ISwiftMovesPayloadOnConstruction
{
}
