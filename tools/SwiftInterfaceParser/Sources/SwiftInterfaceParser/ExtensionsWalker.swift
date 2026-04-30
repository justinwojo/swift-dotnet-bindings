// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks every `extension X { ... }` block and emits a flat,
/// module-context-free list of direct func/var members. The .NET-side facts
/// helper `SwiftInterfaceFacts.ResolveForeignExtensions` and
/// `RegexInterfaceFactsProducer.DeriveProtocolExtensionMethods` partition this
/// list using `protocolNames` and `moduleTypeNames` from the ABI parse.
///
/// PARITY CONTRACT WITH `SwiftInterfaceAccessParser.GetExtensionMemberCandidates`
/// (line 2068):
///
/// 1. **Extension target gate**: same-line `{` AND a `[\w.]+` capture on the
///    extended type (regex `ExtensionDeclRegex` + tracker's `openBraces > 0`).
///    Extensions whose body opens on a later line, or whose target uses
///    parentheses/angle brackets/composition, are skipped — no candidates
///    emitted from those blocks.
///
/// 2. **Direct members only**: members at the extension body's outer brace
///    level. Members of nested types declared inside the extension body are
///    NOT emitted. Mirrors the regex producer's `insideNestedType` skip.
///    SwiftSyntax-side: `visit(_: ExtensionDeclSyntax)` returns `.skipChildren`
///    after manually iterating `node.memberBlock.members`, so descent never
///    enters nested type members.
///
/// 3. **Decl kind filter**: `func` and `var` only. `let` is excluded
///    (`ExtensionVarRegex` uses literal `var`, not `(?:var|let)`). `init`,
///    `subscript`, `typealias`, `case`, etc. are excluded.
///
/// 4. **STRICT modifier shapes**:
///      * Func: `(?:public|open)\s+(?:static\s+)?(?:mutating\s+)?func`. Only
///        `static` and `mutating` are allowed between access and `func`, in that
///        order. `public final func`, `public override func`,
///        `public nonisolated func` are rejected.
///      * Var: `(?:public|open)\s+(?:static\s+)?var`. Only `static` is allowed
///        between access and `var`. `public lazy var`, `public weak var` rejected.
///      * Modifiers BEFORE access are tolerated (regex unanchored Match scan).
///
/// 5. **Extended type: verbatim**: `node.extendedType.trimmedDescription` is
///    used as `extendedTypeName` (e.g., `"UIKit.UIView"`, `"Mod.MyProto"`,
///    `"MyType"`). Partitioning happens .NET-side via the first-dot rule.
///
/// 6. **Where constraints**: each top-level requirement in the extension's
///    `genericWhereClause` is emitted as its source-text-trimmed description.
///    Order is preserved. Empty list when there's no where clause.
///
/// 7. **`@MainActor` detection**: any attribute named `MainActor` or
///    `_Concurrency.MainActor` on the func/var decl flips
///    `isMainActorIsolated`. The regex producer also lifts a same-file pending
///    `@MainActor` annotation onto the next decl line — SwiftSyntax handles
///    that automatically because attributes are part of the FunctionDeclSyntax /
///    VariableDeclSyntax node regardless of source line breaks.
///
/// 8. **`@available(*, deprecated, ...)` detection**: any attribute whose
///    arguments contain `*, deprecated` flips `isDeprecated`. Mirrors the
///    regex `@available\(\s*\*\s*,\s*deprecated`.
///
/// 9. **Self return detection**: `-> Self` (or `-> ... Self` ending with
///    bare `Self`) at the trailing edge of the func's signature. Optional
///    Self / Self? would not match (regex's `EndsWith("-> Self")`).
///
/// 10. **Setter detection (var only)**: any `set` accessor in the binding's
///     accessor block, including `nonmutating set` and `set` with attributes.
///     Mirrors the regex's brace-tracking `set`/`nonmutating set` line scan.
///
/// 11. **Method name**: `node.name.text` for funcs (skipped if non-identifier
///     like an operator symbol — regex `\w+` parity), property name for vars
///     (the binding's identifier text, backtick-stripped to match
///     `ExtensionVarRegex` capture).
///
/// 12. **Printed name**: Swift-canonical `name(label1:label2:)`. Mirrors
///     `SwiftInterfaceAccessParser.ExtractPrintedName` exactly — first-name
///     of each parameter. Property printedName is just the property name.
///
/// 13. **RawSignature**: the source text of the decl, with newline+leading-
///     whitespace runs collapsed into single spaces. The regex producer
///     similarly multi-line-collapses via `continuationLine += " " + trimmed`,
///     so consumer regexes (`func name<`, `\basync\b`, `\bthrows\b`,
///     `" where "` substring) fire identically. The regex producer captures
///     `RawSignature = trimmed` (the source line containing the access modifier),
///     so attributes on the SAME line as `public`/`open` are included while
///     attributes on EARLIER lines are dropped (consumed via `pendingMainActor`/
///     `pendingDeprecated` booleans). Mirror that exactly: attribute-line ==
///     access-modifier-line → keep; otherwise drop. This makes RawSignature
///     byte-equal to the regex producer's output for the parity contract.
final class ExtensionsWalker: SyntaxVisitor {
    private(set) var extensionMemberCandidates: [ExtensionMemberCandidateInfo] = []

    private let converter: SourceLocationConverter

    init(converter: SourceLocationConverter) {
        self.converter = converter
        super.init(viewMode: .sourceAccurate)
    }

    static func parse(filePath: String, source: String) -> [ExtensionMemberCandidateInfo] {
        let tree = Parser.parse(source: source)
        let converter = SourceLocationConverter(fileName: filePath, tree: tree)
        let walker = ExtensionsWalker(converter: converter)
        walker.walk(tree)
        return walker.extensionMemberCandidates
    }

    // MARK: - Extension entry point

    override func visit(_ node: ExtensionDeclSyntax) -> SyntaxVisitorContinueKind {
        // Same-line `{` gate (regex tracker's `openBraces > 0`).
        guard RegexShape.opensOnSameLine(keyword: node.extensionKeyword,
                                          leftBrace: node.memberBlock.leftBrace,
                                          converter: converter) else {
            return .visitChildren
        }
        // `[\w.]+` capture on extended type (regex `ExtensionDeclRegex`).
        let extendedType = node.extendedType.trimmedDescription
        guard RegexShape.isWordOrDotOnly(extendedType) else {
            return .visitChildren
        }

        let whereConstraints = parseWhereConstraints(node.genericWhereClause)

        // Walk DIRECT members only — never recurse into nested types declared
        // inside the extension body (regex parity: `insideNestedType` skip).
        // `#if … #else … #endif` blocks are flattened: regex parser skips the
        // directive lines and processes both branches' contents, so we mirror by
        // descending into every clause's elements.
        processExtensionMembers(node.memberBlock.members,
                                 extendedType: extendedType,
                                 whereConstraints: whereConstraints)

        // Don't descend — extension's children handled manually above. Crucially
        // this also skips nested type members (regex's direct-only semantics).
        return .skipChildren
    }

    private func processExtensionMembers(_ members: MemberBlockItemListSyntax,
                                          extendedType: String,
                                          whereConstraints: [String]) {
        for member in members {
            if let funcDecl = member.decl.as(FunctionDeclSyntax.self) {
                handleFunc(funcDecl, extendedType: extendedType, whereConstraints: whereConstraints)
            } else if let varDecl = member.decl.as(VariableDeclSyntax.self) {
                handleVar(varDecl, extendedType: extendedType, whereConstraints: whereConstraints)
            } else if let ifConfigDecl = member.decl.as(IfConfigDeclSyntax.self) {
                // Regex producer skips `#if` / `#else` / `#endif` directive lines and
                // collects every member line in between regardless of branch — so we
                // walk every clause's elements as if they were direct members.
                for clause in ifConfigDecl.clauses {
                    if let elements = clause.elements?.as(MemberBlockItemListSyntax.self) {
                        processExtensionMembers(elements,
                                                 extendedType: extendedType,
                                                 whereConstraints: whereConstraints)
                    }
                }
            }
            // Other decls (TypeAliasDecl, NestedTypeDecl, InitDecl, SubscriptDecl, etc.) — ignored.
        }
    }

    // MARK: - Func member

    private func handleFunc(_ node: FunctionDeclSyntax,
                             extendedType: String,
                             whereConstraints: [String]) {
        // `ExtensionFuncRegex` strict shape: (public|open) + optional static + optional mutating + func.
        guard matchesExtensionFuncShape(node.modifiers) else { return }
        // Operator funcs — `\w+` capture rejects them.
        guard RegexShape.isWordIdentifier(node.name.text) else { return }

        let methodName = node.name.text
        let printedName = buildFuncPrintedName(name: methodName,
                                                params: node.signature.parameterClause.parameters)
        let anchorLine = accessModifierLine(node.modifiers)
        let detached = node.detached.with(\.attributes, sameLineAttributes(node.attributes, anchorLine: anchorLine))
        let rawSignature = collapseWhitespace(detached.trimmedDescription)
        let returnsSelf = detectSelfReturn(returnClause: node.signature.returnClause)
        let isMainActor = hasMainActorAttribute(node.attributes)
        let isStatic = hasModifier(node.modifiers, "static")
        let isMutating = hasModifier(node.modifiers, "mutating")
        let isDeprecated = hasDeprecatedAttribute(node.attributes)

        extensionMemberCandidates.append(ExtensionMemberCandidateInfo(
            extendedTypeName: extendedType,
            methodName: methodName,
            rawSignature: rawSignature,
            printedName: printedName,
            returnsSelf: returnsSelf,
            isMainActorIsolated: isMainActor,
            isStatic: isStatic,
            isProperty: false,
            hasSetter: false,
            isDeprecated: isDeprecated,
            isMutating: isMutating,
            whereConstraints: whereConstraints
        ))
    }

    // MARK: - Var member

    private func handleVar(_ node: VariableDeclSyntax,
                            extendedType: String,
                            whereConstraints: [String]) {
        // `ExtensionVarRegex` strict shape: (public|open) + optional static + var.
        guard matchesExtensionVarShape(node.modifiers, bindingKeyword: node.bindingSpecifier) else { return }
        // Pick the first binding's identifier name (swiftinterface always produces
        // single-binding `var`/`let`; multi-binding `var a, b: Int` is never produced).
        guard let binding = node.bindings.first,
              let pattern = binding.pattern.as(IdentifierPatternSyntax.self) else { return }
        // Regex parity: `ExtensionVarRegex`'s `(\w+)` capture fails on backtick-escaped
        // names entirely — emit nothing. SwiftSyntax's `.text` strips the backticks, so
        // we have to inspect the source representation via `trimmedDescription` to
        // detect the escape.
        let sourceText = pattern.identifier.trimmedDescription
        if sourceText.hasPrefix("`") { return }
        let propertyName = pattern.identifier.text
        guard RegexShape.isWordIdentifier(propertyName) else { return }

        let anchorLine = accessModifierLine(node.modifiers)
        let rawSignature = buildVarRawSignature(node, binding: binding, anchorLine: anchorLine)
        let isMainActor = hasMainActorAttribute(node.attributes)
        let isStatic = hasModifier(node.modifiers, "static")
        let isDeprecated = hasDeprecatedAttribute(node.attributes)
        let hasSetter = detectSetter(binding.accessorBlock)

        extensionMemberCandidates.append(ExtensionMemberCandidateInfo(
            extendedTypeName: extendedType,
            methodName: propertyName,
            rawSignature: rawSignature,
            printedName: propertyName,
            returnsSelf: false,
            isMainActorIsolated: isMainActor,
            isStatic: isStatic,
            isProperty: true,
            hasSetter: hasSetter,
            isDeprecated: isDeprecated,
            isMutating: false,
            whereConstraints: whereConstraints
        ))
    }

    // MARK: - Modifier-shape gates

    /// `ExtensionFuncRegex` shape: `(?:public|open)\s+(?:static\s+)?(?:mutating\s+)?func`.
    /// STRICT order — only `static` then `mutating` allowed between access and `func`.
    private func matchesExtensionFuncShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard RegexShape.advanceToAccess(&iter, ["public", "open"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "static", mod.detail == nil {
            current = iter.next()
        }
        if let mod = current, mod.name.text == "mutating", mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// `ExtensionVarRegex` shape: `(?:public|open)\s+(?:static\s+)?var`.
    /// STRICT order — only `static` allowed between access and `var`. `let` is rejected
    /// (regex uses literal `var`, not `(?:var|let)`).
    private func matchesExtensionVarShape(_ modifiers: DeclModifierListSyntax,
                                            bindingKeyword: TokenSyntax) -> Bool {
        // The regex specifically requires `var` (not `let`) — match it.
        if bindingKeyword.text != "var" { return false }
        var iter = modifiers.makeIterator()
        guard RegexShape.advanceToAccess(&iter, ["public", "open"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "static", mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    private func hasModifier(_ modifiers: DeclModifierListSyntax, _ keyword: String) -> Bool {
        for m in modifiers where m.name.text == keyword && m.detail == nil {
            return true
        }
        return false
    }

    // MARK: - Same-line attribute filter (RawSignature parity)

    /// Source line of the first `public`/`open` modifier, or -1. The regex producer's
    /// RawSignature is the trimmed line containing this modifier — so any attribute
    /// on a different source line was dropped from the regex's RawSignature (consumed
    /// via `pendingMainActor`/`pendingDeprecated`).
    private func accessModifierLine(_ modifiers: DeclModifierListSyntax) -> Int {
        for m in modifiers where (m.name.text == "public" || m.name.text == "open") && m.detail == nil {
            return converter.location(for: m.positionAfterSkippingLeadingTrivia).line
        }
        return -1
    }

    /// Builds RawSignature for a var, mirroring the regex producer's source-line capture:
    /// when the accessor block spans multiple source lines, regex captures only the var's
    /// first line — which may include `{` alone OR `{` followed by accessors that share
    /// the same source line (e.g. `{ get` then `set` on the next line). The accessor body
    /// lines past the first are processed separately for setter detection, NOT joined into
    /// RawSignature. When the accessor block fits on one line (`{ get set }`), regex
    /// captures the entire trimmed line including the body.
    ///
    /// Mirror by clipping `accessorBlock.description` at the first newline for multi-line
    /// blocks — preserving any leading-line accessor tokens (`{ get`, `{ @objc set` etc.) —
    /// and keeping the full accessor block intact for single-line blocks.
    private func buildVarRawSignature(_ node: VariableDeclSyntax,
                                        binding: PatternBindingSyntax,
                                        anchorLine: Int) -> String {
        let multilineAccessorFirstLine: String?
        if let accessorBlock = binding.accessorBlock {
            let openLine = converter.location(for: accessorBlock.leftBrace.positionAfterSkippingLeadingTrivia).line
            let closeLine = converter.location(for: accessorBlock.rightBrace.positionAfterSkippingLeadingTrivia).line
            if openLine != closeLine {
                let bodyText = accessorBlock.description
                if let nl = bodyText.firstIndex(of: "\n") {
                    multilineAccessorFirstLine = String(bodyText[..<nl])
                        .trimmingCharacters(in: .whitespaces)
                } else {
                    // Defensive: if open != close lines but there's no newline in the
                    // description (shouldn't happen in well-formed source), fall back
                    // to the full trimmed body.
                    multilineAccessorFirstLine = bodyText.trimmingCharacters(in: .whitespaces)
                }
            } else {
                multilineAccessorFirstLine = nil
            }
        } else {
            multilineAccessorFirstLine = nil
        }
        let bindingForSig = multilineAccessorFirstLine != nil
            ? binding.with(\.accessorBlock, nil)
            : binding
        let detached = node.detached
            .with(\.attributes, sameLineAttributes(node.attributes, anchorLine: anchorLine))
            .with(\.bindings, PatternBindingListSyntax([bindingForSig]))
        var rawSignature = collapseWhitespace(detached.trimmedDescription)
        if let suffix = multilineAccessorFirstLine {
            rawSignature += " " + suffix
        }
        return rawSignature
    }

    /// Filters `attributes` to only those whose source line equals `anchorLine`.
    /// `#if`/`#endif`-wrapped attributes are kept conservatively — they're vanishingly
    /// rare in `.swiftinterface` output and the regex producer's behavior on them is
    /// undefined, so byte-equal parity is not asserted there.
    private func sameLineAttributes(_ attributes: AttributeListSyntax,
                                      anchorLine: Int) -> AttributeListSyntax {
        var kept: [AttributeListSyntax.Element] = []
        for attrEl in attributes {
            switch attrEl {
            case .attribute(let attr):
                let line = converter.location(for: attr.positionAfterSkippingLeadingTrivia).line
                if line == anchorLine {
                    kept.append(attrEl)
                }
            case .ifConfigDecl:
                kept.append(attrEl)
            }
        }
        return AttributeListSyntax(kept)
    }

    // MARK: - Attribute detection

    /// Mirrors `MainActorAnnotationRegex = @(?:_Concurrency\.)?MainActor`.
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

    /// Mirrors `DeprecatedAnnotationRegex = @available\(\s*\*\s*,\s*deprecated`.
    /// Whitespace-tolerant substring match on the attribute's argument list.
    private func hasDeprecatedAttribute(_ attributes: AttributeListSyntax) -> Bool {
        for element in attributes {
            guard case .attribute(let attribute) = element else { continue }
            guard attribute.attributeName.trimmedDescription == "available" else { continue }
            // Source-text scan on the attribute's full text to mirror the regex's
            // line-substring behavior. `available(*, deprecated, ...)` — match.
            let text = attribute.trimmedDescription
            // Strip "@available(" prefix and look for `*` then `deprecated` separated by whitespace+comma.
            // Use a simple regex equivalent: `\(\s*\*\s*,\s*deprecated`.
            if let regex = try? NSRegularExpression(pattern: "\\(\\s*\\*\\s*,\\s*deprecated", options: []),
               regex.firstMatch(in: text, options: [], range: NSRange(location: 0, length: text.utf16.count)) != nil {
                return true
            }
        }
        return false
    }

    // MARK: - Setter detection

    private func detectSetter(_ accessorBlock: AccessorBlockSyntax?) -> Bool {
        guard let accessorBlock = accessorBlock else { return false }
        switch accessorBlock.accessors {
        case .accessors(let list):
            for accessor in list {
                let kw = accessor.accessorSpecifier.text
                if kw == "set" {
                    return true
                }
            }
            return false
        case .getter:
            // `var x: Int { return 0 }` form — no setter syntax possible.
            return false
        }
    }

    // MARK: - Self return detection

    /// Mirrors `DetectSelfReturn`: `-> Self` (or `-> X Self` where the trailing
    /// trimmed text ends with `-> Self`) after the last paren of the signature.
    /// SwiftSyntax skips the manual paren-walk by using the structured returnClause.
    private func detectSelfReturn(returnClause: ReturnClauseSyntax?) -> Bool {
        guard let returnClause = returnClause else { return false }
        let text = returnClause.type.trimmedDescription
        return text == "Self" || text.hasSuffix(" Self")
    }

    // MARK: - Where-clause parsing

    /// Mirrors `ParseWhereConstraints`: split top-level requirements by comma, trim each.
    private func parseWhereConstraints(_ whereClause: GenericWhereClauseSyntax?) -> [String] {
        guard let whereClause = whereClause else { return [] }
        var constraints: [String] = []
        for requirement in whereClause.requirements {
            // `requirement.requirement.trimmedDescription` returns the source text of
            // the requirement (without the trailing comma). Mirrors what the regex
            // parser captures via `SplitParameters` on the `where ...` substring.
            let text = requirement.requirement.trimmedDescription
            if !text.isEmpty {
                constraints.append(text)
            }
        }
        return constraints
    }

    // MARK: - Printed name

    /// Mirrors `ExtractPrintedName`: `name(label1:label2:)` from the parameter list,
    /// using each parameter's FIRST name as its label (the external/argument label).
    /// Zero-param signatures are `name()`.
    private func buildFuncPrintedName(name: String, params: FunctionParameterListSyntax) -> String {
        if params.isEmpty { return "\(name)()" }
        var labels: [String] = []
        for p in params {
            labels.append(p.firstName.text)
        }
        return "\(name)(\(labels.map { "\($0):" }.joined()))"
    }

    // MARK: - Whitespace collapsing

    /// Collapses runs of whitespace (spaces, tabs, newlines) into a single space —
    /// mirrors the regex producer's multi-line `continuationLine += " " + trimmed`.
    /// Unlike `String.trimmingCharacters` this is whitespace-run-aware: every
    /// whitespace run becomes EXACTLY one space, and we then trim leading/trailing.
    private func collapseWhitespace(_ s: String) -> String {
        var result = ""
        var inWhitespace = false
        for scalar in s.unicodeScalars {
            // Treat only the typical regex whitespace as collapsible: space, tab, CR, LF.
            // Other Unicode whitespace is left intact since the regex producer wouldn't
            // collapse it either (input is .swiftinterface ASCII whitespace).
            if scalar == " " || scalar == "\t" || scalar == "\r" || scalar == "\n" {
                if !inWhitespace {
                    result.unicodeScalars.append(" ")
                    inWhitespace = true
                }
            } else {
                result.unicodeScalars.append(scalar)
                inWhitespace = false
            }
        }
        // Trim leading/trailing single space.
        if result.hasPrefix(" ") { result.removeFirst() }
        if result.hasSuffix(" ") { result.removeLast() }
        return result
    }
}
