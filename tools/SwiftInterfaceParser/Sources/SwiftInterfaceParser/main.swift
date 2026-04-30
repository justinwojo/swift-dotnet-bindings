// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

/// SwiftInterfaceParser — a Swift host program that uses SwiftSyntax to extract
/// supplementary facts from a .swiftinterface file. The .NET-side generator invokes
/// this binary, parses its JSON stdout, and merges per-fact via
/// `InterfaceFactsAggregator`.
///
/// Session 1 emits only `mainActorTypes` and `mainActorTypePositions`. The contract
/// is byte-equal parity with the regex producer (`SwiftInterfaceAccessParser`)
/// across the validation corpus — that's what gates flipping the default in M2 S3.
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

let (types, positions) = MainActorWalker.parse(filePath: path, source: source)

let output = ParserOutput(
    coveredFacts: ["MainActorTypes", "MainActorTypePositions"],
    facts: Facts(
        mainActorTypes: types,
        mainActorTypePositions: positions
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
