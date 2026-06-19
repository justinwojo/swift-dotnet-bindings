// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks subscript declarations and surfaces external argument labels —
/// "TypePath.subscript(label1:label2:) -> [label1, label2]" where each entry
/// is either the explicit external label or `"_"` for unlabeled positions.
///
/// EXTRACTION CONTRACT:
///
/// 1. **Decl kind**: only `subscript`. Generic subscripts (`subscript<T>(...)`)
///    are matched. `static subscript` is matched.
///
/// 2. **Access modifier**: `SubscriptDeclRegex` requires `public` or `open` —
///    bare protocol-requirement subscripts are NOT in the result.
///
/// 3. **Module-level subscripts skipped**: the scope stack must be non-empty
///    (at least one enclosing type). Module-level subscripts are skipped.
///
/// 4. **Extension key shape**: FIRST-dot-strip (preserve nesting). So
///    `extension Mod.P256.Signing { subscript(...) }` keys as `"P256.Signing.subscript(...)"`.
///    This matches `EnumCaseLabels` etc., NOT the `LastIndexOf` strategy used
///    by `ParameterNames`/`InternalMembers`.
///
/// 5. **Label semantics** (per parameter):
///      * Explicit external label (Swift `subscript(bitAt index: Int)`):
///        firstName=`bitAt`, secondName=`index` → label `"bitAt"`.
///      * Single-name (`subscript(index: Int)`): firstName=`index`,
///        secondName=nil → label `"_"` (single-name subscripts have no
///        call-site label in Swift).
///      * Underscored (`subscript(_ index: Int)`): firstName=`_`,
///        secondName=`index` → label `"_"`.
///
/// 6. **Key shape**: `"TypePath.subscript(label1:label2:)"`. Labels join with
///    trailing `:`.
///
/// 7. **Collision quirk**: duplicate keys are silently overwritten — last write
///    wins. The collision is rare in canonical swiftinterfaces; documented in the
///    spec but not normalized.
///
/// 8. **Type-scope push gate**: `TypeDeclRegex` (public/internal/open + optional
///    `final`) must match AND the body's `{` must be on the same source line.
///    Non-matching shapes such as `public indirect enum`, or types whose body
///    opens on a later line, are NOT pushed; subscripts inside such bodies see
///    an empty scope stack and (per rule 3) are skipped.
final class SubscriptLabelsWalker: SyntaxVisitor {
    private(set) var subscriptLabels: [String: [String]] = [:]

    private var scopeStack: [String] = []

    /// Parallel stack: each visited type/extension records whether it actually
    /// pushed a frame on `scopeStack`. The gated-push pattern ensures `visitPost`
    /// only pops when the corresponding `visit` actually pushed.
    private var scopePushed: [Bool] = []

    private let converter: SourceLocationConverter

    init(converter: SourceLocationConverter) {
        self.converter = converter
        super.init(viewMode: .sourceAccurate)
    }

    static func parse(filePath: String, source: String) -> [String: [String]] {
        let tree = Parser.parse(source: source)
        let converter = SourceLocationConverter(fileName: filePath, tree: tree)
        let walker = SubscriptLabelsWalker(converter: converter)
        walker.walk(tree)
        return walker.subscriptLabels
    }

    // MARK: - Type decls (gated push, mirroring `TypeDeclRegex` + `openBraces > 0`)

    override func visit(_ node: ClassDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeScope(name: node.name.text, modifiers: node.modifiers,
                              keyword: node.classKeyword,
                              leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: ClassDeclSyntax) { leaveTypeScope() }

    override func visit(_ node: StructDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeScope(name: node.name.text, modifiers: node.modifiers,
                              keyword: node.structKeyword,
                              leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: StructDeclSyntax) { leaveTypeScope() }

    override func visit(_ node: EnumDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeScope(name: node.name.text, modifiers: node.modifiers,
                              keyword: node.enumKeyword,
                              leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: EnumDeclSyntax) { leaveTypeScope() }

    override func visit(_ node: ActorDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeScope(name: node.name.text, modifiers: node.modifiers,
                              keyword: node.actorKeyword,
                              leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: ActorDeclSyntax) { leaveTypeScope() }

    override func visit(_ node: ProtocolDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeScope(name: node.name.text, modifiers: node.modifiers,
                              keyword: node.protocolKeyword,
                              leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: ProtocolDeclSyntax) { leaveTypeScope() }

    // MARK: - Extensions (first-dot-strip; gated on same-line `{`)

    override func visit(_ node: ExtensionDeclSyntax) -> SyntaxVisitorContinueKind {
        guard typeOpensOnSameLine(keyword: node.extensionKeyword,
                                  leftBrace: node.memberBlock.leftBrace) else {
            scopePushed.append(false)
            return .visitChildren
        }
        let qualified = node.extendedType.trimmedDescription
        // Regex `ExtensionDeclRegex` captures only `[\w.]+` for the extended type.
        guard RegexShape.isWordOrDotOnly(qualified) else {
            scopePushed.append(false)
            return .visitChildren
        }
        let stripped: String
        if let firstDot = qualified.firstIndex(of: ".") {
            stripped = String(qualified[qualified.index(after: firstDot)...])
        } else {
            stripped = qualified
        }
        scopeStack.append(stripped)
        scopePushed.append(true)
        return .visitChildren
    }
    override func visitPost(_ node: ExtensionDeclSyntax) { leaveTypeScope() }

    private func enterTypeScope(name: String,
                                modifiers: DeclModifierListSyntax,
                                keyword: TokenSyntax,
                                leftBrace: TokenSyntax) -> SyntaxVisitorContinueKind {
        // `TypeDeclRegex` ends in bare `(\w+)` — backtick-escaped names fail the
        // Unicode word-class check, so SwiftSyntax must skip pushing them.
        if matchesTypeDeclShape(modifiers),
           RegexShape.isWordIdentifier(name),
           typeOpensOnSameLine(keyword: keyword, leftBrace: leftBrace) {
            scopeStack.append(name)
            scopePushed.append(true)
        } else {
            scopePushed.append(false)
        }
        return .visitChildren
    }

    private func leaveTypeScope() {
        if let pushed = scopePushed.popLast(), pushed {
            _ = scopeStack.popLast()
        }
    }

    /// `TypeDeclRegex = (?:public|internal|open)\s+(?:final\s+)?(?:class|struct|enum|actor|protocol)\s+(\w+)`.
    /// Modifiers BEFORE access are tolerated (unanchored scan); after access only
    /// an optional `final` is allowed. `public indirect enum`, etc. fail this gate
    /// and are not pushed onto the scope stack.
    private func matchesTypeDeclShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["public", "internal", "open"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "final", mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// True iff the type/extension keyword and its body's opening `{` are on the
    /// same source line — the `openBraces > 0` condition on the `TypeDeclRegex`
    /// match line.
    private func typeOpensOnSameLine(keyword: TokenSyntax, leftBrace: TokenSyntax) -> Bool {
        let kwLine = converter.location(for: keyword.positionAfterSkippingLeadingTrivia).line
        let braceLine = converter.location(for: leftBrace.positionAfterSkippingLeadingTrivia).line
        return kwLine == braceLine
    }

    // MARK: - Subscripts

    override func visit(_ node: SubscriptDeclSyntax) -> SyntaxVisitorContinueKind {
        // Module-level subscripts skipped (empty scope stack = no enclosing type).
        guard !scopeStack.isEmpty else { return .skipChildren }
        guard matchesPublicSubscriptShape(node.modifiers) else { return .skipChildren }

        let params = node.parameterClause.parameters
        guard !params.isEmpty else { return .skipChildren }

        var labels: [String] = []
        for param in params {
            let firstText = param.firstName.text
            let hasSecond = param.secondName != nil

            // External-label rule: `words.Length >= 2 && words[0] != "_"`.
            if hasSecond && firstText != "_" {
                labels.append(firstText)
            } else {
                labels.append("_")
            }
        }

        let typePrefix = scopeStack.joined(separator: ".")
        let labelStr = labels.map { "\($0):" }.joined()
        let key = "\(typePrefix).subscript(\(labelStr))"
        subscriptLabels[key] = labels  // last-write-wins on duplicate keys

        return .skipChildren
    }

    /// `SubscriptDeclRegex` shape: `(?:public|open)\s+(?:static\s+)?subscript`.
    /// STRICT order — only `static` is allowed between access and `subscript`.
    /// `nonisolated`, `final`, `dynamic`, etc. fail the shape and are skipped.
    private func matchesPublicSubscriptShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["public", "open"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "static", mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// Iterator helper: consume modifiers until we find one whose `name.text` is in
    /// `accessTexts` AND whose `detail` is nil. Modifiers before the access keyword
    /// are tolerated (unanchored scan).
    private func advanceToAccess(_ iter: inout DeclModifierListSyntax.Iterator, _ accessTexts: [String]) -> Bool {
        while let mod = iter.next() {
            if accessTexts.contains(mod.name.text) && mod.detail == nil {
                return true
            }
        }
        return false
    }
}
