// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

/// SwiftInterfaceParser — a Swift host program that uses SwiftSyntax to extract
/// supplementary facts from a .swiftinterface file. The .NET-side generator invokes
/// this binary, parses its JSON stdout, and merges per-fact via
/// `InterfaceFactsAggregator`.
///
/// Session 3 lights up the remaining 14 facts (type collection, enum facts,
/// signature facts, subscript labels, protocol facts), bringing SwiftSyntax to
/// 100% fact coverage (24/24). The contract is byte-equal parity with the regex
/// producer (`SwiftInterfaceAccessParser`) across the validation corpus — that
/// gates flipping the default in M2 S3.
///
/// CLI:
///   SwiftInterfaceParser --input <path-to-swiftinterface>
///
/// Output: JSON to stdout, ParserOutput shape (see Output.swift).
/// Exit codes: 0 = ok, 1 = invocation error (bad args or missing input), 2 = parse error.

func usage() -> Never {
    FileHandle.standardError.write(Data("usage: SwiftInterfaceParser --input <path>\n".utf8))
    exit(1)
}

let args = CommandLine.arguments

var inputPath: String? = nil
var i = 1
while i < args.count {
    let a = args[i]
    switch a {
    case "--input":
        i += 1
        guard i < args.count else { usage() }
        inputPath = args[i]
    case "--help", "-h":
        FileHandle.standardOutput.write(Data("SwiftInterfaceParser: extract SwiftInterfaceFacts via SwiftSyntax.\n".utf8))
        FileHandle.standardOutput.write(Data("Usage: SwiftInterfaceParser --input <path-to-swiftinterface>\n".utf8))
        exit(0)
    default:
        FileHandle.standardError.write(Data("unknown argument: \(a)\n".utf8))
        usage()
    }
    i += 1
}

guard let path = inputPath else {
    usage()
}

guard let data = try? Data(contentsOf: URL(fileURLWithPath: path)) else {
    FileHandle.standardError.write(Data("could not read input file: \(path)\n".utf8))
    exit(2)
}
guard let source = String(data: data, encoding: .utf8) else {
    FileHandle.standardError.write(Data("input file is not UTF-8: \(path)\n".utf8))
    exit(2)
}

let (mainActorTypes, mainActorPositions) = MainActorWalker.parse(filePath: path, source: source)
let actorIsolation = ActorIsolationWalker.parse(filePath: path, source: source)
let availability = AvailabilityWalker.parse(filePath: path, source: source)
let typedThrows = ThrowsWalker.parse(filePath: path, source: source)

// Session 3 walkers.
let publicTypeNames = PublicTypeNamesWalker.parse(filePath: path, source: source)
let memberCollection = MemberCollectionWalker.parse(filePath: path, source: source)
let markerProtocolConformances = MarkerProtocolWalker.parse(filePath: path, source: source)
let enumFacts = EnumFactsWalker.parse(filePath: path, source: source)
let signatureFacts = SignatureFactsWalker.parse(filePath: path, source: source)
let subscriptLabels = SubscriptLabelsWalker.parse(filePath: path, source: source)
let protocolFacts = ProtocolFactsWalker.parse(filePath: path, source: source)

// M2 S4 — non-fact methods migrated behind the producer abstraction.
let protocolNames = ProtocolNamesWalker.parse(filePath: path, source: source)
let extensionMemberCandidates = ExtensionsWalker.parse(filePath: path, source: source)

// Derive protocolExtensionMethods from candidates + protocolNames using the
// first-dot-stripped lookup. Mirrors `RegexInterfaceFactsProducer.DeriveProtocolExtensionMethods`
// so both producers parity-match on this dict shape.
var protocolExtensionMethods: [String: [ProtocolExtensionMethodInfo]] = [:]
let protocolNameSet = Set(protocolNames)
if !protocolNameSet.isEmpty {
    for candidate in extensionMemberCandidates {
        let qualified = candidate.extendedTypeName
        let typePath: String
        if let firstDot = qualified.firstIndex(of: ".") {
            typePath = String(qualified[qualified.index(after: firstDot)...])
        } else {
            typePath = qualified
        }
        guard protocolNameSet.contains(typePath) else { continue }
        let info = ProtocolExtensionMethodInfo(
            methodName: candidate.methodName,
            rawSignature: candidate.rawSignature,
            printedName: candidate.printedName,
            returnsSelf: candidate.returnsSelf,
            isMainActorIsolated: candidate.isMainActorIsolated,
            isStatic: candidate.isStatic,
            isProperty: candidate.isProperty,
            hasSetter: candidate.hasSetter,
            isDeprecated: candidate.isDeprecated,
            isMutating: candidate.isMutating,
            whereConstraints: candidate.whereConstraints
        )
        protocolExtensionMethods[qualified, default: []].append(info)
    }
}

let output = ParserOutput(
    coveredFacts: [
        "MainActorTypes",
        "MainActorTypePositions",
        // Actor isolation cluster (M2 S2).
        "ActorIsolatedMembers",
        "MainActorIsolatedMembers",
        "NonisolatedMembers",
        "CustomActorTypes",
        "CustomActorIsolatorMap",
        // Availability cluster (M2 S2).
        "AvailabilityAnnotations",
        "AvailabilityAnnotationPositions",
        // Typed throws (M2 S2).
        "TypedThrowsErrors",
        // Session 3 — type & member collection.
        "PublicTypeNames",
        "InternalMemberKeys",
        "PublicMemberNames",
        "MarkerProtocolConformances",
        // Session 3 — enum facts.
        "EnumCaseLabels",
        "EnumCaseRawValues",
        // Session 3 — signature facts.
        "ParameterNames",
        "DefaultParameterValues",
        "AutoclosureParameters",
        "SubscriptLabels",
        "VariadicMembers",
        // Session 3 — protocol-level facts.
        "ConventionCProtocols",
        "ConventionCProtocolPositions",
        "HiddenRequirementProtocols",
        // M2 S4 — non-fact methods migrated behind the producer abstraction.
        "ProtocolNames",
        "ProtocolExtensionMethods",
        "ExtensionMemberCandidates",
    ],
    facts: Facts(
        mainActorTypes: mainActorTypes,
        mainActorTypePositions: mainActorPositions,
        actorIsolatedMembers: actorIsolation.actorIsolatedMembers,
        mainActorIsolatedMembers: actorIsolation.mainActorIsolatedMembers,
        nonisolatedMembers: actorIsolation.nonisolatedMembers,
        customActorTypes: actorIsolation.customActorTypes,
        customActorIsolatorMap: actorIsolation.customActorIsolatorMap,
        availabilityAnnotations: availability.availabilityAnnotations,
        availabilityAnnotationPositions: availability.availabilityAnnotationPositions,
        typedThrowsErrors: typedThrows,
        publicTypeNames: publicTypeNames,
        internalMemberKeys: memberCollection.internalMemberKeys,
        publicMemberNames: memberCollection.publicMemberNames,
        markerProtocolConformances: markerProtocolConformances,
        enumCaseLabels: enumFacts.labels,
        enumCaseRawValues: enumFacts.rawValues,
        parameterNames: signatureFacts.parameterNames,
        defaultParameterValues: signatureFacts.defaultParameterValues,
        autoclosureParameters: signatureFacts.autoclosureParameters,
        subscriptLabels: subscriptLabels,
        variadicMembers: signatureFacts.variadicMembers,
        conventionCProtocols: protocolFacts.conventionCProtocols,
        conventionCProtocolPositions: protocolFacts.conventionCProtocolPositions,
        hiddenRequirementProtocols: protocolFacts.hiddenRequirementProtocols,
        protocolNames: protocolNames,
        protocolExtensionMethods: protocolExtensionMethods,
        extensionMemberCandidates: extensionMemberCandidates
    )
)

let encoder = JSONEncoder()
// Sorted keys keep stdout byte-stable for golden tests and parity diffs.
encoder.outputFormatting = [.sortedKeys]
let json: Data
do {
    json = try encoder.encode(output)
} catch {
    FileHandle.standardError.write(Data("JSON encoding failed: \(error)\n".utf8))
    exit(2)
}
FileHandle.standardOutput.write(json)
FileHandle.standardOutput.write(Data("\n".utf8))
