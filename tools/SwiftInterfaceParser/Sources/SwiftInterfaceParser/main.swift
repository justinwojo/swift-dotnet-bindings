// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

/// SwiftInterfaceParser — a Swift host program that uses SwiftSyntax to extract
/// supplementary facts from a .swiftinterface file. The .NET-side generator invokes
/// this binary, parses its JSON stdout, and merges per-fact via
/// `InterfaceFactsAggregator`.
///
/// Covers the complete SwiftInterfaceFacts set (type collection, enum facts, signature
/// facts incl. const-literal + closure-attribute params, subscript labels, protocol facts,
/// and SPI-only conformances).
///
/// CLI:
///   SwiftInterfaceParser --input <path-to-swiftinterface> [--private-input <path-to-private-swiftinterface>]
///
/// `--private-input` is the sibling `*.private.swiftinterface` (derived by the .NET-side
/// producer). It is the ONLY source of `SpiOnlyConformances`; when omitted, that fact is
/// covered-but-empty (no SPI conformances when no private interface exists).
///
/// Output: JSON to stdout, ParserOutput shape (see Output.swift).
/// Exit codes: 0 = ok, 1 = invocation error (bad args or missing input), 2 = parse error.

func usage() -> Never {
    FileHandle.standardError.write(Data("usage: SwiftInterfaceParser --input <path> [--private-input <path>]\n".utf8))
    exit(1)
}

let args = CommandLine.arguments

var inputPath: String? = nil
var privateInputPath: String? = nil
var i = 1
while i < args.count {
    let a = args[i]
    switch a {
    case "--input":
        i += 1
        guard i < args.count else { usage() }
        inputPath = args[i]
    case "--private-input":
        i += 1
        guard i < args.count else { usage() }
        privateInputPath = args[i]
    case "--help", "-h":
        FileHandle.standardOutput.write(Data("SwiftInterfaceParser: extract SwiftInterfaceFacts via SwiftSyntax.\n".utf8))
        FileHandle.standardOutput.write(Data("Usage: SwiftInterfaceParser --input <path-to-swiftinterface> [--private-input <path>]\n".utf8))
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

let publicTypeNames = PublicTypeNamesWalker.parse(filePath: path, source: source)
let memberCollection = MemberCollectionWalker.parse(filePath: path, source: source)
let markerProtocolConformances = MarkerProtocolWalker.parse(filePath: path, source: source)
let enumFacts = EnumFactsWalker.parse(filePath: path, source: source)
let signatureFacts = SignatureFactsWalker.parse(filePath: path, source: source)
let subscriptLabels = SubscriptLabelsWalker.parse(filePath: path, source: source)
let protocolFacts = ProtocolFactsWalker.parse(filePath: path, source: source)
let asyncAccessorMembers = AsyncAccessorWalker.parse(source: source)

let protocolNames = ProtocolNamesWalker.parse(filePath: path, source: source)
let extensionMemberCandidates = ExtensionsWalker.parse(filePath: path, source: source)

// SPI-only conformances come exclusively from the sibling `*.private.swiftinterface`,
// derived and passed by the .NET-side producer as `--private-input`. When absent (most
// libraries ship no private interface), this fact is covered-but-empty.
// A missing or unreadable private file is NOT an error: it just means no SPI conformances.
var spiOnlyConformances: [String] = []
if let privatePath = privateInputPath,
   let privateData = try? Data(contentsOf: URL(fileURLWithPath: privatePath)),
   let privateSource = String(data: privateData, encoding: .utf8) {
    spiOnlyConformances = SpiOnlyConformancesScanner.parse(source: privateSource)
}

// Explicit `@objc(CustomName)` type renames — read from the public interface.
let objcRuntimeNames = ObjCRuntimeNamesWalker.parse(filePath: path, source: source)

// Derive protocolExtensionMethods from candidates + protocolNames using the
// first-dot-stripped lookup: strip the leading module component from the extension's
// qualified type name, then match against the protocol-names set.
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
        // Actor isolation cluster.
        "ActorIsolatedMembers",
        "MainActorIsolatedMembers",
        "NonisolatedMembers",
        "CustomActorTypes",
        "CustomActorIsolatorMap",
        // Availability cluster.
        "AvailabilityAnnotations",
        "AvailabilityAnnotationPositions",
        // Typed throws.
        "TypedThrowsErrors",
        // Type & member collection.
        "PublicTypeNames",
        "InternalMemberKeys",
        "PublicMemberNames",
        "MarkerProtocolConformances",
        // Enum facts.
        "EnumCaseLabels",
        "EnumCaseRawValues",
        // Signature facts.
        "ParameterNames",
        "DefaultParameterValues",
        "AutoclosureParameters",
        "SubscriptLabels",
        "VariadicMembers",
        "ConstLiteralParameters",
        "ClosureParameterAttributes",
        // Async accessors — the swiftinterface-side oracle for `{ get async }`.
        "AsyncAccessorMembers",
        // SPI-only conformances (covered-but-empty when no private interface).
        "SpiOnlyConformances",
        // Explicit @objc(CustomName) type renames.
        "ObjCRuntimeNames",
        // Protocol-level facts.
        "ConventionCProtocols",
        "ConventionCProtocolPositions",
        "HiddenRequirementProtocols",
        // Non-fact methods behind the producer abstraction.
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
        constLiteralParameters: signatureFacts.constLiteralParameters,
        closureParameterAttributes: signatureFacts.closureParameterAttributes,
        asyncAccessorMembers: asyncAccessorMembers,
        spiOnlyConformances: spiOnlyConformances,
        objcRuntimeNames: objcRuntimeNames,
        conventionCProtocols: protocolFacts.conventionCProtocols,
        conventionCProtocolPositions: protocolFacts.conventionCProtocolPositions,
        hiddenRequirementProtocols: protocolFacts.hiddenRequirementProtocols,
        protocolNames: protocolNames,
        protocolExtensionMethods: protocolExtensionMethods,
        extensionMemberCandidates: extensionMemberCandidates
    )
)

let encoder = JSONEncoder()
// Sorted keys keep stdout byte-stable for golden tests and reproducible diffs.
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
