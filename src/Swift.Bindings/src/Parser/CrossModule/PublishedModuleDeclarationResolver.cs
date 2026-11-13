// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// The fact resolver for re-reading the declarations of a dependency whose type records were
    /// already loaded from its published module database. It answers every question about the
    /// module being read as the raw-ABI dependency branch would have: that module's own types are not
    /// yet registered and have no foreign shape. Without that, the parse would meet each of the
    /// module's own types already registered and reject it as a duplicate. Facts about every other
    /// module come from the wrapped resolver unchanged.
    /// </summary>
    internal sealed class PublishedModuleDeclarationResolver : ICrossModuleFactResolver
    {
        private readonly ICrossModuleFactResolver _inner;
        private readonly string _moduleName;

        public PublishedModuleDeclarationResolver(ICrossModuleFactResolver inner, string moduleName)
        {
            _inner = inner;
            _moduleName = moduleName;
        }

        private bool IsOwn(SwiftTypeName typeName) => typeName.Module == _moduleName;

        public bool IsTypeRegistered(SwiftTypeName typeName)
            => !IsOwn(typeName) && _inner.IsTypeRegistered(typeName);

        public bool TryGetForeignTypeShape(SwiftTypeName typeName, out ForeignTypeShape shape)
        {
            if (IsOwn(typeName))
            {
                shape = default;
                return false;
            }
            return _inner.TryGetForeignTypeShape(typeName, out shape);
        }

        public bool IsSystemReexportAllowedModule(string moduleName)
            => _inner.IsSystemReexportAllowedModule(moduleName);

        public bool TryGetMetadataAccessor(SwiftTypeName typeName, out string symbol)
            => _inner.TryGetMetadataAccessor(typeName, out symbol);

        public string GetMetadataAccessor(SwiftTypeName typeName)
            => _inner.GetMetadataAccessor(typeName);

        public bool TryGetProtocolConformanceDescriptor(SwiftTypeName implementingType, SwiftTypeName protocol, out string symbol)
            => _inner.TryGetProtocolConformanceDescriptor(implementingType, protocol, out symbol);
    }
}
