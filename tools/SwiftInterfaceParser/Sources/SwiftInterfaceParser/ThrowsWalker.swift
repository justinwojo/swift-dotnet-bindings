// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Typed-throws refinements keyed by the full owner path and printed name.
/// As with availability, overloads are staged before choosing bare or parameter-
/// signature keys. Every declaration participates, including plain throws.
/// Conflicting refinements under a normalized signature are omitted safely.
final class ThrowsWalker: SyntaxVisitor {
    private(set) var typedThrowsErrors: [String: String] = [:]
    private var scopeStack: [String] = []
    private struct Declaration {
        let signature: String
        let errorType: String?
    }
    private var declarations: [String: [Declaration]] = [:]

    init() {
        super.init(viewMode: .sourceAccurate)
    }

    static func parse(filePath: String, source: String) -> [String: String] {
        let tree = Parser.parse(source: source)
        let walker = ThrowsWalker()
        walker.walk(tree)
        walker.finalize()
        return walker.typedThrowsErrors
    }

    private func finalize() {
        for (bareKey, members) in declarations {
            let groups = Dictionary(grouping: members, by: \.signature)
            for (signature, siblings) in groups {
                // A normalized signature can still merge unsupported distinctions.
                // Never choose one declaration's refinement by source order.
                guard let errorType = siblings.first?.errorType,
                      siblings.allSatisfy({ $0.errorType == errorType }) else { continue }
                let key = groups.count == 1 ? bareKey : "\(bareKey)|\(signature)"
                typedThrowsErrors[key] = errorType
            }
        }
    }

    // Syntax scope is independent of declaration formatting and access modifiers.
    override func visit(_ node: ClassDeclSyntax) -> SyntaxVisitorContinueKind {
        scopeStack.append(node.name.text.replacingOccurrences(of: "`", with: "")); return .visitChildren
    }
    override func visitPost(_ node: ClassDeclSyntax) { scopeStack.removeLast() }
    override func visit(_ node: StructDeclSyntax) -> SyntaxVisitorContinueKind {
        scopeStack.append(node.name.text.replacingOccurrences(of: "`", with: "")); return .visitChildren
    }
    override func visitPost(_ node: StructDeclSyntax) { scopeStack.removeLast() }
    override func visit(_ node: EnumDeclSyntax) -> SyntaxVisitorContinueKind {
        scopeStack.append(node.name.text.replacingOccurrences(of: "`", with: "")); return .visitChildren
    }
    override func visitPost(_ node: EnumDeclSyntax) { scopeStack.removeLast() }
    override func visit(_ node: ProtocolDeclSyntax) -> SyntaxVisitorContinueKind {
        scopeStack.append(node.name.text.replacingOccurrences(of: "`", with: "")); return .visitChildren
    }
    override func visitPost(_ node: ProtocolDeclSyntax) { scopeStack.removeLast() }
    override func visit(_ node: ActorDeclSyntax) -> SyntaxVisitorContinueKind {
        scopeStack.append(node.name.text.replacingOccurrences(of: "`", with: "")); return .visitChildren
    }
    override func visitPost(_ node: ActorDeclSyntax) { scopeStack.removeLast() }

    override func visit(_ node: ExtensionDeclSyntax) -> SyntaxVisitorContinueKind {
        // Module-qualified swiftinterface targets use the availability convention:
        // drop the module component, preserving every nested owner component.
        let qualified = node.extendedType.trimmedDescription.replacingOccurrences(of: "`", with: "")
        let owner = qualified.firstIndex(of: ".").map { String(qualified[qualified.index(after: $0)...]) } ?? qualified
        scopeStack.append(owner)
        return .visitChildren
    }
    override func visitPost(_ node: ExtensionDeclSyntax) { scopeStack.removeLast() }

    override func visit(_ node: FunctionDeclSyntax) -> SyntaxVisitorContinueKind {
        record(name: node.name.text, signature: node.signature)
        return .skipChildren
    }
    override func visit(_ node: InitializerDeclSyntax) -> SyntaxVisitorContinueKind {
        record(name: "init", signature: node.signature)
        return .skipChildren
    }

    private func record(name: String, signature: FunctionSignatureSyntax) {
        let printed = buildPrintedName(funcName: name, params: signature.parameterClause)
        let key = (scopeStack + [printed]).joined(separator: ".")
        declarations[key, default: []].append(Declaration(
            signature: AvailabilityWalker.buildParamSignature(params: signature.parameterClause),
            errorType: extractTypedThrows(effectSpecifiers: signature.effectSpecifiers)))
    }

    private func extractTypedThrows(effectSpecifiers: FunctionEffectSpecifiersSyntax?) -> String? {
        guard let throwsClause = effectSpecifiers?.throwsClause else { return nil }
        guard let errorType = throwsClause.type else { return nil }
        let raw = errorType.trimmedDescription.trimmingCharacters(in: .whitespaces)
        return raw.isEmpty ? nil : raw
    }

    private func buildPrintedName(funcName: String, params: FunctionParameterClauseSyntax) -> String {
        let paramList = params.parameters
        if paramList.isEmpty { return "\(funcName)()" }
        var labels: [String] = []
        for param in paramList {
            let firstName = RegexShape.isOperatorIdentifier(funcName) ? "_" : param.firstName.text.replacingOccurrences(of: "`", with: "")
            if firstName.isEmpty { continue }
            labels.append(firstName)
        }
        if labels.isEmpty { return "\(funcName)()" }
        return "\(funcName)(\(labels.map { "\($0):" }.joined()))"
    }
}
