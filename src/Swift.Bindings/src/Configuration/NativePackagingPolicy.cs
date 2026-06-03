// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// How a consumer's generated <c>.targets</c> should reference the original source
    /// xcframework's native (Gap 2). Distinct from the include/exclude boolean because the
    /// frozen consumer targets are written before the wrapper's fate is known and are evaluated
    /// later, on the consumer's machine — so they must defer the dedup-vs-carrier choice to a
    /// consume-time <c>Exists(wrapper)</c> check rather than baking a drop decision.
    /// </summary>
    public enum SourceReferenceMode
    {
        /// <summary>
        /// Reference the source xcframework whenever it is present (its own <c>Exists</c> guard).
        /// Used for a dynamic source (always carried by the source) and for a static source with
        /// no wrapper (the source is then the sole carrier).
        /// </summary>
        Always,

        /// <summary>
        /// Reference the source only when the wrapper is absent on the consumer's disk
        /// (<c>!Exists(wrapper) AND Exists(source)</c>). The static archive is force-loaded into
        /// the wrapper, so when the wrapper is present the source must stay inert to avoid
        /// double-registering its ObjC classes; when a soft-failed/skipped wrapper compile leaves
        /// no wrapper, the source self-heals as the fallback carrier instead of vanishing.
        /// </summary>
        WrapperAbsentFallback,
    }

    /// <summary>
    /// Single source of truth for the "should a consumer reference the original source
    /// xcframework's native library" decision (Gap 2). The source is the sole runtime carrier
    /// unless it is a static <c>ar</c> archive force-loaded into the Swift wrapper; in that case
    /// the wrapper carries it and referencing the source too would double-register its ObjC
    /// classes. A dynamic source is always referenced.
    ///
    /// <para>
    /// The decision takes two shapes depending on whether the wrapper's existence is known when
    /// the reference is written:
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="ShouldIncludeSourceXcframework"/> — a plain include/exclude for the
    ///   two "exists now" carriers, where the wrapper is already on disk at decision time:
    ///   the standalone <c>.csproj</c> link path and the SDK pack item
    ///   (<c>_SwiftBindingIncludeSourceXcframework</c>, computed from live disk state). Dropping
    ///   here is safe because the carrier's fate is settled.</item>
    ///   <item><see cref="ResolveConsumerSourceReferenceMode"/> — for the frozen consumer
    ///   <c>.targets</c> (emitted nupkg and local ProjectReference), written before the SDK's
    ///   two-pass flow compiles the wrapper and evaluated later on the consumer's machine. It
    ///   never drops the source; it emits a wrapper-absent fallback guard so the carrier resolves
    ///   from disk truth at consume time, surviving a soft-failed or skipped wrapper compile.</item>
    /// </list>
    ///
    /// <para>
    /// Keeping both formulas here — with <c>wrapperCarrierPresent</c> a required argument — is
    /// what stops the former scattered emission/pack sites from drifting: a caller cannot silently
    /// forget the carrier term. The MSBuild SDK link/pack path expresses the include/exclude
    /// formula once as the computed <c>_SwiftBindingIncludeSourceXcframework</c> property (derived
    /// at execution time from live disk state, so it cannot share this C# helper directly).
    /// </para>
    /// </summary>
    public static class NativePackagingPolicy
    {
        /// <summary>
        /// Whether the original source xcframework's native should be referenced/packed, for a
        /// carrier whose existence is already settled (on-disk) at decision time.
        /// </summary>
        /// <param name="sourceLinkage">How the source native is linked (static archive vs dynamic).</param>
        /// <param name="wrapperCarrierPresent">Whether a wrapper carrier exists for this context
        /// (see the type remarks for the per-context meaning).</param>
        public static bool ShouldIncludeSourceXcframework(
            NativeLinkage sourceLinkage, bool wrapperCarrierPresent)
            => sourceLinkage != NativeLinkage.Static || !wrapperCarrierPresent;

        /// <summary>
        /// How a frozen consumer <c>.targets</c> file should reference the source xcframework.
        /// A static source paired with a wrapper becomes a <see cref="SourceReferenceMode.WrapperAbsentFallback"/>
        /// (deferring dedup-vs-carrier to consume-time <c>Exists(wrapper)</c>); every other case
        /// references the source unconditionally (<see cref="SourceReferenceMode.Always"/>).
        /// </summary>
        /// <param name="sourceLinkage">How the source native is linked (static archive vs dynamic).</param>
        /// <param name="wrapperCarrierPresent">Whether a wrapper is (or will be) produced to carry
        /// the binding — the "will be produced" intent, since the wrapper does not yet exist when
        /// these targets are written.</param>
        public static SourceReferenceMode ResolveConsumerSourceReferenceMode(
            NativeLinkage sourceLinkage, bool wrapperCarrierPresent)
            => sourceLinkage == NativeLinkage.Static && wrapperCarrierPresent
                ? SourceReferenceMode.WrapperAbsentFallback
                : SourceReferenceMode.Always;
    }
}
