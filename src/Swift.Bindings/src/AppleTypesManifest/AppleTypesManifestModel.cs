// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Newtonsoft.Json;

namespace BindingsGeneration.AppleTypesManifest;

// Serializable model mirroring `src/Swift.Bindings.Sdk/tools/apple-types-manifest/schema.json`.
// Field order and naming here are contractual — they drive the on-disk JSON shape so the
// hand-readable manifest stays stable across regenerations. See
// `src/docs/apple-swift-types-architecture.md` §Q7 for the format contract.

public sealed class Manifest
{
    [JsonProperty("$schema", Order = -100)] public string? Schema { get; set; } = "./schema.json";
    [JsonProperty("manifest_version", Order = -90)] public int ManifestVersion { get; set; } = 1;
    [JsonProperty("generated_by", Order = -80)] public string? GeneratedBy { get; set; }
    [JsonProperty("generated_at", Order = -70)] public string? GeneratedAt { get; set; }
    [JsonProperty("sdk_train", Order = -60)] public required SdkTrain SdkTrain { get; set; }
    [JsonProperty("modules", Order = -50)] public SortedDictionary<string, Module> Modules { get; set; } = new(StringComparer.Ordinal);
}

public sealed class SdkTrain
{
    [JsonProperty("major", Order = 1)] public required int Major { get; set; }
    [JsonProperty("label", Order = 2, NullValueHandling = NullValueHandling.Ignore)] public string? Label { get; set; }
    [JsonProperty("platforms", Order = 3)] public Availability Platforms { get; set; } = new();
}

public sealed class Module
{
    [JsonProperty("types", Order = 1)] public List<TypeEntry> Types { get; set; } = new();
    [JsonProperty("typealiases", Order = 2)] public List<TypeAliasEntry> Typealiases { get; set; } = new();
}

public sealed class TypeEntry
{
    [JsonProperty("swift_identity", Order = 1)] public required string SwiftIdentity { get; set; }
    [JsonProperty("managed_projection", Order = 2)] public required ManagedRef ManagedProjection { get; set; }
    [JsonProperty("abi_carrier", Order = 3)] public required ManagedRef AbiCarrier { get; set; }
    [JsonProperty("kind", Order = 4)] public required string Kind { get; set; }
    [JsonProperty("frozen", Order = 5)] public bool Frozen { get; set; }
    [JsonProperty("size", Order = 6)] public int? Size { get; set; }
    [JsonProperty("alignment", Order = 7)] public int? Alignment { get; set; }
    [JsonProperty("stride", Order = 8)] public int? Stride { get; set; }
    [JsonProperty("metadata_accessor", Order = 9)] public MetadataAccessor? MetadataAccessor { get; set; }
    [JsonProperty("value_witness", Order = 10)] public ValueWitness ValueWitness { get; set; } = new();
    [JsonProperty("storage_strategy", Order = 11)] public string StorageStrategy { get; set; } = "vwt_opaque";
    [JsonProperty("sequential_layout_whitelisted", Order = 12)] public bool SequentialLayoutWhitelisted { get; set; }
    [JsonProperty("sequential_layout_evidence", Order = 13, NullValueHandling = NullValueHandling.Ignore)] public SequentialLayoutEvidence? SequentialLayoutEvidence { get; set; }
    [JsonProperty("conformance_descriptors", Order = 14)] public List<ConformanceDescriptor> ConformanceDescriptors { get; set; } = new();
    [JsonProperty("status", Order = 15)] public string Status { get; set; } = "generated";
}

/// <summary>
/// Evidence that a type is safe to emit with sequential (stored-field) layout instead of
/// the default VWT-backed opaque storage. Every condition must be affirmative — the
/// emitter refuses the sequential path otherwise and falls back to VWT-opaque while
/// failing a validation gate, so the opt-in cannot silently become a memory-corruption
/// vector when one of the inputs is missing or incorrect.
/// </summary>
/// <remarks>
/// The three fields cover the conditions that the 6-gate checklist (frozen, non-generic,
/// size+alignment) does NOT cover: exhaustive stored-field layout knowledge, ARC/destroy
/// handling strategy, and a live-SDK round-trip validation result. Evidence lives in the
/// manifest (not a separate approval record) so the whitelist claim and its justification
/// travel together through git.
/// </remarks>
public sealed class SequentialLayoutEvidence
{
    /// <summary>
    /// True when every stored field's layout is fully known to the manifest authors,
    /// and the declaration path + sizes match the SDK's ABI for the exact Swift compiler
    /// version the supplement ships against. False leaves the gate closed.
    /// </summary>
    [JsonProperty("stored_fields_known", Order = 1)] public bool StoredFieldsKnown { get; set; }

    /// <summary>
    /// How the type's copy/destroy semantics are handled in emitted code. "trivial" means
    /// the type has no ARC / resource cleanup obligations (bitwise-copyable stored fields
    /// only). "explicit_vwt" means the emitter wraps copies/destroys through the VWT
    /// (sequential layout but non-trivial ARC).
    /// </summary>
    [JsonProperty("copy_destroy_handling", Order = 2)] public string CopyDestroyHandling { get; set; } = "";

    /// <summary>
    /// True when a live round-trip test (construct in Swift, marshal to C#, marshal back
    /// to Swift, validate field-wise equality) has passed against the target SDK. The
    /// validation gate flags a whitelist entry whose roundtrip has not yet been performed.
    /// </summary>
    [JsonProperty("roundtrip_validated", Order = 3)] public bool RoundtripValidated { get; set; }
}

public sealed class ManagedRef
{
    [JsonProperty("namespace", Order = 1)] public required string Namespace { get; set; }
    [JsonProperty("declaration_path", Order = 2)] public required List<string> DeclarationPath { get; set; } = new();
}

public sealed class MetadataAccessor
{
    [JsonProperty("symbol", Order = 1)] public required string Symbol { get; set; }
    [JsonProperty("library", Order = 2)] public required string Library { get; set; }
    [JsonProperty("availability", Order = 3)] public Availability Availability { get; set; } = new();
    [JsonProperty("weak_link", Order = 4)] public bool WeakLink { get; set; }
}

public sealed class ValueWitness
{
    [JsonProperty("source", Order = 1)] public string Source { get; set; } = "metadata";
    [JsonProperty("static_symbol", Order = 2, NullValueHandling = NullValueHandling.Ignore)] public string? StaticSymbol { get; set; }
    [JsonProperty("trivial", Order = 3)] public bool Trivial { get; set; }
}

public sealed class ConformanceDescriptor
{
    [JsonProperty("protocol_identity", Order = 1)] public required string ProtocolIdentity { get; set; }
    [JsonProperty("descriptor_symbol", Order = 2)] public required string DescriptorSymbol { get; set; }
    [JsonProperty("owning_module", Order = 3, NullValueHandling = NullValueHandling.Ignore)] public string? OwningModule { get; set; }
    [JsonProperty("availability", Order = 4, NullValueHandling = NullValueHandling.Ignore)] public Availability? Availability { get; set; }
}

public sealed class TypeAliasEntry
{
    [JsonProperty("alias_identity", Order = 1)] public required string AliasIdentity { get; set; }
    [JsonProperty("target_identity", Order = 2)] public required string TargetIdentity { get; set; }
    [JsonProperty("availability", Order = 3, NullValueHandling = NullValueHandling.Ignore)] public Availability? Availability { get; set; }
}

public sealed class Availability
{
    [JsonProperty("ios", Order = 1)] public string? Ios { get; set; }
    [JsonProperty("maccatalyst", Order = 2)] public string? Maccatalyst { get; set; }
    [JsonProperty("tvos", Order = 3)] public string? Tvos { get; set; }
    [JsonProperty("macos", Order = 4)] public string? Macos { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Ios is null && Maccatalyst is null && Tvos is null && Macos is null;
}
