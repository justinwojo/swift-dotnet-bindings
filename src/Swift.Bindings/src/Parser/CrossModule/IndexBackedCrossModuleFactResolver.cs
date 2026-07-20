// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// The two-phase cross-module fact resolver: it answers a query from the graph-wide
    /// <see cref="ModuleFactIndexSet"/> (order-independent) where doing so only RECOVERS a fact the
    /// order-sensitive legacy path was losing, and delegates everything else to the wrapped
    /// <see cref="LegacyCrossModuleFactResolver"/> unchanged — so a run currently producing correct
    /// output produces byte-identical output, and the only behavioral delta is at the exact points
    /// the legacy combination threw or emitted an empty descriptor for a sibling that simply had not
    /// been finalized yet.
    /// </summary>
    /// <remarks>
    /// Two deliberate asymmetries, both to preserve legacy behavior everywhere it already resolves:
    /// <list type="bullet">
    /// <item><description>
    /// Metadata accessor: the parser's resolution ladder is TByGet (current TBD) → synthesize
    /// <c>{mangled}Ma</c> for a current-module type → synthesize for an already-registered foreign
    /// type → GetMetadataAccessor (throw). We leave <see cref="TryGetMetadataAccessor"/> on legacy so
    /// the first three rungs are untouched, and consult the index ONLY inside
    /// <see cref="GetMetadataAccessor"/> — the terminal throw. There the type is provably foreign and
    /// not-yet-registered, so returning the owning module's real accessor symbol is a recovery, never
    /// a synthesis, honoring "never synthesize <c>{mangled}Ma</c> unless the graph proves the owning
    /// framework".
    /// </description></item>
    /// <item><description>
    /// Conformance descriptor: there is no synthesis ladder — legacy returns false and the caller
    /// warns and emits an empty descriptor — so the index is consulted on legacy miss, recovering a
    /// descriptor the sequential order had not loaded.
    /// </description></item>
    /// </list>
    /// Ownership / foreign-type shape / system-reexport queries feed layout and frozenness decisions
    /// that the symbol index alone cannot safely change; they delegate to legacy until the SCC-aware
    /// finalize handles them in a later stage.
    /// </remarks>
    internal sealed class IndexBackedCrossModuleFactResolver : ICrossModuleFactResolver
    {
        private readonly ModuleFactIndexSet _index;
        private readonly ICrossModuleFactResolver _legacy;

        public IndexBackedCrossModuleFactResolver(ModuleFactIndexSet index, ICrossModuleFactResolver legacy)
        {
            _index = index;
            _legacy = legacy;
        }

        // --- Ownership / foreign-type shape / reexport policy: layout-affecting, delegate to legacy. ---

        public bool IsTypeRegistered(SwiftTypeName typeName)
            => _legacy.IsTypeRegistered(typeName);

        public bool TryGetForeignTypeShape(SwiftTypeName typeName, out ForeignTypeShape shape)
            => _legacy.TryGetForeignTypeShape(typeName, out shape);

        public bool IsSystemReexportAllowedModule(string moduleName)
            => _legacy.IsSystemReexportAllowedModule(moduleName);

        // --- Metadata accessor: legacy ladder preserved; index recovers only at the terminal throw. ---

        public bool TryGetMetadataAccessor(SwiftTypeName typeName, out string symbol)
            => _legacy.TryGetMetadataAccessor(typeName, out symbol);

        public string GetMetadataAccessor(SwiftTypeName typeName)
        {
            // Reached only after the legacy ladder exhausted its non-throwing rungs (current-TBD
            // lookup, current-module synthesis, registered-foreign synthesis), so the type is a
            // genuinely foreign, not-yet-registered nominal. Recover its real accessor from the
            // owning module's indexed TBD instead of throwing; fall through to the legacy throw
            // (with its canonical "not found" message) only when no module in the graph owns it.
            if (_index.TryGetMetadataAccessor(typeName, out var symbol))
                return symbol;
            return _legacy.GetMetadataAccessor(typeName);
        }

        // --- Conformance descriptor: legacy first, index recovers on miss. ---

        public bool TryGetProtocolConformanceDescriptor(SwiftTypeName implementingType, SwiftTypeName protocol, out string symbol)
        {
            if (_legacy.TryGetProtocolConformanceDescriptor(implementingType, protocol, out symbol))
                return true;
            return _index.TryGetProtocolConformanceDescriptor(implementingType, protocol, out symbol);
        }
    }
}
