// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

/// JSON contract version. Bump deliberately on every breaking change to the schema:
/// adding optional fields (new fact dictionaries, new fields on existing structs) is
/// non-breaking only if the .NET deserializer already tolerates extras for that key.
/// In practice the deserializer rejects unknown facts (UnmappedMemberHandling=Disallow)
/// for drift safety, so any net-new fact name is breaking and warrants a bump.
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
/// Session 1 only emits `mainActorTypes` and `mainActorTypePositions`. Session 2+
/// will light up the rest. Field names are lowerCamelCase to match System.Text.Json's
/// default `PropertyNamingPolicy.CamelCase`.
struct Facts: Encodable {
    var mainActorTypes: [String]?
    var mainActorTypePositions: [String: SourcePositionJson]?
}

struct SourcePositionJson: Encodable {
    let filePath: String
    let line: Int
    let column: Int
}
