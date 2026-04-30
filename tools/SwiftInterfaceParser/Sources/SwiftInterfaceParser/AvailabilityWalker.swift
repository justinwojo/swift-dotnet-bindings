// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks the syntax tree of a .swiftinterface and surfaces `availabilityAnnotations`
/// + `availabilityAnnotationPositions`.
///
/// PARITY CONTRACT WITH `SwiftInterfaceAccessParser.GetAvailabilityAnnotations`:
///
/// 1. **Three sources combined per decl** (see `CollectAvailabilityAnnotations`,
///    SwiftInterfaceAccessParser.cs:3505). For each emitting decl, we concatenate
///    in order:
///      a) Pending annotation lines (preceding `@available(...)` lines on
///         standalone attribute lines). SwiftSyntax already groups all attributes
///         with the decl, so this is implicit in the visitor model.
///      b) Extension-scope inherited annotations (an `@available` on the `extension`
///         declaration applies to every member inside). Tracked here via
///         `extensionScopeAnnotations` stack.
///      c) Inline `@available` clauses on the decl itself. Same pool as (a) for
///         SwiftSyntax.
///
/// 2. **Decl key shape** (matches `tracker.QualifiedTypePath` /
///    `tracker.BuildMemberKey`):
///      - Type-level: full nested type path (e.g., `"Outer.Inner"`). For extensions,
///        the leading module dot-component is stripped (`extension Mod.X` → key prefix
///        is `"X"`).
///      - Member: `"<typePath>.<printedName>"`. For free functions: bare `printedName`.
///      - Grouped enum cases (`case foo, bar(Int)`): one entry per case name.
///
/// 3. **Member-printedName extraction policy**: only public/open members + any
///    enum case + any subscript with `public|open` qualify
///    (`SwiftInterfaceContextTracker.ExtractMemberPrintedName`, lines 231-253). Bare
///    protocol requirements are NOT keyed by the regex parser — we mirror that. A
///    SEMANTIC CLIFF: SwiftSyntax could see and key bare protocol-requirement
///    members; the regex misses them. Documented in m2-semantic-cliffs.md.
///
/// 4. **Position calculation**: 1-based line/column landing on the decl keyword
///    after skipping inline `@xxx(...)` annotations, mirroring
///    `SkipLeadingAnnotations` (line 3683). For SwiftSyntax we use the position of
///    the FIRST decl modifier or — when no modifiers — the decl keyword token.
///    Multi-line member signatures: the regex points at the line where the multi-
///    line completes (line 3373), not the opening line. SwiftSyntax naturally has
///    the opening line — we deliberately apply a one-line-shift for byte parity.
///
///    SEMANTIC CLIFF: the regex's last-line behavior is documented as imprecision
///    that "tightens when SwiftSyntax replaces the regex parser post-1.0". Since
///    M2 S2 must hold byte-equal parity, we mirror the regex (last-line). M2 S4
///    flips this to the correct opening-line behavior.
///
/// 5. **First-position-wins**: `if (!positions.ContainsKey(key))` at line 3416.
///    Repeated decls with the same key keep the first observed line.
///
/// 6. **Annotations append on duplicate keys** (`AddAnnotations` at line 3773).
///    Stacked declarations of the same key (e.g., extension members) accumulate
///    annotations; we mirror.
///
/// 7. **Three @available clause forms** parsed inside `parseAvailableClause`
///    (lines 3571-3675):
///      a) Per-platform lifecycle: `@available(iOS, introduced: 10, deprecated: 12)`.
///      b) Unconditional: `@available(*, deprecated, message: "...")`.
///      c) Shorthand multi-platform: `@available(iOS 16.0, macOS 13, *)`.
///    Skip `swift`, `SwiftStdlib`, `_PackageDescription` first-tokens entirely.
///    Only known platforms (`IsKnownPlatform`, line 3741) emit; unknown shorthand
///    platforms drop silently (regex parity quirk).
///
/// 8. **Platform name normalization**: ApplicationExtension variants normalize to
///    their base platform (`iOSApplicationExtension` → `iOS`).
final class AvailabilityWalker: SyntaxVisitor {
    let filePath: String
    let source: String
    let sourceLines: [String]
    let converter: SourceLocationConverter

    private(set) var availabilityAnnotations: [String: [AvailabilityAnnotationJson]] = [:]
    private(set) var availabilityAnnotationPositions: [String: SourcePositionJson] = [:]

    /// Scope tracking. `name` is the simple type name (or first-dot-stripped path
    /// for extensions). `isExtension` matches the regex tracker's flag.
    private struct Scope {
        let name: String
        let isExtension: Bool
        /// Annotations declared on the extension decl itself, applied to every
        /// member inside. Only populated when `isExtension == true`. Mirrors the
        /// regex tracker's pending-OR-inline exclusivity (line 145-157).
        let extensionScopeAttributes: [AttributeSyntax]
    }
    private var scopeStack: [Scope] = []

    // Modifier-shape gates mirroring the regex's PublicFuncRegex / PublicInitRegex /
    // PublicVarRegex / subscript regex. ORDER matters in the regex (anchored
    // sequence), so we step through `node.modifiers` in source order and reject any
    // modifier outside the regex's pattern OR appearing in the wrong slot. ANY
    // modifier mismatch causes the regex to skip the line entirely (`override`,
    // `weak`, `required`, `nonisolated`, `dynamic`, `lazy`, `unowned`,
    // `nonmutating`, `indirect`, `final`-after-`mutating`, etc.). For byte-equal
    // parity we mirror the rejection. SEMANTIC CLIFF: SwiftSyntax could see all of
    // these — see m2-semantic-cliffs.md.

    init(filePath: String, source: String) {
        self.filePath = filePath
        self.source = source
        // Split once for O(1) random-access during multi-line position computation.
        // Mirror File.ReadAllLines: split on \n, then strip trailing \r so CRLF is handled.
        self.sourceLines = source
            .split(omittingEmptySubsequences: false, whereSeparator: { $0 == "\n" })
            .map { line -> String in
                let s = String(line)
                if s.hasSuffix("\r") { return String(s.dropLast()) }
                return s
            }
        self.converter = SourceLocationConverter(fileName: filePath, tree: Parser.parse(source: source))
        super.init(viewMode: .sourceAccurate)
    }

    static func parse(filePath: String, source: String) -> AvailabilityResult {
        let tree = Parser.parse(source: source)
        let walker = AvailabilityWalker(filePath: filePath, source: source)
        walker.walk(tree)
        return AvailabilityResult(
            availabilityAnnotations: walker.availabilityAnnotations,
            availabilityAnnotationPositions: walker.availabilityAnnotationPositions
        )
    }

    private var qualifiedTypePath: String {
        scopeStack.map { $0.name }.joined(separator: ".")
    }

    /// Build member key — `"<qualifiedTypePath>.<printedName>"` or bare `printedName`
    /// when at module scope.
    private func memberKey(printedName: String) -> String {
        let path = qualifiedTypePath
        return path.isEmpty ? printedName : "\(path).\(printedName)"
    }

    /// Concatenated availability annotations from extension scope + decl-local
    /// attributes. Returns parsed JSON-friendly records.
    private func collectAnnotations(declAttrs: AttributeListSyntax) -> [AvailabilityAnnotationJson] {
        var out: [AvailabilityAnnotationJson] = []

        // Extension-scope annotations (from any enclosing `extension`).
        for scope in scopeStack where scope.isExtension {
            for attr in scope.extensionScopeAttributes {
                if attr.attributeName.trimmedDescription == "available" {
                    out.append(contentsOf: parseAvailableAttribute(attr))
                }
            }
        }

        // Inline / decl-local annotations.
        for el in declAttrs {
            guard case .attribute(let attr) = el else { continue }
            if attr.attributeName.trimmedDescription == "available" {
                out.append(contentsOf: parseAvailableAttribute(attr))
            }
        }
        return out
    }

    /// Compute byte-equal position for a decl. Matches the regex parser's
    /// per-line semantics:
    ///   `column = leading + SkipLeadingAnnotations(trimmed) + 1`
    /// applied on the LINE WHERE THE TRACKER FIRES MemberLine. For single-line
    /// decls that's the only line. For multi-line member signatures the regex
    /// fires on the CLOSING line of the parens (line 3373 in the parser); the
    /// stored position is `(closingLine, leading + 1)` since the closing line
    /// has no `@xxx` annotations to skip.
    ///
    /// IMPLEMENTATION:
    /// - `anchor` is the first non-attribute token of the decl (modifier or
    ///   keyword) — gives us the opening-line column post-attribute-skip.
    /// - `endPos` is the line of the decl's last token before trailing trivia.
    /// - If single-line: emit (anchor.line, anchor.column).
    /// - If multi-line: emit (endLine, leadingWhitespace(endLine)+1) — exactly
    ///   what the regex emits on the closing line.
    ///
    /// SEMANTIC CLIFF: see m2-semantic-cliffs.md #4 — multi-line opening-line
    /// would be more correct; we mirror the regex's last-line behavior for
    /// byte-equal parity.
    private func declarationPosition(anchor: TokenSyntax, declEnd: AbsolutePosition) -> SourcePositionJson? {
        let anchorLoc = converter.location(for: anchor.positionAfterSkippingLeadingTrivia)
        let endLoc = converter.location(for: declEnd)
        if anchorLoc.line == endLoc.line {
            return SourcePositionJson(
                filePath: filePath,
                line: anchorLoc.line,
                column: anchorLoc.column
            )
        }
        // Multi-line: emit at the closing line, column = first non-whitespace + 1.
        let column = leadingWhitespaceColumn(forLine: endLoc.line)
        return SourcePositionJson(
            filePath: filePath,
            line: endLoc.line,
            column: column
        )
    }

    /// Return the 1-based column of the first non-whitespace character on the
    /// given 1-based source line. Returns 1 if the line is missing or blank.
    /// Mirrors `leading + 1` from the regex parser where `leading = line.Length -
    /// line.TrimStart().Length`.
    private func leadingWhitespaceColumn(forLine line1Based: Int) -> Int {
        let idx = line1Based - 1
        guard idx >= 0 && idx < sourceLines.count else { return 1 }
        let lineText = sourceLines[idx]
        var col = 1
        for ch in lineText {
            if ch == " " || ch == "\t" {
                col += 1
            } else {
                return col
            }
        }
        return col
    }

    /// Returns the first declaration-keyword/modifier token we'd want to point a
    /// position at. Falls back to the decl's first token after trivia.
    private func declAnchorToken(modifiers: DeclModifierListSyntax, fallback: TokenSyntax) -> TokenSyntax {
        // Modifiers in source order: pick the first one (e.g., `public`/`internal`/
        // `open`/`final`/...). If none, the keyword (e.g., `class`/`struct`/`func`)
        // is the anchor. Matches `SkipLeadingAnnotations` which advances past `@xxx`
        // attributes only.
        for modifier in modifiers {
            return modifier.name
        }
        return fallback
    }

    private func emitDeclAnnotations(key: String, decl: AttributeListSyntax, anchor: TokenSyntax, declEnd: AbsolutePosition) {
        let annotations = collectAnnotations(declAttrs: decl)
        guard !annotations.isEmpty else { return }
        if availabilityAnnotations[key] != nil {
            availabilityAnnotations[key]!.append(contentsOf: annotations)
        } else {
            availabilityAnnotations[key] = annotations
        }
        if availabilityAnnotationPositions[key] == nil,
           let pos = declarationPosition(anchor: anchor, declEnd: declEnd) {
            availabilityAnnotationPositions[key] = pos
        }
    }

    /// PublicFuncRegex shape: `(public|open) final? (static|class)? mutating? func`.
    /// CRITICAL PARITY DETAIL: the regex is UNANCHORED — `Match` scans the line for
    /// the access keyword and checks the ALLOWED-AFTER sequence up to `func`. Any
    /// modifiers BEFORE the access keyword (`@MainActor`, `nonisolated`, `final`,
    /// `weak`, `dynamic`, attributes that survive into the modifier list, …) are
    /// invisible to the regex because it didn't see them at the access keyword.
    /// Once the access keyword is found, modifiers AFTER it must follow the strict
    /// pattern — anything outside that sequence (e.g., `nonisolated` between
    /// `public` and `func`) breaks the match. We mirror by ignoring everything up
    /// to and including the first `public`/`open` modifier and validating the tail.
    private func matchesPublicFuncShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["public", "open"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "final", mod.detail == nil {
            current = iter.next()
        }
        if let mod = current, (mod.name.text == "static" || mod.name.text == "class"), mod.detail == nil {
            current = iter.next()
        }
        if let mod = current, mod.name.text == "mutating", mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// PublicInitRegex shape: `(public|open) convenience? init`.
    /// Same unanchored-match parity rule as `matchesPublicFuncShape`.
    private func matchesPublicInitShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["public", "open"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "convenience", mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// PublicVarRegex shape: `(public|open) ((private|internal|public)\(set\))?
    /// final? (static|class)? (var|let)`. Same unanchored-match parity rule.
    private func matchesPublicVarShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["public", "open"]) else { return false }
        var current = iter.next()
        // Optional setter-restriction: e.g., `private(set)`.
        if let mod = current,
           (mod.name.text == "private" || mod.name.text == "internal" || mod.name.text == "public"),
           let detail = mod.detail,
           detail.detail.text == "set" {
            current = iter.next()
        }
        if let mod = current, mod.name.text == "final", mod.detail == nil {
            current = iter.next()
        }
        if let mod = current, (mod.name.text == "static" || mod.name.text == "class"), mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// TypeDeclRegex shape: `(public|internal|open) final? (class|struct|enum|actor|protocol) Name`.
    /// Tracker also requires the line to contain `{` (line 125: `openBraces > 0`),
    /// which we mirror by checking that the type-keyword and `{` are on the same
    /// source line. Mirrors the regex's same-line gating.
    ///
    /// Same unanchored-match parity rule as `matchesPublicFuncShape` — modifiers
    /// before access (`final public class TipGroup`, `@objc public class Foo`, …)
    /// are tolerated because the regex doesn't see them.
    private func matchesPublicTypeShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["public", "internal", "open"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "final", mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// Iterator helper: consume modifiers until we find one whose `name.text` is in
    /// `accessTexts` AND whose `detail` is nil. Returns `true` and leaves the iterator
    /// positioned just after the access modifier; `false` if no matching access
    /// modifier exists in the list. Mirrors the regex's unanchored search for the
    /// access keyword in the line.
    private func advanceToAccess(_ iter: inout DeclModifierListSyntax.Iterator, _ accessTexts: [String]) -> Bool {
        while let mod = iter.next() {
            if accessTexts.contains(mod.name.text) && mod.detail == nil {
                return true
            }
        }
        return false
    }

    /// True iff the type's keyword and its body's opening `{` are on the same
    /// source line (regex requires this — `openBraces > 0` on the same trimmedLine
    /// as the TypeDeclRegex match at line 125 of the tracker).
    private func typeOpensOnSameLine(keyword: TokenSyntax, leftBrace: TokenSyntax) -> Bool {
        let kwLine = converter.location(for: keyword.positionAfterSkippingLeadingTrivia).line
        let braceLine = converter.location(for: leftBrace.positionAfterSkippingLeadingTrivia).line
        return kwLine == braceLine
    }

    /// Subscript regex shape: `(public|open) static? subscript`.
    /// Same unanchored-match parity rule as `matchesPublicFuncShape`.
    private func matchesPublicSubscriptShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["public", "open"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "static", mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// Returns the AbsolutePosition that the regex parser would treat as "the line
    /// where MemberLine fires" — the position of the last token of the decl
    /// signature (excluding any body / accessor block / trailing trivia). Mirrors
    /// the regex's pumped-line completion semantics.
    private func declSignatureEnd(_ node: SyntaxProtocol) -> AbsolutePosition {
        return node.endPositionBeforeTrailingTrivia
    }

    /// Signature end for VariableDeclSyntax — stops BEFORE the accessor block (`{
    /// get }`). The regex tracker tracks paren balance only (HasUnmatchedOpenParen),
    /// so a property's accessor block doesn't trigger a multi-line continuation;
    /// MemberLine fires on the line containing `var <name>: <Type>`. We mirror
    /// by ending the signature at whichever-comes-last among:
    /// `accessorBlock.position`, `initializer.endPos`, `typeAnnotation.endPos`,
    /// `pattern.endPos`.
    private func signatureEndForVar(_ node: VariableDeclSyntax) -> AbsolutePosition {
        guard let lastBinding = node.bindings.last else {
            return node.endPositionBeforeTrailingTrivia
        }
        if let accessor = lastBinding.accessorBlock {
            return accessor.position
        }
        if let initializer = lastBinding.initializer {
            return initializer.endPositionBeforeTrailingTrivia
        }
        if let typeAnnot = lastBinding.typeAnnotation {
            return typeAnnot.endPositionBeforeTrailingTrivia
        }
        return lastBinding.pattern.endPositionBeforeTrailingTrivia
    }

    /// Signature end for SubscriptDeclSyntax — stops BEFORE the accessor block.
    /// Like properties, subscripts have a paren-balanced signature and an
    /// optional `{ get set }` block; the regex's MemberLine fires on the line
    /// where the param-clause parens close.
    private func signatureEndForSubscript(_ node: SubscriptDeclSyntax) -> AbsolutePosition {
        if let accessor = node.accessorBlock {
            return accessor.position
        }
        if let whereClause = node.genericWhereClause {
            return whereClause.endPositionBeforeTrailingTrivia
        }
        return node.returnClause.endPositionBeforeTrailingTrivia
    }

    // MARK: - Type declarations

    /// Tracks how many scopes were actually pushed for a type decl (0 or 1).
    /// `visitPost` consults this to decide whether to pop. Stored as a stack so
    /// nested types each manage their own push state. The inner Bool is `true`
    /// when scope was pushed (so should be popped); `false` when the type failed
    /// the regex-parity gate and no scope was pushed.
    private var scopePushed: [Bool] = []

    /// Common type-decl visit logic. Push scope and emit only when the type
    /// passes the regex shape + same-line `{` gate. Member keying inside the
    /// body still works because we push the scope in the gated path; for the
    /// failing path, members under the body get keyed at module scope (matching
    /// the regex tracker's behavior of never pushing the type onto its stack).
    private func visitTypeDecl(name: String,
                                modifiers: DeclModifierListSyntax,
                                attributes: AttributeListSyntax,
                                keyword: TokenSyntax,
                                leftBrace: TokenSyntax) -> SyntaxVisitorContinueKind {
        // `PublicTypeDeclRegex` ends in bare `(\w+)` — backtick-escaped names
        // (`public struct \`class\``) miss the regex capture, so SwiftSyntax must
        // also skip pushing them.
        if matchesPublicTypeShape(modifiers),
           RegexShape.isWordIdentifier(name),
           typeOpensOnSameLine(keyword: keyword, leftBrace: leftBrace) {
            let qualified = pushTypeScope(name: name)
            scopePushed.append(true)
            let anchor = declAnchorToken(modifiers: modifiers, fallback: keyword)
            let declEnd = leftBrace.position
            emitDeclAnnotations(key: qualified, decl: attributes, anchor: anchor, declEnd: declEnd)
        } else {
            scopePushed.append(false)
        }
        return .visitChildren
    }

    private func popTypeDecl() {
        if let pushed = scopePushed.popLast(), pushed {
            _ = scopeStack.popLast()
        }
    }

    override func visit(_ node: ClassDeclSyntax) -> SyntaxVisitorContinueKind {
        return visitTypeDecl(
            name: node.name.text,
            modifiers: node.modifiers,
            attributes: node.attributes,
            keyword: node.classKeyword,
            leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: ClassDeclSyntax) { popTypeDecl() }

    override func visit(_ node: StructDeclSyntax) -> SyntaxVisitorContinueKind {
        return visitTypeDecl(
            name: node.name.text,
            modifiers: node.modifiers,
            attributes: node.attributes,
            keyword: node.structKeyword,
            leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: StructDeclSyntax) { popTypeDecl() }

    override func visit(_ node: EnumDeclSyntax) -> SyntaxVisitorContinueKind {
        return visitTypeDecl(
            name: node.name.text,
            modifiers: node.modifiers,
            attributes: node.attributes,
            keyword: node.enumKeyword,
            leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: EnumDeclSyntax) { popTypeDecl() }

    override func visit(_ node: ProtocolDeclSyntax) -> SyntaxVisitorContinueKind {
        return visitTypeDecl(
            name: node.name.text,
            modifiers: node.modifiers,
            attributes: node.attributes,
            keyword: node.protocolKeyword,
            leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: ProtocolDeclSyntax) { popTypeDecl() }

    override func visit(_ node: ActorDeclSyntax) -> SyntaxVisitorContinueKind {
        return visitTypeDecl(
            name: node.name.text,
            modifiers: node.modifiers,
            attributes: node.attributes,
            keyword: node.actorKeyword,
            leftBrace: node.memberBlock.leftBrace)
    }
    override func visitPost(_ node: ActorDeclSyntax) { popTypeDecl() }

    /// Stack matching scopePushed but for extension scopes — tracks whether
    /// `visit(ExtensionDeclSyntax)` actually pushed.
    private var extensionScopePushed: [Bool] = []

    override func visit(_ node: ExtensionDeclSyntax) -> SyntaxVisitorContinueKind {
        // Regex parity: tracker only fires ExtensionDeclaration when the
        // extension keyword and `{` are on the same source line (line 138:
        // openBraces > 0). Mirror by checking the same.
        if !typeOpensOnSameLine(keyword: node.extensionKeyword, leftBrace: node.memberBlock.leftBrace) {
            extensionScopePushed.append(false)
            return .visitChildren
        }
        let qualified = node.extendedType.trimmedDescription
        // `ExtensionDeclRegex` captures only `[\w.]+` for the extended type —
        // backtick-escaped or otherwise non-word/dot extension targets fail the
        // capture and never push.
        guard RegexShape.isWordOrDotOnly(qualified) else {
            extensionScopePushed.append(false)
            return .visitChildren
        }
        let stripped: String
        if let firstDot = qualified.firstIndex(of: ".") {
            stripped = String(qualified[qualified.index(after: firstDot)...])
        } else {
            stripped = qualified
        }
        // Mirror regex tracker's pending-OR-inline exclusivity (lines 145-157):
        // - "Pending" = attributes on lines BEFORE the `extension` keyword line.
        // - "Inline" = attributes on the SAME line as the `extension` keyword.
        // If pending exist, use only pending; otherwise use only inline.
        let extensionLine = converter.location(for: node.extensionKeyword.positionAfterSkippingLeadingTrivia).line
        var pending: [AttributeSyntax] = []
        var inline: [AttributeSyntax] = []
        for el in node.attributes {
            guard case .attribute(let attr) = el else { continue }
            let attrLine = converter.location(for: attr.atSign.positionAfterSkippingLeadingTrivia).line
            if attrLine < extensionLine {
                pending.append(attr)
            } else {
                inline.append(attr)
            }
        }
        let scopeAttrs: [AttributeSyntax] = !pending.isEmpty ? pending : inline
        scopeStack.append(Scope(name: stripped, isExtension: true, extensionScopeAttributes: scopeAttrs))
        extensionScopePushed.append(true)
        // Note: extension decls themselves do NOT emit a key into
        // `availabilityAnnotations` — the regex parser handles them via
        // `tracker.ConsumePendingAnnotations()` in the ExtensionDeclaration case
        // (line 3424-3426) without inserting into `result`. We mirror exactly.
        return .visitChildren
    }
    override func visitPost(_ node: ExtensionDeclSyntax) {
        if let pushed = extensionScopePushed.popLast(), pushed {
            _ = scopeStack.popLast()
        }
    }

    private func pushTypeScope(name: String) -> String {
        scopeStack.append(Scope(name: name, isExtension: false, extensionScopeAttributes: []))
        return qualifiedTypePath
    }

    // MARK: - Member declarations

    override func visit(_ node: FunctionDeclSyntax) -> SyntaxVisitorContinueKind {
        // Order-strict shape gate matching PublicFuncRegex.
        guard matchesPublicFuncShape(node.modifiers) else {
            return .skipChildren
        }
        // `PublicFuncRegex` ends in bare `(\w+)\s*(?:<[^>]*>\s*)?\(` — operator
        // funcs (`==`, `+`, …) and backtick-escaped names fail the `\w+` capture
        // and never key into the result.
        guard RegexShape.isWordIdentifier(node.name.text) else {
            return .skipChildren
        }
        let printed = buildPrintedName(funcName: node.name.text, params: node.signature.parameterClause)
        let key = memberKey(printedName: printed)
        let anchor = declAnchorToken(modifiers: node.modifiers, fallback: node.funcKeyword)
        // Decl signature ends at the closing paren of the param clause + optional
        // return type / where clause. Use the body's `{` if present, else the
        // signature's last token.
        let declEnd = node.body?.leftBrace.position ?? declSignatureEnd(node)
        emitDeclAnnotations(key: key, decl: node.attributes, anchor: anchor, declEnd: declEnd)
        return .skipChildren
    }

    override func visit(_ node: InitializerDeclSyntax) -> SyntaxVisitorContinueKind {
        guard matchesPublicInitShape(node.modifiers) else {
            return .skipChildren
        }
        let printed = buildPrintedName(funcName: "init", params: node.signature.parameterClause)
        let key = memberKey(printedName: printed)
        let anchor = declAnchorToken(modifiers: node.modifiers, fallback: node.initKeyword)
        let declEnd = node.body?.leftBrace.position ?? declSignatureEnd(node)
        emitDeclAnnotations(key: key, decl: node.attributes, anchor: anchor, declEnd: declEnd)
        return .skipChildren
    }

    override func visit(_ node: VariableDeclSyntax) -> SyntaxVisitorContinueKind {
        guard matchesPublicVarShape(node.modifiers) else {
            return .skipChildren
        }
        // First-binding identifier: regex `PublicVarRegex` captures only the first
        // `(\w+)` after `var`/`let`. Mirror.
        guard let firstBinding = node.bindings.first,
              let identifier = firstBinding.pattern.as(IdentifierPatternSyntax.self) else {
            return .skipChildren
        }
        // `PublicVarRegex` ends in bare `(\w+)` — backtick-escaped names
        // (`public var \`class\``) fail the capture and never key into the result.
        guard RegexShape.isWordIdentifier(identifier.identifier.text) else {
            return .skipChildren
        }
        let key = memberKey(printedName: identifier.identifier.text)
        let anchor = declAnchorToken(modifiers: node.modifiers, fallback: node.bindingSpecifier)
        let declEnd = signatureEndForVar(node)
        emitDeclAnnotations(key: key, decl: node.attributes, anchor: anchor, declEnd: declEnd)
        return .skipChildren
    }

    override func visit(_ node: SubscriptDeclSyntax) -> SyntaxVisitorContinueKind {
        guard matchesPublicSubscriptShape(node.modifiers) else {
            return .skipChildren
        }
        // SubscriptDecl's printedName format from `ExtractSubscriptPrintedName`:
        // `subscript(label1:label2:)` using external labels (`_` or external word).
        let printed = buildPrintedNameForSubscript(params: node.parameterClause)
        let key = memberKey(printedName: printed)
        let anchor = declAnchorToken(modifiers: node.modifiers, fallback: node.subscriptKeyword)
        let declEnd = signatureEndForSubscript(node)
        emitDeclAnnotations(key: key, decl: node.attributes, anchor: anchor, declEnd: declEnd)
        return .skipChildren
    }

    override func visit(_ node: EnumCaseDeclSyntax) -> SyntaxVisitorContinueKind {
        // Enum case: each case name keys a separate entry. Empty enum cases
        // (`case .none`) and grouped cases are both handled by iterating elements.
        let attrs = node.attributes
        // First annotate position from the case keyword token.
        let anchor = node.caseKeyword
        let declEnd = declSignatureEnd(node)
        for element in node.elements {
            // Strip backticks if present.
            var caseName = element.name.text
            if caseName.count >= 2 && caseName.hasPrefix("`") && caseName.hasSuffix("`") {
                caseName = String(caseName.dropFirst().dropLast())
            }
            let key = memberKey(printedName: caseName)
            emitDeclAnnotations(key: key, decl: attrs, anchor: anchor, declEnd: declEnd)
        }
        return .skipChildren
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

    private func buildPrintedNameForSubscript(params: FunctionParameterClauseSyntax) -> String {
        let paramList = params.parameters
        if paramList.isEmpty { return "subscript()" }
        var labels: [String] = []
        for param in paramList {
            // Subscript labels: external label or `_`.
            // Same first-name semantics as funcs.
            let firstName = param.firstName.text
            if firstName.isEmpty { continue }
            labels.append(firstName)
        }
        if labels.isEmpty { return "subscript()" }
        return "subscript(\(labels.map { "\($0):" }.joined()))"
    }

    // MARK: - @available parsing

    /// Parse a single `@available(...)` attribute into one or more
    /// AvailabilityAnnotationJson records. Mirrors `ParseAvailableClause`
    /// (SwiftInterfaceAccessParser.cs:3571).
    private func parseAvailableAttribute(_ attr: AttributeSyntax) -> [AvailabilityAnnotationJson] {
        // The argument list shape for `@available` varies (per-platform vs. shorthand
        // vs. unconditional). The most reliable approach for byte-equal parity with
        // the regex-based parser is to format the arguments back to source text and
        // reuse the same comma-splitting logic the regex applies. SwiftSyntax's
        // `arguments?.trimmedDescription` gives us the inside-the-parens content.
        guard let arguments = attr.arguments else { return [] }
        let clauseText = arguments.trimmedDescription
        return AvailabilityClauseParser.parse(clause: clauseText)
    }
}

/// Output bag for the availability cluster.
struct AvailabilityResult {
    let availabilityAnnotations: [String: [AvailabilityAnnotationJson]]
    let availabilityAnnotationPositions: [String: SourcePositionJson]
}

/// Pure-string parser for `@available` clause contents. Lifted out of the walker
/// so its byte-equal-with-regex semantics can be unit-tested independently.
///
/// Accepts the inside-the-parens text of `@available(...)` and returns one or more
/// parsed annotation records. Mirrors `SwiftInterfaceAccessParser.ParseAvailableClause`
/// (line 3571) including:
///  - `swift` / `SwiftStdlib` / `_PackageDescription` first-token skip.
///  - `IsKnownPlatform` allow-list (excludes `visionOSApplicationExtension`).
///  - Three-form dispatch (per-platform / unconditional / shorthand multi-platform).
///  - `NormalizePlatformName` (`iOSApplicationExtension` → `iOS`, etc.).
///  - `IsUnconditionallyDeprecated = isDeprecated && deprecated == null`.
enum AvailabilityClauseParser {
    static func parse(clause: String) -> [AvailabilityAnnotationJson] {
        let parts = splitClause(clause: clause)
        guard !parts.isEmpty else { return [] }

        let first = parts[0].trimmingCharacters(in: .whitespaces)

        // Skip compiler-level forms.
        if equalsCaseInsensitive(first, "swift") || startsWithCaseInsensitive(first, "swift ") ||
           equalsCaseInsensitive(first, "SwiftStdlib") || startsWithCaseInsensitive(first, "SwiftStdlib ") ||
           equalsCaseInsensitive(first, "_PackageDescription") || startsWithCaseInsensitive(first, "_PackageDescription ") {
            return []
        }

        var annotations: [AvailabilityAnnotationJson] = []

        // Per-platform lifecycle: `@available(iOS, introduced: 10, deprecated: 12)`.
        if parts.count >= 2 && isKnownPlatform(first) && !first.contains(" ") {
            var introduced: String? = nil
            var deprecated: String? = nil
            var obsoleted: String? = nil
            var message: String? = nil
            var renamed: String? = nil
            var isUnavailable = false
            var isDeprecated = false
            for part in parts.dropFirst() {
                let kv = part.trimmingCharacters(in: .whitespaces)
                if kv.hasPrefix("introduced:") {
                    introduced = String(kv.dropFirst("introduced:".count)).trimmingCharacters(in: .whitespaces)
                } else if kv.hasPrefix("deprecated:") {
                    deprecated = String(kv.dropFirst("deprecated:".count)).trimmingCharacters(in: .whitespaces)
                } else if kv.hasPrefix("obsoleted:") {
                    obsoleted = String(kv.dropFirst("obsoleted:".count)).trimmingCharacters(in: .whitespaces)
                } else if kv.hasPrefix("message:") {
                    let raw = String(kv.dropFirst("message:".count)).trimmingCharacters(in: .whitespaces)
                    message = extractQuotedString(raw)
                } else if kv.hasPrefix("renamed:") {
                    let raw = String(kv.dropFirst("renamed:".count)).trimmingCharacters(in: .whitespaces)
                    renamed = extractQuotedString(raw)
                } else if kv == "unavailable" {
                    isUnavailable = true
                } else if kv == "deprecated" {
                    isDeprecated = true
                }
            }
            annotations.append(AvailabilityAnnotationJson(
                platform: normalizePlatformName(first),
                introducedVersion: introduced,
                deprecatedVersion: deprecated,
                obsoletedVersion: obsoleted,
                isUnconditionallyDeprecated: isDeprecated && deprecated == nil,
                isUnconditionallyUnavailable: isUnavailable,
                message: message,
                renamed: renamed
            ))
            return annotations
        }

        // Unconditional: `@available(*, deprecated, ...)` / `@available(*, unavailable)`.
        if first == "*" && parts.count >= 2 {
            var message: String? = nil
            var renamed: String? = nil
            var isDeprecated = false
            var isUnavailable = false
            for part in parts.dropFirst() {
                let kv = part.trimmingCharacters(in: .whitespaces)
                if kv == "deprecated" {
                    isDeprecated = true
                } else if kv == "unavailable" {
                    isUnavailable = true
                } else if kv.hasPrefix("message:") {
                    let raw = String(kv.dropFirst("message:".count)).trimmingCharacters(in: .whitespaces)
                    message = extractQuotedString(raw)
                } else if kv.hasPrefix("renamed:") {
                    let raw = String(kv.dropFirst("renamed:".count)).trimmingCharacters(in: .whitespaces)
                    renamed = extractQuotedString(raw)
                }
            }
            annotations.append(AvailabilityAnnotationJson(
                platform: nil,
                introducedVersion: nil,
                deprecatedVersion: nil,
                obsoletedVersion: nil,
                isUnconditionallyDeprecated: isDeprecated,
                isUnconditionallyUnavailable: isUnavailable,
                message: message,
                renamed: renamed
            ))
            return annotations
        }

        // Shorthand multi-platform: `@available(iOS 16.0, macOS 13, *)`.
        for part in parts {
            let p = part.trimmingCharacters(in: .whitespaces)
            if p == "*" { continue }
            guard let spaceIdx = p.firstIndex(of: " ") else { continue }
            let platform = String(p[..<spaceIdx])
            let version = String(p[p.index(after: spaceIdx)...]).trimmingCharacters(in: .whitespaces)
            if isKnownPlatform(platform) {
                annotations.append(AvailabilityAnnotationJson(
                    platform: normalizePlatformName(platform),
                    introducedVersion: version,
                    deprecatedVersion: nil,
                    obsoletedVersion: nil,
                    isUnconditionallyDeprecated: false,
                    isUnconditionallyUnavailable: false,
                    message: nil,
                    renamed: nil
                ))
            }
        }
        return annotations
    }

    /// Split clause by commas, respecting double-quoted strings and nested parens.
    /// Mirrors `SplitAvailableClause` (line 3719).
    private static func splitClause(clause: String) -> [String] {
        var parts: [String] = []
        var start = clause.startIndex
        var inQuote = false
        var parenDepth = 0
        var i = clause.startIndex
        while i < clause.endIndex {
            let c = clause[i]
            if c == "\"" {
                inQuote.toggle()
            } else if !inQuote && c == "(" {
                parenDepth += 1
            } else if !inQuote && c == ")" {
                parenDepth -= 1
            } else if !inQuote && parenDepth == 0 && c == "," {
                parts.append(String(clause[start..<i]))
                start = clause.index(after: i)
            }
            i = clause.index(after: i)
        }
        parts.append(String(clause[start..<clause.endIndex]))
        return parts
    }

    private static func isKnownPlatform(_ name: String) -> Bool {
        switch name {
        case "iOS", "macOS", "tvOS", "watchOS", "visionOS",
             "macCatalyst", "iOSApplicationExtension", "macOSApplicationExtension",
             "tvOSApplicationExtension", "watchOSApplicationExtension":
            return true
        default:
            return false
        }
    }

    /// Mirrors `NormalizePlatformName` (line 3752). `visionOSApplicationExtension`
    /// is INTENTIONALLY not in `isKnownPlatform` (regex parity quirk — see
    /// m2-semantic-cliffs.md), so it never reaches this function.
    private static func normalizePlatformName(_ name: String) -> String {
        switch name {
        case "iOS", "iOSApplicationExtension": return "iOS"
        case "macOS", "macOSApplicationExtension": return "macOS"
        case "tvOS", "tvOSApplicationExtension": return "tvOS"
        case "watchOS", "watchOSApplicationExtension": return "watchOS"
        case "visionOS": return "visionOS"
        case "macCatalyst": return "macCatalyst"
        default: return name
        }
    }

    private static func extractQuotedString(_ value: String) -> String? {
        if value.count >= 2 && value.hasPrefix("\"") && value.hasSuffix("\"") {
            return String(value.dropFirst().dropLast())
        }
        return value
    }

    private static func equalsCaseInsensitive(_ a: String, _ b: String) -> Bool {
        return a.caseInsensitiveCompare(b) == .orderedSame
    }

    private static func startsWithCaseInsensitive(_ s: String, _ prefix: String) -> Bool {
        guard s.count >= prefix.count else { return false }
        let candidate = String(s.prefix(prefix.count))
        return candidate.caseInsensitiveCompare(prefix) == .orderedSame
    }
}
