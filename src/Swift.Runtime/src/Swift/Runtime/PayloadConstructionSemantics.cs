// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Declares how an <see cref="ISwiftObject"/> type's <c>NewFromPayload</c> takes ownership of the
/// wire buffer it is constructed from. This is the single declared source of truth the marshal seam
/// reads to balance Swift ARC and free the temporary correctly, replacing the former post-hoc
/// ownership detection (comparing the constructed wrapper's <c>SwiftHandle</c> to the temporary
/// buffer address, plus probing a marker interface) that was re-implemented at four extraction sites.
/// </summary>
public enum PayloadConstructionSemantics
{
    /// <summary>
    /// <c>NewFromPayload</c> wraps the wire handle directly: the wrapper's <see
    /// cref="System.Runtime.InteropServices.SafeHandle"/> adopts the temporary buffer and its
    /// <c>+1</c>. Cleanup leaves the temporary alone (the wrapper owns it). Non-frozen structs,
    /// complex (payload) enums, the bare-<see cref="ISwiftObject"/> SwiftUI value wrappers, and the
    /// hand-written <c>Hasher</c>/<c>DispatchQueue</c> proxies.
    /// </summary>
    Adopt,

    /// <summary>
    /// <c>NewFromPayload</c> allocates its own buffer and <c>InitializeWithCopy</c>s into it, taking a
    /// fresh <c>+1</c>. The temporary's <c>+1</c> is orphaned — cleanup value-witness <c>Destroy</c>s
    /// the temporary, then frees the dead buffer. Frozen-projected-as-class (ref-bearing) structs and
    /// <c>SwiftArray</c>/<c>SwiftDictionary</c>/<c>SwiftSet</c>/<c>SwiftResult</c>/<c>SwiftOptional</c>/
    /// <c>SwiftClosedRange</c>.
    /// </summary>
    Copy,

    /// <summary>
    /// <c>NewFromPayload</c> allocates its own buffer and <b>bitwise</b>-copies the source, transferring
    /// the temporary's <c>+1</c> into the wrapper without taking a new one. Cleanup only frees the dead
    /// buffer (no <c>Destroy</c> — that would over-release the now-shared reference). <c>SwiftString</c>
    /// only (the bitwise-move-on-construction shape a dedicated marker interface once flagged).
    /// </summary>
    Move,

    /// <summary>
    /// <c>NewFromPayload</c> reads the value by value (<c>*(T*)handle</c>); there is no SafeHandle and no
    /// buffer ownership. Cleanup frees the temporary and never touches <c>SwiftHandle</c> (which is the
    /// throwing default for these types). Frozen blittable value-type <see cref="ISwiftObject"/> structs
    /// (e.g. <c>LargeValueStruct</c>, <c>FrozenPoint</c>, <c>ExtractionPodPoint</c>) and the value-type
    /// proxies <c>AnyHashable</c>/<c>AnyType</c>. Non-<see cref="ISwiftObject"/> payloads
    /// (primitives, tuples, existential containers) are read by value the same way.
    /// </summary>
    Inline,
}
