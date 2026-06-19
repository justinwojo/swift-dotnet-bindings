// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks the syntax tree of a .swiftinterface and surfaces marker-protocol
/// conformances — extension declarations of the form
/// `extension SomeType : SomeProto { }` whose body contains zero member declarations.
/// Empty-body conformance is the structural signal a "marker protocol" is in use
/// (the conforming type adds nothing to the protocol).
///
/// EXTRACTION CONTRACT:
///
/// 1. **Decl kind**: `extension` only. The extension must declare at least one
///    inheritance entry (i.e. `extension Foo : Bar { }`). Bodyless extensions
///    without `: Proto` are silently ignored.
///
/// 2. **Empty-body detection**: `members.members.isEmpty` — structurally equivalent
///    to the two textual patterns (same-line `{ }` OR opening `{` followed by the
///    next non-blank line being `}`).
///
/// 3. **Key shape**: the dictionary key is the LAST dot-component of each
///    conforming protocol name. So `extension Swift.Int : SomeModule.ConstraintOffsetTarget`
///    keys as `"ConstraintOffsetTarget"`.
///
/// 4. **Value shape**: each value is a list of fully-qualified conforming type
///    names — the verbatim text from `extension <here>`. So `extension Swift.Int : ...`
///    contributes the string `"Swift.Int"` (no stripping).
///
/// 5. **Multi-protocol conformance**: `extension Foo : Proto1, Proto2 { }`
///    contributes one value per protocol — `Proto1 -> Foo`, `Proto2 -> Foo`.
///
/// 6. **No access modifier filter**: `public extension`, bare `extension`, etc.
///    all match.
///
/// 7. **Insertion order preserved**: lists keep file order; duplicates suppressed
///    via a contains-before-insert check.
final class MarkerProtocolWalker: SyntaxVisitor {
    private(set) var markerProtocolConformances: [String: [String]] = [:]

    init() {
        super.init(viewMode: .sourceAccurate)
    }

    static func parse(filePath _: String, source: String) -> [String: [String]] {
        let tree = Parser.parse(source: source)
        let walker = MarkerProtocolWalker()
        walker.walk(tree)
        return walker.markerProtocolConformances
    }

    override func visit(_ node: ExtensionDeclSyntax) -> SyntaxVisitorContinueKind {
        // Only conformance extensions with empty bodies count.
        guard let inheritance = node.inheritanceClause else { return .visitChildren }
        guard node.memberBlock.members.isEmpty else { return .visitChildren }

        // `ConformanceExtensionRegex` shape:
        //   extension\s+([\w.]+)\s*:\s*([\w.,\s]+)\s*\{
        // — the brace MUST follow directly after the inheritance list with only
        // whitespace between, so any `where` clause fails the shape.
        // Reject extensions with a generic where clause to mirror that.
        if node.genericWhereClause != nil { return .visitChildren }

        let extendedType = node.extendedType.trimmedDescription
        // Type capture is `[\w.]+`: word characters and dots only. `extension Foo<T>`,
        // `extension (Foo)`, and similar would not match — skip them.
        if !isWordOrDotOnly(extendedType) { return .visitChildren }

        // Inheritance capture is `[\w.,\s]+` — each individual protocol must be
        // word-and-dot only (commas + whitespace are list separators). Anything
        // else (composition `&`, generics `<>`, optionality `?`, etc.) fails the
        // capture shape, so reject the whole extension.
        for entry in inheritance.inheritedTypes {
            let proto = entry.type.trimmedDescription
            if !isWordOrDotOnly(proto) { return .visitChildren }
        }

        for entry in inheritance.inheritedTypes {
            let proto = entry.type.trimmedDescription
            // Last-dot-component as key (qualifiers stripped).
            let key: String
            if let lastDot = proto.lastIndex(of: ".") {
                key = String(proto[proto.index(after: lastDot)...])
            } else {
                key = proto
            }

            var list = markerProtocolConformances[key] ?? []
            if !list.contains(extendedType) {
                list.append(extendedType)
            }
            markerProtocolConformances[key] = list
        }

        return .visitChildren
    }

    /// True iff every character in `s` is either `.` or a `\w` (Unicode word) char
    /// — the `[\w.]+` capture class.
    private func isWordOrDotOnly(_ s: String) -> Bool {
        if s.isEmpty { return false }
        for scalar in s.unicodeScalars {
            if scalar == "." { continue }
            switch scalar.properties.generalCategory {
            case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter,
                 .modifierLetter, .otherLetter,
                 .nonspacingMark, .decimalNumber, .connectorPunctuation:
                continue
            default:
                return false
            }
        }
        return true
    }
}
