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
