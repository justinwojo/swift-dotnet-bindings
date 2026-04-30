// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks the syntax tree of a .swiftinterface and surfaces the unqualified names
/// of every public/open protocol declared in the module.
///
/// PARITY CONTRACT WITH `SwiftInterfaceAccessParser.GetProtocolNames` (line 1148)
/// and the underlying `ProtocolDeclRegex = (?:public|open)\s+protocol\s+(\w+)`:
///
/// 1. **Decl kind**: `protocol` ONLY. No classes, structs, etc.
///
/// 2. **Access modifier**: `public` or `open`. Bare and `internal` protocols are
///    skipped (the regex doesn't match `internal protocol Foo` at all).
///
/// 3. **STRICT modifier shape**: `(?:public|open)\s+protocol`. No modifier
///    between the access keyword and `protocol`. SwiftSyntax doesn't see
///    `protocol` as a modifier (it's the `protocolKeyword` token), so we just
///    check that the modifier list contains exactly the access modifier and
///    nothing else after it.
///
/// 4. **Name shape**: bare `(\w+)` capture. Backtick-escaped names like
///    `\`class\`` fail Unicode word-class and miss the regex; mirror via
///    `RegexShape.isWordIdentifier`.
///
/// 5. **No same-line `{` gate**: the regex is run line-by-line without any
///    brace tracking, so `public protocol Foo` followed by `{` on the next
///    line still emits `Foo`. Don't gate on `typeOpensOnSameLine` here.
///
/// 6. **Unqualified key**: regex's `Groups[1]` is the bare protocol name —
///    no nesting prefix even if the protocol is declared inside another type.
///    Nested protocols are unusual but parity-mirrored.
final class ProtocolNamesWalker: SyntaxVisitor {
    private(set) var protocolNames: [String] = []

    init() {
        super.init(viewMode: .sourceAccurate)
    }

    static func parse(filePath: String, source: String) -> [String] {
        let tree = Parser.parse(source: source)
        let walker = ProtocolNamesWalker()
        walker.walk(tree)
        return walker.protocolNames
    }

    override func visit(_ node: ProtocolDeclSyntax) -> SyntaxVisitorContinueKind {
        guard matchesShape(node.modifiers),
              RegexShape.isWordIdentifier(node.name.text) else {
            return .visitChildren
        }
        protocolNames.append(node.name.text)
        return .visitChildren
    }

    /// `ProtocolDeclRegex` shape: `(?:public|open)\s+protocol\s+(\w+)`.
    /// Modifiers BEFORE access are tolerated (regex unanchored Match scan);
    /// after access only the `protocol` keyword may follow — i.e. NO other
    /// modifiers in the list after the access one.
    private func matchesShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard RegexShape.advanceToAccess(&iter, ["public", "open"]) else { return false }
        return iter.next() == nil
    }
}
