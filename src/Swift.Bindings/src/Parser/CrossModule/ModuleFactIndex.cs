// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.Demangling;

namespace BindingsGeneration
{
    /// <summary>
    /// An immutable, per-module index of the cross-module facts a single-module parse needs about
    /// types owned by THIS module — its metadata-accessor symbols and protocol-conformance
    /// descriptors, keyed by module-qualified identity. Built once from a module's demangled TBD and
    /// never mutated, so it can be shared across every parser in the run as a graph-wide view whose
    /// answers do not depend on parse order.
    /// </summary>
    /// <remarks>
    /// This is the "symbol index" half of the two-phase design: it carries only facts that name a
    /// symbol (accessor / conformance-descriptor) and the nominal-ownership identity derived from
    /// them. It deliberately carries NOTHING that feeds layout / frozenness decisions — those stay
    /// on the order-sensitive type-database path until the SCC-aware finalize (a later stage), so
    /// preloading this index cannot silently change a struct's frozen/memory-management verdict.
    /// </remarks>
    internal sealed class ModuleFactIndex
    {
        /// <summary>The module whose TBD produced this index.</summary>
        public string ModuleName { get; }

        /// <summary>Module-qualified nominal name → metadata-accessor symbol.</summary>
        public IReadOnlyDictionary<string, string> MetadataAccessors { get; }

        /// <summary>(implementing type module-qualified name, protocol module-qualified name) → descriptor symbol.</summary>
        public IReadOnlyDictionary<(string ImplementingType, string Protocol), string> ConformanceDescriptors { get; }

        private ModuleFactIndex(
            string moduleName,
            IReadOnlyDictionary<string, string> metadataAccessors,
            IReadOnlyDictionary<(string, string), string> conformanceDescriptors)
        {
            ModuleName = moduleName;
            MetadataAccessors = metadataAccessors;
            ConformanceDescriptors = conformanceDescriptors;
        }

        /// <summary>
        /// Builds the index from a module's demangled TBD. The keys mirror EXACTLY the identity the
        /// legacy path matches on — <see cref="DemanglingResults.GetMetadataAccessor"/> compares
        /// <c>TypeSpec.Name</c> to the type's <c>ModuleQualifiedName</c>, and the conformance lookup
        /// compares <c>ImplementingType.Name</c> / <c>ProtocolType.Name</c> — so an index hit yields
        /// the same symbol the legacy lookup would have, had the owning module been loaded first.
        /// </summary>
        public static ModuleFactIndex FromDemangledTbd(string moduleName, DemanglingResults demangledTbd)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var accessor in demangledTbd.MetadataAccessors)
            {
                // A TBD can list the same nominal's accessor more than once; first writer wins,
                // matching FirstOrDefault in the legacy lookup.
                metadata.TryAdd(accessor.TypeSpec.Name, accessor.Symbol);
            }

            var conformances = new Dictionary<(string, string), string>();
            foreach (var descriptor in demangledTbd.ProtocolConformanceDescriptors)
            {
                conformances.TryAdd((descriptor.ImplementingType.Name, descriptor.ProtocolType.Name), descriptor.Symbol);
            }

            return new ModuleFactIndex(moduleName, metadata, conformances);
        }
    }
}
