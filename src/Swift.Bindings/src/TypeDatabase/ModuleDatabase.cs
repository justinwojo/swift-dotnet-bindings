// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration
{
    /// <summary>
    /// Represents a Swift module in C#, managing type records and module metadata.
    /// </summary>
    public class ModuleTypeDatabase
    {
        /// <summary>
        /// The type records associated with the module, where the key is the Swift type identifier.
        /// </summary>
        private readonly ConcurrentDictionary<SwiftTypeName, TypeRecord> _typeRecords;

        private readonly HashSet<string> _suppressedProxyClassNames = new(StringComparer.Ordinal);

        public ModuleTypeDatabase(string name, string path)
        {
            Name = name;
            Path = path;

            _typeRecords = new ConcurrentDictionary<SwiftTypeName, TypeRecord>();
        }

        /// <summary>
        /// Gets the name of the module.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the file path to the module.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Checks whether a type has already been processed in the module.
        /// </summary>
        /// <param name="typeIdentifier">The identifier for the Swift type.</param>
        /// <returns><c>true</c> if the type has been processed; otherwise, <c>false</c>.</returns>
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName)
        {
            return _typeRecords.ContainsKey(swiftTypeName);
        }

        /// <summary>
        /// Registers a type record with the specified type identifier in the module.
        /// </summary>
        /// <param name="typeIdentifier">The identifier for the Swift type.</param>
        /// <param name="record">The type record to register.</param>
        public void RegisterType(SwiftTypeName swiftTypeName, TypeRecord record)
        {
            _typeRecords.AddOrUpdate(swiftTypeName, record, (_, _) => record);
        }

        /// <summary>
        /// Attempts to retrieve the type record for the specified type identifier.
        /// </summary>
        /// <param name="typeIdentifier">The identifier for the Swift type.</param>
        /// <param name="record">
        /// When this method returns, contains the type record if found; otherwise, <c>null</c>.
        /// </param>
        /// <returns><c>true</c> if the type record is found; otherwise, <c>false</c>.</returns>
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
        {
            if (_typeRecords.TryGetValue(swiftTypeName, out record))
                return true;

            return false;
        }

        /// <summary>
        /// Enumerates all type records in this module database.
        /// Used by ModuleDatabaseEmitter to serialize records for cross-module resolution.
        /// </summary>
        public IEnumerable<KeyValuePair<SwiftTypeName, TypeRecord>> GetAllTypeRecords()
            => _typeRecords;

        /// <summary>
        /// Records a proxy class name that was suppressed during this module's emission.
        /// Used so downstream modules can strip method bodies that reference the cross-module
        /// qualified form (<c>{Namespace}.SwiftInterop.{ProxyName}</c>) when the umbrella-aware
        /// protocol-emission resolver routes them to a suppressed proxy.
        /// </summary>
        public void RegisterSuppressedProxyClassName(string proxyClassName)
        {
            _suppressedProxyClassNames.Add(proxyClassName);
        }

        /// <summary>
        /// The C# namespace into which suppressed proxies would have been emitted (i.e.
        /// <c>{generatedNamespace}.SwiftInterop</c> minus the trailing <c>.SwiftInterop</c>).
        /// Persisted in the module database so downstream modules can build the exact
        /// qualified-form needle the umbrella-aware marshaler emits — which uses the
        /// protocol record's C# namespace, NOT the Swift module name. With the default
        /// <c>namespacePattern</c> the two are equal, but they diverge under a custom
        /// pattern, and the post-pass match must follow the C# namespace.
        /// Defaults to <see cref="Name"/> on databases that predate this property.
        /// </summary>
        public string? SuppressedProxyNamespace { get; set; }

        /// <summary>
        /// Gets the set of proxy class names suppressed during this module's emission.
        /// </summary>
        public IReadOnlyCollection<string> SuppressedProxyClassNames => _suppressedProxyClassNames;
    }
}
