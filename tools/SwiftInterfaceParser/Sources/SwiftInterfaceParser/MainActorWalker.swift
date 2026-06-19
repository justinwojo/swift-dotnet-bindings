// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks the syntax tree of a .swiftinterface and surfaces every `class`, `struct`,
/// `enum`, or `protocol` declaration that carries `@MainActor` (or
/// `@_Concurrency.MainActor`).
///
/// EXTRACTION CONTRACT:
///
/// 1. **Decl kinds**: `class` / `struct` / `enum` / `protocol` always
///    eligible. `actor` is eligible too BUT with a quirk: the `ActorDeclRegex`
///    shape only matches `public actor` / `open actor`, so internal actors with
///    `@MainActor` slip past the suppression and ARE emitted. Emit
///    `@MainActor internal actor X` but suppress `@MainActor public actor X`
///    and `@MainActor open actor X`.
///    Skip `extension` — extensions push a scope frame for nesting context but
///    are never emitted into MainActorTypes.
///    Skip `typealias` / `associatedtype` — not in the `TypeDeclRegex` set.
///
/// 2. **Access modifier**: emit only when the declaration carries one of
///    `public`, `internal`, or `open`. `TypeDeclRegex` requires one of those —
///    declarations without an access modifier (e.g. protocol requirements that
///    inherit visibility) don't appear in `MainActorTypePositions`.
///
/// 3. **Attribute name match**: literal `MainActor` or `_Concurrency.MainActor`
///    (pattern `@(?:_Concurrency\.)?MainActor`). No other module-qualified
///    aliases. SwiftSyntax sees only what's written; we don't resolve typealiases
///    or import-aliased names.
///
/// 4. **Position**: 1-based line/column pointing at the access modifier token
///    (`public` / `internal` / `open`). The column corresponds to the start of
///    the access modifier in the source line.
///
/// 5. **Qualified path**: dot-joined names of enclosing type decls
///    (e.g. `Outer.Inner`). Extension nesting context must be tracked: the
///    `extension Mod.Type { … }` form gets the leading module component
///    stripped (first-dot-strip) before being pushed onto the scope stack.
///
/// 6. **Type-scope push gate**: a type scope is pushed only when `TypeDeclRegex`
///    (public|internal|open + optional final) matches AND the body's `{` is on
///    the same source line. Names must satisfy the `(\w+)` capture (no
///    backtick-escaped names). Non-matching shapes such as `public indirect enum`
///    or split-line bodies do NOT push, so nested types inside them lose the
///    outer prefix.
///    Extension push is additionally gated on same-line `{` AND a `[\w.]+`
///    extended-type capture.
final class MainActorWalker: SyntaxVisitor {
    let filePath: String
    let converter: SourceLocationConverter

    private(set) var mainActorTypes: [String] = []
    private(set) var mainActorTypePositions: [String: SourcePositionJson] = [:]

    private var scopeStack: [String] = []

    /// Parallel stack: each visited type/extension records whether it actually
    /// pushed a frame on `scopeStack`. The gated-push pattern ensures `visitPost`
    /// only pops when the corresponding `visit` actually pushed.
    private var scopePushed: [Bool] = []

    init(filePath: String, source: String) {
        self.filePath = filePath
        self.converter = SourceLocationConverter(fileName: filePath, tree: Parser.parse(source: source))
        super.init(viewMode: .sourceAccurate)
    }

    static func parse(filePath: String, source: String) -> (types: [String], positions: [String: SourcePositionJson]) {
        let tree = Parser.parse(source: source)
        let walker = MainActorWalker(filePath: filePath, source: source)
        walker.walk(tree)
        return (walker.mainActorTypes, walker.mainActorTypePositions)
    }

    // MARK: - Type declarations we care about

    override func visit(_ node: ClassDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeDecl(name: node.name.text, modifiers: node.modifiers,
                             attributes: node.attributes,
                             keyword: node.classKeyword,
                             leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: ClassDeclSyntax) { leaveTypeScope() }

    override func visit(_ node: StructDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeDecl(name: node.name.text, modifiers: node.modifiers,
                             attributes: node.attributes,
                             keyword: node.structKeyword,
                             leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: StructDeclSyntax) { leaveTypeScope() }

    override func visit(_ node: EnumDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeDecl(name: node.name.text, modifiers: node.modifiers,
                             attributes: node.attributes,
                             keyword: node.enumKeyword,
                             leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: EnumDeclSyntax) { leaveTypeScope() }

    override func visit(_ node: ProtocolDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeDecl(name: node.name.text, modifiers: node.modifiers,
                             attributes: node.attributes,
                             keyword: node.protocolKeyword,
                             leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: ProtocolDeclSyntax) { leaveTypeScope() }

    // MARK: - Scope-only declarations (no emit, but track nesting)

    /// Extensions push the first-dot-stripped type path onto the scope stack so
    /// `extension Mod.Type` contributes `Type` as the nesting prefix. Push gated
    /// on same-line `{` AND a `[\w.]+` extended-type capture.
    override func visit(_ node: ExtensionDeclSyntax) -> SyntaxVisitorContinueKind {
        guard RegexShape.opensOnSameLine(keyword: node.extensionKeyword,
                                         leftBrace: node.memberBlock.leftBrace,
                                         converter: converter) else {
            scopePushed.append(false)
            return .visitChildren
        }
        let qualified = extendedTypeName(node.extendedType)
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

    /// Actor declarations participate in MainActorTypes only when their access modifier
    /// is `internal` — the `ActorDeclRegex` shape suppresses public/open actors but
    /// lets internal actors fall through into emission. Always push the scope so
    /// nested types resolve correctly.
    override func visit(_ node: ActorDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeDecl(name: node.name.text, modifiers: node.modifiers,
                             attributes: node.attributes,
                             keyword: node.actorKeyword,
                             leftBrace: node.memberBlock.leftBrace,
                             isActor: true)
    }

    override func visitPost(_ node: ActorDeclSyntax) { leaveTypeScope() }

    // MARK: - Helpers

    private func enterTypeDecl(
        name: String,
        modifiers: DeclModifierListSyntax,
        attributes: AttributeListSyntax,
        keyword: TokenSyntax,
        leftBrace: TokenSyntax,
        isActor: Bool = false
    ) -> SyntaxVisitorContinueKind {
        // Gated scope push: TypeDeclRegex (public|internal|open + optional final)
        // must match, the name must be a valid `\w+` identifier, and the body's
        // `{` must be on the same source line.
        guard RegexShape.matchesTypeDeclShape(modifiers),
              RegexShape.isWordIdentifier(name),
              RegexShape.opensOnSameLine(keyword: keyword, leftBrace: leftBrace, converter: converter) else {
            scopePushed.append(false)
            return .visitChildren
        }
        scopeStack.append(name)
        scopePushed.append(true)

        guard hasMainActorAttribute(attributes) else { return .visitChildren }

        guard let accessKeyword = firstAccessModifier(modifiers) else {
            // Regex requires public/internal/open — match that. (matchesTypeDeclShape
            // already enforced this; this is a belt-and-suspenders fallback.)
            return .visitChildren
        }

        // `actor` keyword: `ActorDeclRegex` suppresses only public/open actors.
        // Internal actors with @MainActor fall through into emission.
        if isActor {
            let text = accessKeyword.text
            if text == "public" || text == "open" {
                return .visitChildren
            }
        }

        let qualifiedPath = scopeStack.joined(separator: ".")
        mainActorTypes.append(qualifiedPath)

        let location = converter.location(for: accessKeyword.positionAfterSkippingLeadingTrivia)
        mainActorTypePositions[qualifiedPath] = SourcePositionJson(
            filePath: filePath,
            line: location.line,
            column: location.column
        )
        return .visitChildren
    }

    private func leaveTypeScope() {
        if let pushed = scopePushed.popLast(), pushed {
            _ = scopeStack.popLast()
        }
    }

    private func hasMainActorAttribute(_ attributes: AttributeListSyntax) -> Bool {
        for element in attributes {
            guard case .attribute(let attribute) = element else { continue }
            let typeName = attribute.attributeName.trimmedDescription
            if typeName == "MainActor" || typeName == "_Concurrency.MainActor" {
                return true
            }
        }
        return false
    }

    private func firstAccessModifier(_ modifiers: DeclModifierListSyntax) -> TokenSyntax? {
        for modifier in modifiers {
            let text = modifier.name.text
            if text == "public" || text == "internal" || text == "open" {
                return modifier.name
            }
        }
        return nil
    }

    private func extendedTypeName(_ type: TypeSyntax) -> String {
        // SwiftSyntax's TypeSyntax round-trips back to the original source text via
        // `trimmedDescription`. For `extension CryptoKit.P256.Signing { … }` this returns
        // "CryptoKit.P256.Signing" — exactly the `[\w.]+` extended-type text.
        return type.trimmedDescription
    }
}
