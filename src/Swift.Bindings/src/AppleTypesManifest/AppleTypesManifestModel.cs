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
    [JsonProperty("conformance_descriptors", Order = 13)] public List<ConformanceDescriptor> ConformanceDescriptors { get; set; } = new();
    [JsonProperty("status", Order = 14)] public string Status { get; set; } = "generated";
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

    public void MergeFrom(Availability other)
    {
        Ios ??= other.Ios;
        Maccatalyst ??= other.Maccatalyst;
        Tvos ??= other.Tvos;
        Macos ??= other.Macos;
    }
}
