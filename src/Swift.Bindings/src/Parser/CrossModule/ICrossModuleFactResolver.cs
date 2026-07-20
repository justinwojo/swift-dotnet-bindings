// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// The shape facts a foreign (cross-module) nominal type contributes to the parser's
    /// receiver-eligibility decision: its record kind and layout flags. Carries exactly the two
    /// fields the cross-module struct-extension gate reads today
    /// (<see cref="TypeRecordKind"/> + <see cref="TypeRecordFlags"/>), so a resolver can answer the
    /// probe without exposing the full <see cref="TypeRecord"/>.
    /// </summary>
    internal readonly record struct ForeignTypeShape(TypeRecordKind Kind, TypeRecordFlags Flags);

    /// <summary>
    /// Resolves the cross-module facts a single-module ABI parse needs about types owned by OTHER
    /// modules — nominal ownership / foreign-type shape, metadata-accessor symbols, and
    /// protocol-conformance descriptors. It is the seam that lets the answer to "what do we know
    /// about a foreign type?" stop depending on <em>which modules happened to be finalized before
    /// this parse</em>.
    /// </summary>
    /// <remarks>
    /// The historical behavior is a straight combination of the current module's demangled TBD
    /// (<see cref="Demangling.DemanglingResults"/>) and the running <see cref="ITypeDatabase"/> —
    /// both order-sensitive: the TBD only knows the module being parsed, and the type database only
    /// knows modules already loaded. <see cref="LegacyCrossModuleFactResolver"/> reproduces exactly
    /// that combination; a graph-wide index-backed resolver can layer a preloaded, order-independent
    /// view in front of it. The interface is deliberately narrow — it exposes the SAME queries the
    /// parser already makes, so swapping the implementation is behavior-preserving except where the
    /// legacy combination was losing a fact it could not have seen yet. The migration between
    /// implementations is entirely internal; there is no CLI surface for it.
    /// </remarks>
    internal interface ICrossModuleFactResolver
    {
        // --- Nominal ownership / foreign-type shape ---

        /// <summary>
        /// Whether <paramref name="typeName"/> is already registered by a loaded dependency
        /// database — the narrow "was this type contributed by another module" predicate the parser
        /// uses to decide duplicate handling and metadata-accessor synthesis
        /// (<see cref="ITypeDatabase.IsTypeRegistered"/> semantics).
        /// </summary>
        bool IsTypeRegistered(SwiftTypeName typeName);

        /// <summary>
        /// Attempts to read the layout shape (kind + flags) of a foreign nominal type from a loaded
        /// dependency database. Mirrors <see cref="ITypeDatabase.TryGetTypeRecord"/> reduced to the
        /// two fields the cross-module struct-receiver gate consults.
        /// </summary>
        bool TryGetForeignTypeShape(SwiftTypeName typeName, out ForeignTypeShape shape);

        /// <summary>
        /// Whether a foreign module is on the system / common-Apple re-export keep-list — the policy
        /// that decides whether a third-party module's re-exported foreign nominal (with no
        /// current-module extension children) is kept with a module-name override or dropped as a
        /// pure re-export. Mirrors <see cref="AppleFrameworkRegistry.IsSystemReexportAllowedModule"/>.
        /// </summary>
        bool IsSystemReexportAllowedModule(string moduleName);

        // --- Metadata-accessor lookup ---

        /// <summary>
        /// Attempts to resolve the metadata-accessor symbol for <paramref name="typeName"/> from a
        /// demangled TBD. Non-throwing; mirrors
        /// <see cref="Demangling.DemanglingResults.TryGetMetadataAccessor"/>.
        /// </summary>
        bool TryGetMetadataAccessor(SwiftTypeName typeName, out string symbol);

        /// <summary>
        /// Resolves the metadata-accessor symbol for <paramref name="typeName"/> from a demangled
        /// TBD, throwing when none is found. Mirrors
        /// <see cref="Demangling.DemanglingResults.GetMetadataAccessor"/> — the terminal
        /// "accessor genuinely missing" signal the parser surfaces loudly.
        /// </summary>
        string GetMetadataAccessor(SwiftTypeName typeName);

        // --- Conformance-descriptor lookup ---

        /// <summary>
        /// Attempts to resolve the protocol-conformance-descriptor symbol for an
        /// (implementing type, protocol) pair from a demangled TBD. Non-throwing; mirrors
        /// <see cref="Demangling.DemanglingResults.TryGetProtocolConformanceDescriptor"/>.
        /// </summary>
        bool TryGetProtocolConformanceDescriptor(SwiftTypeName implementingType, SwiftTypeName protocol, out string symbol);
    }
}
