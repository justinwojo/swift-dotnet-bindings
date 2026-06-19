// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks the syntax tree of a .swiftinterface and surfaces every type declaration that
/// carries an explicit `@objc(CustomName)` runtime-name rename, keyed by its dot-qualified
/// type path (e.g. `"Widget"` or `"Outer.Inner"` → the custom ObjC runtime name).
///
/// EXTRACTION CONTRACT:
///
/// 1. **Decl kinds**: `class` / `struct` / `enum` / `actor` / `protocol` — exactly the
///    `TypeDeclRegex` set. Unlike `MainActorWalker` there is NO public/open suppression
///    for `actor`: the ObjC scanner uses `TypeDeclRegex` directly, so any public/internal/
///    open type (optional `final`) is eligible. `extension` pushes a scope frame for nested
///    qualified paths but never records its own `@objc` (recorded only on the
///    `TypeDeclRegex` branch, never the `ExtensionDeclRegex` branch).
///
/// 2. **Attribute match**: `ObjCCustomNameRegex = @objc\s*\(\s*([A-Za-z_]\w*)\s*\)`.
///    A bare `@objc` (no argument) leaves the runtime name equal to the Swift
///    name and is NOT recorded. Method/property selector renames (`@objc(foo:bar:)`,
///    `@objc(initWithName:)`) carry a trailing colon and fail the
///    `([A-Za-z_]\w*)\s*\)` shape, so they're excluded too. We run the same regex over
///    each attribute's text rather than guessing SwiftSyntax's selector-piece model.
///
/// 3. **Same-line / own-line attribute**: `@objc(Name)` is recorded whether it sits on
///    the declaration line or is deferred from the line immediately above. SwiftSyntax
///    attaches a leading attribute to its declaration regardless of line, so scanning
///    `node.attributes` covers both. An `@objc(Name)` followed by a member declaration
///    binds to that member (which a type-only walk ignores).
///
/// 4. **First-match wins**: a later member-level `@objc(name)` cannot overwrite a type-level
///    rename already recorded for the same qualified path.
///
/// 5. **Type-scope push gate**: identical to `MainActorWalker` — `TypeDeclRegex`
///    (public|internal|open + optional final) match AND same-line `{` AND a `\w+` name.
///    Extension push is additionally gated on same-line `{` AND a `[\w.]+` extended-type
///    capture, with the leading module component stripped (first-dot).
final class ObjCRuntimeNamesWalker: SyntaxVisitor {
    let converter: SourceLocationConverter

    private(set) var objcRuntimeNames: [String: String] = [:]

    private var scopeStack: [String] = []

    /// Parallel stack: each visited type/extension records whether it actually pushed a
    /// frame on `scopeStack`. The gated-push pattern ensures `visitPost` only pops when
    /// the corresponding `visit` actually pushed.
    private var scopePushed: [Bool] = []

    /// `ObjCCustomNameRegex`: `@objc\s*\(\s*([A-Za-z_]\w*)\s*\)`. The argument is a
    /// bare identifier; the closing paren immediately after it excludes selector renames.
    private static let objcCustomNameRegex =
        try! NSRegularExpression(pattern: "@objc\\s*\\(\\s*([A-Za-z_]\\w*)\\s*\\)")

    init(converter: SourceLocationConverter) {
        self.converter = converter
        super.init(viewMode: .sourceAccurate)
    }

    static func parse(filePath: String, source: String) -> [String: String] {
        let tree = Parser.parse(source: source)
        let converter = SourceLocationConverter(fileName: filePath, tree: tree)
        let walker = ObjCRuntimeNamesWalker(converter: converter)
        walker.walk(tree)
        return walker.objcRuntimeNames
    }

    // MARK: - Type declarations (TypeDeclRegex set; actor included, no access suppression)

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

    override func visit(_ node: ActorDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeDecl(name: node.name.text, modifiers: node.modifiers,
                             attributes: node.attributes,
                             keyword: node.actorKeyword,
                             leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: ActorDeclSyntax) { leaveTypeScope() }

    override func visit(_ node: ProtocolDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeDecl(name: node.name.text, modifiers: node.modifiers,
                             attributes: node.attributes,
                             keyword: node.protocolKeyword,
                             leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: ProtocolDeclSyntax) { leaveTypeScope() }

    // MARK: - Extensions (scope-only; first-dot-strip, same-line `{` gated, never emit)

    override func visit(_ node: ExtensionDeclSyntax) -> SyntaxVisitorContinueKind {
        guard RegexShape.opensOnSameLine(keyword: node.extensionKeyword,
                                         leftBrace: node.memberBlock.leftBrace,
                                         converter: converter) else {
            scopePushed.append(false)
            return .visitChildren
        }
        let qualified = node.extendedType.trimmedDescription
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

    // MARK: - Helpers

    private func enterTypeDecl(
        name: String,
        modifiers: DeclModifierListSyntax,
        attributes: AttributeListSyntax,
        keyword: TokenSyntax,
        leftBrace: TokenSyntax
    ) -> SyntaxVisitorContinueKind {
        // Gated scope push, identical to MainActorWalker.
        guard RegexShape.matchesTypeDeclShape(modifiers),
              RegexShape.isWordIdentifier(name),
              RegexShape.opensOnSameLine(keyword: keyword, leftBrace: leftBrace, converter: converter) else {
            scopePushed.append(false)
            return .visitChildren
        }
        scopeStack.append(name)
        scopePushed.append(true)

        // Record the qualified path (including the just-pushed current type) on first match.
        if let customName = objcCustomName(attributes) {
            let qualifiedPath = scopeStack.joined(separator: ".")
            if objcRuntimeNames[qualifiedPath] == nil {
                objcRuntimeNames[qualifiedPath] = customName
            }
        }
        return .visitChildren
    }

    private func leaveTypeScope() {
        if let pushed = scopePushed.popLast(), pushed {
            _ = scopeStack.popLast()
        }
    }

    /// Returns the first attribute matching `@objc(BareIdentifier)`, or nil. Runs
    /// `ObjCCustomNameRegex` over the attribute text, so selector renames
    /// (`@objc(foo:bar:)`) and bare `@objc` are excluded by the shape.
    private func objcCustomName(_ attributes: AttributeListSyntax) -> String? {
        for element in attributes {
            guard case .attribute(let attribute) = element else { continue }
            let text = attribute.trimmedDescription
            let ns = text as NSString
            guard let match = ObjCRuntimeNamesWalker.objcCustomNameRegex.firstMatch(
                in: text, range: NSRange(location: 0, length: ns.length)) else { continue }
            let g = match.range(at: 1)
            guard g.location != NSNotFound else { continue }
            return ns.substring(with: g)
        }
        return nil
    }
}
