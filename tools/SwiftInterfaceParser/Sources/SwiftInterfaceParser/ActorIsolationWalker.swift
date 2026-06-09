// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks the syntax tree of a .swiftinterface and surfaces the actor-isolation cluster:
/// `customActorTypes`, `customActorIsolatorMap`, `actorIsolatedMembers`,
/// `mainActorIsolatedMembers`, and `nonisolatedMembers`.
///
/// The five facts share a single tree pass because their inputs are intertwined —
/// `customActorTypes` feeds the short-name set the regex builds for member-level
/// custom-actor matching, and `mainActorIsolatedMembers` is a strict subset of
/// `actorIsolatedMembers` keyed off the same per-decl attribute scan.
///
/// PARITY CONTRACT WITH `SwiftInterfaceAccessParser`:
///
/// 1. **Member key shape (`actorIsolatedMembers`, `mainActorIsolatedMembers`,
///    `nonisolatedMembers`)**: full dot-joined nested type path + `.` + printedName.
///    For free functions: bare `printedName` only (no type prefix). Extension
///    declarations push the FIRST-DOT-STRIPPED qualified type path (matching
///    `GetActorIsolatedMembers` lines 696-708 / `GetNonisolatedMembers` lines 863-876).
///
/// 2. **Custom-actor short-name set**: built from `customActorTypes` qualified paths
///    by taking each path's last `.`-separated component (regex equivalent at
///    `GetCustomActorIsolatorMap` lines 452-457). Used for:
///      a) The local-actor regex `@(?:\w+\.)?(<name>|...)\b` in
///         `GetCustomActorIsolatorMap` line 467.
///      b) The custom-actor regex `@(?:\w+\.)?(?:<name>|...)\b` in
///         `GetActorIsolatedMembers` line 607.
///    These two regexes are nearly identical — local has a capture group, member
///    detection does not — but both demand the same name set and treat module-prefix
///    optionally.
///
/// 3. **Imported custom-actor heuristic** (`@(?:\w+\.)+(?!MainActor\b)(\w*Actor)\b`):
///    only `customActorIsolatorMap` uses this fallback. Member-level `actorIsolatedMembers`
///    detection does NOT include the imported pattern — match the regex parser exactly.
///
/// 4. **Suppressions**:
///    - `actor` keyword decls are NOT eligible for member-level isolation processing
///      (regex's customActorRegex/MainActorAnnotationRegex still hits them, but the
///      type's own decl is filtered by `ActorDeclRegex` checks). For
///      `customActorIsolatorMap`, we skip when the decl line itself matches `actor`
///      because `@MyActor public actor MyActor` is the actor-keyword form — already
///      tracked by `customActorTypes`.
///    - `MainActorIsolatedMembers` is a strict subset of `actorIsolatedMembers` —
///      a member is in BOTH iff its triggering attribute is `@MainActor` /
///      `@_Concurrency.MainActor`. Custom-actor-only isolation goes to
///      `actorIsolatedMembers` only.
///
/// 5. **Free-function path**: only `public`/`open` `func` at top level. The regex
///    parser's free-function path uses `PublicFuncRegex` — no init, no var, no bare
///    func. We mirror exactly: top-level `init`/`var` are not tracked even with
///    `@MainActor`.
///
/// 6. **Custom-actor decl-level skip**: `customActorIsolatorMap` records only when
///    the type decl is NOT an `actor` keyword decl. This matches the regex's
///    `!ActorDeclRegex.IsMatch(trimmed)` guard at `GetCustomActorIsolatorMap`
///    line 540 — `@MyActor public actor MyActor` doesn't get a self-entry.
///
/// 7. **CustomActorIsolatorMap "first match wins"**: regex line 545 — repeated
///    annotations on the same qualified path don't overwrite. We mirror.
///
/// 8. **Top-level free function with @MainActor**: bare `printedName` key (no type
///    prefix). Free top-level vars/inits with @MainActor are NOT tracked.
final class ActorIsolationWalker: SyntaxVisitor {
    let filePath: String
    let converter: SourceLocationConverter

    /// Output buckets — sorted to dedupe / stabilize JSON output before being
    /// converted into Sets / Dicts.
    private var actorIsolatedMembers: Set<String> = []
    private var mainActorIsolatedMembers: Set<String> = []
    private var nonisolatedMembers: Set<String> = []
    private(set) var customActorTypes: [String] = []
    private(set) var customActorIsolatorMap: [String: String] = [:]

    /// Set of "leaf" custom-actor names (e.g., "ImagePipelineActor"). Built lazily
    /// after the first walk pass collects `customActorTypes`. Empty during the
    /// initial pass — the cluster does both passes inline so `actorIsolatedMembers`
    /// can match against custom-actor names declared anywhere in the same file.
    private var customActorShortNames: Set<String> = []

    /// Scope stack: name + isExtension flag. For nested types we push the simple
    /// name; for extensions we push the qualified type path with the first dot
    /// component (module prefix) stripped.
    private struct Scope {
        let name: String
        let isExtension: Bool
    }
    private var scopeStack: [Scope] = []

    /// Parallel stack: each visited type/extension records whether it actually
    /// pushed a frame on `scopeStack`. Mirrors the regex tracker's gated push so
    /// `visitPost` knows whether to pop.
    private var scopePushed: [Bool] = []

    init(filePath: String, source: String, customActorShortNames: Set<String> = []) {
        self.filePath = filePath
        self.converter = SourceLocationConverter(fileName: filePath, tree: Parser.parse(source: source))
        self.customActorShortNames = customActorShortNames
        super.init(viewMode: .sourceAccurate)
    }

    /// Two-pass entry. Pass 1 walks the tree to collect `customActorTypes`. Pass 2
    /// re-walks with the resulting short-name set so member-level custom-actor
    /// detection works the same way as the regex parser's
    /// `GetActorIsolatedMembers(..., customActorTypeNames, ...)` two-step.
    static func parse(filePath: String, source: String) -> ActorIsolationResult {
        let tree = Parser.parse(source: source)

        // Pass 1: discover custom actor types only.
        let pass1 = ActorIsolationWalker(filePath: filePath, source: source)
        pass1.collectCustomActorTypesOnly = true
        pass1.walk(tree)

        // Build short-name set, matching `GetCustomActorIsolatorMap` lines 452-457:
        // strip everything before the last `.`, drop empty leftovers, dedupe.
        var shortNames = Set<String>()
        for qualified in pass1.customActorTypes {
            let leaf: String
            if let dotIdx = qualified.lastIndex(of: ".") {
                leaf = String(qualified[qualified.index(after: dotIdx)...])
            } else {
                leaf = qualified
            }
            if !leaf.isEmpty {
                shortNames.insert(leaf)
            }
        }

        // Pass 2: full extraction with the short-name set in hand.
        let pass2 = ActorIsolationWalker(filePath: filePath, source: source, customActorShortNames: shortNames)
        pass2.walk(tree)

        return ActorIsolationResult(
            actorIsolatedMembers: Array(pass2.actorIsolatedMembers).sorted(),
            mainActorIsolatedMembers: Array(pass2.mainActorIsolatedMembers).sorted(),
            nonisolatedMembers: Array(pass2.nonisolatedMembers).sorted(),
            customActorTypes: pass2.customActorTypes,
            customActorIsolatorMap: pass2.customActorIsolatorMap
        )
    }

    /// Pass-1 mode: skip member-level work. Set by `parse(...)` between the two walks.
    private var collectCustomActorTypesOnly: Bool = false

    // MARK: - Type declarations

    override func visit(_ node: ClassDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeDecl(name: node.name.text, isActor: false,
                             attributes: node.attributes, modifiers: node.modifiers,
                             keyword: node.classKeyword,
                             leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: ClassDeclSyntax) { exitTypeDecl() }

    override func visit(_ node: StructDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeDecl(name: node.name.text, isActor: false,
                             attributes: node.attributes, modifiers: node.modifiers,
                             keyword: node.structKeyword,
                             leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: StructDeclSyntax) { exitTypeDecl() }

    override func visit(_ node: EnumDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeDecl(name: node.name.text, isActor: false,
                             attributes: node.attributes, modifiers: node.modifiers,
                             keyword: node.enumKeyword,
                             leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: EnumDeclSyntax) { exitTypeDecl() }

    override func visit(_ node: ProtocolDeclSyntax) -> SyntaxVisitorContinueKind {
        return enterTypeDecl(name: node.name.text, isActor: false,
                             attributes: node.attributes, modifiers: node.modifiers,
                             keyword: node.protocolKeyword,
                             leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: ProtocolDeclSyntax) { exitTypeDecl() }

    /// `actor X { }` declarations — eligible for `customActorTypes` only when the
    /// access modifier is `public` or `open` (matching `ActorDeclRegex` at line 70-72:
    /// `(?:public|open)\s+actor\s+(\w+)`). Scope push gated through the same
    /// TypeDeclRegex shape as other types so non-matching shapes (e.g., bodies
    /// that open on a later line, backtick-escaped names) don't push.
    override func visit(_ node: ActorDeclSyntax) -> SyntaxVisitorContinueKind {
        let pushed = pushTypeScopeIfMatching(name: node.name.text,
                                             modifiers: node.modifiers,
                                             keyword: node.actorKeyword,
                                             leftBrace: node.memberBlock.leftBrace,
                                             isExtension: false)
        if pushed,
           let access = firstAccessModifier(node.modifiers),
           access == "public" || access == "open" {
            let qualifiedPath = scopeStack.map { $0.name }.joined(separator: ".")
            // Pass-1 collects this; pass-2 re-emits it deterministically.
            if !customActorTypes.contains(qualifiedPath) {
                customActorTypes.append(qualifiedPath)
            }
        }
        // CustomActorIsolatorMap regex skips `ActorDeclRegex.IsMatch(trimmed)` lines
        // (line 540). The actor-keyword form is tracked by `customActorTypes`, not here.
        return .visitChildren
    }
    override func visitPost(_ node: ActorDeclSyntax) { exitTypeDecl() }

    /// Extensions push scope with first-dot-stripped qualified type path
    /// (matching GetActorIsolatedMembers lines 696-708, GetNonisolatedMembers
    /// lines 863-876, GetCustomActorIsolatorMap lines 552-559). Push gated on
    /// same-line `{` AND a `[\w.]+` extended-type capture.
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
        scopeStack.append(Scope(name: stripped, isExtension: true))
        scopePushed.append(true)
        return .visitChildren
    }
    override func visitPost(_ node: ExtensionDeclSyntax) { exitTypeDecl() }

    // MARK: - Member declarations

    override func visit(_ node: FunctionDeclSyntax) -> SyntaxVisitorContinueKind {
        if collectCustomActorTypesOnly { return .skipChildren }
        let kind = MemberKind.function(name: node.name.text)
        emitMember(kind: kind, attributes: node.attributes, modifiers: node.modifiers, params: node.signature.parameterClause)
        return .skipChildren
    }

    override func visit(_ node: InitializerDeclSyntax) -> SyntaxVisitorContinueKind {
        if collectCustomActorTypesOnly { return .skipChildren }
        let kind = MemberKind.initializer
        emitMember(kind: kind, attributes: node.attributes, modifiers: node.modifiers, params: node.signature.parameterClause)
        return .skipChildren
    }

    override func visit(_ node: VariableDeclSyntax) -> SyntaxVisitorContinueKind {
        if collectCustomActorTypesOnly { return .skipChildren }
        // VariableDecl can declare multiple bindings (`var x, y, z`); regex keys each
        // off the FIRST identifier in `(?:var|let)\s+(\w+)` — match that.
        guard let firstBinding = node.bindings.first,
              let identifier = firstBinding.pattern.as(IdentifierPatternSyntax.self) else {
            return .skipChildren
        }
        let kind = MemberKind.property(name: identifier.identifier.text)
        emitMember(kind: kind, attributes: node.attributes, modifiers: node.modifiers, params: nil)
        return .skipChildren
    }

    // MARK: - Helpers

    /// Used by `enterTypeDecl` for class/struct/enum/protocol — and by the actor
    /// branch directly. Pushes scope through the gated regex-shape predicate.
    /// Then, if the decl is a non-actor type with a custom-actor attribute,
    /// contributes to `customActorIsolatorMap`.
    private func enterTypeDecl(name: String, isActor: Bool,
                               attributes: AttributeListSyntax,
                               modifiers: DeclModifierListSyntax,
                               keyword: TokenSyntax,
                               leftBrace: TokenSyntax) -> SyntaxVisitorContinueKind {
        let pushed = pushTypeScopeIfMatching(name: name, modifiers: modifiers,
                                             keyword: keyword, leftBrace: leftBrace,
                                             isExtension: false)

        // Skip pass 1 — only need actor types from it.
        if collectCustomActorTypesOnly { return .visitChildren }

        // Skip the `actor` keyword case (excluded by `!ActorDeclRegex.IsMatch(trimmed)`
        // at GetCustomActorIsolatorMap line 540).
        if isActor { return .visitChildren }

        if pushed, let actorName = matchAnyCustomActor(attributes: attributes, includeImported: true) {
            let qualifiedPath = scopeStack.map { $0.name }.joined(separator: ".")
            // First match wins (regex line 545).
            if customActorIsolatorMap[qualifiedPath] == nil {
                customActorIsolatorMap[qualifiedPath] = actorName
            }
        }

        return .visitChildren
    }

    /// Push the type onto the scope stack iff the regex tracker would have:
    /// `TypeDeclRegex` (public|internal|open + optional final) matches, the name
    /// satisfies `\w+`, and the body's `{` is on the same source line as the
    /// keyword. Returns whether the push happened so callers can gate side-effects.
    @discardableResult
    private func pushTypeScopeIfMatching(name: String,
                                         modifiers: DeclModifierListSyntax,
                                         keyword: TokenSyntax,
                                         leftBrace: TokenSyntax,
                                         isExtension: Bool) -> Bool {
        if RegexShape.matchesTypeDeclShape(modifiers),
           RegexShape.isWordIdentifier(name),
           RegexShape.opensOnSameLine(keyword: keyword, leftBrace: leftBrace, converter: converter) {
            scopeStack.append(Scope(name: name, isExtension: isExtension))
            scopePushed.append(true)
            return true
        }
        scopePushed.append(false)
        return false
    }

    private func exitTypeDecl() {
        if let pushed = scopePushed.popLast(), pushed {
            scopeStack.removeLast()
        }
    }

    /// MemberKind feeds key construction; the regex parser uses `printedName` for
    /// func / init and the bare identifier for var/let.
    private enum MemberKind {
        case function(name: String)
        case initializer
        case property(name: String)
    }

    /// Per-decl member emission. Three buckets contributed:
    ///  - actor-isolated (any of @MainActor / @_Concurrency.MainActor / custom actor)
    ///  - main-actor-isolated (subset: only @MainActor / @_Concurrency.MainActor)
    ///  - nonisolated (member has the `nonisolated` modifier)
    private func emitMember(kind: MemberKind, attributes: AttributeListSyntax, modifiers: DeclModifierListSyntax, params: FunctionParameterClauseSyntax?) {
        let isMainActor = hasMainActorAttribute(attributes)
        // Member-level custom-actor matching uses the SHORT-NAME set only, NOT the
        // imported `\w*Actor` heuristic — matches `GetActorIsolatedMembers` line 605-609
        // which builds the regex purely from `customActorTypeNames`.
        let isCustomActor = matchAnyCustomActor(attributes: attributes, includeImported: false) != nil
        let isAnyActor = isMainActor || isCustomActor
        let isNonisolated = hasNonisolatedModifier(modifiers)

        if !isAnyActor && !isNonisolated {
            return
        }

        // Build the printedName according to the decl kind. For func/init, we mirror
        // ExtractPrintedName output: `name(label1:label2:)` or `name()` for zero
        // params (also the no-label / wildcard case).
        let printedName: String?
        switch kind {
        case .function(let name):
            printedName = buildPrintedName(funcName: name, params: params)
        case .initializer:
            printedName = buildPrintedName(funcName: "init", params: params)
        case .property(let name):
            printedName = name
        }
        guard let pname = printedName else { return }

        let qualifiedType = scopeStack.map { $0.name }.joined(separator: ".")

        // Free function path: only `public`/`open` `func` qualifies. Regex restricts
        // top-level free-func tracking to `PublicFuncRegex` (lines 732-748) — no
        // init/var/subscript at module scope, no bare/protocol-style unmodified
        // funcs at top level.
        if scopeStack.isEmpty {
            guard case .function = kind,
                  let access = firstAccessModifier(modifiers),
                  access == "public" || access == "open"
            else { return }
            if isAnyActor {
                actorIsolatedMembers.insert(pname)
                if isMainActor {
                    mainActorIsolatedMembers.insert(pname)
                }
            }
            // NOTE: nonisolated free functions are NOT tracked by the regex parser
            // (GetNonisolatedMembers requires `typeStack.Count > 0` at line 879). We
            // mirror — drop nonisolated at module scope.
            return
        }

        let key = "\(qualifiedType).\(pname)"

        if isAnyActor {
            actorIsolatedMembers.insert(key)
            if isMainActor {
                mainActorIsolatedMembers.insert(key)
            }
        }

        if isNonisolated {
            nonisolatedMembers.insert(key)
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

    /// Match attribute against either the local short-name set or, optionally, the
    /// imported `(\w+\.)+(?!MainActor\b)\w*Actor` heuristic. Returns the matched
    /// actor's leaf identifier (the same string the regex captures with its first
    /// group).
    ///
    /// - parameter includeImported: when true, falls back to the imported pattern
    ///   after the local short-name set fails. Used by `customActorIsolatorMap`
    ///   only — `actorIsolatedMembers` does NOT enable this fallback.
    private func matchAnyCustomActor(attributes: AttributeListSyntax, includeImported: Bool) -> String? {
        var localMatch: String? = nil
        var importedMatch: String? = nil

        for element in attributes {
            guard case .attribute(let attribute) = element else { continue }
            let typeName = attribute.attributeName.trimmedDescription

            // Skip MainActor — handled separately and excluded by both regexes.
            if typeName == "MainActor" || typeName == "_Concurrency.MainActor" { continue }

            // Local-actor regex: `@(?:\w+\.)?(<short_name>)\b`. Accept both bare
            // (`@MyActor`) and one-segment-prefixed (`@Module.MyActor`).
            if let leaf = leafIdentifier(of: typeName), customActorShortNames.contains(leaf) {
                if isQualifiedWithSinglePrefixOrBare(typeName) && localMatch == nil {
                    localMatch = leaf
                }
            }

            // Imported regex: `@(?:\w+\.)+(?!MainActor\b)(\w*Actor)\b` — requires AT
            // LEAST one `Module.` prefix and a leaf ending in `Actor`. Uses the same
            // `MainActor` exclusion as the regex parser's negative lookahead.
            if includeImported && importedMatch == nil {
                if let leaf = qualifiedLeafEndingInActor(typeName), leaf != "MainActor" {
                    importedMatch = leaf
                }
            }
        }

        // Priority: local (same-module) match wins over imported (matches regex line 508-509).
        return localMatch ?? importedMatch
    }

    /// Last `.`-separated component of a typeName, or the whole string when there
    /// is no dot. Returns nil for empty inputs.
    private func leafIdentifier(of typeName: String) -> String? {
        if let lastDot = typeName.lastIndex(of: ".") {
            let leaf = String(typeName[typeName.index(after: lastDot)...])
            return leaf.isEmpty ? nil : leaf
        }
        return typeName.isEmpty ? nil : typeName
    }

    /// True when `typeName` is bare (no dot) or has exactly one prefix segment
    /// (`Module.Name`). Matches `(?:\w+\.)?` — zero or one prefix component.
    private func isQualifiedWithSinglePrefixOrBare(_ typeName: String) -> Bool {
        var dotCount = 0
        for ch in typeName where ch == "." { dotCount += 1 }
        return dotCount <= 1
    }

    /// True when `typeName` matches `(?:\w+\.)+(?!MainActor\b)(\w*Actor)\b`:
    /// at least one `.` prefix component and a leaf ending in `Actor`. Returns
    /// the leaf identifier when matched, else nil.
    private func qualifiedLeafEndingInActor(_ typeName: String) -> String? {
        guard let lastDot = typeName.lastIndex(of: ".") else { return nil }
        let leaf = String(typeName[typeName.index(after: lastDot)...])
        // \w* before Actor: leaf is ALL word chars and ends with `Actor`.
        guard !leaf.isEmpty, leaf.hasSuffix("Actor") else { return nil }
        for ch in leaf where !(ch.isLetter || ch.isNumber || ch == "_") {
            return nil
        }
        return leaf
    }

    private func hasNonisolatedModifier(_ modifiers: DeclModifierListSyntax) -> Bool {
        for modifier in modifiers {
            // `nonisolated` plain or `nonisolated(unsafe)` — both have name == "nonisolated".
            // SEMANTIC CLIFF: the regex's `NonisolatedRegex`
            // (`nonisolated\s+(?:public|open|final|var|let|func|static|class)`) misses
            // `nonisolated(unsafe)` because the `(` after `nonisolated` breaks the
            // `\s+keyword` requirement. SwiftSyntax sees both forms as the same modifier
            // and would correctly emit them. To preserve byte-equal parity with the
            // regex parser, we DROP `nonisolated(unsafe)` matches — this is a known
            // semantic cliff that will be fixed when the regex parser is retired.
            if modifier.name.text == "nonisolated" {
                if let detail = modifier.detail, detail.detail.text == "unsafe" {
                    continue
                }
                return true
            }
        }
        return false
    }

    private func firstAccessModifier(_ modifiers: DeclModifierListSyntax) -> String? {
        for modifier in modifiers {
            let text = modifier.name.text
            if text == "public" || text == "internal" || text == "open" {
                return text
            }
        }
        return nil
    }

    /// Builds the `printedName` form (e.g., `foo(_:bar:)`) for a function or init
    /// declaration. Mirrors `ExtractPrintedName` semantics — uses the EXTERNAL label
    /// for each parameter, falling back to `_` when the external is `_` or absent.
    /// Returns `name()` for zero params or no first-name parameters.
    private func buildPrintedName(funcName: String, params: FunctionParameterClauseSyntax?) -> String {
        guard let params = params else { return "\(funcName)()" }
        let paramList = params.parameters
        if paramList.isEmpty {
            return "\(funcName)()"
        }
        var labels: [String] = []
        for param in paramList {
            // FunctionParameter.firstName is the external label (`_` for unlabeled);
            // SwiftSyntax represents an explicit `_` as the firstName token text "_".
            // ExtractPrintedName uses `words.Split(' ').First()` of the
            // before-colon text, which IS the external label in source order.
            let firstName = param.firstName.text
            if firstName.isEmpty { continue }
            labels.append(firstName)
        }
        if labels.isEmpty {
            return "\(funcName)()"
        }
        return "\(funcName)(\(labels.map { "\($0):" }.joined()))"
    }
}

/// Output bag for the cluster — five facts plus a sentinel telling the host whether
/// any custom actors were observed (which affects how `actorIsolatedMembers` matches).
struct ActorIsolationResult {
    let actorIsolatedMembers: [String]
    let mainActorIsolatedMembers: [String]
    let nonisolatedMembers: [String]
    let customActorTypes: [String]
    let customActorIsolatorMap: [String: String]
}
