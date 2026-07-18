// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Type database. Holds module databases and type records for Swift types.
/// </summary>
public interface ITypeDatabase
{
    /// <summary>
    /// Checks whether a specific type in a specified module has been processed.
    /// </summary>
    /// <param name="moduleName">The name of the module.</param>
    /// <param name="swiftTypeName">The name of the Swift type.</param>
    /// <returns><c>true</c> if the type has been processed; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// Finding 10: defined as "<see cref="TryGetTypeRecord(SwiftTypeName, out TypeRecord?)"/>
    /// succeeds" — a type the resolver can hand back a record for is, by definition, processed.
    /// This intentionally agrees with <c>TryGetTypeRecord</c> (the historical 3-arm subset that
    /// silently disagreed on supplement / out-of-module / <c>Swift.Error</c> identities is retired).
    /// Callers that mean the narrower "already registered in a loaded module/dependency database"
    /// question — not "resolvable by the full machinery" — must use <see cref="IsTypeRegistered"/>
    /// instead; the parser's duplicate-detection and metadata-accessor decisions depend on that
    /// narrower meaning and would mis-fire on the resolvable definition.
    /// </remarks>
    public bool IsTypeProcessed(SwiftTypeName swiftTypeName);

    /// <summary>
    /// Finding 10: the narrow "is this identity already registered in a loaded module or
    /// dependency database" predicate — the registration question, distinct from
    /// <see cref="IsTypeProcessed"/>'s "is this resolvable by the full machinery" question.
    /// It deliberately excludes the Apple-supplement, out-of-module, and <c>Swift.Error</c> arms
    /// (and records nothing), because its consumers — the parser's duplicate gate and
    /// metadata-accessor choice — must NOT treat a supplement-owned same-module type as
    /// "already processed" (doing so would throw a spurious duplicate or pick the wrong
    /// metadata symbol). Defaults to <see cref="IsTypeProcessed"/> so the many test mocks that
    /// implement <see cref="ITypeDatabase"/> need not override it; the real
    /// <see cref="TypeDatabase"/> overrides it with the registration-only lookup.
    /// </summary>
    public bool IsTypeRegistered(SwiftTypeName swiftTypeName) => IsTypeProcessed(swiftTypeName);

    /// <summary>
    /// Attempts to retrieve the type record for a specified type identifier within a module.
    /// </summary>
    /// <param name="moduleName">The name of the module.</param>
    /// <param name="swiftTypeName">The name of the Swift type.</param>
    /// <param name="record">
    /// When this method returns, contains the type record if found; otherwise, <c>null</c>.
    /// </param>
    /// <returns><c>true</c> if the type record was found; otherwise, <c>false</c>.</returns>
    public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record);

    /// <summary>
    /// Finding 10: the type-record lookup with the Apple-supplement arm omitted — Arms 2–6 of
    /// the cascade (module DB, <c>Ref</c>-variant, out-of-module, type-alias, <c>Swift.Error</c>),
    /// everything except the leading supplement consult. Used by the resolver leg
    /// (<c>DatabaseLookupStrategy</c>) so the supplement is consulted exactly once — at the
    /// dedicated <c>AppleSupplementStrategy</c> that already runs earlier in the strategy order —
    /// retiring the "supplement consulted twice at two precedence positions" duplication and its
    /// INVARIANT comment. Behavior-preserving: any identity the supplement owns is claimed by the
    /// earlier strategy and never reaches this method. Defaults to
    /// <see cref="TryGetTypeRecord(SwiftTypeName, out TypeRecord?)"/> so test mocks (which have no
    /// supplement arm) are unaffected; the real <see cref="TypeDatabase"/> overrides it.
    /// </summary>
    public bool TryGetTypeRecordWithoutSupplement(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
        => TryGetTypeRecord(swiftTypeName, out record);

    /// <summary>
    /// Retrieves the library path for the specified module.
    /// </summary>
    /// <param name="moduleName">The name of the module.</param>
    /// <returns>The file path of the library associated with the module.</returns>
    /// <exception cref="Exception">Thrown if the library path does not exist for the specified module.</exception>
    public string GetLibraryPath(string moduleName);

    /// <summary>
    /// Gets the library name for async wrapper functions.
    /// This is where the generated Swift async wrappers are compiled to.
    /// If null, falls back to the module's library path.
    /// </summary>
    public string? AsyncLibraryName { get; }

    /// <summary>
    /// The explicit <see cref="BindingsGeneration.GenerationMode"/> for this run, derived once
    /// from whether a companion wrapper library is configured. Prefer reading this (or
    /// <c>WrapperValidation.IsXCFrameworkMode</c>) over re-checking <see cref="AsyncLibraryName"/>
    /// emptiness at call sites — it names the single decision rather than copying the sentinel.
    /// </summary>
    public GenerationMode GenerationMode =>
        string.IsNullOrEmpty(AsyncLibraryName) ? GenerationMode.Direct : GenerationMode.XCFramework;

    /// <summary>
    /// General structural write: overwrites the full type record for <paramref name="name"/>.
    /// </summary>
    /// <param name="name">The Swift type name.</param>
    /// <param name="record">The updated type record.</param>
    /// <remarks>
    /// Finding 47: this is a pre-freeze-only structural write. After <see cref="Freeze"/> the
    /// registry is immutable to full-record overwrites — the real <see cref="TypeDatabase"/>
    /// implementation throws (SWIFTBIND045) if called once frozen. The <em>only</em> sanctioned
    /// post-freeze mutation is <see cref="ApplyEmissionResult"/>, which stamps emission-discovered
    /// facts (and nothing else) onto an already-registered record. Production emission no longer
    /// routes through this method at all; it survives as the general/structural write used by
    /// test setup and any pre-freeze full-record rewrite (e.g. a nested type's C# name).
    /// </remarks>
    public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record);

    /// <summary>
    /// Finding 47: the sole sanctioned post-<see cref="Freeze"/> mutation — stamps the
    /// emission-discovered facts in <paramref name="result"/> (interface member count, surviving
    /// class methods, metadata-P/Invoke presence) onto the already-registered record for
    /// <paramref name="name"/>, leaving every structural field untouched. Applied even after the
    /// registry is frozen, because these facts are not knowable until the type's body is emitted.
    /// <para>The default body THROWS rather than no-op'ing. An emission-stamp (a nested-type
    /// rename, a surviving-method count) is committed to by the rest of emission BEFORE it reaches
    /// here, so an <see cref="ITypeDatabase"/> that silently swallowed the stamp would leave the
    /// generated code referencing a name the database never recorded — a silent miscompile. The
    /// real <see cref="TypeDatabase"/> overrides this; a test double that never drives an
    /// emission-stamp path never calls it, so the throw fires only on a genuine swallow rather than
    /// on every mock that merely implements the interface.</para>
    /// </summary>
    /// <param name="name">The Swift type name whose record receives the emission facts.</param>
    /// <param name="result">The emission-discovered facts to apply.</param>
    public void ApplyEmissionResult(SwiftTypeName name, TypeEmissionResult result)
        => throw new System.NotImplementedException(
            $"ITypeDatabase.ApplyEmissionResult('{name}') reached the no-implementation default. "
            + "An emission-discovered fact would be silently discarded; the implementing database "
            + "must override ApplyEmissionResult (the concrete TypeDatabase does).");

    /// <summary>
    /// Writes <paramref name="record"/> back verbatim, undoing the emission stamps a discarded
    /// emission attempt applied to <paramref name="name"/>.
    /// <para>This is the inverse of <see cref="ApplyEmissionResult"/> and exists only because that
    /// method has no inverse of its own: it merges, treating a null field as "leave unchanged", so no
    /// result value can express "restore the previous state". Like the forward path this bypasses the
    /// freeze guard, and for the same reason — it writes only emission-discovered facts, just older
    /// ones. It is reached exclusively from the emission attempt loop's discard path.</para>
    /// <para>The default throws for the same reason <see cref="ApplyEmissionResult"/>'s does: a
    /// database that silently declined to restore would let a discarded attempt's stamps survive into
    /// the attempt that replaces it, which is precisely the leak the attempt loop exists to prevent.
    /// A test double that never stamps never restores, so the throw fires only on a real swallow.</para>
    /// </summary>
    /// <param name="name">The Swift type name whose record is being rolled back.</param>
    /// <param name="record">The record as it stood before the discarded attempt stamped it.</param>
    public void RestoreEmissionRecord(SwiftTypeName name, TypeRecord record)
        => throw new System.NotImplementedException(
            $"ITypeDatabase.RestoreEmissionRecord('{name}') reached the no-implementation default. "
            + "A discarded emission attempt's stamps would survive into the retry; the implementing "
            + "database must override RestoreEmissionRecord (the concrete TypeDatabase does).");

    /// <summary>
    /// Finding 47: marks the registry immutable to structural writes. After this,
    /// <see cref="UpdateTypeRecord"/> and the module-level registration path throw (SWIFTBIND045);
    /// only <see cref="ApplyEmissionResult"/> may still mutate records. No-op default for test
    /// mocks; the real <see cref="TypeDatabase"/> overrides it to freeze every loaded module.
    /// </summary>
    public void Freeze() { }

    /// <summary>
    /// Gets <c>(namespace, proxyName)</c> pairs for proxy classes that were suppressed by
    /// previously generated (dependency) modules whose database XML has been loaded into
    /// this type database. The umbrella-aware existential marshaler can emit cross-module
    /// qualified references (<c>{Namespace}.SwiftInterop.{ProxyName}</c>) to a proxy that
    /// lives in a dependency; if that dependency suppressed the proxy, those references must
    /// be stripped during the local module's post-pass. The pair preserves the dependency's
    /// C# namespace (NOT its Swift module name — those diverge under a custom
    /// <c>namespacePattern</c>) so the post-pass can match the exact qualified form the
    /// generator emitted. Matching by simple name across modules would false-positive on a
    /// future module that legitimately emits its own proxy with the same simple class name.
    /// Defaults to empty so the many test mocks that implement <see cref="ITypeDatabase"/>
    /// don't need to override this. The real <see cref="TypeDatabase"/> overrides it.
    /// </summary>
    public IReadOnlyCollection<(string Namespace, string ProxyName)> GetCrossModuleSuppressedProxyClassNames()
        => Array.Empty<(string, string)>();

    /// <summary>
    /// Records a framework-dependency module's parsed <see cref="ModuleDecl"/> so consumer-side
    /// emitters can walk its declarations (e.g. constructor shapes) that the TypeRecord projection
    /// discards. Only <see cref="ModuleTypeDatabase"/> records are retained for type resolution;
    /// the full <see cref="ModuleDecl"/> is otherwise dropped after name precomputation. Defaults
    /// to a no-op so the many test mocks that implement <see cref="ITypeDatabase"/> don't need to
    /// override it. The real <see cref="TypeDatabase"/> overrides it.
    /// </summary>
    public void AddDependencyModuleDecl(ModuleDecl moduleDecl) { }

    /// <summary>
    /// Gets the framework-dependency module declarations retained via
    /// <see cref="AddDependencyModuleDecl"/>. Defaults to empty for test mocks.
    /// </summary>
    public IReadOnlyList<ModuleDecl> GetDependencyModuleDecls() => Array.Empty<ModuleDecl>();

    /// <summary>
    /// Records that a FOREIGN concrete type (one with no local <see cref="TypeDecl"/> in any
    /// processed module — e.g. <c>Swift.Int</c>, <c>Foundation.Date</c>) conforms to a
    /// synthesized underscore PAT whose conformance record swift-api-digester stripped from
    /// the ABI JSON (e.g. <c>AppIntents._IntentValue</c>). Fed by
    /// <c>UnderscoreProtocolSynthesizer.IngestStrippedConformances</c> from the owning module's
    /// swiftinterface extension headers. Defaults to a no-op so the many test mocks that
    /// implement <see cref="ITypeDatabase"/> don't need to override it.
    /// </summary>
    public void RegisterStrippedConformance(SwiftTypeName concreteType, SwiftTypeName protocolName) { }

    /// <summary>
    /// Returns true when <paramref name="concreteType"/> was registered (via
    /// <see cref="RegisterStrippedConformance"/>) as conforming to
    /// <paramref name="protocolName"/>. Consulted by
    /// <c>BoundGenericsHandler.SatisfiesConstraint</c> in its <c>typeArgumentDecl == null</c>
    /// branch so closed bound generics over foreign conformers (e.g. <c>IntentParameter&lt;Int&gt;</c>)
    /// are not skipped. Defaults to false for test mocks.
    /// </summary>
    public bool HasStrippedConformance(SwiftTypeName concreteType, SwiftTypeName protocolName) => false;
}
