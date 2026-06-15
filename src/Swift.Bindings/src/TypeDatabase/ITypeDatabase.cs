// Copyright (c) Microsoft Corporation.
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
    public bool IsTypeProcessed(SwiftTypeName swiftTypeName);

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
    /// Updates a type record in the database (e.g., to rename a nested type's C# name).
    /// </summary>
    /// <param name="name">The Swift type name.</param>
    /// <param name="record">The updated type record.</param>
    public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record);

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
