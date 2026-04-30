// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BindingsGeneration.Producers;

/// <summary>
/// AOT-safe deserialization context for the SwiftInterfaceParser JSON contract.
/// System.Text.Json's reflection-based path is gated by IL2026/IL3050; declaring the
/// shape here lets the source generator produce trim/AOT-safe metadata.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    ReadCommentHandling = JsonCommentHandling.Disallow)]
[JsonSerializable(typeof(InterfaceFactsJson))]
[JsonSerializable(typeof(InterfaceFactsJsonPayload))]
[JsonSerializable(typeof(SourcePositionJson))]
internal partial class InterfaceFactsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// JSON contract between the SwiftSyntax host program (tools/SwiftInterfaceParser) and the
/// .NET aggregator. Property names are lowerCamelCase to match
/// <c>JsonNamingPolicy.CamelCase</c>; the deserializer rejects unknown properties so a host
/// binary that drifts from the .NET schema fails fast instead of silently dropping data.
/// <para/>
/// SCHEMA VERSIONING: <see cref="SchemaVersion"/> on the host output must match
/// <see cref="ExpectedSchemaVersion"/> on the .NET side. Adding a new optional fact field
/// is breaking under <c>UnmappedMemberHandling.Disallow</c>; bump the version and update
/// both ends.
/// </summary>
internal sealed class InterfaceFactsJson
{
    /// <summary>Bump in lockstep with <c>kSchemaVersion</c> in
    /// <c>tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/Output.swift</c>.</summary>
    public const int ExpectedSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    /// <summary>Names of <see cref="InterfaceFactKind"/> values the producer populated.
    /// Authoritative coverage signal for the aggregator. Empty string entries or unknown
    /// names cause hard error during conversion.</summary>
    [JsonPropertyName("coveredFacts")]
    public List<string> CoveredFacts { get; set; } = new();

    [JsonPropertyName("facts")]
    public InterfaceFactsJsonPayload Facts { get; set; } = new();
}

/// <summary>Mirror of <see cref="PartialSwiftInterfaceFacts"/>'s wire shape. Every fact
/// is nullable; nullness alone is NOT the coverage signal — the aggregator combines
/// <see cref="InterfaceFactsJson.CoveredFacts"/> with the payload to merge correctly.</summary>
internal sealed class InterfaceFactsJsonPayload
{
    [JsonPropertyName("mainActorTypes")]
    public List<string>? MainActorTypes { get; set; }

    [JsonPropertyName("mainActorTypePositions")]
    public Dictionary<string, SourcePositionJson>? MainActorTypePositions { get; set; }

    // Session 1 only emits the two MainActor* facts. As subsequent sessions migrate
    // additional facts, this DTO grows in lockstep with InterfaceFactsJson.SchemaVersion.
}

internal sealed class SourcePositionJson
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }
}
