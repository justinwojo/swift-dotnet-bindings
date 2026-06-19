// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks the syntax tree and surfaces three protocol-level facts:
///   * `conventionCProtocols`           — Set of public/open protocol names whose
///     bodies contain `@convention(c)` or `@convention(block)` parameters
///     (either directly or via a typealias defined in this file).
///   * `conventionCProtocolPositions`   — line/column of each detected protocol's
///     access-modifier token. Position points at the `public`/`open` keyword,
///     NOT at the `@convention` reference.
///   * `hiddenRequirementProtocols`     — `protocolName -> Set<memberName>` for
///     requirements that are NOT satisfied by a same-module extension default and that
///     swift-api-digester strips from the ABI JSON. Two shapes qualify: (a) the member
///     NAME is `__`-prefixed, or (b) the member's SIGNATURE references a `__`-prefixed SPI
///     type (e.g. `func _resolve(in: __Ctx) -> __Resolved`). Used downstream to suppress
///     EveryProtocol generation.
///
/// EXTRACTION CONTRACT:
///
/// 1. **Public/open protocol filter**: only protocols with `public` or `open`
///    modifier participate. Bare/internal protocols are ignored.
///
/// 2. **Module name source**: extracted from the swiftinterface header comment
///    `// swift-module-flags: ... -module-name X` (first 64 lines). When absent,
///    qualified extensions never match (no module name means no qualified extension
///    can be confirmed same-module).
///
/// 3. **`@convention` typealias resolution** (one level): a top-level typealias
///    whose initializer text contains `@convention(c)` or `@convention(block)`
///    contributes its name to the alias set. Inside a protocol body, a member
///    whose source text contains a word-boundary match of any alias name flags
///    the protocol. Indirect alias chains (alias -> alias) are NOT resolved.
///
/// 4. **Position semantics**: `conventionCProtocolPositions[protoName]` points
///    at the location of the access modifier (`public`/`open`) token at the
///    protocol declaration.
///
/// 5. **`__`-prefix detection**: `var`/`let` and `func` decls whose name starts with two
///    literal underscores. `typealias`/`associatedtype` with `__` names are NOT detected.
///    The separate **type-hidden** path (signature references a `__`-type)
///    additionally covers `init` and `subscript`, keyed by `"init"`/`"subscript"`.
///
/// 6. **Same-module extension matching**: an extension's qualifier (text before
///    the FIRST `.` in the extended type) is compared (ordinal) to the file's
///    `-module-name`. Unqualified extensions (no dot) are ALWAYS same-module
///    (Swift's grammar requires same-module for unqualified extensions).
///
/// 7. **Multi-segment qualifiers**: `extension Foo.Bar.Baz` — qualifier is
///    `"Foo"`, simpleName is `"Bar.Baz"`. The simpleName must exactly match a
///    tracked protocol name; nested protocol names are unusual but mirrored.
final class ProtocolFactsWalker: SyntaxVisitor {
    // Output state
    private(set) var conventionCProtocols: [String] = []
    private(set) var conventionCProtocolPositions: [String: SourcePositionJson] = [:]
    private(set) var hiddenRequirementProtocols: [String: Set<String>] = [:]

    // Internal state — populated by pre-passes.
    private let filePath: String
    private let converter: SourceLocationConverter
    private let conventionAliases: Set<String>
    private let aliasRegexes: [(name: String, regex: NSRegularExpression)]
    private let moduleName: String?

    /// Pass-1 protocols with their __-requirement candidates. Populated by walk;
    /// pass-2 extensions then consume this map to determine final unsatisfied set.
    private var requirementCandidates: [String: Set<String>] = [:]
    /// Pass-2 satisfied-by-extension defaults, keyed by protocol simple name.
    private var extensionDefaults: [String: Set<String>] = [:]

    init(filePath: String, source: String, tree: SourceFileSyntax) {
        self.filePath = filePath
        self.converter = SourceLocationConverter(fileName: filePath, tree: tree)
        self.moduleName = ProtocolFactsWalker.extractModuleName(from: source)
        let aliases = ProtocolFactsWalker.collectConventionAliases(tree: tree)
        self.conventionAliases = aliases
        self.aliasRegexes = aliases.compactMap { name in
            // Word-boundary match for the alias name. NSRegularExpression's `\b`
            // treats `.` as a non-word char so `\bFTS5TokenCallback\b` matches
            // inside `SomeModule.FTS5TokenCallback`.
            guard let r = try? NSRegularExpression(
                pattern: "\\b\(NSRegularExpression.escapedPattern(for: name))\\b",
                options: []) else { return nil }
            return (name, r)
        }
        super.init(viewMode: .sourceAccurate)
    }

    static func parse(filePath: String, source: String) -> (
        conventionCProtocols: [String],
        conventionCProtocolPositions: [String: SourcePositionJson],
        hiddenRequirementProtocols: [String: [String]]
    ) {
        let tree = Parser.parse(source: source)
        let walker = ProtocolFactsWalker(filePath: filePath, source: source, tree: tree)
        walker.walk(tree)
        // Build final hiddenRequirementProtocols by subtracting extension defaults.
        var finalHidden: [String: [String]] = [:]
        for (proto, candidates) in walker.requirementCandidates {
            let satisfied = walker.extensionDefaults[proto] ?? []
            let unsatisfied = candidates.subtracting(satisfied)
            if !unsatisfied.isEmpty {
                // Stable order for deterministic JSON.
                finalHidden[proto] = unsatisfied.sorted()
            }
        }
        return (walker.conventionCProtocols,
                walker.conventionCProtocolPositions,
                finalHidden)
    }

    // MARK: - Module-name + alias prepass

    /// Scan the first ~64 lines of source for a `// swift-module-flags: ... -module-name X`
    /// directive. Stops at the first non-comment, non-blank line.
    private static func extractModuleName(from source: String) -> String? {
        let lines = source.split(separator: "\n", maxSplits: 64, omittingEmptySubsequences: false)
        let pattern = try? NSRegularExpression(pattern: "-module-name\\s+([A-Za-z_][A-Za-z0-9_]*)", options: [])
        for raw in lines.prefix(64) {
            let line = String(raw)
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("//") {
                if let match = pattern?.firstMatch(in: line, options: [], range: NSRange(location: 0, length: line.utf16.count)),
                   match.numberOfRanges >= 2,
                   let r = Range(match.range(at: 1), in: line) {
                    return String(line[r])
                }
                continue
            }
            if trimmed.isEmpty { continue }
            // Stop at first real source line.
            break
        }
        return nil
    }

    /// Collect names of typealiases (at ANY nesting depth) whose initializer
    /// references `@convention(c)` or `@convention(block)`. Nested aliases such as
    /// `public enum E { public typealias Callback = @convention(c) ... }` are
    /// captured the same as top-level ones — the bare alias name is recorded and
    /// the protocol-body word-boundary check on the unqualified name finds
    /// matches like `E.Callback`.
    private static func collectConventionAliases(tree: SourceFileSyntax) -> Set<String> {
        let collector = ConventionAliasCollector()
        collector.walk(tree)
        return collector.aliases
    }

    // MARK: - Protocol body walk

    override func visit(_ node: ProtocolDeclSyntax) -> SyntaxVisitorContinueKind {
        guard let accessToken = firstAccessModifierToken(node.modifiers, accept: ["public", "open"]) else {
            return .visitChildren
        }
        let protoName = node.name.text

        // Pass 1 (convention): walk each direct member; flag if any contains
        // `@convention(c)`/`@convention(block)` directly OR a word-boundary alias name.
        var flagged = false
        for member in node.memberBlock.members {
            let text = member.decl.trimmedDescription
            if text.contains("@convention(c)") || text.contains("@convention(block)") {
                flagged = true
                break
            }
            for (_, regex) in aliasRegexes {
                if regex.firstMatch(in: text, options: [], range: NSRange(location: 0, length: text.utf16.count)) != nil {
                    flagged = true
                    break
                }
            }
            if flagged { break }
        }
        if flagged {
            conventionCProtocols.append(protoName)
            let loc = converter.location(for: accessToken.positionAfterSkippingLeadingTrivia)
            conventionCProtocolPositions[protoName] = SourcePositionJson(
                filePath: filePath, line: loc.line, column: loc.column)
        }

        // Pass 1 (hidden requirements): collect __-prefixed var/let/func names at
        // the direct member level only (depth 0). Accessors inside those decls are
        // NOT scanned.
        var candidates: Set<String> = []
        for member in node.memberBlock.members {
            collectUnderscoredRequirements(from: member.decl, into: &candidates)
        }
        if !candidates.isEmpty {
            // First-protocol-wins: in practice protocol names don't repeat within
            // a file, but guard against it explicitly.
            if requirementCandidates[protoName] == nil {
                requirementCandidates[protoName] = candidates
            }
        }

        return .visitChildren
    }

    // MARK: - Extension walk (pass 2 — collect satisfying defaults)

    override func visit(_ node: ExtensionDeclSyntax) -> SyntaxVisitorContinueKind {
        let qualified = node.extendedType.trimmedDescription
        let firstDot = qualified.firstIndex(of: ".")
        let simpleName: String?
        if let dot = firstDot {
            let qualifier = String(qualified[..<dot])
            // Same-module check: the file's `-module-name` (extracted from header)
            // must equal the qualifier. If we have no module name, qualified
            // extensions are never considered same-module.
            guard let mod = moduleName, qualifier == mod else { return .visitChildren }
            simpleName = String(qualified[qualified.index(after: dot)...])
        } else {
            // Unqualified extensions are always same-module.
            simpleName = qualified
        }
        guard let target = simpleName else { return .visitChildren }

        // SOURCE-ORDER: do NOT gate on `requirementCandidates[target] != nil`.
        // An extension default declared BEFORE its protocol in source order still
        // satisfies the hidden requirement. A SwiftSyntax `walk()` visits in source
        // order, so an earlier extension wouldn't yet see a later protocol's
        // requirementCandidates entry. Instead, collect defaults unconditionally
        // (keyed by simple name) and let the final reduction in `parse()` consume
        // only the protocols we actually tracked. Storage is bounded — we only
        // collect names with the `__` prefix, which is rare.
        var defaults = extensionDefaults[target] ?? []
        for member in node.memberBlock.members {
            collectUnderscoredRequirements(from: member.decl, into: &defaults)
        }
        if !defaults.isEmpty {
            extensionDefaults[target] = defaults
        }

        return .visitChildren
    }

    // MARK: - Helpers

    private func collectUnderscoredRequirements(from decl: DeclSyntax, into set: inout Set<String>) {
        if let v = decl.as(VariableDeclSyntax.self) {
            for binding in v.bindings {
                guard let pattern = binding.pattern.as(IdentifierPatternSyntax.self) else { continue }
                let name = pattern.identifier.text
                if name.hasPrefix("__") {
                    set.insert(name)
                } else if let ty = binding.typeAnnotation?.type,
                          ProtocolFactsWalker.textReferencesUnderscoredType(ty.trimmedDescription) {
                    // Type-hidden requirement: ordinary name, but the property type
                    // references a __-prefixed SPI type (stripped from the ABI JSON).
                    set.insert(name)
                }
            }
        } else if let f = decl.as(FunctionDeclSyntax.self) {
            let name = f.name.text
            if name.hasPrefix("__") {
                set.insert(name)
            } else if ProtocolFactsWalker.textReferencesUnderscoredType(f.signature.trimmedDescription) {
                set.insert(name)
            }
        } else if let i = decl.as(InitializerDeclSyntax.self) {
            if ProtocolFactsWalker.textReferencesUnderscoredType(i.signature.trimmedDescription) {
                set.insert("init")
            }
        } else if let s = decl.as(SubscriptDeclSyntax.self) {
            let sig = s.parameterClause.trimmedDescription + s.returnClause.trimmedDescription
            if ProtocolFactsWalker.textReferencesUnderscoredType(sig) {
                set.insert("subscript")
            }
        }
    }

    /// True when `text` references a `__`-prefixed identifier (typically an SPI type such as
    /// `RealityFoundation.__ResolvedRealityCoordinateSpace`). Pattern: `(?<![A-Za-z0-9_])__\w+`.
    /// The negative lookbehind excludes alnum/underscore but NOT `.`, so module-qualified SPI
    /// types still match while mid-identifier hits like `foo__bar` are rejected.
    private static let underscoredTypeReferenceRegex = try! NSRegularExpression(
        pattern: "(?<![A-Za-z0-9_])__\\w+", options: [])

    private static func textReferencesUnderscoredType(_ text: String) -> Bool {
        return underscoredTypeReferenceRegex.firstMatch(
            in: text, options: [],
            range: NSRange(location: 0, length: text.utf16.count)) != nil
    }

    private func firstAccessModifierToken(_ modifiers: DeclModifierListSyntax, accept: Set<String>) -> TokenSyntax? {
        for m in modifiers where accept.contains(m.name.text) {
            return m.name
        }
        return nil
    }
}

/// Internal visitor used by `ProtocolFactsWalker.collectConventionAliases` to find
/// typealiases of any nesting depth. A `typealias X = @convention(c) ...` declared
/// inside an enum, struct, or protocol body is captured the same as one at module scope.
private final class ConventionAliasCollector: SyntaxVisitor {
    var aliases: Set<String> = []

    init() {
        super.init(viewMode: .sourceAccurate)
    }

    override func visit(_ node: TypeAliasDeclSyntax) -> SyntaxVisitorContinueKind {
        let body = node.initializer.value.trimmedDescription
        if body.contains("@convention(c)") || body.contains("@convention(block)") {
            aliases.insert(node.name.text)
        }
        return .skipChildren
    }
}
