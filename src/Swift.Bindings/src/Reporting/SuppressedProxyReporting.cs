// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The one place that records a <b>degraded EveryProtocol reverse-dispatch member</b> into the persisted
/// binding report. When a protocol's <c>{Protocol}Proxy</c> conformance cannot be synthesized, the
/// generator keeps the surrounding member but degrades it — a PRODUCE getter/return emits a throwing
/// stub, a CONSUME setter/parameter drops its C#-conformer wrap fallback, or a reverse-dispatch receiver
/// fail-fasts. Historically each of those outcomes was silent in the persisted report (a throwing stub, a
/// dropped lambda, or a build-only SWIFTBIND061 warning) even though the proxy <em>class</em> itself was
/// recorded once as <see cref="SkipReason.EveryProtocolConformanceSkipped"/>. This helper is the single
/// classification point for the per-member decline, so every emission boundary records the identical
/// <see cref="SkipReason.SuppressedProxyMemberDegraded"/> with a site-stamped, greppable
/// <see cref="SkippedItem.Details"/> — a classified diagnostic, not a log line.
/// Reader: <see cref="SkipDispositionClassifier"/> buckets it as <see cref="SkipDisposition.KnownLimitation"/>.
/// </summary>
public static class SuppressedProxyReporting
{
    /// <summary>The three boundaries at which a proxy-backed member degrades. See <see cref="Details"/>.</summary>
    public enum Site
    {
        /// <summary>A getter/return that could only construct the missing proxy now emits a throwing stub.</summary>
        ProduceThrow,

        /// <summary>
        /// A setter/parameter keeps round-tripping Swift-vended conformers, but a C#-authored conformer
        /// cannot be marshalled in (no proxy to wrap it) — so a callback/delegate set from C# never fires.
        /// </summary>
        ConsumeDegraded,

        /// <summary>
        /// A reverse-dispatch receiver fail-fasts because its existential payload references the missing
        /// proxy; the vtable slot is retained so <c>VtableLayout</c> stays byte-aligned with Swift.
        /// </summary>
        ReceiverFailFast,
    }

    /// <summary>The stable, machine-greppable site token stamped into <see cref="SkippedItem.Details"/>.</summary>
    public static string Token(Site site) => site switch
    {
        Site.ProduceThrow => "produce-throw",
        Site.ConsumeDegraded => "consume-degraded",
        Site.ReceiverFailFast => "receiver-failfast",
        _ => "unknown",
    };

    /// <summary>Builds the classified <see cref="SkippedItem.Details"/> for a degraded reverse-dispatch member.</summary>
    public static string Details(Site site, string? proxyOrProtocol)
    {
        var subject = string.IsNullOrEmpty(proxyOrProtocol) ? "a suppressed protocol proxy" : proxyOrProtocol;
        return site switch
        {
            Site.ProduceThrow =>
                $"Reverse-dispatch member degraded ({Token(site)}): the getter/return constructs {subject}, "
                + "whose EveryProtocol conformance was not emitted, so it emits a throwing stub.",
            Site.ConsumeDegraded =>
                $"Reverse-dispatch member degraded ({Token(site)}): the setter/parameter round-trips Swift-vended "
                + $"conformers of {subject}, but a C#-authored conformer cannot be marshalled in (proxy not emitted).",
            Site.ReceiverFailFast =>
                $"Reverse-dispatch member degraded ({Token(site)}): the reverse-dispatch receiver for {subject} "
                + "fail-fasts because the proxy was not emitted; the vtable slot is retained for layout parity.",
            _ => $"Reverse-dispatch member degraded: {subject}.",
        };
    }

    /// <summary>Records a degraded proxy-backed method (or property/subscript accessor method).</summary>
    public static void Record(MethodDecl method, Site site, string? proxyOrProtocol)
        => ReportCollector.RecordMemberSkipped(
            method, SkipReason.SuppressedProxyMemberDegraded, Details(site, proxyOrProtocol));

    /// <summary>Records a degraded proxy-backed property. Pass the affected accessor for get+set granularity.</summary>
    public static void Record(PropertyDecl property, Site site, string? proxyOrProtocol,
        AccessorKind accessor = AccessorKind.None)
        => ReportCollector.RecordMemberSkipped(
            property, SkipReason.SuppressedProxyMemberDegraded, Details(site, proxyOrProtocol), accessor);

    /// <summary>Records a degraded proxy-backed subscript.</summary>
    public static void Record(SubscriptDecl subscriptDecl, Site site, string? proxyOrProtocol,
        AccessorKind accessor = AccessorKind.None)
        => ReportCollector.RecordMemberSkipped(
            subscriptDecl, SkipReason.SuppressedProxyMemberDegraded, Details(site, proxyOrProtocol), accessor);

    /// <summary>
    /// Records a degraded proxy-backed synthesized member that has no dedicated member <c>BaseDecl</c>
    /// (e.g. an enum-case payload accessor synthesized on the containing type). Pass the containing
    /// decl so the row attributes to the right type.
    /// </summary>
    public static void Record(BindingItemKind kind, string memberDescriptor, BaseDecl? containingDecl,
        Site site, string? proxyOrProtocol)
        => ReportCollector.RecordMemberSkipped(
            kind, memberDescriptor, containingDecl, SkipReason.SuppressedProxyMemberDegraded, Details(site, proxyOrProtocol));

    /// <summary>
    /// Records a degraded reverse-dispatch receiver from its member descriptor string (the receiver
    /// emission site has no <c>BaseDecl</c> in scope, only the descriptor). Complements — does not
    /// replace — the SWIFTBIND061 build warning, promoting the decline to a persisted classified skip.
    /// </summary>
    public static void RecordReceiver(string memberDescriptor, string? proxyOrProtocol)
        => ReportCollector.RecordMemberSkipped(
            BindingItemKind.Method, memberDescriptor, containingDecl: null,
            SkipReason.SuppressedProxyMemberDegraded, Details(Site.ReceiverFailFast, proxyOrProtocol));
}
