// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks the syntax tree of a .swiftinterface and surfaces `availabilityAnnotations`
/// + `availabilityAnnotationPositions`.
///
/// EXTRACTION CONTRACT:
///
/// 1. **Three sources combined per decl**. For each emitting decl, we concatenate
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
/// 2. **Decl key shape**:
///      - Type-level: full nested type path (e.g., `"Outer.Inner"`). For extensions,
///        the leading module dot-component is stripped (`extension Mod.X` → key prefix
///        is `"X"`).
///      - Member: `"<typePath>.<printedName>"`. For free functions: bare `printedName`.
///      - Grouped enum cases (`case foo, bar(Int)`): one entry per case name.
///
/// 3. **Member-printedName extraction policy**: OUTSIDE a protocol body only
///    public/open members + any enum case + any subscript with `public|open`
///    qualify (the modifier-shape gate). INSIDE a protocol body that shape gate
///    is SKIPPED, so bare protocol requirements — which carry no access modifier
///    in `.swiftinterface` text — ARE keyed with their `@available` floor. This is
///    the Family-F-2 protocol-scope lift (member visitors gate on `isInsideProtocol`);
///    pinned by `Availability_OnProtocolRequirementsWithoutAccessModifier_IsHarvested`.
///
/// 4. **Position calculation**: 1-based line/column landing on the decl keyword
///    after skipping inline `@xxx(...)` annotations. The position is the FIRST
///    decl modifier or — when no modifiers — the decl keyword token.
///    Multi-line member signatures: the walker uses the CLOSING line of the
///    signature (where the paren-balanced parameter list completes), not the
///    opening line. For single-line decls the two coincide.
///
///    SEMANTIC CLIFF: closing-line positioning is a known imprecision; a future
///    pass will flip this to opening-line behavior.
///
/// 5. **First-position-wins**: repeated decls with the same key keep the first
///    observed line.
///
/// 6. **Annotations append on duplicate keys**: stacked declarations of the same
///    key (e.g., extension members) accumulate annotations.
///
/// 7. **Three @available clause forms** parsed inside `parseAvailableClause`:
///      a) Per-platform lifecycle: `@available(iOS, introduced: 10, deprecated: 12)`.
///      b) Unconditional: `@available(*, deprecated, message: "...")`.
///      c) Shorthand multi-platform: `@available(iOS 16.0, macOS 13, *)`.
///    Skip `swift`, `SwiftStdlib`, `_PackageDescription` first-tokens entirely.
///    Only known platforms (`IsKnownPlatform`) emit; unknown shorthand
///    platforms drop silently (`visionOSApplicationExtension` is excluded).
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
    /// for extensions). `isExtension` is true for extension scopes.
    /// `isProtocol` is true when the scope is a `protocol` body — used to lift
    /// the modifier-shape gate on protocol requirements (Family-F-2: protocol
    /// methods have no `public`/`open` modifier and are otherwise skipped by the
    /// shape gate, dropping their `@available` clauses).
    private struct Scope {
        let name: String
        let isExtension: Bool
        let isProtocol: Bool
        /// Annotations declared on the extension decl itself, applied to every
        /// member inside. Only populated when `isExtension == true`. See
        /// pending-OR-inline exclusivity logic in `visit(_: ExtensionDeclSyntax)`.
        let extensionScopeAttributes: [AttributeSyntax]
    }
    private var scopeStack: [Scope] = []

    /// True when the innermost non-extension scope is a protocol body.
    private var isInsideProtocol: Bool {
        for scope in scopeStack.reversed() where !scope.isExtension {
            return scope.isProtocol
        }
        return false
    }

    /// Per-decl staging for overload disambiguation. The walker stages every
    /// member-keyed annotation here during the visit pass, then `finalize()`
    /// folds them into `availabilityAnnotations` with bare-vs-disamb keys decided
    /// by `memberSignatures` (the set of distinct param-sigs seen for each bare
    /// key). Single-overload bare keys keep the legacy bare-key storage so any
    /// .NET-side consumer that can't compute a sig still hits.
    private struct StagedAnnotation {
        let bareKey: String
        let paramSig: String
        let annotations: [AvailabilityAnnotationJson]
        let position: SourcePositionJson?
    }
    private var stagedMemberAnnotations: [StagedAnnotation] = []
    /// Set of distinct param signatures seen for each bare key. 2+ entries =
    /// overloaded → disambiguate.
    private var memberSignatures: [String: Set<String>] = [:]

    // Modifier-shape gates for PublicFuncRegex / PublicInitRegex / PublicVarRegex /
    // subscript shapes. ORDER matters — the walker steps through `node.modifiers` in
    // source order and rejects any modifier that falls outside the allowed sequence
    // or appears in the wrong slot. ANY modifier mismatch causes the decl to be
    // skipped entirely (`override`, `weak`, `required`, `nonisolated`, `dynamic`,
    // `lazy`, `unowned`, `nonmutating`, `indirect`, `final`-after-`mutating`, etc.).
    // SEMANTIC CLIFF: SwiftSyntax sees all of these modifiers, but the line-shape
    // patterns do not admit them.

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
        walker.finalize()
        return AvailabilityResult(
            availabilityAnnotations: walker.availabilityAnnotations,
            availabilityAnnotationPositions: walker.availabilityAnnotationPositions
        )
    }

    /// Folds staged member annotations into `availabilityAnnotations` after the
    /// visit pass completes:
    ///   - bare key has 1 distinct sig → store under bare key (legacy)
    ///   - bare key has 2+ distinct sigs → store each annotation list under
    ///     `{bareKey}|{sig}` and remove the bare-key position entry so an
    ///     unmatched .NET-side bare-key lookup misses cleanly rather than
    ///     misapplying another overload's annotations (Family-F-1 / F-4).
    private func finalize() {
        let positionsByBareKey = availabilityAnnotationPositions
        for staged in stagedMemberAnnotations {
            let distinctSigs = memberSignatures[staged.bareKey]?.count ?? 1
            let storageKey: String
            if distinctSigs <= 1 {
                storageKey = staged.bareKey
            } else {
                storageKey = staged.paramSig.isEmpty
                    ? staged.bareKey
                    : "\(staged.bareKey)|\(staged.paramSig)"
                if availabilityAnnotationPositions[storageKey] == nil,
                   let bp = positionsByBareKey[staged.bareKey] {
                    availabilityAnnotationPositions[storageKey] = bp
                }
            }
            if availabilityAnnotations[storageKey] != nil {
                availabilityAnnotations[storageKey]!.append(contentsOf: staged.annotations)
            } else {
                availabilityAnnotations[storageKey] = staged.annotations
            }
            if availabilityAnnotationPositions[storageKey] == nil,
               let pos = staged.position {
                availabilityAnnotationPositions[storageKey] = pos
            }
        }
        // Drop bare-key positions that won't have a matching annotation entry
        // (every overload was ambiguous and got disamb-keyed). Otherwise the
        // position dict has orphan entries pointing at the wrong overload.
        for (bareKey, sigs) in memberSignatures where sigs.count > 1 {
            if availabilityAnnotations[bareKey] == nil {
                availabilityAnnotationPositions.removeValue(forKey: bareKey)
            }
        }
    }

    /// Records that a member-line decl with the given bare key was seen. Called
    /// for every member-shape match (whether or not it has @available) so the
    /// overload-count check in `finalize()` covers decls without annotations
    /// (Family-F-1: the recommended overload has no annotations but its presence
    /// must trigger disambiguation).
    private func recordMemberSignature(bareKey: String, paramSig: String) {
        var set = memberSignatures[bareKey] ?? Set<String>()
        set.insert(paramSig)
        memberSignatures[bareKey] = set
    }

    /// Stage member annotations for the post-walk finalize pass. The bareKey
    /// here is what `memberKey(printedName:)` produced; finalize() decides
    /// whether to actually store under bare or disamb.
    private func stageMemberAnnotations(bareKey: String, paramSig: String,
                                        annotations: [AvailabilityAnnotationJson],
                                        anchor: TokenSyntax, declEnd: AbsolutePosition) {
        let position = declarationPosition(anchor: anchor, declEnd: declEnd)
        if availabilityAnnotationPositions[bareKey] == nil, let pos = position {
            availabilityAnnotationPositions[bareKey] = pos
        }
        if !annotations.isEmpty {
            stagedMemberAnnotations.append(StagedAnnotation(
                bareKey: bareKey,
                paramSig: paramSig,
                annotations: annotations,
                position: position
            ))
        }
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

    /// Compute the position for a decl. Column lands on the first non-attribute
    /// token (modifier or keyword), derived as:
    ///   `column = leading + SkipLeadingAnnotations(trimmed) + 1`
    ///
    /// IMPLEMENTATION:
    /// - `anchor` is the first non-attribute token of the decl (modifier or
    ///   keyword) — gives us the opening-line column post-attribute-skip.
    /// - `endPos` is the line of the decl's last token before trailing trivia.
    /// - If single-line: emit (anchor.line, anchor.column).
    /// - If multi-line: emit (endLine, leadingWhitespace(endLine)+1) — the
    ///   closing-line value.
    ///
    /// SEMANTIC CLIFF: multi-line opening-line would be more correct; uses the
    /// last-line value for now.
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
    /// Computed as `leading + 1` where `leading` is the count of leading
    /// space/tab characters.
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

    /// PublicFuncRegex shape: `(public|open) final? (static|class)? (mutating|consuming|borrowing)? func`.
    /// UNANCHORED scan: the match starts at the first `public`/`open` modifier and
    /// checks the ALLOWED-AFTER sequence up to `func`. Any modifiers BEFORE the
    /// access keyword (`@MainActor`, `nonisolated`, `final`, `weak`, `dynamic`,
    /// attributes that survive into the modifier list, …) are tolerated and ignored.
    /// Once the access keyword is found, modifiers AFTER it must follow the strict
    /// pattern — anything outside that sequence (e.g., `nonisolated` between
    /// `public` and `func`) breaks the match. Implemented by ignoring everything up
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
        // PublicFuncRegex allows one of {mutating, consuming, borrowing} in this slot — the
        // `consuming`/`borrowing` ownership modifiers appear on `~Copyable` instance methods.
        if let mod = current, ["mutating", "consuming", "borrowing"].contains(mod.name.text), mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// PublicInitRegex shape: `(public|open) convenience? init`.
    /// Same unanchored-match rule as `matchesPublicFuncShape`.
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
    /// final? (static|class)? (var|let)`. Same unanchored-match rule.
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
    /// The type-keyword and `{` must be on the same source line — the walker
    /// enforces this via `typeOpensOnSameLine`.
    ///
    /// Same unanchored-match rule as `matchesPublicFuncShape` — modifiers before
    /// access (`final public class TipGroup`, `@objc public class Foo`, …) are
    /// tolerated and ignored.
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
    /// modifier exists in the list. Implements the unanchored scan for the access
    /// keyword.
    private func advanceToAccess(_ iter: inout DeclModifierListSyntax.Iterator, _ accessTexts: [String]) -> Bool {
        while let mod = iter.next() {
            if accessTexts.contains(mod.name.text) && mod.detail == nil {
                return true
            }
        }
        return false
    }

    /// True iff the type's keyword and its body's opening `{` are on the same
    /// source line. Types whose body opens on a later line are not pushed onto
    /// the scope stack.
    private func typeOpensOnSameLine(keyword: TokenSyntax, leftBrace: TokenSyntax) -> Bool {
        let kwLine = converter.location(for: keyword.positionAfterSkippingLeadingTrivia).line
        let braceLine = converter.location(for: leftBrace.positionAfterSkippingLeadingTrivia).line
        return kwLine == braceLine
    }

    /// Subscript shape: `(public|open) static? subscript`.
    /// Same unanchored-match rule as `matchesPublicFuncShape`.
    private func matchesPublicSubscriptShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["public", "open"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "static", mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// Returns the position of the last token of the decl signature (excluding
    /// any body / accessor block / trailing trivia) — the point where paren-
    /// balanced signature completion is detected.
    private func declSignatureEnd(_ node: SyntaxProtocol) -> AbsolutePosition {
        return node.endPositionBeforeTrailingTrivia
    }

    /// Signature end for VariableDeclSyntax — stops BEFORE the accessor block (`{
    /// get }`). Paren-balance tracking only considers the type annotation and
    /// initializer, not the accessor block, so the signature ends on the line
    /// containing `var <name>: <Type>`. Uses whichever-comes-last among:
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
    /// optional `{ get set }` block; the signature end is the line where the
    /// param-clause parens close.
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
    /// the shape gate and no scope was pushed.
    private var scopePushed: [Bool] = []

    /// Common type-decl visit logic. Push scope and emit only when the type
    /// passes the modifier-shape + same-line `{` gate. Member keying inside the
    /// body still works because we push the scope in the gated path; for the
    /// failing path, members under the body get keyed at module scope (the type
    /// is never pushed onto the scope stack).
    private func visitTypeDecl(name: String,
                                modifiers: DeclModifierListSyntax,
                                attributes: AttributeListSyntax,
                                keyword: TokenSyntax,
                                leftBrace: TokenSyntax,
                                isProtocol: Bool = false) -> SyntaxVisitorContinueKind {
        // `PublicTypeDeclRegex` ends in bare `(\w+)` — backtick-escaped names
        // (`public struct \`class\``) fail the word-identifier gate and are not
        // pushed onto the scope stack.
        if matchesPublicTypeShape(modifiers),
           RegexShape.isWordIdentifier(name),
           typeOpensOnSameLine(keyword: keyword, leftBrace: leftBrace) {
            let qualified = pushTypeScope(name: name, isProtocol: isProtocol)
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
            leftBrace: node.memberBlock.leftBrace,
            isProtocol: true)
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
        // Extension scope is pushed only when the extension keyword and `{` are
        // on the same source line.
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
        // Pending-OR-inline exclusivity: if any attributes appear on lines BEFORE
        // the `extension` keyword line ("pending"), use only those; otherwise use
        // only attributes on the SAME line as the keyword ("inline").
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
        scopeStack.append(Scope(name: stripped, isExtension: true, isProtocol: false, extensionScopeAttributes: scopeAttrs))
        extensionScopePushed.append(true)
        // Note: extension decls themselves do NOT emit a key into
        // `availabilityAnnotations` — the extension's own annotations are
        // consumed as scope annotations and applied to its members.
        return .visitChildren
    }
    override func visitPost(_ node: ExtensionDeclSyntax) {
        if let pushed = extensionScopePushed.popLast(), pushed {
            _ = scopeStack.popLast()
        }
    }

    private func pushTypeScope(name: String, isProtocol: Bool = false) -> String {
        scopeStack.append(Scope(name: name, isExtension: false, isProtocol: isProtocol, extensionScopeAttributes: []))
        return qualifiedTypePath
    }

    // MARK: - Member declarations

    override func visit(_ node: FunctionDeclSyntax) -> SyntaxVisitorContinueKind {
        // Order-strict PublicFuncRegex shape gate. SKIPPED inside protocol
        // bodies — protocol requirements have no access modifier in swiftinterface
        // text (Family-F-2).
        if !isInsideProtocol {
            guard matchesPublicFuncShape(node.modifiers) else {
                return .skipChildren
            }
        }
        // PublicFuncRegex now also matches operator-character runs (`==`, `+`, …)
        // so static-func operator declarations propagate the enclosing extension's
        // @available floor onto the operator's MethodDecl. Without this the @_cdecl
        // equality wrapper for retroactive Equatable conformances (e.g.
        // RealityFoundation.TextureResource — class is iOS 13+, Equatable is
        // iOS 18+) compiles at the type's lower @available than the conformance
        // requires. Backtick-escaped identifier names (`var \`class\``) fail the
        // word-identifier gate (`\w+`) and are skipped.
        guard RegexShape.isWordIdentifier(node.name.text) ||
              RegexShape.isOperatorIdentifier(node.name.text) else {
            return .skipChildren
        }
        let printed = buildPrintedName(funcName: node.name.text, params: node.signature.parameterClause)
        let key = memberKey(printedName: printed)
        let paramSig = AvailabilityWalker.buildParamSignature(params: node.signature.parameterClause)
        recordMemberSignature(bareKey: key, paramSig: paramSig)
        let annotations = collectAnnotations(declAttrs: node.attributes)
        let anchor = declAnchorToken(modifiers: node.modifiers, fallback: node.funcKeyword)
        let declEnd = node.body?.leftBrace.position ?? declSignatureEnd(node)
        stageMemberAnnotations(bareKey: key, paramSig: paramSig, annotations: annotations,
                               anchor: anchor, declEnd: declEnd)
        return .skipChildren
    }

    override func visit(_ node: InitializerDeclSyntax) -> SyntaxVisitorContinueKind {
        if !isInsideProtocol {
            guard matchesPublicInitShape(node.modifiers) else {
                return .skipChildren
            }
        }
        let printed = buildPrintedName(funcName: "init", params: node.signature.parameterClause)
        let key = memberKey(printedName: printed)
        let paramSig = AvailabilityWalker.buildParamSignature(params: node.signature.parameterClause)
        recordMemberSignature(bareKey: key, paramSig: paramSig)
        let annotations = collectAnnotations(declAttrs: node.attributes)
        let anchor = declAnchorToken(modifiers: node.modifiers, fallback: node.initKeyword)
        let declEnd = node.body?.leftBrace.position ?? declSignatureEnd(node)
        stageMemberAnnotations(bareKey: key, paramSig: paramSig, annotations: annotations,
                               anchor: anchor, declEnd: declEnd)
        return .skipChildren
    }

    override func visit(_ node: VariableDeclSyntax) -> SyntaxVisitorContinueKind {
        if !isInsideProtocol {
            guard matchesPublicVarShape(node.modifiers) else {
                return .skipChildren
            }
        }
        // First-binding identifier: the word-identifier gate (`\w+`) captures only
        // the first identifier after `var`/`let`.
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
        // Vars cannot be overloaded; sig is empty. Recording still ensures the
        // bare-key path stays the storage path.
        recordMemberSignature(bareKey: key, paramSig: "")
        let annotations = collectAnnotations(declAttrs: node.attributes)
        let anchor = declAnchorToken(modifiers: node.modifiers, fallback: node.bindingSpecifier)
        let declEnd = signatureEndForVar(node)
        stageMemberAnnotations(bareKey: key, paramSig: "", annotations: annotations,
                               anchor: anchor, declEnd: declEnd)
        return .skipChildren
    }

    override func visit(_ node: SubscriptDeclSyntax) -> SyntaxVisitorContinueKind {
        if !isInsideProtocol {
            guard matchesPublicSubscriptShape(node.modifiers) else {
                return .skipChildren
            }
        }
        // SubscriptDecl's printedName format from `ExtractSubscriptPrintedName`:
        // `subscript(label1:label2:)` using external labels (`_` or external word).
        let printed = buildPrintedNameForSubscript(params: node.parameterClause)
        let key = memberKey(printedName: printed)
        let paramSig = AvailabilityWalker.buildParamSignature(params: node.parameterClause)
        recordMemberSignature(bareKey: key, paramSig: paramSig)
        let annotations = collectAnnotations(declAttrs: node.attributes)
        let anchor = declAnchorToken(modifiers: node.modifiers, fallback: node.subscriptKeyword)
        let declEnd = signatureEndForSubscript(node)
        stageMemberAnnotations(bareKey: key, paramSig: paramSig, annotations: annotations,
                               anchor: anchor, declEnd: declEnd)
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
        // Operators have no argument labels at the call site — the ABI printedName
        // uses `_` for every parameter (`==(_:_:)`, not `==(lhs:rhs:)`). The
        // `IsOperatorName` substitution ensures the walker-produced key matches
        // the ABI lookup key.
        let funcIsOperator = RegexShape.isOperatorIdentifier(funcName)
        var labels: [String] = []
        for param in paramList {
            if funcIsOperator {
                labels.append("_")
                continue
            }
            let firstName = param.firstName.text
            if firstName.isEmpty { continue }
            labels.append(firstName)
        }
        if labels.isEmpty { return "\(funcName)()" }
        return "\(funcName)(\(labels.map { "\($0):" }.joined()))"
    }

    /// Build the parameter-type signature used to disambiguate overloads that share
    /// a `printedName`. Mirrors `MemberSignatureNormalizer.BuildSignature` on the
    /// .NET side: per-parameter normalization (strip ownership/opaque modifiers,
    /// optionality, generic args, take last `.`-segment, drop backticks) joined
    /// with `,`. Returns an empty string when there are no parameters.
    static func buildParamSignature(params: FunctionParameterClauseSyntax) -> String {
        let paramList = params.parameters
        if paramList.isEmpty { return "" }
        var tails: [String] = []
        for param in paramList {
            let raw = param.type.trimmedDescription
            tails.append(normalizeParamType(raw))
        }
        return tails.joined(separator: ",")
    }

    /// Mirrors `MemberSignatureNormalizer.NormalizeParamType` on the .NET side. Both
    /// sides MUST collapse to the same string for the disamb keys to match. Generic
    /// argument lists are preserved and normalized recursively so overloads
    /// distinguished only by their generic specialization (`Array<Int>` vs
    /// `Array<String>`) keep distinct signatures — stripping the generics outright
    /// would reintroduce the Family-F broadcast / merge bug.
    ///
    /// LOAD-BEARING CROSS-LANGUAGE CONTRACT (Finding 46). This function and its
    /// helpers (`splitTopLevelCommas`, `canonicalizeCollectionSugar`) are a hand-written
    /// parallel implementation of the C# `MemberSignatureNormalizer.NormalizeParamType`.
    /// The contract is that both sides emit byte-identical output keys on every input
    /// that actually reaches them — not that the two are character-for-character
    /// identical: the C# side carries a variadic `...` pre-strip and quoted-string
    /// tracking in its generic-arg splitter that this Swift side omits, because the
    /// structured `param.type` text fed here never carries a trailing `...` or a
    /// string-literal-bearing type (those extra C# defenses are no-ops on these inputs).
    /// This Swift tool cannot call into the .NET generator, so the two implementations
    /// are duplicated by hand and their output keys must stay byte-identical — if they
    /// drift, the availability annotation staged under `"Type.printedName|sig"`
    /// lands under a key the .NET ABI consumer never looks up, and the `@available`
    /// floor silently detaches from its member. Any edit here MUST be mirrored verbatim
    /// in `MemberSignatureNormalizer.cs` (and vice versa) and re-proven by the
    /// cross-language normalizer corpus tests. The C# side deliberately does NOT
    /// route this through its unified `TypeSpecParser` grammar, because that grammar
    /// reprints differently (spaced generic commas, `Optional<Int>`, `()` for `Void`,
    /// EOF-strict) and would desync this mirror. The clean fix that would delete this
    /// duplication entirely is to key availability on a stable identity (usr / mangled
    /// name) instead of a normalized textual signature — but a `.swiftinterface`
    /// carries no usr/mangled name, so that is out-of-scope Finding 3 work.
    static func normalizeParamType(_ raw: String) -> String {
        var s = raw.trimmingCharacters(in: .whitespaces)
        let prefixes = ["inout", "borrowing", "consuming", "some", "any", "__owned", "__shared"]
        var stripped = true
        while stripped {
            stripped = false
            for prefix in prefixes {
                if s.hasPrefix(prefix + " ") {
                    s = String(s.dropFirst(prefix.count + 1))
                        .trimmingCharacters(in: .whitespaces)
                    stripped = true
                    break
                }
            }
        }
        // Drop default-value tail.
        if let eqIdx = s.firstIndex(of: "=") {
            s = String(s[..<eqIdx]).trimmingCharacters(in: .whitespaces)
        }
        // Strip trailing `?` / `!` BEFORE peeling off generics so `Array<Int>?`
        // recurses into `Array<Int>` correctly.
        while let last = s.last, last == "?" || last == "!" {
            s = String(s.dropLast())
        }
        s = s.trimmingCharacters(in: .whitespaces)

        // Canonicalize collection sugar (`[T]` → `Array<T>`, `[K: V]` →
        // `Dictionary<K,V>`). Mirrors the .NET-side step so both parsers
        // converge on the nominal form before the generic split.
        s = canonicalizeCollectionSugar(s)

        // Split off the outer generic argument list (if any). Use the FIRST `<`
        // and require the string to end with `>`; the inside is normalized
        // recursively so nested generics like `Dictionary<String, Array<Int>>`
        // round-trip cleanly.
        var outer: String
        var argsInner: String? = nil
        if let ltIdx = s.firstIndex(of: "<"), s.last == ">", s.distance(from: s.startIndex, to: ltIdx) > 0 {
            outer = String(s[..<ltIdx]).trimmingCharacters(in: .whitespaces)
            let afterLt = s.index(after: ltIdx)
            let beforeGt = s.index(before: s.endIndex)
            argsInner = String(s[afterLt..<beforeGt])
        } else {
            outer = s
        }

        // Take last `.`-segment of the outer head.
        if let lastDot = outer.lastIndex(of: ".") {
            outer = String(outer[outer.index(after: lastDot)...])
        }
        // Strip backticks on the outer head.
        if outer.count >= 2, outer.hasPrefix("`"), outer.hasSuffix("`") {
            outer = String(outer.dropFirst().dropLast())
        }

        guard let argsInner = argsInner else { return outer }

        // Recursively normalize each comma-split argument, then reassemble.
        let parts = splitTopLevelCommas(argsInner).map { normalizeParamType($0) }
        return outer + "<" + parts.joined(separator: ",") + ">"
    }

    /// Folds Swift collection sugar to the equivalent nominal generic form.
    /// Mirrors `MemberSignatureNormalizer.CanonicalizeCollectionSugar` on the
    /// .NET side. Returns the input unchanged when it isn't a single
    /// bracket-enclosed run.
    private static func canonicalizeCollectionSugar(_ s: String) -> String {
        guard s.count >= 2, s.hasPrefix("["), s.hasSuffix("]") else { return s }

        // Verify the outer brackets enclose the WHOLE string.
        var depth = 0
        var idx = s.startIndex
        while idx < s.endIndex {
            let c = s[idx]
            if c == "(" || c == "[" || c == "<" {
                depth += 1
            } else if c == ")" || c == "]" || c == ">" {
                depth -= 1
                if depth == 0 && idx != s.index(before: s.endIndex) {
                    return s
                }
            }
            idx = s.index(after: idx)
        }
        if depth != 0 { return s }

        let inner = String(s.dropFirst().dropLast())

        // Find a top-level `:` that separates dictionary key/value.
        depth = 0
        var colonOffset: Int? = nil
        for (offset, c) in inner.enumerated() {
            if c == "(" || c == "[" || c == "<" {
                depth += 1
            } else if c == ")" || c == "]" || c == ">" {
                depth -= 1
            } else if c == ":" && depth == 0 {
                colonOffset = offset
                break
            }
        }

        guard let colonOffset = colonOffset else {
            return "Array<" + inner.trimmingCharacters(in: .whitespaces) + ">"
        }

        let colonIdx = inner.index(inner.startIndex, offsetBy: colonOffset)
        let key = String(inner[..<colonIdx]).trimmingCharacters(in: .whitespaces)
        let val = String(inner[inner.index(after: colonIdx)...]).trimmingCharacters(in: .whitespaces)
        return "Dictionary<" + key + "," + val + ">"
    }

    /// Splits `text` on commas at depth-0 with respect to `(`, `[`, and `<`.
    /// Mirrors `MemberSignatureNormalizer.SplitGenericArgsTopLevel` — necessary because
    /// `Dictionary<String, Array<Int>>`'s outer comma must split, but the inner
    /// args of `Array<Int>` must not. The C# side is intentionally the *unguarded*
    /// generic-arg splitter — NOT the arrow-guarded `SwiftTypeListText.SplitTopLevelParameters`
    /// — so this Swift implementation must produce the same splits; see the contract
    /// block above `normalizeParamType`.
    private static func splitTopLevelCommas(_ text: String) -> [String] {
        var results: [String] = []
        var depth = 0
        var start = text.startIndex
        var i = text.startIndex
        while i < text.endIndex {
            let c = text[i]
            if c == "(" || c == "[" || c == "<" {
                depth += 1
            } else if c == ")" || c == "]" || c == ">" {
                depth -= 1
            } else if c == "," && depth == 0 {
                results.append(String(text[start..<i]))
                start = text.index(after: i)
            }
            i = text.index(after: i)
        }
        if start <= text.endIndex {
            results.append(String(text[start..<text.endIndex]))
        }
        return results
    }

    private func buildPrintedNameForSubscript(params: FunctionParameterClauseSyntax) -> String {
        // Mirrors the ABI JSON's printedName for a SubscriptDecl. Subscripts are called
        // bracket-style (`obj[val]`) with NO label by default, so a single-name parameter
        // like `subscript(key: KeyType)` keys as `subscript(_:)`. Only a two-name param
        // where the first isn't `_` carries an external label:
        // `subscript(bitAt index: Int)` → `subscript(bitAt:)`. SwiftSyntax exposes the
        // first/second name distinction via `firstName`/`secondName` so we can detect
        // the two-name case directly. Without this substitution, the parser-emitted key
        // (`subscript(entityPath:)`) won't match the ABI lookup key (`subscript(_:)`),
        // and `ApplyMemberAvailability` silently drops the extension's @available floor.
        let paramList = params.parameters
        if paramList.isEmpty { return "subscript()" }
        var labels: [String] = []
        for param in paramList {
            let firstName = param.firstName.text
            let secondName = param.secondName?.text
            if let secondName, !secondName.isEmpty, firstName != "_" {
                labels.append(firstName)
            } else {
                labels.append("_")
            }
        }
        return "subscript(\(labels.map { "\($0):" }.joined()))"
    }

    // MARK: - @available parsing

    /// Parse a single `@available(...)` attribute into one or more
    /// AvailabilityAnnotationJson records. Delegates to `AvailabilityClauseParser`.
    private func parseAvailableAttribute(_ attr: AttributeSyntax) -> [AvailabilityAnnotationJson] {
        // The argument list shape for `@available` varies (per-platform vs. shorthand
        // vs. unconditional). The arguments are formatted back to source text and
        // passed to the shared clause-splitting logic. SwiftSyntax's
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
/// so its semantics can be unit-tested independently.
///
/// Accepts the inside-the-parens text of `@available(...)` and returns one or more
/// parsed annotation records. Extraction rules:
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

    /// `visionOSApplicationExtension` is INTENTIONALLY not in `isKnownPlatform`,
    /// so it never reaches this function and is silently dropped.
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
