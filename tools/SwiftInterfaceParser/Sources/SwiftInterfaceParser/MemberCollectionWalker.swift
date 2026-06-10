// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks declarations and surfaces two member-set facts:
///   * `internalMemberKeys` — Set of `"Type.printedName"` keys for members marked
///     `internal` that appear in the swiftinterface (because `@inlinable` /
///     `@usableFromInline` exposed them despite the access level). These are the
///     "false public" entries the ABI parser uses for negative-space detection.
///   * `publicMemberNames`  — Set of `"Type.printedName"` keys for members marked
///     `public` or `open`. Includes free (module-level) public members with bare
///     keys (no type prefix).
///
/// PARITY CONTRACT WITH `SwiftInterfaceAccessParser.GetInternalMembers(path, out publicMemberNames)`
/// (line 2095):
///
/// 1. **Decl kinds covered**:
///      * `internalMemberKeys`: `func`, `var`/`let`, `init`. NOT subscript, case,
///        or nested type decls.
///      * `publicMemberNames`: `func`, `var`/`let`, `init`, `subscript`. NOT
///        case, deinit, or operator decls.
///
/// 2. **Type prefix uses PEEK-ONLY** (NOT a full nesting join). For
///    `public class Outer { public class Inner { public func helper() } }`,
///    the key is `"Inner.helper()"`, not `"Outer.Inner.helper()"`. This matches
///    the regex's `typeStack.Peek().Name` strategy and the consumer's lookup
///    via `parentDecl.Name` (simple name).
///
/// 3. **Extension scope uses LAST-dot-component** (regex `LastIndexOf('.')`).
///    `extension SomeModule.AES` pushes `"AES"`. Members keyed as `"AES.foo()"`.
///
/// 4. **Module-level (free) members**:
///      * `internalMemberKeys`: regex requires `typeStack.Count > 0`, so module-
///        level internals are NOT emitted. Mirror by gating on a non-empty stack.
///      * `publicMemberNames`: free public members get a BARE key (no type
///        prefix). E.g. `public func tlMain()` → `"tlMain()"`.
///
/// 5. **Internal-set modifier-shape gates** mirror the regex patterns:
///      * Func: `InternalFuncRegex = internal\s+(?:final\s+)?(?:static\s+)?(?:(?:mutating|consuming|borrowing)\s+)?func`
///        — STRICT order. One ownership modifier (`mutating`/`consuming`/`borrowing`)
///        IS allowed in its slot after `static`, so `internal consuming func` (on a
///        `~Copyable` type) matches; other keywords between `internal` and `func` do
///        not. Note: `@usableFromInline internal` and `@inlinable internal` BOTH carry
///        the `internal` modifier; the attribute prefix is invisible to the modifier-list scan.
///      * Var/let: `InternalVarRegex = internal\s+(?:final\s+)?(?:var|let)` —
///        STRICT. `internal static var` does NOT match (no `static` allowed).
///        `internal(set)` (setter-only visibility) is NOT internal access.
///      * Init: `InternalInitRegex = internal\s+(?:convenience\s+)?init` —
///        STRICT. `internal required init` does NOT match.
///
/// 6. **Public-set modifier-shape gates** mirror the BROAD regex patterns:
///      * Func: `BroadPublicFuncRegex` allows
///        `{final, static, class, mutating, nonmutating, consuming, borrowing, override}`
///        between `public/open` and `func`, in any order/quantity.
///      * Var/let: `BroadPublicVarRegex` allows
///        `{final, static, class, lazy, weak, unowned}` plus setter visibility
///        (`internal(set)`/`private(set)`/`public(set)`) between access and
///        `var`/`let`, in any order.
///      * Init: `BroadPublicInitRegex` allows `{convenience, required, override}`.
///      * Subscript: `PublicSubscriptRegex` allows only `static` (STRICT order).
///
///    All gates are UNANCHORED at the access modifier — modifiers BEFORE access
///    (e.g., `final public func`) are tolerated because the regex doesn't see
///    them when it scans for `public/open`. After access, modifiers must satisfy
///    the per-kind allow-list above; e.g., `public nonisolated func` is rejected
///    by both the regex and the walker (parity).
///
/// 7. **Backticks stripped from var/let names**: `public var \`operator\`: Int`
///    keys as `KeywordTest.operator`.
///
/// 8. **Failable init**: `init?(...)` keys as `Type.init(labels)` (no `?`).
final class MemberCollectionWalker: SyntaxVisitor {
    private(set) var internalMemberKeys: [String] = []
    private(set) var publicMemberNames: [String] = []

    /// Single-element scope: the IMMEDIATELY enclosing type/extension name.
    /// We track the full stack but use only the top (peek) when building keys —
    /// matching the regex parser's `typeStack.Peek().Name` strategy.
    private var scopeStack: [String] = []

    /// Parallel stack tracking whether each visited type/extension actually pushed
    /// a scope. Mirrors `SwiftInterfaceContextTracker`'s gated `typeStack.Push` —
    /// the regex tracker only pushes when `TypeDeclRegex` (or `ExtensionDeclRegex`)
    /// matches AND `openBraces > 0` on the same source line. Type decls with extra
    /// modifiers (e.g. `public indirect enum`) or types whose body opens on a
    /// later line don't push, so their members must NOT be keyed by the type
    /// scope. Each `visitPost` pops only when its corresponding visit pushed.
    private var scopePushed: [Bool] = []

    private let converter: SourceLocationConverter

    init(converter: SourceLocationConverter) {
        self.converter = converter
        super.init(viewMode: .sourceAccurate)
    }

    static func parse(filePath: String, source: String) -> (
        internalMemberKeys: [String],
        publicMemberNames: [String]
    ) {
        let tree = Parser.parse(source: source)
        let converter = SourceLocationConverter(fileName: filePath, tree: tree)
        let walker = MemberCollectionWalker(converter: converter)
        walker.walk(tree)
        return (walker.internalMemberKeys, walker.publicMemberNames)
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

    // MARK: - Extension (last-dot strip; gated on same-line `{`)

    override func visit(_ node: ExtensionDeclSyntax) -> SyntaxVisitorContinueKind {
        // Regex `ExtensionDeclRegex` matches any line containing `extension <type>`,
        // captures only `[\w.]+` for the type, AND the tracker requires the same-line
        // `{` gate (`openBraces > 0`). Mirror all three constraints.
        guard typeOpensOnSameLine(keyword: node.extensionKeyword,
                                  leftBrace: node.memberBlock.leftBrace) else {
            scopePushed.append(false)
            return .visitChildren
        }
        let qualified = node.extendedType.trimmedDescription
        guard RegexShape.isWordOrDotOnly(qualified) else {
            scopePushed.append(false)
            return .visitChildren
        }
        let lastComponent: String
        if let lastDot = qualified.lastIndex(of: ".") {
            lastComponent = String(qualified[qualified.index(after: lastDot)...])
        } else {
            lastComponent = qualified
        }
        scopeStack.append(lastComponent)
        scopePushed.append(true)
        return .visitChildren
    }
    override func visitPost(_ node: ExtensionDeclSyntax) { leaveTypeScope() }

    private func enterTypeScope(name: String,
                                modifiers: DeclModifierListSyntax,
                                keyword: TokenSyntax,
                                leftBrace: TokenSyntax) -> SyntaxVisitorContinueKind {
        // `TypeDeclRegex` ends in bare `(\w+)` — names that fail Unicode word-class
        // (e.g. backtick-escaped `\`class\``) miss the regex capture, so SwiftSyntax
        // must also skip pushing them.
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

    /// Mirrors `TypeDeclRegex = (?:public|internal|open)\s+(?:final\s+)?(?:class|struct|enum|actor|protocol)\s+(\w+)`.
    /// Modifiers BEFORE access are tolerated (regex unanchored Match scan); after
    /// access only an optional `final` is allowed before the type keyword.
    /// `public indirect enum`, `public nonisolated class`, etc. fail this gate
    /// and so are NOT pushed onto the scope stack — matching the regex tracker.
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
    /// same source line. Mirrors `openBraces > 0` on the TypeDeclRegex match line
    /// in `SwiftInterfaceContextTracker`.
    private func typeOpensOnSameLine(keyword: TokenSyntax, leftBrace: TokenSyntax) -> Bool {
        let kwLine = converter.location(for: keyword.positionAfterSkippingLeadingTrivia).line
        let braceLine = converter.location(for: leftBrace.positionAfterSkippingLeadingTrivia).line
        return kwLine == braceLine
    }

    // MARK: - Members
    //
    // PER-DECL-KIND MODIFIER-SHAPE GATES:
    //
    // The regex producer only emits when the line matches a SHAPE-specific regex,
    // not a plain "has internal" / "has public" check. Mirror those shapes here so
    // SwiftSyntax does NOT emit broader keys than the regex on lines like
    //   `internal nonisolated func ...`  (regex InternalFuncRegex disallows nonisolated;
    //                                      one ownership modifier mutating/consuming/borrowing IS allowed)
    //   `internal static var ...`        (regex InternalVarRegex disallows static)
    //   `public required init(...)`      (BroadPublicInitRegex allows required — OK)
    //   `public nonisolated func ...`    (BroadPublicFuncRegex disallows nonisolated)
    //
    // Internal-set gates mirror `InternalFuncRegex` / `InternalVarRegex` /
    // `InternalInitRegex` (STRICT order). Public-set gates mirror
    // `BroadPublicFuncRegex` / `BroadPublicVarRegex` / `BroadPublicInitRegex` /
    // `PublicSubscriptRegex` (a fixed set of allowed-after modifiers in any order).
    // All gates are unanchored at the access modifier — modifiers BEFORE access
    // (e.g., `final public class`, `nonisolated public func` after the regex
    // preprocessing strips leading `nonisolated`) are tolerated; modifiers AFTER
    // access must satisfy the per-kind allow-list.

    override func visit(_ node: FunctionDeclSyntax) -> SyntaxVisitorContinueKind {
        // Operator functions (`public static func == (...)`) have a `name` token of
        // kind `binaryOperator` / `prefixOperator` / `postfixOperator` whose `.text`
        // is the symbol itself. The regex producer's `(\w+)` capture skips operators
        // because `\w` does not match `=`, `+`, `<`, etc. Mirror by skipping
        // non-identifier names entirely.
        guard isIdentifierName(node.name.text) else { return .skipChildren }
        let printed = buildFuncPrintedName(name: node.name.text, params: node.signature.parameterClause.parameters)
        let isInternal = matchesInternalFuncShape(node.modifiers)
        let isPublic = matchesBroadPublicFuncShape(node.modifiers)
        emitForMember(printedName: printed, isInternal: isInternal, isPublicOrOpen: isPublic, allowInternal: true, allowFreePublic: true)
        return .skipChildren
    }

    override func visit(_ node: InitializerDeclSyntax) -> SyntaxVisitorContinueKind {
        // Failable `init?` — key uses bare `init`.
        let printed = buildFuncPrintedName(name: "init", params: node.signature.parameterClause.parameters)
        let isInternal = matchesInternalInitShape(node.modifiers)
        let isPublic = matchesBroadPublicInitShape(node.modifiers)
        // Module-level init is invalid Swift; emit only when inside a type.
        emitForMember(printedName: printed, isInternal: isInternal, isPublicOrOpen: isPublic, allowInternal: true, allowFreePublic: false)
        return .skipChildren
    }

    override func visit(_ node: SubscriptDeclSyntax) -> SyntaxVisitorContinueKind {
        // Subscripts contribute to publicMemberNames ONLY (not internal). Module-level
        // subscripts are skipped (the regex's `if (typeStack.Count == 0) continue` for
        // subscript collection).
        guard !scopeStack.isEmpty else { return .skipChildren }
        let labels = subscriptLabelList(params: node.parameterClause.parameters)
        let printed = "subscript(\(labels.map { "\($0):" }.joined()))"
        let isPublic = matchesPublicSubscriptShape(node.modifiers)
        emitForMember(printedName: printed, isInternal: false, isPublicOrOpen: isPublic, allowInternal: false, allowFreePublic: false)
        return .skipChildren
    }

    override func visit(_ node: VariableDeclSyntax) -> SyntaxVisitorContinueKind {
        // var/let — one entry per binding. The regex only matches the FIRST identifier
        // on the line, but real swiftinterface output emits one binding per `var`/`let`.
        // For parity safety, emit per-binding; multi-binding `var a, b: Int` is
        // never produced by swiftc.
        let isInternal = matchesInternalVarShape(node.modifiers)
        let isPublic = matchesBroadPublicVarShape(node.modifiers)
        // Early-out: neither set will contribute, no need to walk bindings.
        if !isInternal && !isPublic { return .skipChildren }
        for binding in node.bindings {
            guard let pattern = binding.pattern.as(IdentifierPatternSyntax.self) else { continue }
            let raw = pattern.identifier.text
            let isBackticked = raw.count >= 2 && raw.hasPrefix("`") && raw.hasSuffix("`")
            let stripped = isBackticked ? String(raw.dropFirst().dropLast()) : raw
            // Regex parity: `BroadPublicVarRegex` accepts backticks (`\`?(\w+)\`?`)
            // and captures the inner word. `InternalVarRegex` is bare `(\w+)` with
            // no backtick handling — `internal var \`class\`: Int` would NOT match
            // because `\w` doesn't match the leading backtick character. Suppress
            // the internal emission for backtick-escaped names to mirror that gap.
            let internalPath = isInternal && !isBackticked
            emitForMember(printedName: stripped, isInternal: internalPath, isPublicOrOpen: isPublic, allowInternal: true, allowFreePublic: true)
        }
        return .skipChildren
    }

    // MARK: - Helpers

    private func emitForMember(printedName: String, isInternal: Bool, isPublicOrOpen: Bool, allowInternal: Bool, allowFreePublic: Bool) {
        let inTypeScope = !scopeStack.isEmpty
        let typePrefix = scopeStack.last  // peek-only

        // Internal set: only emit when (a) inside a type scope, (b) member is internal,
        // (c) decl kind allows internal collection.
        if allowInternal && isInternal && inTypeScope, let prefix = typePrefix {
            internalMemberKeys.append("\(prefix).\(printedName)")
        }

        // Public set: emit when public/open. Inside type → prefixed; module-level →
        // bare (only if allowFreePublic).
        if isPublicOrOpen {
            if let prefix = typePrefix {
                publicMemberNames.append("\(prefix).\(printedName)")
            } else if allowFreePublic {
                publicMemberNames.append(printedName)
            }
        }
    }

    private func buildFuncPrintedName(name: String, params: FunctionParameterListSyntax) -> String {
        if params.isEmpty { return "\(name)()" }
        var labels: [String] = []
        for p in params { labels.append(p.firstName.text) }
        return "\(name)(\(labels.map { "\($0):" }.joined()))"
    }

    private func subscriptLabelList(params: FunctionParameterListSyntax) -> [String] {
        var labels: [String] = []
        for p in params {
            let first = p.firstName.text
            if p.secondName != nil && first != "_" {
                labels.append(first)
            } else {
                labels.append("_")
            }
        }
        return labels
    }

    // MARK: - Modifier-shape matchers (regex parity)

    /// Iterator helper: consume modifiers until we find one whose `name.text` is in
    /// `accessTexts` AND whose `detail` is nil. Returns `true` and leaves the iterator
    /// positioned just after the access modifier; `false` if no matching access
    /// modifier exists in the list. Mirrors the regex's unanchored search for the
    /// access keyword (modifiers before the access are tolerated).
    private func advanceToAccess(_ iter: inout DeclModifierListSyntax.Iterator, _ accessTexts: [String]) -> Bool {
        while let mod = iter.next() {
            if accessTexts.contains(mod.name.text) && mod.detail == nil {
                return true
            }
        }
        return false
    }

    /// `InternalFuncRegex` shape: `internal\s+(?:final\s+)?(?:static\s+)?(?:(?:mutating|consuming|borrowing)\s+)?func`.
    /// STRICT order: `final` then `static` then one of {mutating, consuming, borrowing}. `class`/etc.
    /// are not allowed. The ownership modifiers appear on `~Copyable` instance methods exposed as
    /// false-public via `@usableFromInline internal` / `@inlinable internal`.
    private func matchesInternalFuncShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["internal"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "final", mod.detail == nil {
            current = iter.next()
        }
        if let mod = current, mod.name.text == "static", mod.detail == nil {
            current = iter.next()
        }
        if let mod = current, ["mutating", "consuming", "borrowing"].contains(mod.name.text), mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// `InternalVarRegex` shape: `internal\s+(?:final\s+)?(?:var|let)`.
    /// STRICT — disallows `static`, setter visibility, etc.
    private func matchesInternalVarShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["internal"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "final", mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// `InternalInitRegex` shape: `internal\s+(?:convenience\s+)?init`.
    /// STRICT — disallows `required`/`override`.
    private func matchesInternalInitShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["internal"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "convenience", mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// `BroadPublicFuncRegex` shape: `(public|open)\s+(?:(final|static|class|mutating|nonmutating|consuming|borrowing|override)\s+)*func`.
    /// SET-based: any of {final, static, class, mutating, nonmutating, consuming, borrowing, override} in any order/quantity.
    /// Disallows `nonisolated`, `dynamic`, `weak`, `lazy`, `convenience`, etc.
    /// `consuming`/`borrowing` are the ownership modifiers on `~Copyable` instance methods
    /// (`public consuming func consume()`); omitting them mis-flags those public methods as
    /// internal via negative-space detection, dropping their `@_cdecl` wrapper.
    private func matchesBroadPublicFuncShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["public", "open"]) else { return false }
        let allowed: Set<String> = ["final", "static", "class", "mutating", "nonmutating", "consuming", "borrowing", "override"]
        while let mod = iter.next() {
            guard mod.detail == nil else { return false }
            if !allowed.contains(mod.name.text) { return false }
        }
        return true
    }

    /// `BroadPublicVarRegex` shape: `(public|open)\s+(?:(final|static|class|lazy|weak|unowned|(internal|private|public)\(set\))\s+)*(var|let)`.
    /// SET-based: any of {final, static, class, lazy, weak, unowned} OR setter
    /// visibility (`internal(set)`/`private(set)`/`public(set)`) in any order.
    private func matchesBroadPublicVarShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["public", "open"]) else { return false }
        let allowedPlain: Set<String> = ["final", "static", "class", "lazy", "weak", "unowned"]
        let allowedSetter: Set<String> = ["internal", "private", "public"]
        while let mod = iter.next() {
            let text = mod.name.text
            if let detail = mod.detail {
                // Setter-visibility modifier like `private(set)`.
                guard allowedSetter.contains(text), detail.detail.text == "set" else { return false }
                continue
            }
            if !allowedPlain.contains(text) { return false }
        }
        return true
    }

    /// `BroadPublicInitRegex` shape: `(public|open)\s+(?:(convenience|required|override)\s+)*init`.
    /// SET-based: any of {convenience, required, override} in any order.
    private func matchesBroadPublicInitShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["public", "open"]) else { return false }
        let allowed: Set<String> = ["convenience", "required", "override"]
        while let mod = iter.next() {
            guard mod.detail == nil else { return false }
            if !allowed.contains(mod.name.text) { return false }
        }
        return true
    }

    /// `PublicSubscriptRegex` shape: `(public|open)\s+(?:static\s+)?subscript`.
    /// STRICT order — only `static` is allowed between access and `subscript`.
    private func matchesPublicSubscriptShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["public", "open"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "static", mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// True iff `s` matches .NET's default `\w+` semantics — Unicode word characters
    /// (general categories `L`, `Mn`, `Nd`, `Pc`, `Lm`). Used to skip operator
    /// functions whose `name.text` is the symbol literal (`==`, `+`, `<`, etc.) —
    /// the regex producer's `(\w+)` capture rejects those. Names like `GreetCafé`
    /// (Latin letter with diacritic) DO match `\w+` and so MUST pass this gate.
    ///
    /// Backticks are NOT stripped: the regex `BroadPublicFuncRegex`/`InternalFuncRegex`/
    /// `AnyFuncRegex` capture is bare `(\w+)` with no `\`?` wrapper, so a literal
    /// `func \`class\`()` (where SwiftSyntax keeps the backticks in `name.text`) would
    /// not match the regex either. Backtick is not a word character, so the natural
    /// failure of this scan covers backtick-escaped function names.
    private func isIdentifierName(_ s: String) -> Bool {
        if s.isEmpty { return false }
        for scalar in s.unicodeScalars {
            switch scalar.properties.generalCategory {
            case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter,
                 .modifierLetter, .otherLetter,
                 .nonspacingMark, .decimalNumber, .connectorPunctuation:
                continue
            default:
                return false
            }
        }
        return true
    }

    private func stripBackticks(_ s: String) -> String {
        if s.hasPrefix("`") && s.hasSuffix("`") && s.count >= 2 {
            return String(s.dropFirst().dropLast())
        }
        return s
    }
}
