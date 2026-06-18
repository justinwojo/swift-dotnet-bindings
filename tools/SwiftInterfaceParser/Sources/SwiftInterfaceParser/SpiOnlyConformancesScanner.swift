// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

/// Single-line scanner that surfaces SPI-only conformances from a
/// `*.private.swiftinterface`: `@_spi(...) extension Mod.Type : P1, P2 { ... }`
/// blocks whose conformances are unreachable under a plain (non-`@_spi`) `import`.
/// Each entry has the form `"QualifiedType::UnqualifiedProtocol"`.
///
/// PARITY CONTRACT WITH `SwiftInterfaceAccessParser.GetSpiOnlyConformances` (line 1160)
/// and its helpers `ExtractConformanceSection` (1219), `FindTopLevelWhereKeyword` (1260),
/// `SplitConformancesAtTopLevel` (1290), `StripLeadingAttributes` (1318).
///
/// STRICTLY SINGLE-LINE (critical parity trap): the regex producer matches only when
/// `@_spi(`, the `extension` header, the `:` conformance list, AND the body-opening `{`
/// all sit on ONE trimmed source line — `ExtractConformanceSection` returns nil when no
/// depth-zero `{` is found on that same post-colon slice. A structured
/// `ExtensionDeclSyntax` walk would also match multi-line headers, which would be MORE
/// permissive than the regex and a behavior change the parity-emulator charter defers.
/// This is therefore a textual line scanner, NOT a SwiftSyntax tree walk, and it reads
/// the conformance list by the same slice/split/strip pipeline the regex uses rather than
/// trusting structured `inheritedTypes` (which diverges on `@retroactive`/`@available`
/// attributes, generic conformances, qualified names, and `Codable`).
///
/// Returns a sorted, de-duplicated array (the .NET side rehydrates it into a
/// `HashSet<string>`, so element order is immaterial to consumers; sorting only keeps
/// stdout byte-stable for golden diffs).
enum SpiOnlyConformancesScanner {
    /// Mirrors `SwiftInterfaceAccessParser.SpiExtensionHeaderRegex` (line 1138):
    /// `^@_spi\([^)]*\)\s+(?:public\s+|open\s+)?extension\s+([\w.]+)\s*:\s*`.
    /// The qualified extended type is captured as group 1; the conformance list is NOT
    /// captured here — the post-colon slice is handed to the textual pipeline below.
    private static let headerRegex = try! NSRegularExpression(
        pattern: "^@_spi\\([^)]*\\)\\s+(?:public\\s+|open\\s+)?extension\\s+([\\w.]+)\\s*:\\s*")

    static func parse(source: String) -> [String] {
        var result = Set<String>()

        // `String.enumerateLines` strips the terminator and treats `\n`, `\r\n`, and `\r`
        // as line breaks — matching `File.ReadAllLines` (which the regex producer feeds).
        source.enumerateLines { rawLine, _ in
            // TrimStart: drop leading whitespace, matching C# `string.TrimStart()`.
            let trimmed = String(rawLine.drop(while: { $0.isWhitespace }))
            guard trimmed.hasPrefix("@_spi(") else { return }

            let ns = trimmed as NSString
            guard let headerMatch = headerRegex.firstMatch(
                in: trimmed, range: NSRange(location: 0, length: ns.length)) else { return }

            let typeGroup = headerMatch.range(at: 1)
            guard typeGroup.location != NSNotFound else { return }
            let qualifiedType = ns.substring(with: typeGroup)

            // `afterColon = trimmed.Substring(headerMatch.Index + headerMatch.Length)`.
            let afterStart = headerMatch.range.location + headerMatch.range.length
            let afterColon = ns.substring(from: afterStart)

            guard let conformanceSection = extractConformanceSection(afterColon) else { return }

            for proto in splitConformancesAtTopLevel(conformanceSection) {
                let protoName = stripLeadingAttributes(proto)
                if protoName.isEmpty { continue }

                // ABI JSON stores conformance protocol names unqualified, so the filter key
                // matches on the unqualified tail (last dot component).
                let unqualifiedName: String
                if let dot = protoName.lastIndex(of: ".") {
                    unqualifiedName = String(protoName[protoName.index(after: dot)...])
                } else {
                    unqualifiedName = protoName
                }

                // The `Codable` typealias expands to Encodable + Decodable in ABI JSON — the
                // compiler synthesizes two separate conformance entries. Expand here so the
                // filter matches the conformance shape the parser actually sees.
                if unqualifiedName == "Codable" {
                    result.insert("\(qualifiedType)::Encodable")
                    result.insert("\(qualifiedType)::Decodable")
                } else {
                    result.insert("\(qualifiedType)::\(unqualifiedName)")
                }
            }
        }

        return result.sorted()
    }

    /// Ports `ExtractConformanceSection` (line 1219): slices the substring between the
    /// extension header's `:` and its body-opening `{` (depth-zero so `<`/`>` in generic
    /// args and `(`/`)` in attributes don't terminate early), excluding any trailing
    /// `where ...` clause. Returns nil if no depth-zero `{` exists on the line.
    private static func extractConformanceSection(_ afterColon: String) -> String? {
        let chars = Array(afterColon)
        var parenDepth = 0
        var angleDepth = 0
        var braceIdx = -1
        for i in 0..<chars.count {
            let c = chars[i]
            if c == "(" { parenDepth += 1 }
            else if c == ")" { if parenDepth > 0 { parenDepth -= 1 } }
            else if c == "<" { angleDepth += 1 }
            else if c == ">" { if angleDepth > 0 { angleDepth -= 1 } }
            else if c == "{" && parenDepth == 0 && angleDepth == 0 {
                braceIdx = i
                break
            }
        }
        if braceIdx < 0 { return nil }

        var section = String(chars[0..<braceIdx])

        // Strip a trailing top-level `where <requirements>` clause.
        let whereIdx = findTopLevelWhereKeyword(section)
        if whereIdx >= 0 {
            section = String(Array(section)[0..<whereIdx])
        }

        // TrimEnd.
        while let last = section.last, last.isWhitespace { section.removeLast() }
        return section
    }

    /// Ports `FindTopLevelWhereKeyword` (line 1260): index of the standalone `where`
    /// keyword at zero paren/angle depth, or -1. Index is into the `Array(section)`
    /// character view (matching the C# `string` index since the inputs are conformance
    /// lists of ASCII protocol names).
    private static func findTopLevelWhereKeyword(_ section: String) -> Int {
        let chars = Array(section)
        var parenDepth = 0
        var angleDepth = 0
        var i = 0
        while i + 5 <= chars.count {
            let c = chars[i]
            if c == "(" { parenDepth += 1; i += 1; continue }
            if c == ")" { if parenDepth > 0 { parenDepth -= 1 }; i += 1; continue }
            if c == "<" { angleDepth += 1; i += 1; continue }
            if c == ">" { if angleDepth > 0 { angleDepth -= 1 }; i += 1; continue }
            if parenDepth != 0 || angleDepth != 0 { i += 1; continue }
            if c != "w" { i += 1; continue }
            if i > 0 && isWordChar(chars[i - 1]) { i += 1; continue }
            if String(chars[i..<(i + 5)]) != "where" { i += 1; continue }
            let nextIdx = i + 5
            if nextIdx >= chars.count || isWordChar(chars[nextIdx]) { i += 1; continue }
            return i
        }
        return -1
    }

    /// Ports `SplitConformancesAtTopLevel` (line 1290): splits on commas at zero
    /// paren/angle depth. Commas inside `@available(*, deprecated)` or `P<A, B>` stay glued.
    private static func splitConformancesAtTopLevel(_ section: String) -> [String] {
        let chars = Array(section)
        var parenDepth = 0
        var angleDepth = 0
        var start = 0
        var parts: [String] = []
        for i in 0..<chars.count {
            let c = chars[i]
            if c == "(" { parenDepth += 1 }
            else if c == ")" { if parenDepth > 0 { parenDepth -= 1 } }
            else if c == "<" { angleDepth += 1 }
            else if c == ">" { if angleDepth > 0 { angleDepth -= 1 } }
            else if c == "," && parenDepth == 0 && angleDepth == 0 {
                parts.append(String(chars[start..<i]))
                start = i + 1
            }
        }
        if start < chars.count {
            parts.append(String(chars[start...]))
        }
        return parts
    }

    /// Ports `StripLeadingAttributes` (line 1318): strips leading `@attr` / `@attr(...)`
    /// tokens and returns the bare (whitespace-trimmed) protocol name. Returns empty when
    /// the entry is entirely attributes or an attribute's parens are unbalanced.
    private static func stripLeadingAttributes(_ entry: String) -> String {
        var current = Array(entry.trimmingCharacters(in: .whitespaces))
        while !current.isEmpty && current[0] == "@" {
            // Consume '@' + identifier characters.
            var i = 1
            while i < current.count && isWordChar(current[i]) { i += 1 }
            // Consume optional balanced parens immediately after the identifier.
            if i < current.count && current[i] == "(" {
                var depth = 1
                i += 1
                while i < current.count && depth > 0 {
                    if current[i] == "(" { depth += 1 }
                    else if current[i] == ")" { depth -= 1 }
                    i += 1
                }
                if depth != 0 { return "" } // unbalanced — give up rather than emit garbage
            }
            // Consume trailing whitespace separating attribute from next token.
            while i < current.count && current[i].isWhitespace { i += 1 }
            current = Array(current[i...])
        }
        return String(current).trimmingCharacters(in: .whitespaces)
    }

    /// Mirrors C# `char.IsLetterOrDigit(c) || c == '_'` for the ASCII-identifier checks
    /// in the ported helpers.
    private static func isWordChar(_ c: Character) -> Bool {
        return c.isLetter || c.isNumber || c == "_"
    }
}
