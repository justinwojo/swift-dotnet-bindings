// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import SwiftSyntax

/// Resolves the `@MainActor` / `@Sendable` attributes a parameter's closure type carries through
/// a typealias. A parameter spelled `@escaping Module.CompletionBlock`, where
/// `typealias CompletionBlock = @MainActor @Sendable (T) -> Void`, has no attribute in its own
/// text, but the ABI descriptor desugars the alias and drops the attributes, so without this the
/// closure-attribute fact is silently empty and a synthesized protocol witness declares a plain
/// closure the conformance checker rejects.
///
/// Aliases are indexed by simple name with the chain of enclosing type/extension names, since a
/// reference may be module-qualified (`Module.Block`), scope-qualified (`Outer.Block`), or
/// relative (`Block` inside `Outer`). A reference that matches aliases with different attribute
/// sets is ambiguous and resolves to no attributes rather than a guess.
struct ClosureTypeAliasIndex {
    private struct Entry {
        let path: [String]
        let target: TypeSyntax
    }

    private var entries: [String: [Entry]] = [:]

    static let closureAttributeNames: Set<String> = ["MainActor", "Sendable"]

    static func build(_ tree: SourceFileSyntax) -> ClosureTypeAliasIndex {
        let collector = Collector(viewMode: .sourceAccurate)
        collector.walk(tree)
        var index = ClosureTypeAliasIndex()
        for (path, target) in collector.aliases {
            guard let name = path.last else { continue }
            index.entries[name, default: []].append(Entry(path: path, target: target))
        }
        return index
    }

    /// The closure attributes of `type` found by following typealiases, in first-seen order.
    /// Returns an empty list when `type` does not name an attributed closure alias.
    func attributes(of type: TypeSyntax) -> [String] {
        return resolve(type, depth: 0)
    }

    private func resolve(_ type: TypeSyntax, depth: Int) -> [String] {
        // Alias chains are short in practice; the bound only guards a self-referential cycle.
        guard depth < 8 else { return [] }

        if let attributed = type.as(AttributedTypeSyntax.self) {
            var attrs: [String] = []
            for element in attributed.attributes {
                guard let attribute = element.as(AttributeSyntax.self) else { continue }
                let fullName = attribute.attributeName.trimmedDescription
                let name = fullName.split(separator: ".").last.map(String.init) ?? fullName
                if Self.closureAttributeNames.contains(name), !attrs.contains(name) {
                    attrs.append(name)
                }
            }
            if attributed.baseType.is(FunctionTypeSyntax.self) {
                return attrs
            }
            // `@escaping Alias`: the attributes live on the alias's own function type.
            for name in resolve(attributed.baseType, depth: depth) where !attrs.contains(name) {
                attrs.append(name)
            }
            return attrs
        }
        if let optional = type.as(OptionalTypeSyntax.self) {
            return resolve(optional.wrappedType, depth: depth)
        }
        if let iuo = type.as(ImplicitlyUnwrappedOptionalTypeSyntax.self) {
            return resolve(iuo.wrappedType, depth: depth)
        }
        if let tuple = type.as(TupleTypeSyntax.self), tuple.elements.count == 1,
           let only = tuple.elements.first, only.firstName == nil, only.ellipsis == nil {
            return resolve(only.type, depth: depth)
        }
        guard let reference = Self.dottedName(type) else { return [] }
        guard let candidates = entries[reference.last!] else { return [] }

        var resolved: [String]? = nil
        for entry in candidates where Self.refersTo(reference, entry.path) {
            let attrs = resolve(entry.target, depth: depth + 1)
            if let previous = resolved, previous != attrs {
                return []
            }
            resolved = attrs
        }
        return resolved ?? []
    }

    /// `A.B.C` for an identifier or member type without generic arguments; nil otherwise.
    private static func dottedName(_ type: TypeSyntax) -> [String]? {
        if let identifier = type.as(IdentifierTypeSyntax.self) {
            guard identifier.genericArgumentClause == nil else { return nil }
            return [identifier.name.text]
        }
        if let member = type.as(MemberTypeSyntax.self) {
            guard member.genericArgumentClause == nil,
                  let base = dottedName(member.baseType) else { return nil }
            return base + [member.name.text]
        }
        return nil
    }

    /// True when `reference` names the alias declared at `path`: the path ends with the reference
    /// (scope-qualified or relative), or the reference is the path under one leading module name.
    private static func refersTo(_ reference: [String], _ path: [String]) -> Bool {
        if path.count >= reference.count, Array(path.suffix(reference.count)) == reference {
            return true
        }
        return reference.count == path.count + 1 && Array(reference.dropFirst()) == path
    }

    private final class Collector: SyntaxVisitor {
        var aliases: [(path: [String], target: TypeSyntax)] = []

        override func visit(_ node: TypeAliasDeclSyntax) -> SyntaxVisitorContinueKind {
            // A generic alias needs its arguments substituted before its attributes mean
            // anything at a use site; references to one are never plain dotted names anyway.
            guard node.genericParameterClause == nil else { return .skipChildren }
            aliases.append((Self.enclosingNames(of: Syntax(node)) + [node.name.text], node.initializer.value))
            return .skipChildren
        }

        private static func enclosingNames(of node: Syntax) -> [String] {
            var names: [String] = []
            var current = node.parent
            while let decl = current {
                if let d = decl.as(ClassDeclSyntax.self) { names.append(d.name.text) }
                else if let d = decl.as(StructDeclSyntax.self) { names.append(d.name.text) }
                else if let d = decl.as(EnumDeclSyntax.self) { names.append(d.name.text) }
                else if let d = decl.as(ActorDeclSyntax.self) { names.append(d.name.text) }
                else if let d = decl.as(ProtocolDeclSyntax.self) { names.append(d.name.text) }
                else if let d = decl.as(ExtensionDeclSyntax.self) {
                    // `extension Module.Outer.Inner` — the leading module name, if any, is
                    // tolerated by `refersTo` the same way a module-qualified reference is.
                    names.append(contentsOf: d.extendedType.trimmedDescription
                        .split(separator: ".").map(String.init).reversed())
                }
                current = decl.parent
            }
            return names.reversed()
        }
    }
}
