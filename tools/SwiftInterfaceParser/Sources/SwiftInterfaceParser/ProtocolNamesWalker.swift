// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks the syntax tree of a .swiftinterface and surfaces the unqualified names
/// of every public/open protocol declared in the module.
///
/// EXTRACTION CONTRACT (`ProtocolDeclRegex = (?:public|open)\s+protocol\s+(\w+)`):
///
/// 1. **Decl kind**: `protocol` ONLY. No classes, structs, etc.
///
/// 2. **Access modifier**: `public` or `open`. Bare and `internal` protocols are
///    skipped (`internal protocol Foo` does not match `(?:public|open)\s+protocol`).
///
/// 3. **STRICT modifier shape**: `(?:public|open)\s+protocol`. No modifier
///    between the access keyword and `protocol`. SwiftSyntax doesn't see
///    `protocol` as a modifier (it's the `protocolKeyword` token), so we just
///    check that the modifier list contains exactly the access modifier and
///    nothing else after it.
///
/// 4. **Name shape**: bare `(\w+)` capture. Backtick-escaped names like
///    `\`class\`` fail Unicode word-class and are excluded via
///    `RegexShape.isWordIdentifier`.
///
/// 5. **No same-line `{` gate**: the pattern is matched without brace tracking,
///    so `public protocol Foo` followed by `{` on the next line still emits `Foo`.
///    Don't gate on `typeOpensOnSameLine` here.
///
/// 6. **Unqualified key**: the bare protocol name — no nesting prefix even if the
///    protocol is declared inside another type. Nested protocols are unusual but
///    handled the same way.
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
    /// Modifiers BEFORE access are tolerated (unanchored scan); after access
    /// only the `protocol` keyword may follow — i.e. NO other modifiers in
    /// the list after the access one.
    private func matchesShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard RegexShape.advanceToAccess(&iter, ["public", "open"]) else { return false }
        return iter.next() == nil
    }
}
