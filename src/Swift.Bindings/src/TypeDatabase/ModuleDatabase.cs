// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

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

        /// <summary>
        /// Optional logger used solely to surface last-write-wins collisions in
        /// <see cref="RegisterType"/> (Finding 47 observability). Null in contexts (e.g. tests)
        /// that do not supply one — collision detection then runs silently, as before.
        /// </summary>
        private readonly ILogger? _logger;

        public ModuleTypeDatabase(string name, string path, ILogger? logger = null)
        {
            Name = name;
            Path = path;
            _logger = logger;

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
        /// <remarks>
        /// The store is unconditional last-write-wins. Finding 47 (observability): rather than
        /// overwrite silently, every collision that actually changes the stored record is now
        /// surfaced. A same-name registration whose <see cref="TypeRecord.Kind"/> differs is a
        /// genuine conflict (warned, SWIFTBIND024); a same-kind content change is an intentional
        /// last-write-wins update (e.g. a cross-module conformance merge) and is logged at
        /// information level. Conflict <em>policy</em> and a post-registration freeze point are
        /// deliberately out of scope here (Session 9 owns the <c>Register(record, ConflictPolicy)</c>
        /// refactor); this only makes the existing behavior loud.
        /// </remarks>
        public void RegisterType(SwiftTypeName swiftTypeName, TypeRecord record)
        {
            _typeRecords.AddOrUpdate(
                swiftTypeName,
                record,
                (key, existing) =>
                {
                    if (_logger != null && !ReferenceEquals(existing, record) && !existing.Equals(record))
                    {
                        if (existing.Kind != record.Kind)
                        {
                            _logger.LogWarning(
                                "SWIFTBIND024: type-registry collision in module '{Module}': '{Type}' was registered as "
                                + "{ExistingKind} and is being overwritten as {NewKind} (last-write-wins).",
                                Name, key, existing.Kind, record.Kind);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "SWIFTBIND024: type-registry last-write-wins update in module '{Module}': record for "
                                + "'{Type}' ({Kind}) was overwritten with different content.",
                                Name, key, record.Kind);
                        }
                    }

                    return record;
                });
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
