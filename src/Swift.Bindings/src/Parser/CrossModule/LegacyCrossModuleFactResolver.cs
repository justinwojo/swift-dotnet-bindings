// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.Demangling;

namespace BindingsGeneration
{
    /// <summary>
    /// The order-sensitive cross-module fact resolver that reproduces the generator's historical
    /// behavior EXACTLY: every query is answered from the current module's demangled TBD
    /// (<see cref="DemanglingResults"/>) plus the running <see cref="ITypeDatabase"/>, with no
    /// graph-wide view. It is the behavior baseline the two-phase index-backed resolver is measured
    /// against — a straight forwarding shim over the same two collaborators the parser held
    /// directly before the seam existed.
    /// </summary>
    /// <remarks>
    /// Each method forwards to the identical call the parser made inline before
    /// <see cref="ICrossModuleFactResolver"/> was introduced, so a parse driven by this resolver
    /// produces byte-identical output to the pre-seam generator. The TBD is the module being parsed;
    /// the type database holds whatever modules have been loaded so far — both are the exact sources
    /// whose "answer depends on when you ask" property the graph-wide resolver removes.
    /// </remarks>
    internal sealed class LegacyCrossModuleFactResolver : ICrossModuleFactResolver
    {
        private readonly ITypeDatabase _typeDatabase;
        private readonly DemanglingResults _demangledTbd;

        public LegacyCrossModuleFactResolver(ITypeDatabase typeDatabase, DemanglingResults demangledTbd)
        {
            _typeDatabase = typeDatabase;
            _demangledTbd = demangledTbd;
        }

        public bool IsTypeRegistered(SwiftTypeName typeName)
            => _typeDatabase.IsTypeRegistered(typeName);

        public bool TryGetForeignTypeShape(SwiftTypeName typeName, out ForeignTypeShape shape)
        {
            if (_typeDatabase.TryGetTypeRecord(typeName, out var record))
            {
                shape = new ForeignTypeShape(record.Kind, record.Flags);
                return true;
            }
            shape = default;
            return false;
        }

        public bool IsSystemReexportAllowedModule(string moduleName)
            => AppleFrameworkRegistry.IsSystemReexportAllowedModule(moduleName);

        public bool TryGetMetadataAccessor(SwiftTypeName typeName, out string symbol)
            => _demangledTbd.TryGetMetadataAccessor(typeName, out symbol);

        public string GetMetadataAccessor(SwiftTypeName typeName)
            => _demangledTbd.GetMetadataAccessor(typeName);

        public bool TryGetProtocolConformanceDescriptor(SwiftTypeName implementingType, SwiftTypeName protocol, out string symbol)
            => _demangledTbd.TryGetProtocolConformanceDescriptor(implementingType, protocol, out symbol);
    }
}
