// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks the syntax tree of a .swiftinterface and surfaces `typedThrowsErrors`
/// (per-method typed-throws error type, e.g.
/// `func parseNumber(_:) throws(Module.ParseError)`).
///
/// PARITY CONTRACT WITH `SwiftInterfaceAccessParser.GetTypedThrowsErrors`:
///
/// 1. **Decl kinds**: any `func` or `init` with `throws(<Type>)`. Subscripts and
///    properties are not in scope (regex's `IsFuncOrInitLine` only covers func/init).
///
/// 2. **Key shape**: TOP-OF-STACK simple type name + `.` + printedName. The regex's
///    `ProcessFuncLineForTypedThrows` (line 3202-3204) uses `typeStack.Peek().Name`,
///    NOT the full nested path. For free functions: bare `printedName`. For
///    extension members: the simple last-component name from `extension Mod.X`
///    (regex strips to last component at line 3151-3152).
///
///    SEMANTIC NOTE: this is intentionally less qualified than ActorIsolatedMembers
///    (which uses full path). The reason: the ABI parser queries
///    `_typedThrowsErrors` with `parentDecl.Name` (simple name only).
///
/// 3. **Value**: the syntactic spelling of the error type as written in source.
///    The regex `throws\(([^)]+)\)` captures up to the FIRST `)` and trims; SwiftSyntax
///    gives us the precise error-type syntax node, which we render back via
///    `trimmedDescription`. Both produce the same string for legal Swift code (the
///    Swift grammar doesn't allow `)` inside `throws(...)` types).
///
/// 4. **Untyped throws is excluded**: only `throws(T)` form contributes; `throws`
///    or non-throwing functions never key.
///
/// 5. **Access modifier**: regex's `AnyFuncRegex` accepts `public|open|internal|`
///    bare-no-modifier. We mirror — both public/open AND no-modifier (protocol
///    requirement) members emit. The regex's logic also covers extension-internal
///    members.
final class ThrowsWalker: SyntaxVisitor {
    let filePath: String
    let converter: SourceLocationConverter

    private(set) var typedThrowsErrors: [String: String] = [:]

    /// Scope stack with isExtension flag — extensions push the LAST-DOT-COMPONENT
    /// (simple) name for typed-throws keying (matching regex line 3151-3152).
    /// Nested types push their simple name. Top-of-stack is always queried for
    /// the key prefix.
    private struct Scope {
        let name: String
    }
    private var scopeStack: [Scope] = []

    init(filePath: String, source: String) {
        self.filePath = filePath
        self.converter = SourceLocationConverter(fileName: filePath, tree: Parser.parse(source: source))
        super.init(viewMode: .sourceAccurate)
    }

    static func parse(filePath: String, source: String) -> [String: String] {
        let tree = Parser.parse(source: source)
        let walker = ThrowsWalker(filePath: filePath, source: source)
        walker.walk(tree)
        return walker.typedThrowsErrors
    }

    // MARK: - Type declarations (push simple name)

    override func visit(_ node: ClassDeclSyntax) -> SyntaxVisitorContinueKind {
        scopeStack.append(Scope(name: node.name.text))
        return .visitChildren
    }
    override func visitPost(_ node: ClassDeclSyntax) { _ = scopeStack.popLast() }

    override func visit(_ node: StructDeclSyntax) -> SyntaxVisitorContinueKind {
        scopeStack.append(Scope(name: node.name.text))
        return .visitChildren
    }
    override func visitPost(_ node: StructDeclSyntax) { _ = scopeStack.popLast() }

    override func visit(_ node: EnumDeclSyntax) -> SyntaxVisitorContinueKind {
        scopeStack.append(Scope(name: node.name.text))
        return .visitChildren
    }
    override func visitPost(_ node: EnumDeclSyntax) { _ = scopeStack.popLast() }

    override func visit(_ node: ProtocolDeclSyntax) -> SyntaxVisitorContinueKind {
        scopeStack.append(Scope(name: node.name.text))
        return .visitChildren
    }
    override func visitPost(_ node: ProtocolDeclSyntax) { _ = scopeStack.popLast() }

    override func visit(_ node: ActorDeclSyntax) -> SyntaxVisitorContinueKind {
        scopeStack.append(Scope(name: node.name.text))
        return .visitChildren
    }
    override func visitPost(_ node: ActorDeclSyntax) { _ = scopeStack.popLast() }

    /// Extensions: push LAST-component of the qualified type as the simple name.
    /// Mirrors the regex's `qualifiedName.Substring(LastIndexOf('.') + 1)` at
    /// line 3151-3152 — typed throws uses simple name, NOT first-stripped path.
    override func visit(_ node: ExtensionDeclSyntax) -> SyntaxVisitorContinueKind {
        let qualified = node.extendedType.trimmedDescription
        let simple: String
        if let lastDot = qualified.lastIndex(of: ".") {
            simple = String(qualified[qualified.index(after: lastDot)...])
        } else {
            simple = qualified
        }
        scopeStack.append(Scope(name: simple))
        return .visitChildren
    }
    override func visitPost(_ node: ExtensionDeclSyntax) { _ = scopeStack.popLast() }

    // MARK: - Member declarations

    override func visit(_ node: FunctionDeclSyntax) -> SyntaxVisitorContinueKind {
        if let errorType = extractTypedThrows(effectSpecifiers: node.signature.effectSpecifiers) {
            let printed = buildPrintedName(funcName: node.name.text, params: node.signature.parameterClause)
            let key = makeKey(printedName: printed)
            typedThrowsErrors[key] = errorType
        }
        return .skipChildren
    }

    override func visit(_ node: InitializerDeclSyntax) -> SyntaxVisitorContinueKind {
        if let errorType = extractTypedThrows(effectSpecifiers: node.signature.effectSpecifiers) {
            let printed = buildPrintedName(funcName: "init", params: node.signature.parameterClause)
            let key = makeKey(printedName: printed)
            typedThrowsErrors[key] = errorType
        }
        return .skipChildren
    }

    private func extractTypedThrows(effectSpecifiers: FunctionEffectSpecifiersSyntax?) -> String? {
        guard let throwsClause = effectSpecifiers?.throwsClause else { return nil }
        guard let errorType = throwsClause.type else { return nil }
        let raw = errorType.trimmedDescription.trimmingCharacters(in: .whitespaces)
        return raw.isEmpty ? nil : raw
    }

    private func makeKey(printedName: String) -> String {
        if let top = scopeStack.last {
            return "\(top.name).\(printedName)"
        }
        return printedName
    }

    private func buildPrintedName(funcName: String, params: FunctionParameterClauseSyntax) -> String {
        let paramList = params.parameters
        if paramList.isEmpty { return "\(funcName)()" }
        var labels: [String] = []
        for param in paramList {
            let firstName = param.firstName.text
            if firstName.isEmpty { continue }
            labels.append(firstName)
        }
        if labels.isEmpty { return "\(funcName)()" }
        return "\(funcName)(\(labels.map { "\($0):" }.joined()))"
    }
}
