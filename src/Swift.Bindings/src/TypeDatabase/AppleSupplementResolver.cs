// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Reflection;
using BindingsGeneration.AppleTypesManifest;
using Newtonsoft.Json;
using Swift.Runtime;

namespace BindingsGeneration;

/// <summary>
/// Resolves Swift identities published by <c>SwiftBindings.Apple</c> into synthetic
/// <see cref="TypeRecord"/> instances that point at the supplement's managed projection.
/// The resolver runs AFTER <see cref="TypeOwnerRegistry"/> assigns canonical identity —
/// it is a policy-neutral source of projections, not an identity authority.
/// </summary>
/// <remarks>
/// <para>The manifest (<c>apple-types-manifest/manifest.json</c>) is embedded as a generator
/// resource so the resolver is self-contained — no disk I/O at runtime and no coupling to
/// the consumer's SDK install path. Each manifest entry yields a synthetic TypeRecord whose
/// CSharpTypeName matches the namespace + leaf declaration name the supplement actually
/// emits, letting downstream marshalling code reference the projection by symbolic name.</para>
/// <para>The resolver only produces a hit when <see cref="TypeOwnerRegistry.Resolve"/> returns
/// <see cref="TypeOwnerKind.AppleSupplement"/> AND the identity is present in the manifest.
/// Registry-owned Apple modules whose types are not (yet) in the manifest fall through so
/// the surrounding fallback chain (ObjC synthetic bridging, unsupported) still handles them.</para>
/// </remarks>
internal static class AppleSupplementResolver
{
    private const string ManifestResourceName = "Swift.Bindings.apple-types-manifest.json";

    // Keyed on Swift identity (module-qualified, generic-stripped).
    private static readonly Lazy<IReadOnlyDictionary<string, TypeRecord>> s_records =
        new(LoadManifestAndBuildRecords, isThreadSafe: true);

    /// <summary>
    /// Tries to resolve a Swift identity to a synthetic <see cref="TypeRecord"/> for the
    /// Apple supplement. Callers are expected to have already consulted the primary type
    /// database — this method is the fallback owning identities the consumer assembly does
    /// not carry natively.
    /// </summary>
    /// <param name="swiftTypeName">
    /// The module-qualified Swift identity. Nested identities like
    /// <c>Foundation.Locale.Language</c> are supported — the declaration path in the
    /// manifest entry determines the leaf C# type name.
    /// </param>
    /// <param name="currentlyGeneratingModule">
    /// Swift module currently being emitted. Passed to <see cref="TypeOwnerRegistry.Resolve"/>
    /// so same-module generation isn't hijacked by the supplement.
    /// </param>
    public static bool TryResolve(
        SwiftTypeName swiftTypeName,
        string? currentlyGeneratingModule,
        out TypeRecord record)
    {
        var identity = swiftTypeName.ModuleQualifiedName;
        var owner = TypeOwnerRegistry.Resolve(identity, currentlyGeneratingModule);
        if (owner.Kind != TypeOwnerKind.AppleSupplement)
        {
            record = null!;
            return false;
        }

        if (s_records.Value.TryGetValue(identity, out var hit))
        {
            record = hit with { SwiftTypeName = swiftTypeName };
            return true;
        }

        record = null!;
        return false;
    }

    /// <summary>
    /// Managed namespaces the <c>SwiftBindings.Apple</c> assembly declares, excluding the bare
    /// <c>Swift</c> namespace (which <c>SwiftBindings.Runtime</c> also declares and which is
    /// always referenced anyway, so it carries no supplement signal).
    /// </summary>
    /// <remarks>
    /// This — not the manifest — is the ownership oracle for "does referencing this projection
    /// require the supplement package?". The manifest is a deliberately positive-only include
    /// list: the hand-rolled canonicals compiled straight into
    /// <c>Swift.Bindings.Apple/Sources/</c> (<c>Data</c>, <c>URL</c>, <c>URLRequest</c>,
    /// <c>Measurement</c>, <c>AnyError</c>, <c>ManagedSettings.Token</c>, <c>SwiftUI.Text</c>)
    /// are excluded from it on purpose so the manifest-driven emitter cannot shadow them. Those
    /// types reach the generator through the XML type databases instead, and keying the
    /// supplement reference off manifest membership therefore misses every one of them —
    /// emitting C# that names an assembly the csproj does not reference.
    /// Pinned against drift by <c>SupplementNamespaceOwnershipTests</c>, which reads the
    /// supplement's own sources and asserts this set matches the namespaces it declares.
    /// </remarks>
    private static readonly string[] s_ownedNamespaces =
    {
        "Swift.ActivityKit",
        "Swift.CryptoKit",
        "Swift.Foundation",
        "Swift.ManagedSettings",
        "Swift.SwiftUI",
    };

    /// <summary>
    /// The managed namespaces owned by <c>SwiftBindings.Apple</c>, in ordinal order.
    /// </summary>
    public static IReadOnlyList<string> OwnedNamespaces => s_ownedNamespaces;

    /// <summary>
    /// True when a resolved projection's managed namespace is supplied by the
    /// <c>SwiftBindings.Apple</c> package. Nested namespaces count: a manifest entry whose
    /// declaration path folds into the namespace (e.g.
    /// <c>Swift.CryptoKit.P256.Signing</c>) is still supplement-owned.
    /// </summary>
    public static bool IsSupplementOwnedNamespace(string? managedNamespace)
    {
        if (string.IsNullOrEmpty(managedNamespace))
            return false;

        foreach (var owned in s_ownedNamespaces)
        {
            if (managedNamespace.Length == owned.Length)
            {
                if (string.Equals(managedNamespace, owned, StringComparison.Ordinal))
                    return true;
            }
            else if (managedNamespace.Length > owned.Length &&
                     managedNamespace[owned.Length] == '.' &&
                     managedNamespace.StartsWith(owned, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the manifest contains an entry for the given Swift identity.</summary>
    public static bool Contains(string swiftIdentity) => s_records.Value.ContainsKey(swiftIdentity);

    /// <summary>Number of types published by the supplement. Useful for smoke tests.</summary>
    public static int TypeCount => s_records.Value.Count;

    private static IReadOnlyDictionary<string, TypeRecord> LoadManifestAndBuildRecords()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ManifestResourceName);
        if (stream is null)
        {
            // Missing embed is fatal for generator correctness — fail loud so the resolver
            // doesn't silently behave as if no Apple types existed.
            throw new InvalidOperationException(
                $"Embedded resource '{ManifestResourceName}' not found. Ensure the manifest " +
                "is wired up as EmbeddedResource in Swift.Bindings.csproj.");
        }

        using var reader = new StreamReader(stream);
        var manifest = JsonConvert.DeserializeObject<Manifest>(reader.ReadToEnd())
            ?? throw new InvalidOperationException("Failed to deserialize apple-types-manifest.json.");

        var result = new Dictionary<string, TypeRecord>(StringComparer.Ordinal);
        foreach (var (moduleName, module) in manifest.Modules)
        {
            foreach (var entry in module.Types)
            {
                // Skip identities the registry pins to a different owner (legacy canonicals
                // like Foundation.Date). The Apple supplement emitter applies the same guard,
                // so records here and emitted code stay in lockstep.
                var owner = TypeOwnerRegistry.Resolve(entry.SwiftIdentity);
                if (owner.Kind != TypeOwnerKind.AppleSupplement)
                    continue;

                if (entry.ManagedProjection.DeclarationPath.Count == 0)
                    continue;

                var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(entry.SwiftIdentity);

                // The supplement emitter places the type at `namespace`.`declaration_path[^1]`
                // with outer declaration path segments acting as partial struct wrappers —
                // the C# identity consumers see for an entry like CryptoKit.P256.Signing.ECDSASignature
                // is `Swift.CryptoKit.P256.Signing.ECDSASignature`. We fold the outer segments
                // into the namespace so CSharpTypeName.FromNamespaceAndName produces the
                // fully-qualified form the emitted code actually declares.
                var declPath = entry.ManagedProjection.DeclarationPath;
                var leafName = declPath[^1];
                var nsBuilder = entry.ManagedProjection.Namespace;
                for (var i = 0; i < declPath.Count - 1; i++)
                    nsBuilder = nsBuilder + "." + declPath[i];
                var csharpTypeName = CSharpTypeName.FromNamespaceAndName(nsBuilder, leafName);

                var flags = TypeRecordFlags.RequiresMemoryManagement;
                if (entry.Frozen)
                    flags |= TypeRecordFlags.Frozen;

                var metadataAccessor = entry.MetadataAccessor?.Symbol ?? string.Empty;

                var kind = entry.Kind switch
                {
                    "struct" => TypeRecordKind.Struct,
                    "enum" => TypeRecordKind.Enum,
                    "class" => TypeRecordKind.Class,
                    _ => TypeRecordKind.Struct,
                };

                result[entry.SwiftIdentity] = new TypeRecord
                {
                    CSharpTypeName = csharpTypeName,
                    SwiftTypeName = swiftTypeName,
                    MetadataAccessor = metadataAccessor,
                    Flags = flags,
                    Kind = kind,
                };
            }
        }
        return result;
    }
}
