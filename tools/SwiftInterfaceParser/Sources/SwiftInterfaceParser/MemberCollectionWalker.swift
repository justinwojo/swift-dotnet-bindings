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
/// EXTRACTION CONTRACT:
///
/// 1. **Decl kinds covered**:
///      * `internalMemberKeys`: `func`, `var`/`let`, `init`. NOT subscript, case,
///        or nested type decls.
///      * `publicMemberNames`: `func`, `var`/`let`, `init`, `subscript`. NOT
///        case, deinit, or operator decls.
///
/// 2. **Type prefix uses PEEK-ONLY** (NOT a full nesting join). For
///    `public class Outer { public class Inner { public func helper() } }`,
///    the key is `"Inner.helper()"`, not `"Outer.Inner.helper()"`. Matches the
///    ABI consumer's lookup via `parentDecl.Name` (simple name).
///
/// 3. **Extension scope uses LAST-dot-component** (`LastIndexOf('.')`).
///    `extension SomeModule.AES` pushes `"AES"`. Members keyed as `"AES.foo()"`.
///
/// 4. **Module-level (free) members**:
///      * `internalMemberKeys`: module-level internals are NOT emitted (only emit
///        when inside a type scope).
///      * `publicMemberNames`: free public members get a BARE key (no type
///        prefix). E.g. `public func tlMain()` → `"tlMain()"`.
///
/// 5. **Internal-set modifier-shape gates**:
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
/// 6. **Public set follows the access level**: a func, var/let, init or subscript
///    enters `publicMemberNames` iff its own access modifier is `public` or `open`
///    (`DeclAccess.isPublicOrOpen`). Every other modifier is irrelevant to whether
///    the member is public: `public __consuming func`, `public nonisolated func`,
///    `public dynamic var`, `open override subscript` and `public required init`
///    are all public. The set is negative space for the ABI parser, so a public
///    member missing from it is withdrawn as module-internal.
///
/// 7. **Backticks stripped from member names**: `public var \`operator\`: Int`
///    keys as `KeywordTest.operator`, and ``public func `repeat`(count:)`` keys as
///    `Type.repeat(count:)`, matching the ABI JSON's printed name.
///
/// 8. **Failable init**: `init?(...)` keys as `Type.init(labels)` (no `?`).
final class MemberCollectionWalker: SyntaxVisitor {
    private(set) var internalMemberKeys: [String] = []
    private(set) var publicMemberNames: [String] = []

    /// Single-element scope: the IMMEDIATELY enclosing type/extension name.
    /// We track the full stack but use only the top (peek) when building keys.
    private var scopeStack: [String] = []

    /// Parallel stack tracking whether each visited type/extension actually pushed
    /// a scope. A type decl pushes when it states an access level and its name is a
    /// word identifier once unescaped; an extension pushes when its extended type is
    /// a dotted word path. Either needs its body `{` on the keyword's line. Members of a frame that does not push are not keyed by
    /// it. Each `visitPost` pops only when its corresponding visit pushed.
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

    // MARK: - Type decls (pushed when the type states an access level)

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

    // MARK: - Extension (last-dot strip)

    override func visit(_ node: ExtensionDeclSyntax) -> SyntaxVisitorContinueKind {
        // Any extension qualifies (no modifier filter), but the extended type must
        // match `[\w.]+` (word characters and dots only) and the body `{` must be on
        // the keyword's line, as in every other walker.
        let qualified = node.extendedType.dottedDescription
        guard RegexShape.opensOnSameLine(keyword: node.extensionKeyword,
                                         leftBrace: node.memberBlock.leftBrace, converter: converter),
              RegexShape.isWordOrDotOnly(qualified) else {
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
        // Every type the interface declares with an access level scopes its members,
        // whatever other modifiers it carries (`public indirect enum`, `nonisolated
        // public final class`). A type left unscoped would key its members under the
        // enclosing type, where the ABI parser never looks for them.
        let unescaped = DeclAccess.unescape(name)
        if DeclAccess.hasAccess(modifiers, DeclAccess.interfaceAccessLevels),
           RegexShape.isWordIdentifier(unescaped),
           RegexShape.opensOnSameLine(keyword: keyword, leftBrace: leftBrace, converter: converter) {
            scopeStack.append(unescaped)
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

    // MARK: - Members
    //
    // The internal set keeps the strict per-kind shapes below (`InternalFuncRegex` /
    // `InternalVarRegex` / `InternalInitRegex`): it is a positive "false public"
    // marker, so a narrow shape can only miss an internal member, never withdraw a
    // public one. The public set follows the access level alone (see contract 6).

    override func visit(_ node: FunctionDeclSyntax) -> SyntaxVisitorContinueKind {
        // Operator functions (`public static func == (...)`) are never indexed: the
        // ABI parser skips operators before it consults this set. A keyword-named
        // function keys by its unescaped name.
        guard let name = DeclAccess.memberName(node.name) else { return .skipChildren }
        let printed = buildFuncPrintedName(name: name, params: node.signature.parameterClause.parameters)
        let isInternal = matchesInternalFuncShape(node.modifiers)
        let isPublic = DeclAccess.isPublicOrOpen(node.modifiers)
        emitForMember(printedName: printed, isInternal: isInternal, isPublicOrOpen: isPublic, allowInternal: true, allowFreePublic: true)
        return .skipChildren
    }

    override func visit(_ node: InitializerDeclSyntax) -> SyntaxVisitorContinueKind {
        // Failable `init?` — key uses bare `init`.
        let printed = buildFuncPrintedName(name: "init", params: node.signature.parameterClause.parameters)
        let isInternal = matchesInternalInitShape(node.modifiers)
        let isPublic = DeclAccess.isPublicOrOpen(node.modifiers)
        // Module-level init is invalid Swift; emit only when inside a type.
        emitForMember(printedName: printed, isInternal: isInternal, isPublicOrOpen: isPublic, allowInternal: true, allowFreePublic: false)
        return .skipChildren
    }

    override func visit(_ node: SubscriptDeclSyntax) -> SyntaxVisitorContinueKind {
        // Subscripts contribute to publicMemberNames ONLY (not internal). Module-level
        // subscripts are skipped (subscripts outside a type scope are not collected).
        guard !scopeStack.isEmpty else { return .skipChildren }
        let labels = subscriptLabelList(params: node.parameterClause.parameters)
        let printed = "subscript(\(labels.map { "\($0):" }.joined()))"
        let isPublic = DeclAccess.isPublicOrOpen(node.modifiers)
        emitForMember(printedName: printed, isInternal: false, isPublicOrOpen: isPublic, allowInternal: false, allowFreePublic: false)
        return .skipChildren
    }

    override func visit(_ node: VariableDeclSyntax) -> SyntaxVisitorContinueKind {
        // var/let — one entry per binding. Real swiftinterface output emits one binding
        // per `var`/`let`, so per-binding emission is correct; multi-binding
        // `var a, b: Int` is never produced by swiftc.
        let isInternal = matchesInternalVarShape(node.modifiers)
        let isPublic = DeclAccess.isPublicOrOpen(node.modifiers)
        // Early-out: neither set will contribute, no need to walk bindings.
        if !isInternal && !isPublic { return .skipChildren }
        for binding in node.bindings {
            guard let pattern = binding.pattern.as(IdentifierPatternSyntax.self) else { continue }
            let raw = pattern.identifier.text
            let isBackticked = raw.count >= 2 && raw.hasPrefix("`") && raw.hasSuffix("`")
            let stripped = isBackticked ? String(raw.dropFirst().dropLast()) : raw
            // `InternalVarRegex` is bare `(\w+)` with no backtick handling, so the
            // internal set keeps skipping backtick-escaped names (see the internal
            // shapes below); the public set keys them by the unescaped name.
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

    // MARK: - Modifier-shape matchers

    /// Iterator helper: consume modifiers until we find one whose `name.text` is in
    /// `accessTexts` AND whose `detail` is nil. Returns `true` and leaves the iterator
    /// positioned just after the access modifier; `false` if no matching access
    /// modifier exists. Modifiers before the access modifier are tolerated
    /// (unanchored scan).
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
}
