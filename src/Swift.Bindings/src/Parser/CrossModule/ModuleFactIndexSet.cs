// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// The graph-wide aggregate of every resolved module's <see cref="ModuleFactIndex"/>: a single,
    /// immutable, order-independent view a parser consults to answer "what does SOME loaded module
    /// know about this foreign type?" without depending on which siblings were finalized first.
    /// </summary>
    /// <remarks>
    /// Module-qualified names are globally unique, so the per-module indexes flatten into merged
    /// dictionaries with first-writer-wins on the rare duplicate (an umbrella re-export listing the
    /// same nominal in two TBDs resolves to the same symbol either way). Ownership is recorded as a
    /// pure identity fact — which module's TBD contributed a nominal — carrying NO layout/shape data,
    /// so it is safe to consult before layout finalization.
    /// </remarks>
    internal sealed class ModuleFactIndexSet
    {
        /// <summary>An empty set that resolves nothing — the composite over it behaves exactly as legacy.</summary>
        public static readonly ModuleFactIndexSet Empty = new(Array.Empty<ModuleFactIndex>());

        private readonly Dictionary<string, string> _metadataAccessors = new(StringComparer.Ordinal);
        private readonly Dictionary<(string, string), string> _conformanceDescriptors = new();
        private readonly Dictionary<string, string> _owningModuleByType = new(StringComparer.Ordinal);

        public ModuleFactIndexSet(IEnumerable<ModuleFactIndex> indexes)
        {
            foreach (var index in indexes)
            {
                foreach (var (typeName, symbol) in index.MetadataAccessors)
                {
                    _metadataAccessors.TryAdd(typeName, symbol);
                    _owningModuleByType.TryAdd(typeName, index.ModuleName);
                }
                foreach (var (key, symbol) in index.ConformanceDescriptors)
                {
                    _conformanceDescriptors.TryAdd(key, symbol);
                }
            }
        }

        /// <summary>Number of modules' facts folded in (diagnostics only).</summary>
        public int MetadataAccessorCount => _metadataAccessors.Count;

        /// <summary>
        /// Resolves the metadata-accessor symbol for a nominal from ANY indexed module's TBD.
        /// </summary>
        public bool TryGetMetadataAccessor(SwiftTypeName typeName, out string symbol)
        {
            if (_metadataAccessors.TryGetValue(typeName.ModuleQualifiedName, out var found))
            {
                symbol = found;
                return true;
            }
            symbol = string.Empty;
            return false;
        }

        /// <summary>
        /// Resolves the protocol-conformance-descriptor symbol for an (implementing type, protocol)
        /// pair from ANY indexed module's TBD.
        /// </summary>
        public bool TryGetProtocolConformanceDescriptor(SwiftTypeName implementingType, SwiftTypeName protocol, out string symbol)
        {
            if (_conformanceDescriptors.TryGetValue((implementingType.ModuleQualifiedName, protocol.ModuleQualifiedName), out var found))
            {
                symbol = found;
                return true;
            }
            symbol = string.Empty;
            return false;
        }

        /// <summary>
        /// The module whose TBD owns a nominal, independent of parse order. A pure identity fact:
        /// it names the owning framework the two-phase design needs before "synthesize a
        /// <c>{mangled}Ma</c> accessor" can be proven safe, and carries no layout/shape data.
        /// </summary>
        public bool TryGetOwningModule(SwiftTypeName typeName, out string moduleName)
        {
            if (_owningModuleByType.TryGetValue(typeName.ModuleQualifiedName, out var found))
            {
                moduleName = found;
                return true;
            }
            moduleName = string.Empty;
            return false;
        }
    }
}
