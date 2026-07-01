// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks the syntax tree of a .swiftinterface and surfaces two enum-related fact
/// dictionaries:
///   * `enumCaseLabels`:    `"TypePath.caseName" -> [associatedValueLabel?]`
///   * `enumCaseRawValues`: `"TypePath.caseName" -> rawValueString` (string-literal
///     content, OR an integer raw value normalized to a base-10 string)
///
/// EXTRACTION CONTRACT:
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
///    AND at least one parameter contribute. Per parameter:
///    - `null` if the param has no top-level colon, or before-colon is `_`, or
///      the colon is inside brackets (e.g. `[String : String]` is unlabeled).
///    - the trimmed before-colon string otherwise.
///    Empty parens (`case foo()`) emit no entry. Cases without parens emit no entry.
///
/// 4. **Grouped `case a(Int), b(Int)` / `case a = "A", b = "B"`**: only the FIRST
///    element of each `EnumCaseDeclSyntax` is emitted. Subsequent elements (`b`,
///    `c`, ...) on the same source line are dropped. This is a semantic cliff
///    (SwiftSyntax can see all elements; only the first is emitted because the
///    downstream consumer expects first-only emission per case-line).
///
/// 5. **Raw values (`enumCaseRawValues`)**: string-literal AND integer-literal raw
///    values contribute. For the string form (`= "..."`), escape sequences are
///    preserved verbatim as written in source — SwiftSyntax's segment text for a basic
///    string literal yields the same; the stored value is the unquoted content. For the
///    integer form (`= 17009`, `= 0xFF`, `= 0o17`, `= 0b101`, `= 1_000`, `= -1`), the
///    value is normalized to a base-10 string (radix prefixes decoded, `_` separators
///    stripped, a leading unary minus preserved as `-`). Other raw value kinds
///    (floating-point, interpolated/multi-line strings, computed expressions) are absent.
///
/// 6. **`indirect case`**: handled — SwiftSyntax exposes `indirect` as a modifier
///    on the case decl and the case name is extracted the same way as a direct case.
///
/// 7. **No access modifier filter**: cases have no access modifier in
///    swiftinterface output; emit all.
///
/// 8. **Type-scope push gate**: a type is pushed onto the scope stack only when
///    the declaration carries a `public`/`internal`/`open` modifier (optionally
///    `final`) AND the body's `{` is on the same source line. Non-matching shapes
///    such as `public indirect enum` or types whose body opens on a later line are
///    NOT pushed; their members end up keyed at module scope (or, if nested, at
///    the outer scope). Each `visit(<TypeDecl>)` push is gated through
///    `enterTypeScope` and the matching `visitPost` pop through `leaveTypeScope`.
final class EnumFactsWalker: SyntaxVisitor {
    private(set) var enumCaseLabels: [String: [String?]] = [:]
    private(set) var enumCaseRawValues: [String: String] = [:]

    /// Scope stack tracking nesting context; for type decls pushes simple name,
    /// for extensions pushes first-dot-stripped target. Joined with `.` to build
    /// the type-prefix portion of every emitted key.
    private var scopeStack: [String] = []

    /// Parallel stack: each visited type/extension records whether it actually
    /// pushed a frame on `scopeStack`, so `visitPost` knows whether to pop.
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

    // MARK: - Type decls (gated push: access-modifier shape + same-line `{`)

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
        // The extended type must match `[\w.]+` (word characters and dots only);
        // generics, parens, or composition types are not captured.
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
        // The type-name capture requires a word identifier (`\w+`) — backtick-escaped
        // names fail the Unicode word-class check, so SwiftSyntax must also skip
        // pushing them.
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

    /// Shape: `(?:public|internal|open)\s+(?:final\s+)?(?:class|struct|enum|actor|protocol)\s+(\w+)`.
    /// Modifiers BEFORE access are tolerated (unanchored scan); after access only
    /// an optional `final` is allowed before the type keyword. `public indirect enum`,
    /// `public nonisolated class`, etc. fail this gate and are NOT pushed.
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
    /// same source line (same-line brace gate).
    private func typeOpensOnSameLine(keyword: TokenSyntax, leftBrace: TokenSyntax) -> Bool {
        let kwLine = converter.location(for: keyword.positionAfterSkippingLeadingTrivia).line
        let braceLine = converter.location(for: leftBrace.positionAfterSkippingLeadingTrivia).line
        return kwLine == braceLine
    }

    /// Iterator helper: consume modifiers until we find one whose `name.text` is in
    /// `accessTexts` AND whose `detail` is nil. Modifiers before the access modifier
    /// are tolerated (unanchored scan).
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
        // syntactically, but guard on a non-empty stack for safety.
        guard !scopeStack.isEmpty else { return .skipChildren }
        let typePrefix = scopeStack.joined(separator: ".")

        // Only the FIRST element of the case decl is inspected. `case a, b(Int)`
        // emits nothing (first element `a` has no associated value and no raw value),
        // and `case a, b = "B"` also emits nothing for raw values. Inspect ONLY
        // `elements.first` and decide based on its own associated value / raw value
        // (or absence thereof).
        guard let element = node.elements.first else { return .skipChildren }
        let caseName = element.name.text
        let key = "\(typePrefix).\(caseName)"

        // EnumCaseLabels — only emit when the FIRST case has at least one associated value.
        if let assoc = element.parameterClause, !assoc.parameters.isEmpty {
            var labels: [String?] = []
            for param in assoc.parameters {
                // SwiftSyntax exposes firstName explicitly.
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

        // EnumCaseRawValues — string-literal AND integer-literal raw values on the FIRST
        // case contribute. String literals store the unquoted content; integer literals
        // are normalized to a base-10 string (hex/octal/binary/underscored/negative forms
        // all collapse to decimal) so the .NET consumer's long.TryParse succeeds.
        if let rawValue = element.rawValue?.value {
            if let stringLiteral = rawValue.as(StringLiteralExprSyntax.self) {
                if let extracted = extractRawSegments(stringLiteral) {
                    enumCaseRawValues[key] = extracted
                }
            } else if let extracted = extractIntegerLiteral(rawValue) {
                enumCaseRawValues[key] = extracted
            }
        }

        return .skipChildren
    }

    /// Extract an integer raw value as a base-10 string. Handles a bare integer literal
    /// (`= 17009`, `= 0xFF`, `= 0o17`, `= 0b101`, `= 1_000`) and a unary-sign prefix
    /// (`= -1`, `= +1`). Hex/octal/binary and underscore-grouped forms are normalized to
    /// decimal. Returns nil for any non-integer expression (floating-point or computed raw
    /// values — which integer-backed enums cannot have — are left absent).
    private func extractIntegerLiteral(_ expr: ExprSyntax) -> String? {
        var negative = false
        var literalExpr = expr
        if let prefix = expr.as(PrefixOperatorExprSyntax.self) {
            // Only a leading unary minus flips the sign; a leading `+` is a no-op.
            switch prefix.operator.text {
            case "-": negative = true
            case "+": negative = false
            default: return nil
            }
            literalExpr = prefix.expression
        }
        guard let intLiteral = literalExpr.as(IntegerLiteralExprSyntax.self),
              let magnitude = Self.parseIntegerMagnitude(intLiteral.literal.text) else {
            return nil
        }
        // "-0" and "0" are the same value; don't emit a misleading leading minus.
        return (negative && magnitude != "0") ? "-\(magnitude)" : magnitude
    }

    /// Parse a Swift integer-literal token (already sign-stripped) into its base-10
    /// magnitude string. Strips `_` digit separators and decodes `0x`/`0o`/`0b` radix
    /// prefixes. Uses UInt64 so the full unsigned range round-trips; returns nil on
    /// overflow or malformed input.
    private static func parseIntegerMagnitude(_ raw: String) -> String? {
        let cleaned = raw.replacingOccurrences(of: "_", with: "")
        if cleaned.isEmpty { return nil }
        let lower = cleaned.lowercased()
        let radix: Int
        let digits: Substring
        if lower.hasPrefix("0x") { radix = 16; digits = cleaned.dropFirst(2) }
        else if lower.hasPrefix("0o") { radix = 8; digits = cleaned.dropFirst(2) }
        else if lower.hasPrefix("0b") { radix = 2; digits = cleaned.dropFirst(2) }
        else { radix = 10; digits = Substring(cleaned) }
        if digits.isEmpty { return nil }
        guard let value = UInt64(digits, radix: radix) else { return nil }
        return String(value)
    }

    /// Extract the verbatim source text of a basic (non-multiline, non-interpolated)
    /// string literal's contents, preserving escape sequences as-written — including
    /// `\t`, `\n`, `\\`, `\"` as the two-character escape sequences from source.
    /// Returns nil for interpolated or multi-line literals (only straight `"..."`
    /// forms produce a value).
    private func extractRawSegments(_ literal: StringLiteralExprSyntax) -> String? {
        // Multi-line (`"""..."""`) and raw (`#"..."#`) strings have non-empty
        // openingPounds or use multi-line delimiters; these are not extracted.
        if literal.openingPounds != nil { return nil }
        if literal.openingQuote.tokenKind == .multilineStringQuote { return nil }

        var result = ""
        for segment in literal.segments {
            switch segment {
            case .stringSegment(let seg):
                // .text holds the raw source spelling: escape sequences are kept as-is.
                result.append(seg.content.text)
            case .expressionSegment:
                // Interpolation cannot be a constant raw value; return nil.
                return nil
            }
        }
        return result
    }
}
