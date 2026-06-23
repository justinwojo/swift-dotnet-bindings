// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Single source of the <c>ISwiftObject.SuppressPayloadFinalizer</c> override line that every
/// ISwiftObject-emitting handler writes — class wrappers (<see cref="ClassHandler"/>), frozen and
/// non-frozen struct wrappers, enum wrappers, and the Apple value-type manifest emitter.
/// <para>Borrowed (+0) marshalling calls <c>SuppressPayloadFinalizer</c> to hand a wrapper's
/// finalization back to native ownership, so the override MUST target the type's actual ARC/handle
/// backing field: <c>_handle</c> for class wrappers (a <c>SwiftClassHandle</c>), <c>_payload</c> for
/// value-type wrappers (a <c>SwiftSafeHandle</c>). Centralizing the line keeps the five emitters from
/// drifting on the field name and FAILS CLOSED on any other field — so a future handle-field rename
/// surfaces here as a generation-time error rather than as a reference to a field the emitted type
/// never declares (a compile break in consumer output) or, worse, a <c>SuppressFinalize</c> on the
/// wrong field that leaves the real payload's native finalizer un-suppressed (double VWT-destroy).</para>
/// </summary>
internal static class FinalizerSeamEmitter
{
    /// <summary>The ARC/handle backing fields an ISwiftObject wrapper may own.</summary>
    private static readonly string[] KnownPayloadFields = { "_handle", "_payload" };

    /// <summary>
    /// Builds the <c>void ISwiftObject.SuppressPayloadFinalizer() =&gt; {GC}.SuppressFinalize({field});</c>
    /// line, byte-for-byte identical to what the handlers emitted inline before centralization.
    /// <paramref name="qualifyGc"/> selects the fully-qualified <c>global::System.GC</c> form used by
    /// the Apple value-type manifest emitter, which writes outside a <c>using System;</c> context.
    /// Throws when <paramref name="payloadFieldName"/> is not a recognized seam field.
    /// </summary>
    internal static string SuppressPayloadFinalizerLine(string payloadFieldName, bool qualifyGc = false)
    {
        if (System.Array.IndexOf(KnownPayloadFields, payloadFieldName) < 0)
        {
            throw new System.InvalidOperationException(
                $"SWIFTBIND048: ISwiftObject.SuppressPayloadFinalizer must target a known ARC/handle backing "
                + "field ('_handle' for class wrappers, '_payload' for value-type wrappers), but got "
                + $"'{payloadFieldName ?? "<null>"}'. Emitting SuppressFinalize on an unrecognized field would "
                + "either reference a field the emitted type never declares or suppress the wrong finalizer "
                + "(double VWT-destroy on the real payload).");
        }
        var gc = qualifyGc ? "global::System.GC" : "GC";
        return $"void ISwiftObject.SuppressPayloadFinalizer() => {gc}.SuppressFinalize({payloadFieldName});";
    }
}
