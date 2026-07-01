// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

/// JSON contract version. Bump only on shape-change (rename / type change / removal /
/// semantic redefinition). Additive evolution — new optional fact fields, new map
/// dictionaries — stays at the current version because the host binary is vendored in
/// the NuGet package alongside the .NET generator and both sides ship in lockstep. The
/// .NET-side `UnmappedMemberHandling.Disallow` setting still catches accidental
/// one-sided drift at deserialize time, so additive evolution does not require a
/// version bump but does require updating both `Facts` here and
/// `InterfaceFactsJsonPayload` on the .NET side in the same commit.
///
/// v2: `availabilityAnnotations` and `availabilityAnnotationPositions` keys were
/// redefined from "bare `Type.printedName`" to "bare key OR `Type.printedName|paramSig`
/// disambiguation key" so per-overload availability stops broadcasting across siblings.
/// A v1 producer's bare-key-only output silently mis-maps under the v2 consumer's disamb
/// lookup, which is exactly the failure mode the schema check exists to catch — pinning
/// the version forces a clear "rebuild the host binary" error rather than mis-emitted
/// `[Obsolete]` attributes on every overload.
///
/// v3: `enumCaseRawValues` redefined from "string-literal raw values only" to "string
/// OR integer raw values (integers normalized to a base-10 string)". A v2 host omits
/// integer raw values, so a v3 consumer pairing with it would silently fall back to
/// declaration-order ordinals for integer-backed enums (the pre-fix bug) instead of the
/// real Swift raw value; pinning the version forces a "rebuild the host binary" error.
///
/// On the .NET side, see `InterfaceFactsJson.SchemaVersion` — values must match.
let kSchemaVersion = 3

/// Top-level JSON document. Mirrors the .NET-side `InterfaceFactsJson` contract.
///
/// `coveredFacts` is the explicit "this producer populated these facts" signal — the
/// aggregator merges per fact based on this set, so a fact that's covered-but-empty
/// (`mainActorTypes: []`) is distinct from a fact that's not produced at all (key
/// absent from `facts`).
struct ParserOutput: Encodable {
    let schemaVersion: Int
    let coveredFacts: [String]
    let facts: Facts

    init(coveredFacts: [String], facts: Facts) {
        self.schemaVersion = kSchemaVersion
        self.coveredFacts = coveredFacts
        self.facts = facts
    }
}

/// The fact bag. Every field is Optional — an absent (null) field means
/// "this producer did not populate this fact"; an empty collection means
/// "this producer populated it and found nothing." The .NET aggregator
/// uses `coveredFacts` (not nullness) as the authoritative coverage signal,
/// but nullable fields prevent silent data loss if `coveredFacts` and the
/// `facts` payload disagree.
///
/// Covers the complete SwiftInterfaceFacts set. Field names are lowerCamelCase to match
/// System.Text.Json's default `PropertyNamingPolicy.CamelCase` on the .NET side.
struct Facts: Encodable {
    var mainActorTypes: [String]?
    var mainActorTypePositions: [String: SourcePositionJson]?

    // Actor isolation cluster.
    var actorIsolatedMembers: [String]?
    var mainActorIsolatedMembers: [String]?
    var nonisolatedMembers: [String]?
    var customActorTypes: [String]?
    var customActorIsolatorMap: [String: String]?

    // Availability cluster.
    var availabilityAnnotations: [String: [AvailabilityAnnotationJson]]?
    var availabilityAnnotationPositions: [String: SourcePositionJson]?

    // Typed throws.
    var typedThrowsErrors: [String: String]?

    // Type & member collection.
    var publicTypeNames: [String]?
    var internalMemberKeys: [String]?
    var publicMemberNames: [String]?
    var markerProtocolConformances: [String: [String]]?

    // Enum facts.
    var enumCaseLabels: [String: [String?]]?
    var enumCaseRawValues: [String: String]?

    // Signature facts.
    var parameterNames: [String: [String]]?
    var defaultParameterValues: [String: [String?]]?
    var autoclosureParameters: [String: [Bool]]?
    var subscriptLabels: [String: [String]]?
    var variadicMembers: [String]?
    var constLiteralParameters: [String: [Bool]]?
    var closureParameterAttributes: [String: [[String]]]?

    // SPI-only conformances (read from the sibling `.private.swiftinterface`).
    var spiOnlyConformances: [String]?

    // Qualified-type-path → explicit `@objc(CustomName)` runtime name.
    var objcRuntimeNames: [String: String]?

    // Protocol-level facts.
    var conventionCProtocols: [String]?
    var conventionCProtocolPositions: [String: SourcePositionJson]?
    var hiddenRequirementProtocols: [String: [String]]?

    // Non-fact methods behind the producer abstraction.
    var protocolNames: [String]?
    var protocolExtensionMethods: [String: [ProtocolExtensionMethodInfo]]?
    var extensionMemberCandidates: [ExtensionMemberCandidateInfo]?
}

struct SourcePositionJson: Encodable {
    let filePath: String
    let line: Int
    let column: Int
}

/// Mirrors .NET's `AvailabilityAnnotation` record. All fields nullable except the booleans;
/// matches what the regex parser's `ParseAvailableClause` constructs.
struct AvailabilityAnnotationJson: Encodable {
    let platform: String?
    let introducedVersion: String?
    let deprecatedVersion: String?
    let obsoletedVersion: String?
    let isUnconditionallyDeprecated: Bool
    let isUnconditionallyUnavailable: Bool
    let message: String?
    let renamed: String?
}

/// Wire shape for `ProtocolExtensionMethodDecl`. Mirrors the model's required
/// fields. `protocolQualifiedName` is excluded from the wire because it's
/// redundant with the dictionary key — the .NET-side conversion fills it from
/// the key when materializing the decl.
struct ProtocolExtensionMethodInfo: Encodable {
    let methodName: String
    let rawSignature: String
    let printedName: String
    let returnsSelf: Bool
    let isMainActorIsolated: Bool
    let isStatic: Bool
    let isProperty: Bool
    let hasSetter: Bool
    let isDeprecated: Bool
    let isMutating: Bool
    let whereConstraints: [String]
}

/// Wire shape for `ExtensionMemberCandidate`. Same payload as
/// `ProtocolExtensionMethodInfo` plus the verbatim `extendedTypeName` (no
/// module-context partitioning happens host-side).
struct ExtensionMemberCandidateInfo: Encodable {
    let extendedTypeName: String
    let methodName: String
    let rawSignature: String
    let printedName: String
    let returnsSelf: Bool
    let isMainActorIsolated: Bool
    let isStatic: Bool
    let isProperty: Bool
    let hasSetter: Bool
    let isDeprecated: Bool
    let isMutating: Bool
    let whereConstraints: [String]
}
