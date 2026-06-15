// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Finding 47: how <see cref="ModuleTypeDatabase.Register"/> resolves a same-key collision.
    /// The write primitive was previously unconditional last-write-wins and the actual intent lived
    /// ad hoc at every call site (a parser-side <c>if (!IsTypeProcessed) Register(...)</c> guard meant
    /// first-wins; a bare call meant last-wins). Naming the policy at the call site makes that intent
    /// explicit and auditable instead of implicit in the surrounding code.
    /// </summary>
    public enum ConflictPolicy
    {
        /// <summary>
        /// Keep the first record registered for a key; a later registration of the same key is
        /// ignored (and logged). Matches the historical <c>if (!IsTypeProcessed) Register(...)</c>
        /// guard — the canonical record stays authoritative.
        /// </summary>
        KeepExisting,

        /// <summary>
        /// Overwrite with the latest record (the historical bare <c>AddOrUpdate</c> behavior). A
        /// collision that actually changes the stored record is logged so the overwrite is visible.
        /// </summary>
        Overwrite,
    }

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
        /// Optional logger used solely to surface registry collisions in
        /// <see cref="Register"/> (Finding 47 observability). Null in contexts (e.g. tests)
        /// that do not supply one — collision detection then runs silently, as before.
        /// </summary>
        private readonly ILogger? _logger;

        /// <summary>
        /// Finding 47: once frozen (after the main module is finalized into the database), the
        /// registry is immutable to structural writes — <see cref="Register"/> throws. The only
        /// sanctioned post-freeze mutation is <see cref="ApplyEmissionResult"/>, which stamps
        /// emission-discovered facts onto an already-registered record. This turns "the database's
        /// answer depends on when in the pipeline you ask" into a hard, observable boundary.
        /// </summary>
        private bool _frozen;

        public ModuleTypeDatabase(string name, string path, ILogger? logger = null)
        {
            Name = name;
            Path = path;
            _logger = logger;

            _typeRecords = new ConcurrentDictionary<SwiftTypeName, TypeRecord>();
        }

        /// <summary>
        /// Finding 47: marks the registry immutable. After this, <see cref="Register"/> throws and
        /// only <see cref="ApplyEmissionResult"/> may mutate records (and only their
        /// emission-discovered facts). Idempotent.
        /// </summary>
        public void Freeze() => _frozen = true;

        /// <summary>True once <see cref="Freeze"/> has been called.</summary>
        public bool IsFrozen => _frozen;

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
        /// Registers a type record under <paramref name="swiftTypeName"/>, resolving same-key
        /// collisions per <paramref name="policy"/>.
        /// </summary>
        /// <param name="swiftTypeName">The identifier for the Swift type.</param>
        /// <param name="record">The type record to register.</param>
        /// <param name="policy">How to resolve a collision with an existing record for this key.</param>
        /// <remarks>
        /// Finding 47: the write primitive now (a) takes an explicit <see cref="ConflictPolicy"/> so
        /// the first-wins vs last-wins intent that previously lived ad hoc at each call site is named
        /// here, (b) folds in the Session 6 collision observability (every collision that changes the
        /// stored record is logged, SWIFTBIND024), and (c) honors the registry freeze point — a
        /// structural registration after <see cref="Freeze"/> is a contract violation (SWIFTBIND045),
        /// because post-freeze the database must be immutable except for the emission-fact stamping
        /// that flows through <see cref="ApplyEmissionResult"/>.
        /// </remarks>
        public void Register(SwiftTypeName swiftTypeName, TypeRecord record, ConflictPolicy policy)
        {
            if (_frozen)
            {
                throw new InvalidOperationException(
                    $"SWIFTBIND045: type registry for module '{Name}' is frozen; cannot Register "
                    + $"'{swiftTypeName.ModuleQualifiedName}' after the freeze point. Post-freeze, only "
                    + "ApplyEmissionResult may mutate records (emission-discovered facts only).");
            }

            switch (policy)
            {
                case ConflictPolicy.KeepExisting:
                    // First-wins: keep the canonical record; a later differing registration is
                    // ignored, logged so the dropped write is visible rather than silent.
                    _typeRecords.AddOrUpdate(
                        swiftTypeName,
                        record,
                        (key, existing) =>
                        {
                            if (_logger != null && !ReferenceEquals(existing, record) && !existing.Equals(record))
                            {
                                _logger.LogInformation(
                                    "SWIFTBIND024: type-registry collision in module '{Module}': '{Type}' is already "
                                    + "registered ({ExistingKind}); a differing registration ({NewKind}) was ignored "
                                    + "(keep-existing).",
                                    Name, key, existing.Kind, record.Kind);
                            }

                            return existing;
                        });
                    break;

                case ConflictPolicy.Overwrite:
                default:
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
                                        + "{ExistingKind} and is being overwritten as {NewKind} (overwrite).",
                                        Name, key, existing.Kind, record.Kind);
                                }
                                else
                                {
                                    _logger.LogInformation(
                                        "SWIFTBIND024: type-registry overwrite in module '{Module}': record for "
                                        + "'{Type}' ({Kind}) was overwritten with different content.",
                                        Name, key, record.Kind);
                                }
                            }

                            return record;
                        });
                    break;
            }
        }

        /// <summary>
        /// Default-policy convenience over <see cref="Register(SwiftTypeName, TypeRecord, ConflictPolicy)"/>
        /// using <see cref="ConflictPolicy.Overwrite"/> — the historical unconditional last-write-wins
        /// behavior. Retained for the many registration/test sites that do not need to name a policy;
        /// production write sites that DO care call the three-argument <see cref="Register"/> directly so
        /// their first-wins vs last-wins intent is explicit. Still honors the freeze (throws SWIFTBIND045
        /// post-<see cref="Freeze"/>) and the SWIFTBIND024 collision logging, since it routes through the
        /// same primitive.
        /// </summary>
        public void RegisterType(SwiftTypeName swiftTypeName, TypeRecord record)
            => Register(swiftTypeName, record, ConflictPolicy.Overwrite);

        /// <summary>
        /// Finding 47: the sole sanctioned post-freeze mutation — overwrites an already-registered
        /// record with one carrying emission-discovered facts. Bypasses the freeze guard by design;
        /// callers reach it only through <see cref="TypeDatabase.ApplyEmissionResult"/>, which builds
        /// the new record by applying a <see cref="TypeEmissionResult"/> onto the existing one. Does
        /// not create new keys: emission facts only ever refine a record that registration already
        /// produced.
        /// </summary>
        internal void ApplyEmissionUpdate(SwiftTypeName swiftTypeName, TypeRecord record)
        {
            _typeRecords[swiftTypeName] = record;
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
