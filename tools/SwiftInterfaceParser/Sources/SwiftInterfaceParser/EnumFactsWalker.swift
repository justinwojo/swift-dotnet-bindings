// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks the syntax tree of a .swiftinterface and surfaces two enum-related fact
/// dictionaries:
///   * `enumCaseLabels`:    `"TypePath.caseName" -> [associatedValueLabel?]`
///   * `enumCaseRawValues`: `"TypePath.caseName" -> rawValueString` (string literal only)
///
/// PARITY CONTRACT WITH `SwiftInterfaceAccessParser.GetEnumCaseLabels` (line 2539)
/// and `GetEnumRawValues` (line 2713).
///
/// 1. **Decl kinds**: only `case` declarations inside an enum (or extension of enum)
///    scope. Module-level cases (impossible in real Swift) are skipped.
///
/// 2. **Key shape**: full dot-joined nesting path of the type stack + `.caseName`.
///    Extensions strip the FIRST dot-component (module prefix) and retain all
///    remaining nesting — `extension Mod.Outer.Inner` keys as `"Outer.Inner"`.
///    Type decls push their simple name. So:
///    - `enum Shape { case circle(radius: Double) }` → `"Shape.circle"`
///    - `struct Config { enum Mode { case fast(Int) } }` → `"Config.Mode.fast"`
///    - `extension Mod.P256.Signing { enum Mode { case x } }` → `"P256.Signing.Mode.x"`
///
/// 3. **Associated-value labels (`enumCaseLabels`)**: only cases with parentheses
///    AND at least one parameter contribute. Per parameter, regex emits:
///    - `null` if the param has no top-level colon, or before-colon is `_`, or
///      the colon is inside brackets (e.g. `[String : String]` is unlabeled).
///    - the trimmed before-colon string otherwise.
///    Empty parens (`case foo()`) emit no entry. Cases without parens emit no entry.
///
/// 4. **Grouped `case a(Int), b(Int)` / `case a = "A", b = "B"`**: both
///    `EnumCaseRegex` (`case\s+(\w+)\s*\(`) and `EnumCaseRawValueRegex`
///    (`case\s+(\w+)\s*=\s*"..."`) call `.Match(line)` which returns only the
///    FIRST hit per line. Subsequent elements (`b`, `c`, ...) on the same source
///    line are dropped by the regex, so the walker MUST also stop after the first
///    element of an `EnumCaseDeclSyntax` to maintain byte-equal parity. This is a
///    semantic cliff (SwiftSyntax can see all elements; we deliberately mirror
///    the regex's first-only emission).
///
/// 5. **Raw values (`enumCaseRawValues`)**: only string-literal raw values
///    contribute. Integer or other raw value kinds are absent (regex requires
///    `= "..."`). Escape sequences are preserved verbatim as written in source —
///    SwiftSyntax's segment text for a basic string literal yields the same.
///    The stored value is the unquoted content.
///
/// 6. **`indirect case`**: handled — both regex and SwiftSyntax see the case
///    declaration the same way (regex strips `indirect ` prefix; SwiftSyntax
///    treats `IndirectModifier` as a modifier on the case decl).
///
/// 7. **No access modifier filter**: cases have no access modifier in
///    swiftinterface output; emit all.
///
/// 8. **Type-scope push gate (regex parity)**: the regex producer's tracker only
///    pushes a type onto its scope stack when `TypeDeclRegex` (public/internal/
///    open + optional `final`) matches AND the body's `{` is on the same source
///    line (`openBraces > 0`). Non-matching shapes such as `public indirect enum`
///    or types whose body opens on a later line are NOT pushed; their members
///    end up keyed at module scope (or, if nested, at the outer scope). Mirror
///    by gating each `visit(<TypeDecl>)` push through `enterTypeScope` and the
///    matching `visitPost` pop through `leaveTypeScope`.
final class EnumFactsWalker: SyntaxVisitor {
    private(set) var enumCaseLabels: [String: [String?]] = [:]
    private(set) var enumCaseRawValues: [String: String] = [:]

    /// Scope stack tracking nesting context; for type decls pushes simple name,
    /// for extensions pushes first-dot-stripped target. Joined with `.` to build
    /// the type-prefix portion of every emitted key.
    private var scopeStack: [String] = []

    /// Parallel stack: each visited type/extension records whether it actually
    /// pushed a frame on `scopeStack`. Mirrors the regex tracker's gated push so
    /// `visitPost` knows whether to pop.
    private var scopePushed: [Bool] = []

    private let converter: SourceLocationConverter

    init(converter: SourceLocationConverter) {
        self.converter = converter
        super.init(viewMode: .sourceAccurate)
    }

    static func parse(filePath: String, source: String) -> (
        labels: [String: [String?]],
        rawValues: [String: String]
    ) {
        let tree = Parser.parse(source: source)
        let converter = SourceLocationConverter(fileName: filePath, tree: tree)
        let walker = EnumFactsWalker(converter: converter)
        walker.walk(tree)
        return (walker.enumCaseLabels, walker.enumCaseRawValues)
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

    // MARK: - Extensions (push first-dot-stripped path; gated on same-line `{`)

    override func visit(_ node: ExtensionDeclSyntax) -> SyntaxVisitorContinueKind {
        guard typeOpensOnSameLine(keyword: node.extensionKeyword,
                                  leftBrace: node.memberBlock.leftBrace) else {
            scopePushed.append(false)
            return .visitChildren
        }
        let qualified = node.extendedType.trimmedDescription
        // Regex `ExtensionDeclRegex` captures only `[\w.]+` for the extended type;
        // anything else (generics, parens, composition) breaks the regex match.
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

    private func enterTypeScope(name: String,
                                modifiers: DeclModifierListSyntax,
                                keyword: TokenSyntax,
                                leftBrace: TokenSyntax) -> SyntaxVisitorContinueKind {
        // `TypeDeclRegex` ends in bare `(\w+)` — backtick-escaped names fail the
        // Unicode word-class check and miss the regex capture, so SwiftSyntax
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
    /// and are NOT pushed onto the scope stack — matching the regex tracker.
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

    /// Iterator helper: consume modifiers until we find one whose `name.text` is in
    /// `accessTexts` AND whose `detail` is nil. Mirrors the regex's unanchored search
    /// for the access keyword (modifiers before the access are tolerated).
    private func advanceToAccess(_ iter: inout DeclModifierListSyntax.Iterator, _ accessTexts: [String]) -> Bool {
        while let mod = iter.next() {
            if accessTexts.contains(mod.name.text) && mod.detail == nil {
                return true
            }
        }
        return false
    }

    // MARK: - Case decls

    override func visit(_ node: EnumCaseDeclSyntax) -> SyntaxVisitorContinueKind {
        // Only emit when inside a type scope. Free `case` declarations cannot exist
        // syntactically, but mirror the regex `typeStack.Count > 0` guard for safety.
        guard !scopeStack.isEmpty else { return .skipChildren }
        let typePrefix = scopeStack.joined(separator: ".")

        // Regex parity: both `EnumCaseRegex.Match` and `EnumCaseRawValueRegex.Match`
        // anchor on the FIRST `case <name>` token only; subsequent elements on the
        // same source line are invisible to the regex even if THEIR element would
        // satisfy the pattern. So `case a, b(Int)` must emit nothing (regex sees
        // `case a` — no `(`, no match) and `case a, b = "B"` must emit nothing for
        // raw values either. Inspect ONLY `elements.first` and decide based on its
        // own associated value / raw value (or absence thereof).
        guard let element = node.elements.first else { return .skipChildren }
        let caseName = element.name.text
        let key = "\(typePrefix).\(caseName)"

        // EnumCaseLabels — only emit when the FIRST case has at least one associated value.
        if let assoc = element.parameterClause, !assoc.parameters.isEmpty {
            var labels: [String?] = []
            for param in assoc.parameters {
                // SwiftSyntax exposes firstName explicitly. Regex parity:
                // - missing first-name (no label, just `Type`): null
                // - explicit `_` first-name: null
                // - any other first-name: that label
                if let firstName = param.firstName {
                    let text = firstName.text
                    labels.append(text == "_" ? nil : text)
                } else {
                    labels.append(nil)
                }
            }
            enumCaseLabels[key] = labels
        }

        // EnumCaseRawValues — only string-literal raw values on the FIRST case contribute.
        if let rawValue = element.rawValue?.value {
            if let stringLiteral = rawValue.as(StringLiteralExprSyntax.self) {
                if let extracted = extractRawSegments(stringLiteral) {
                    enumCaseRawValues[key] = extracted
                }
            }
        }

        return .skipChildren
    }

    /// Extract the verbatim source text of a basic (non-multiline, non-interpolated)
    /// string literal's contents, preserving escape sequences as-written. The regex
    /// captures `(?:[^"\\]|\\.)*` and emits whatever's inside the quotes — including
    /// `\t`, `\n`, `\\`, `\"` as the two-character escape sequences from source.
    /// Returns nil for interpolated or multi-line literals (regex would not match
    /// those either since it requires straight `"..."`).
    private func extractRawSegments(_ literal: StringLiteralExprSyntax) -> String? {
        // Multi-line (`"""..."""`) and raw (`#"..."#`) strings have non-empty
        // openingPounds or use multi-line delimiters; the regex never matched these.
        if literal.openingPounds != nil { return nil }
        if literal.openingQuote.tokenKind == .multilineStringQuote { return nil }

        var result = ""
        for segment in literal.segments {
            switch segment {
            case .stringSegment(let seg):
                // .text holds the raw source spelling: escape sequences are kept as-is.
                result.append(seg.content.text)
            case .expressionSegment:
                // Interpolation cannot be a constant raw value — the regex would not
                // match it either (the inner `(?:[^"\\]|\\.)*` permits no `\(` boundary).
                return nil
            }
        }
        return result
    }
}
