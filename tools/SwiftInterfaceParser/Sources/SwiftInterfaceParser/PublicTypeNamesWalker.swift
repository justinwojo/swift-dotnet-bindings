// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks the syntax tree of a .swiftinterface and surfaces the dot-qualified
/// names of all public/open types declared in this module.
///
/// EXTRACTION CONTRACT:
///
/// 1. **Decl kinds emitted**: `class`, `struct`, `enum`, `actor`, `protocol`
///    declared with `public` or `open`. Internal/private types are NOT emitted
///    (`PublicTypeDeclRegex` only matches public/open).
///
/// 2. **Extensions are NOT emitted**: `extension Foo { … }` is scope-context only;
///    the extension target never enters the result. Extensions are for external
///    module types and should not be treated as types defined in this module.
///
/// 3. **Nested-type qualification**: full dot-joined nesting path. So
///    `public struct Outer { public enum Inner { } }` produces
///    `["Outer", "Outer.Inner"]`.
///
/// 4. **Generic params stripped**: `public class Foo<T>` emits `Foo`, not
///    `Foo<T>` — the `\w+` capture excludes generic angle-brackets.
///
/// 5. **Extension scope context**: when a public type is declared inside an
///    extension body, the extension target's first-dot-stripped form is used as
///    the prefix (`ExtensionDeclRegex` pushes the qualified-after-first-dot name
///    onto the type stack).
///
/// 6. **Other modifiers are irrelevant**: `public final class Foo` and
///    `public indirect enum Tree` are both public; only the type's own access
///    level decides (`DeclAccess`).
///
/// 7. **`internal` types tracked for nesting only**: `public class Outer { internal
///    class Hidden { public class Sub {} } }` — `Hidden` is NOT emitted, but
///    `Sub` is keyed via the full nesting `Outer.Hidden.Sub` because brace-depth
///    tracking still pushes internal types as scope context.
///
/// 8. **Two-gate logic**: an access level the interface prints
///    (public|open|internal|package) gates the scope push; public|open gates
///    emission. Types whose body opens on a later line miss the `openBraces > 0`
///    gate and are not pushed.
final class PublicTypeNamesWalker: SyntaxVisitor {
    private(set) var publicTypeNames: [String] = []

    /// The full nesting path. For type decls (any access), pushes the simple name;
    /// for extensions, pushes the first-dot-stripped target (so
    /// `extension CryptoKit.P256.Signing` becomes `P256.Signing` on the stack,
    /// matching the `ExtensionDeclRegex` substring behavior).
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

    static func parse(filePath: String, source: String) -> [String] {
        let tree = Parser.parse(source: source)
        let converter = SourceLocationConverter(fileName: filePath, tree: tree)
        let walker = PublicTypeNamesWalker(converter: converter)
        walker.walk(tree)
        return walker.publicTypeNames
    }

    // MARK: - Type decls (gated push, mirroring `TypeDeclRegex` + `openBraces > 0`)

    override func visit(_ node: ClassDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeDecl(name: node.name.text, modifiers: node.modifiers,
                             keyword: node.classKeyword,
                             leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: ClassDeclSyntax) { leaveTypeScope() }

    override func visit(_ node: StructDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeDecl(name: node.name.text, modifiers: node.modifiers,
                             keyword: node.structKeyword,
                             leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: StructDeclSyntax) { leaveTypeScope() }

    override func visit(_ node: EnumDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeDecl(name: node.name.text, modifiers: node.modifiers,
                             keyword: node.enumKeyword,
                             leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: EnumDeclSyntax) { leaveTypeScope() }

    override func visit(_ node: ActorDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeDecl(name: node.name.text, modifiers: node.modifiers,
                             keyword: node.actorKeyword,
                             leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: ActorDeclSyntax) { leaveTypeScope() }

    override func visit(_ node: ProtocolDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeDecl(name: node.name.text, modifiers: node.modifiers,
                             keyword: node.protocolKeyword,
                             leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: ProtocolDeclSyntax) { leaveTypeScope() }

    // MARK: - Extensions (scope-only; gated on same-line `{` and `[\w.]+` capture)

    override func visit(_ node: ExtensionDeclSyntax) -> SyntaxVisitorContinueKind {
        guard typeOpensOnSameLine(keyword: node.extensionKeyword,
                                  leftBrace: node.memberBlock.leftBrace) else {
            scopePushed.append(false)
            return .visitChildren
        }
        let qualified = node.extendedType.dottedDescription
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

    /// Two independent gates:
    ///   * SCOPE PUSH gate: an access level the interface prints
    ///     (public|open|internal|package) plus `openBraces > 0`. If this fails, the type contributes no nesting
    ///     prefix — no push, no emit, no context for its children.
    ///   * EMIT gate: public|open, whatever other modifiers follow. If push
    ///     succeeded but emit fails (e.g., `internal class`), the type contributes
    ///     scope context for its children but is not added to `publicTypeNames`.
    private func enterTypeDecl(name: String,
                               modifiers: DeclModifierListSyntax,
                               keyword: TokenSyntax,
                               leftBrace: TokenSyntax) -> SyntaxVisitorContinueKind {
        let sameLine = typeOpensOnSameLine(keyword: keyword, leftBrace: leftBrace)
        // A keyword-named type keys by its unescaped name, as the ABI JSON prints it.
        // Whether the type is public depends on its own access level alone: a
        // `public indirect enum` is as public as a `public final class`, and a public
        // type missing from this list reads as module-internal to the ABI parser.
        let name = DeclAccess.unescape(name)
        if DeclAccess.hasAccess(modifiers, DeclAccess.interfaceAccessLevels), sameLine,
           RegexShape.isWordIdentifier(name) {
            scopeStack.append(name)
            scopePushed.append(true)
            if DeclAccess.isPublicOrOpen(modifiers) {
                publicTypeNames.append(scopeStack.joined(separator: "."))
            }
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

    /// True iff the type/extension keyword and its body's opening `{` are on the
    /// same source line — the `openBraces > 0` condition on the `TypeDeclRegex`
    /// match line.
    private func typeOpensOnSameLine(keyword: TokenSyntax, leftBrace: TokenSyntax) -> Bool {
        let kwLine = converter.location(for: keyword.positionAfterSkippingLeadingTrivia).line
        let braceLine = converter.location(for: leftBrace.positionAfterSkippingLeadingTrivia).line
        return kwLine == braceLine
    }
}
