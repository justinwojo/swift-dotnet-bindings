// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

/// JSON contract version. Bump only on shape-change (rename / type change / removal /
/// semantic redefinition). Additive evolution — new optional fact fields, new map
/// dictionaries — stays at v1 because the host binary is vendored in the NuGet package
/// alongside the .NET generator and both sides ship in lockstep. The .NET-side
/// `UnmappedMemberHandling.Disallow` setting still catches accidental one-sided drift
/// at deserialize time, so additive evolution does not require a version bump but does
/// require updating both `Facts` here and `InterfaceFactsJsonPayload` on the .NET side
/// in the same commit.
///
/// On the .NET side, see `InterfaceFactsJson.SchemaVersion` — values must match.
let kSchemaVersion = 1

/// Top-level JSON document. Mirrors the .NET-side `InterfaceFactsJson` contract.
///
/// `coveredFacts` is the explicit "this producer populated these facts" signal — the
/// aggregator merges per fact based on this set, so a fact that's covered-but-empty
/// (`mainActorTypes: []`) is distinct from a fact that's not produced at all (key
/// absent from `facts`). Session 1 covers MainActorTypes + MainActorTypePositions only.
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
/// Session 2 covers MainActor* + the actor isolation cluster + availability + typed throws.
/// Field names are lowerCamelCase to match System.Text.Json's default
/// `PropertyNamingPolicy.CamelCase` on the .NET side.
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
