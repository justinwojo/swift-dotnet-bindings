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
[JsonSerializable(typeof(AvailabilityAnnotationJson))]
[JsonSerializable(typeof(ProtocolExtensionMethodJson))]
[JsonSerializable(typeof(ExtensionMemberCandidateJson))]
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
/// <see cref="ExpectedSchemaVersion"/> on the .NET side. The host binary is vendored in the
/// NuGet package alongside the .NET generator, so both sides ship and update in lockstep —
/// there is no in-the-wild "older .NET reading newer host output" path under normal use.
/// Policy:
/// <list type="bullet">
/// <item><b>Additive evolution stays at v1.</b> Adding a new optional fact field
///   (new property on <see cref="InterfaceFactsJsonPayload"/>, plus the matching field on
///   <see cref="Facts"/> in <c>tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/Output.swift</c>,
///   plus a new <see cref="InterfaceFactKind"/> enum value when applicable) does NOT need
///   a version bump as long as both ends move together in the same commit. The
///   <c>UnmappedMemberHandling.Disallow</c> check still catches accidental one-sided drift
///   at deserialize time.</item>
/// <item><b>Bump to v2 only on shape change.</b> Renaming a property, changing a property
///   type, removing a property, or changing the meaning of an existing field is breaking;
///   that's when <see cref="ExpectedSchemaVersion"/> moves.</item>
/// </list>
/// Each session adding facts to <see cref="InterfaceFactsJsonPayload"/> is additive, so v1
/// stays put through the M2 migration window.
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
/// <see cref="InterfaceFactsJson.CoveredFacts"/> with the payload to merge correctly.
/// New fact fields are additive (see schema-versioning policy above); pair every new
/// field here with the matching field in <c>Output.swift</c>'s <c>Facts</c> struct and
/// the matching <see cref="InterfaceFactKind"/> enum entry.</summary>
internal sealed class InterfaceFactsJsonPayload
{
    [JsonPropertyName("mainActorTypes")]
    public List<string>? MainActorTypes { get; set; }

    [JsonPropertyName("mainActorTypePositions")]
    public Dictionary<string, SourcePositionJson>? MainActorTypePositions { get; set; }

    // Session 2 — actor isolation cluster (5 facts).
    [JsonPropertyName("actorIsolatedMembers")]
    public List<string>? ActorIsolatedMembers { get; set; }

    [JsonPropertyName("mainActorIsolatedMembers")]
    public List<string>? MainActorIsolatedMembers { get; set; }

    [JsonPropertyName("nonisolatedMembers")]
    public List<string>? NonisolatedMembers { get; set; }

    [JsonPropertyName("customActorTypes")]
    public List<string>? CustomActorTypes { get; set; }

    [JsonPropertyName("customActorIsolatorMap")]
    public Dictionary<string, string>? CustomActorIsolatorMap { get; set; }

    // Session 2 — availability cluster (2 facts).
    [JsonPropertyName("availabilityAnnotations")]
    public Dictionary<string, List<AvailabilityAnnotationJson>>? AvailabilityAnnotations { get; set; }

    [JsonPropertyName("availabilityAnnotationPositions")]
    public Dictionary<string, SourcePositionJson>? AvailabilityAnnotationPositions { get; set; }

    // Session 2 — typed throws.
    [JsonPropertyName("typedThrowsErrors")]
    public Dictionary<string, string>? TypedThrowsErrors { get; set; }

    // Session 3 — type & member collection.
    [JsonPropertyName("publicTypeNames")]
    public List<string>? PublicTypeNames { get; set; }

    [JsonPropertyName("internalMemberKeys")]
    public List<string>? InternalMemberKeys { get; set; }

    [JsonPropertyName("publicMemberNames")]
    public List<string>? PublicMemberNames { get; set; }

    [JsonPropertyName("markerProtocolConformances")]
    public Dictionary<string, List<string>>? MarkerProtocolConformances { get; set; }

    // Session 3 — enum facts.
    [JsonPropertyName("enumCaseLabels")]
    public Dictionary<string, List<string?>>? EnumCaseLabels { get; set; }

    [JsonPropertyName("enumCaseRawValues")]
    public Dictionary<string, string>? EnumCaseRawValues { get; set; }

    // Session 3 — signature facts.
    [JsonPropertyName("parameterNames")]
    public Dictionary<string, List<string>>? ParameterNames { get; set; }

    [JsonPropertyName("defaultParameterValues")]
    public Dictionary<string, List<string?>>? DefaultParameterValues { get; set; }

    [JsonPropertyName("autoclosureParameters")]
    public Dictionary<string, List<bool>>? AutoclosureParameters { get; set; }

    [JsonPropertyName("subscriptLabels")]
    public Dictionary<string, List<string>>? SubscriptLabels { get; set; }

    [JsonPropertyName("variadicMembers")]
    public List<string>? VariadicMembers { get; set; }

    // Session 3 — protocol-level facts.
    [JsonPropertyName("conventionCProtocols")]
    public List<string>? ConventionCProtocols { get; set; }

    [JsonPropertyName("conventionCProtocolPositions")]
    public Dictionary<string, SourcePositionJson>? ConventionCProtocolPositions { get; set; }

    [JsonPropertyName("hiddenRequirementProtocols")]
    public Dictionary<string, List<string>>? HiddenRequirementProtocols { get; set; }

    // M2 S4 — non-fact methods migrated behind the producer abstraction.
    [JsonPropertyName("protocolNames")]
    public List<string>? ProtocolNames { get; set; }

    [JsonPropertyName("protocolExtensionMethods")]
    public Dictionary<string, List<ProtocolExtensionMethodJson>>? ProtocolExtensionMethods { get; set; }

    [JsonPropertyName("extensionMemberCandidates")]
    public List<ExtensionMemberCandidateJson>? ExtensionMemberCandidates { get; set; }
}

/// <summary>Wire shape for <see cref="ProtocolExtensionMethodDecl"/>. Mirrors the
/// model's required fields. <c>protocolQualifiedName</c> is excluded from the wire
/// because it's redundant with the dictionary key — the .NET-side conversion fills
/// it from the key when materializing the decl.</summary>
internal sealed class ProtocolExtensionMethodJson
{
    [JsonPropertyName("methodName")]
    public string MethodName { get; set; } = string.Empty;

    [JsonPropertyName("rawSignature")]
    public string RawSignature { get; set; } = string.Empty;

    [JsonPropertyName("printedName")]
    public string PrintedName { get; set; } = string.Empty;

    [JsonPropertyName("returnsSelf")]
    public bool ReturnsSelf { get; set; }

    [JsonPropertyName("isMainActorIsolated")]
    public bool IsMainActorIsolated { get; set; }

    [JsonPropertyName("isStatic")]
    public bool IsStatic { get; set; }

    [JsonPropertyName("isProperty")]
    public bool IsProperty { get; set; }

    [JsonPropertyName("hasSetter")]
    public bool HasSetter { get; set; }

    [JsonPropertyName("isDeprecated")]
    public bool IsDeprecated { get; set; }

    [JsonPropertyName("isMutating")]
    public bool IsMutating { get; set; }

    [JsonPropertyName("whereConstraints")]
    public List<string> WhereConstraints { get; set; } = new();
}

/// <summary>Wire shape for <see cref="ExtensionMemberCandidate"/>. Same payload as
/// <see cref="ProtocolExtensionMethodJson"/> plus the verbatim
/// <c>extendedTypeName</c> (no module-context partitioning happens host-side).</summary>
internal sealed class ExtensionMemberCandidateJson
{
    [JsonPropertyName("extendedTypeName")]
    public string ExtendedTypeName { get; set; } = string.Empty;

    [JsonPropertyName("methodName")]
    public string MethodName { get; set; } = string.Empty;

    [JsonPropertyName("rawSignature")]
    public string RawSignature { get; set; } = string.Empty;

    [JsonPropertyName("printedName")]
    public string PrintedName { get; set; } = string.Empty;

    [JsonPropertyName("returnsSelf")]
    public bool ReturnsSelf { get; set; }

    [JsonPropertyName("isMainActorIsolated")]
    public bool IsMainActorIsolated { get; set; }

    [JsonPropertyName("isStatic")]
    public bool IsStatic { get; set; }

    [JsonPropertyName("isProperty")]
    public bool IsProperty { get; set; }

    [JsonPropertyName("hasSetter")]
    public bool HasSetter { get; set; }

    [JsonPropertyName("isDeprecated")]
    public bool IsDeprecated { get; set; }

    [JsonPropertyName("isMutating")]
    public bool IsMutating { get; set; }

    [JsonPropertyName("whereConstraints")]
    public List<string> WhereConstraints { get; set; } = new();
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

/// <summary>Wire shape for <see cref="AvailabilityAnnotation"/>. Mirrors the record
/// fields exactly; conversion is a 1:1 copy in <see cref="SwiftSyntaxInterfaceFactsProducer"/>.</summary>
internal sealed class AvailabilityAnnotationJson
{
    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("introducedVersion")]
    public string? IntroducedVersion { get; set; }

    [JsonPropertyName("deprecatedVersion")]
    public string? DeprecatedVersion { get; set; }

    [JsonPropertyName("obsoletedVersion")]
    public string? ObsoletedVersion { get; set; }

    [JsonPropertyName("isUnconditionallyDeprecated")]
    public bool IsUnconditionallyDeprecated { get; set; }

    [JsonPropertyName("isUnconditionallyUnavailable")]
    public bool IsUnconditionallyUnavailable { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("renamed")]
    public string? Renamed { get; set; }
}
