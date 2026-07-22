// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The three-way proxy-emission decision for a protocol. Shared by the emit-time path
/// (<see cref="ProtocolHandler"/>, which decides what to emit and records the skip diagnostic)
/// and the order-independent pre-pass (<see cref="SuppressedProxyPrecomputer"/>, which front-loads
/// the suppressed-name set) so both reach an identical verdict from one predicate.
/// </summary>
internal enum ProxyEmissionDecision
{
    /// <summary>Emit the <c>{Protocol}Proxy</c> class normally.</summary>
    Emit,

    /// <summary>
    /// Proxy skipped because the EveryProtocol conformance was not emitted (class-bound,
    /// genericSig constraint, constructor requirement, etc.). References to the proxy must be
    /// downgraded (CONSUME: drop the wrap fallback) or stubbed (PRODUCE: the member throws).
    /// </summary>
    SuppressedByConformance,

    /// <summary>
    /// Proxy skipped because a required member references an unsupported module — a genuinely
    /// unsupported SwiftUI/Combine type, OR a type withheld from the database by ingestion quarantine
    /// (SWIFTBIND046, e.g. a mangled-name-less <c>Foundation._NSRange</c>). The EveryProtocol
    /// conformance is also skipped, so emitting the proxy would call non-existent Swift symbols.
    /// This IS recorded as a suppressed proxy: the SwiftUI/Combine case usually leaves no retained
    /// consumer (the offending member is itself skipped), but the quarantine case withdraws the
    /// protocol's methods while KEEPING the protocol, its interface, and any retained
    /// <c>consume(base: any P)</c> consumer — whose existential projection would otherwise construct a
    /// dangling <c>new {P}Proxy(…)</c>. Recording it lets the proven consumer-downgrade machinery
    /// (CONSUME drops the wrap fallback; PRODUCE stubs) fire exactly as it does for
    /// <see cref="SuppressedByConformance"/>.
    /// </summary>
    SkippedUnsupportedModule,
}

/// <summary>
/// Single source of truth for whether a protocol's C# proxy class is emitted, and if not, why.
/// </summary>
internal static class ProtocolProxyEmissionPolicy
{
    /// <summary>
    /// Decides the proxy-emission outcome for <paramref name="protocolDecl"/>. Mirrors the
    /// (formerly inline) decision in <see cref="ProtocolHandler"/>: a member with an
    /// unsupported-module type suppresses the whole proxy (SwiftUI/Combine) first; otherwise an
    /// EveryProtocol conformance that was not emitted suppresses it — unless the protocol is a
    /// read-only / Swift-vended-only proxy, which keeps its proxy. All state inputs
    /// (<see cref="ModuleEmissionContext.ConformanceDecisions"/>, the read-only-proxy marks) are
    /// populated during <c>EmitEveryProtocolConformances</c>, so this is order-safe to call once
    /// the Swift conformance pass has run.
    /// </summary>
    public static ProxyEmissionDecision Decide(ProtocolDecl protocolDecl, ITypeDatabase typeDatabase, ModuleEmissionContext? ctx)
    {
        if (ModuleHandler.HasMembersReferencingUnsupportedModule(protocolDecl, typeDatabase))
            return ProxyEmissionDecision.SkippedUnsupportedModule;

        // Dual-oracle parity with the proxy-class emitter. ProtocolProxyEmitter.EmitProxyClass
        // early-returns UNCONDITIONALLY — emitting NO class — for a protocol with a Self requirement or
        // associated types: [UnmanagedCallersOnly]/[DllImport] cannot live in the generic proxy those
        // shapes would need. That early-return is a structural fact, not a prediction. This oracle must
        // agree on its own terms, or the two can diverge: Decide answers Emit (via the WasConformanceEmitted
        // path below) while no proxy class is ever written, and a retained constrained-existential consumer
        // then projects a dangling `new {P}Proxy(…)` for a class that does not exist.
        //
        // Under the current ModuleHandler this arm changes no output: the suitable-protocol filter already
        // excludes Self/AT BEFORE EveryProtocol emission, so no conformance is ever recorded for them and
        // the WasConformanceEmitted path below already returns SuppressedByConformance. Making the
        // withdrawal explicit HERE keeps the single oracle self-consistent with EmitProxyClass directly,
        // rather than depending on that second, coincidental filter to reach the same verdict — so a future
        // suitable-filter change, or any state that records a conformance for an AT/Self protocol, cannot
        // reopen the divergence. Because EmitProxyClass never emits for these flags, this can only ADD
        // suppression bookkeeping — it can never remove a proxy that would otherwise exist; the report row
        // is attributed to the associated-type/Self shape via ForDroppedProtocol. Guarded on ctx != null so
        // the invariant the emit-time switch relies on (SuppressedByConformance ⇒ a ModuleEmissionContext is
        // available for the non-null RecordSuppressedProxy deref) still holds; the null-ctx unit path
        // direct-emits and EmitProxyClass no-ops for these anyway.
        if (ctx != null && (protocolDecl.HasSelfRequirement || protocolDecl.AssociatedTypes.Count > 0))
            return ProxyEmissionDecision.SuppressedByConformance;

        // A read-only (Swift-vended-only) proxy reads `any P` through the existential's own witness
        // table and never calls the EveryProtocol carrier, so it emits regardless of carrier state.
        if (ctx != null && !ctx.IsReadOnlyProxy(protocolDecl.Name))
        {
            // Conformance marker is keyed on the module-qualified name (matching the recorder);
            // IsReadOnlyProxy stays simple-name-keyed (its own family).
            var qualifiedKey = protocolDecl.SwiftTypeName?.ModuleQualifiedName ?? protocolDecl.Name;

            // A FULL (reverse-dispatch) proxy calls the EveryProtocol carrier factory
            // (SBW_CreateEveryProtocol). When the module emitted no carrier — its suitable-protocol set
            // was empty, so EmitEveryProtocolClass never ran — that symbol is undefined and a full proxy
            // would dangle. Suppress it. This is the real "does the carrier exist" fact; the conformance
            // count below is only a proxy for it in modules that DID emit the carrier (the carrier and
            // the first RecordConformanceDecision both happen on the non-empty path), which is why the
            // count alone missed the empty-suitable-protocol module.
            if (!ctx.WasEveryProtocolCarrierEmitted)
                return ProxyEmissionDecision.SuppressedByConformance;

            // Carrier emitted: suppress a proxy whose own EveryProtocol conformance was not emitted
            // (class-bound, genericSig constraint, constructor requirement, etc.). Behaviour here is
            // unchanged from the historical `ConformanceDecisions.Count > 0` gate, since the carrier is
            // only emitted on the path that records at least one conformance decision.
            if (!ctx.WasConformanceEmitted(qualifiedKey))
                return ProxyEmissionDecision.SuppressedByConformance;
        }

        return ProxyEmissionDecision.Emit;
    }

    /// <summary>The C# proxy class simple name for a protocol (e.g. <c>BoxableProxy</c>).</summary>
    public static string ProxyClassName(ProtocolDecl protocolDecl) => $"{protocolDecl.Name}Proxy";
}

/// <summary>
/// Pre-emission pass that records the full set of suppressed proxy class names into the emission
/// context BEFORE any C# member is emitted, so emit-time reference gates are order-independent. Free
/// functions (emitted before any type) and types declared before their protocol would otherwise
/// consult an empty suppressed-name set and emit a live <c>new {Proxy}(…)</c> reference to a proxy
/// that is never generated — the bug the retired whole-file generate-then-strip post-pass used to
/// mask. Must run AFTER <c>EmitEveryProtocolConformances</c> (conformance decisions and
/// read-only-proxy marks populated) and BEFORE C# emission. <see cref="ProtocolHandler"/> still
/// records the same names during emission (idempotent <see cref="HashSet{T}"/> add); this pass only
/// front-loads them so earlier references see a complete set.
/// </summary>
internal static class SuppressedProxyPrecomputer
{
    public static void Precompute(ModuleDecl moduleDecl, ITypeDatabase typeDatabase, ModuleEmissionContext emissionContext)
    {
        foreach (var protocolDecl in EnumerateProtocolsForSuppression(moduleDecl))
        {
            // Record EVERY non-Emit decision. Both suppression arms leave the `{Protocol}Proxy` class
            // unemitted, so any retained consumer that projects `any P` must have its reference
            // downgraded — the completeness invariant. Recording only SuppressedByConformance let a
            // quarantine-suppressed (SkippedUnsupportedModule) proxy's retained consumer ship a dangling
            // `new {P}Proxy(…)` (the SwiftRichString StyleProtocol CS0246).
            if (ProtocolProxyEmissionPolicy.Decide(protocolDecl, typeDatabase, emissionContext) != ProxyEmissionDecision.Emit)
                emissionContext.RecordSuppressedProxy(ProtocolProxyEmissionPolicy.ProxyClassName(protocolDecl));
        }
    }

    // Top-level protocols live in moduleDecl.Protocols; a protocol NESTED inside a type (declared in
    // `enum RiveLog { protocol Logger { … } }`) lives under its parent's TypeDecl.Types and is absent
    // from moduleDecl.Protocols. Its proxy still needs its suppressed name front-loaded so an
    // earlier-declared consumer of `any RiveLog.Logger` sees the suppression instead of shipping a live
    // `new LoggerProxy(…)` for a class that is never emitted (the rive-ios LoggerProxy CS0246). The
    // emitter already reaches nested protocols this way (ProtocolHandler.CollectProtocolDecls); the
    // precompute pass must too, or it defeats its own order-independence guarantee for nested shapes.
    private static IEnumerable<ProtocolDecl> EnumerateProtocolsForSuppression(ModuleDecl moduleDecl)
    {
        foreach (var protocolDecl in moduleDecl.Protocols)
            yield return protocolDecl;

        foreach (var type in moduleDecl.Types)
            foreach (var nested in EnumerateNestedProtocols(type))
                yield return nested;
    }

    // Yields protocols declared inside <paramref name="type"/> at any depth (a nested type's own
    // nested protocols included). Deliberately does NOT yield <paramref name="type"/> itself — a
    // top-level protocol is already yielded by moduleDecl.Protocols, so recursing only its children
    // avoids re-deciding it.
    private static IEnumerable<ProtocolDecl> EnumerateNestedProtocols(TypeDecl type)
    {
        foreach (var nested in type.Types)
        {
            if (nested is ProtocolDecl nestedProtocol)
                yield return nestedProtocol;

            foreach (var deeper in EnumerateNestedProtocols(nested))
                yield return deeper;
        }
    }
}
