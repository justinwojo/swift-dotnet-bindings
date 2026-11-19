// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks every `extension X { ... }` block and emits a flat,
/// module-context-free list of direct func/var members. The .NET-side facts
/// helper `SwiftInterfaceFacts.ResolveForeignExtensions` partitions this
/// list using `protocolNames` and `moduleTypeNames` from the ABI parse.
///
/// EXTRACTION CONTRACT:
///
/// 1. **Extension target gate**: same-line `{` AND a `[\w.]+` capture on the
///    extended type (`ExtensionDeclRegex` shape + `openBraces > 0` same-line
///    requirement). Extensions whose body opens on a later line, or whose
///    target uses parentheses/angle brackets/composition, are skipped — no
///    candidates emitted from those blocks.
///
/// 2. **Direct members only**: members at the extension body's outer brace
///    level. Members of nested types declared inside the extension body are
///    NOT emitted (`insideNestedType` skip). `visit(_: ExtensionDeclSyntax)`
///    returns `.skipChildren` after manually iterating `node.memberBlock.members`,
///    so descent never enters nested type members.
///
/// 3. **Decl kind filter**: `func` and `var` only. `let` is excluded
///    (`ExtensionVarRegex` uses literal `var`, not `(?:var|let)`). `init`,
///    `subscript`, `typealias`, `case`, etc. are excluded.
///
/// 4. **STRICT modifier shapes**:
///      * Func: `(?:public|open)\s+(?:static\s+|class\s+)?(?:mutating\s+)?func`. Only
///        `static` (or `class`, read as static) and `mutating` are allowed between access
///        and `func`, in that order. `public final func`, `public override func`,
///        `public nonisolated func` are rejected.
///      * Var: `(?:public|open)\s+(?:static\s+)?var`. Only `static` is allowed
///        between access and `var`. `public lazy var`, `public weak var` rejected.
///      * Modifiers BEFORE access are tolerated (unanchored scan).
///
/// 5. **Extended type: verbatim**: `node.extendedType.dottedDescription` is
///    used as `extendedTypeName` (e.g., `"UIKit.UIView"`, `"Mod.MyProto"`,
///    `"MyType"`). Partitioning happens .NET-side via the first-dot rule.
///
/// 6. **Where constraints**: each top-level requirement in the extension's
///    `genericWhereClause` is emitted as its source-text-trimmed description.
///    Order is preserved. Empty list when there's no where clause.
///
/// 7. **`@MainActor` detection**: any attribute named `MainActor` or
///    `_Concurrency.MainActor` on the func/var decl flips `isMainActorIsolated`.
///    SwiftSyntax handles pending (pre-decl-line) attributes automatically
///    because attributes are part of the FunctionDeclSyntax /
///    VariableDeclSyntax node regardless of source line breaks.
///
/// 8. **`@available(*, deprecated, ...)` detection**: any attribute whose
///    arguments match `\(\s*\*\s*,\s*deprecated` flips `isDeprecated`.
///
/// 9. **Self return detection**: `-> Self` (or a trailing `-> Self`) at the
///    trailing edge of the func's signature. `Self?` does not match (the
///    check uses `EndsWith("-> Self")`).
///
/// 10. **Setter detection (var only)**: any `set` accessor in the binding's
///     accessor block, including `nonmutating set` and `set` with attributes.
///     Detected by scanning the accessor block for `set`/`nonmutating set`.
///
/// 11. **Method name**: `node.name.text` for funcs (skipped if non-identifier
///     like an operator symbol — the word-identifier gate `\w+` rejects those),
///     property name for vars (the binding's identifier text, backtick-stripped
///     to match the `(\w+)` capture in `ExtensionVarRegex`).
///
/// 12. **Printed name**: Swift-canonical `name(label1:label2:)` — first-name
///     of each parameter. Property printedName is just the property name.
///
/// 14. **Dropped candidates**: every PUBLIC (`public`/`open`) declaration the rules above do
///     not take — an operator, initializer, subscript, typealias or nested type, a member with
///     modifiers or effects outside the strict shapes, a backticked name, and every member of an
///     extension whose header fails rule 1 — is recorded in `droppedExtensionCandidates` with
///     its kind, name and the reason, so the host reports it instead of losing it silently.
///
/// 13. **RawSignature**: the source text of the decl, with newline+leading-
///     whitespace runs collapsed into single spaces, so consumer patterns
///     (`func name<`, `\basync\b`, `\bthrows\b`, `" where "` substring) fire
///     identically on multi-line and single-line signatures. The RawSignature
///     is built from the source line containing the access modifier: attributes
///     on the SAME line as `public`/`open` are included while attributes on
///     EARLIER lines are dropped (they were tracked separately as booleans via
///     `pendingMainActor`/`pendingDeprecated`). Attribute-line == access-modifier-
///     line → keep; otherwise drop.
final class ExtensionsWalker: SyntaxVisitor {
    private(set) var extensionMemberCandidates: [ExtensionMemberCandidateInfo] = []
    private(set) var droppedExtensionCandidates: [DroppedExtensionCandidateInfo] = []
    /// The `where` clause of the extension being walked, so two members with one header in
    /// differently constrained extensions stay two records.
    private var currentWhereClause = ""

    private let converter: SourceLocationConverter

    init(converter: SourceLocationConverter) {
        self.converter = converter
        super.init(viewMode: .sourceAccurate)
    }

    /// The candidates and the public declarations the walker saw and did not take.
    static func parse(filePath: String, source: String)
        -> (candidates: [ExtensionMemberCandidateInfo], dropped: [DroppedExtensionCandidateInfo]) {
        let tree = Parser.parse(source: source)
        let converter = SourceLocationConverter(fileName: filePath, tree: tree)
        let walker = ExtensionsWalker(converter: converter)
        walker.walk(tree)
        return (walker.extensionMemberCandidates, walker.droppedExtensionCandidates)
    }

    // MARK: - Extension entry point

    override func visit(_ node: ExtensionDeclSyntax) -> SyntaxVisitorContinueKind {
        // The constraints that tell apart identical declarations of differently constrained
        // extensions: generic arguments on the extended type, then the `where` clause.
        let headerArguments = Self.genericArguments(node.extendedType.dottedDescription)
        let whereText = node.genericWhereClause.map { collapseWhitespace($0.trimmedDescription) } ?? ""
        currentWhereClause = [headerArguments, whereText].filter { !$0.isEmpty }.joined(separator: " ")
        // Same-line `{` gate (`openBraces > 0` requirement).
        guard RegexShape.opensOnSameLine(keyword: node.extensionKeyword,
                                          leftBrace: node.memberBlock.leftBrace,
                                          converter: converter) else {
            recordDroppedMembers(node.memberBlock.members,
                                 extendedType: Self.withoutGenericArguments(node.extendedType.dottedDescription),
                                 reason: "the extension's body opens on a line after its header, which the interface reader does not read")
            return .visitChildren
        }
        // `[\w.]+` capture on extended type (`ExtensionDeclRegex` shape).
        let extendedType = node.extendedType.dottedDescription
        guard RegexShape.isWordOrDotOnly(extendedType) else {
            recordDroppedMembers(node.memberBlock.members,
                                 extendedType: Self.withoutGenericArguments(extendedType),
                                 reason: "the extension names its type with generic arguments or another form the interface reader does not read")
            return .visitChildren
        }

        let whereConstraints = parseWhereConstraints(node.genericWhereClause)

        // Walk DIRECT members only — never recurse into nested types declared
        // inside the extension body (`insideNestedType` skip).
        // `#if … #else … #endif` blocks are flattened: directive lines are
        // skipped and every clause's elements are processed as direct members.
        processExtensionMembers(node.memberBlock.members,
                                 extendedType: extendedType,
                                 whereConstraints: whereConstraints)

        // Don't descend — extension's children handled manually above. This
        // also skips nested type members (direct-only semantics).
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
                // `#if` / `#else` / `#endif` directive lines are skipped; every
                // clause's elements are collected as direct members regardless of branch.
                for clause in ifConfigDecl.clauses {
                    if let elements = clause.elements?.as(MemberBlockItemListSyntax.self) {
                        processExtensionMembers(elements,
                                                 extendedType: extendedType,
                                                 whereConstraints: whereConstraints)
                    }
                }
            }
            else {
                // Initializers, subscripts, typealiases and nested types are not extension
                // candidates; a public one is recorded so it is reported, not lost.
                recordDroppedDecl(member.decl, extendedType: extendedType, reason: nil)
            }
        }
    }

    // MARK: - Dropped declarations

    /// Records every public declaration of an extension body the walker does not read at all.
    private func recordDroppedMembers(_ members: MemberBlockItemListSyntax, extendedType: String, reason: String) {
        for member in members {
            if let ifConfigDecl = member.decl.as(IfConfigDeclSyntax.self) {
                for clause in ifConfigDecl.clauses {
                    if let elements = clause.elements?.as(MemberBlockItemListSyntax.self) {
                        recordDroppedMembers(elements, extendedType: extendedType, reason: reason)
                    }
                }
                continue
            }
            recordDroppedDecl(member.decl, extendedType: extendedType, reason: reason)
        }
    }

    /// Records `decl` as a dropped candidate when it is public, with `reason` or, when nil, the
    /// reason its kind is not read.
    private func recordDroppedDecl(_ decl: DeclSyntax, extendedType: String, reason: String?) {
        let kind: String
        let name: String
        let printedName: String
        let modifiers: DeclModifierListSyntax
        let kindReason: String
        if let f = decl.as(FunctionDeclSyntax.self) {
            // An escaped name (`` `repeat` ``) is an identifier, not an operator.
            let unescaped = Self.withoutBackticks(f.name.text)
            let isOperator = !(unescaped.first.map { $0.isLetter || $0.isNumber || $0 == "_" } ?? false)
            kind = isOperator ? "operator" : "method"
            name = unescaped
            // An operator's parameters carry no argument labels; the ABI prints `+(_:_:)`.
            printedName = isOperator
                ? "\(name)(\(String(repeating: "_:", count: f.signature.parameterClause.parameters.count)))"
                : buildFuncPrintedName(name: name, params: f.signature.parameterClause.parameters)
            modifiers = f.modifiers
            kindReason = isOperator
                ? "operators declared in an extension are not read by the foreign-extension path"
                : unescaped != f.name.text
                    ? "its name is escaped with backticks, which the foreign-extension path does not read"
                    : "its modifiers are not a shape the foreign-extension path reads"
        } else if let v = decl.as(VariableDeclSyntax.self) {
            guard let binding = v.bindings.first,
                  let pattern = binding.pattern.as(IdentifierPatternSyntax.self) else { return }
            kind = "property"
            name = Self.withoutBackticks(pattern.identifier.text)
            printedName = name
            modifiers = v.modifiers
            kindReason = "its modifiers are not a shape the foreign-extension path reads"
        } else if let i = decl.as(InitializerDeclSyntax.self) {
            kind = "initializer"
            name = "init"
            printedName = buildFuncPrintedName(name: name, params: i.signature.parameterClause.parameters)
            modifiers = i.modifiers
            kindReason = "initializers declared in an extension of another module's type are not bound"
        } else if let s = decl.as(SubscriptDeclSyntax.self) {
            kind = "subscript"
            name = "subscript"
            // A subscript parameter has an argument label only when it spells one apart from
            // its name; the ABI prints `subscript(_:)` for `subscript(i: Int)`.
            let labels = s.parameterClause.parameters.map { p in
                p.secondName != nil && p.firstName.text != "_" ? p.firstName.text : "_"
            }
            printedName = "subscript(\(labels.map { "\($0):" }.joined()))"
            modifiers = s.modifiers
            kindReason = "subscripts declared in an extension of another module's type are not bound"
        } else if let t = decl.as(TypeAliasDeclSyntax.self) {
            kind = "typealias"
            name = Self.withoutBackticks(t.name.text)
            printedName = name
            modifiers = t.modifiers
            kindReason = "typealiases declared in an extension of another module's type are not bound"
        } else if let nominal = decl.asProtocol(NamedDeclSyntax.self),
                  let withModifiers = decl.asProtocol(WithModifiersSyntax.self),
                  decl.is(StructDeclSyntax.self) || decl.is(ClassDeclSyntax.self) || decl.is(EnumDeclSyntax.self)
                    || decl.is(ActorDeclSyntax.self) || decl.is(ProtocolDeclSyntax.self) {
            kind = "type"
            name = Self.withoutBackticks(nominal.name.text)
            printedName = name
            modifiers = withModifiers.modifiers
            kindReason = "types nested in an extension of another module's type are not bound"
        } else {
            return
        }
        guard Self.isPublic(modifiers) else { return }
        droppedExtensionCandidates.append(DroppedExtensionCandidateInfo(
            extendedTypeName: extendedType,
            kind: kind,
            name: name,
            printedName: printedName,
            rawSignature: collapseWhitespace(Self.signatureText(decl)),
            isStatic: hasModifier(modifiers, "static") || hasModifier(modifiers, "class"),
            whereClause: currentWhereClause,
            reason: reason ?? kindReason
        ))
    }

    private static func isPublic(_ modifiers: DeclModifierListSyntax) -> Bool {
        modifiers.contains { ($0.name.text == "public" || $0.name.text == "open") && $0.detail == nil }
    }

    /// The declaration's header without its body, so the record stays one line.
    private static func signatureText(_ decl: DeclSyntax) -> String {
        if let f = decl.as(FunctionDeclSyntax.self) { return f.with(\.body, nil).with(\.attributes, AttributeListSyntax([])).dottedDescription }
        if let i = decl.as(InitializerDeclSyntax.self) { return i.with(\.body, nil).with(\.attributes, AttributeListSyntax([])).dottedDescription }
        if let s = decl.as(SubscriptDeclSyntax.self) { return s.with(\.accessorBlock, nil).with(\.attributes, AttributeListSyntax([])).dottedDescription }
        if let v = decl.as(VariableDeclSyntax.self) { return v.with(\.attributes, AttributeListSyntax([])).dottedDescription }
        if let t = decl.as(TypeAliasDeclSyntax.self) { return t.with(\.attributes, AttributeListSyntax([])).dottedDescription }
        if let nominal = decl.asProtocol(NamedDeclSyntax.self) { return nominal.name.text }
        return decl.dottedDescription
    }

    /// `` `default` `` → `default`: the identifier the ABI names.
    static func withoutBackticks(_ text: String) -> String {
        text.count >= 2 && text.hasPrefix("`") && text.hasSuffix("`") ? String(text.dropFirst().dropLast()) : text
    }

    /// `Mod.Box<Swift.Int>` → `Mod.Box`: generic arguments removed at every level.
    /// The generic argument clause of an extended type (`<Swift.Int32>` of `Swift.Array<Swift.Int32>`),
    /// whitespace collapsed, or "" when it has none.
    static func genericArguments(_ text: String) -> String {
        guard let open = text.firstIndex(of: "<") else { return "" }
        return text[open...].split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")
    }

    static func withoutGenericArguments(_ text: String) -> String {
        var result = ""
        var depth = 0
        for ch in text {
            if ch == "<" { depth += 1; continue }
            if ch == ">" { if depth > 0 { depth -= 1 }; continue }
            if depth == 0 { result.append(ch) }
        }
        return result.trimmingCharacters(in: .whitespaces)
    }

    // MARK: - Func member

    private func handleFunc(_ node: FunctionDeclSyntax,
                             extendedType: String,
                             whereConstraints: [String]) {
        // Operator funcs — `\w+` capture rejects them.
        guard RegexShape.isWordIdentifier(node.name.text) else {
            recordDroppedDecl(DeclSyntax(node), extendedType: extendedType, reason: nil)
            return
        }
        // `ExtensionFuncRegex` strict shape: (public|open) + optional static/class + optional (mutating|consuming|borrowing) + func.
        guard matchesExtensionFuncShape(node.modifiers) else {
            recordDroppedDecl(DeclSyntax(node), extendedType: extendedType, reason: nil)
            return
        }

        let methodName = node.name.text
        let printedName = buildFuncPrintedName(name: methodName,
                                                params: node.signature.parameterClause.parameters)
        let anchorLine = accessModifierLine(node.modifiers)
        let detached = node.detached.with(\.attributes, sameLineAttributes(node.attributes, anchorLine: anchorLine))
        let rawSignature = collapseWhitespace(detached.dottedDescription)
        let returnsSelf = detectSelfReturn(returnClause: node.signature.returnClause)
        let isMainActor = hasMainActorAttribute(node.attributes)
        // A `class func` is a type member as a `static func` is; only a subclass's override
        // tells them apart, and the foreign-extension path binds neither.
        let isStatic = hasModifier(node.modifiers, "static") || hasModifier(node.modifiers, "class")
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
        guard matchesExtensionVarShape(node.modifiers, bindingKeyword: node.bindingSpecifier) else {
            recordDroppedDecl(DeclSyntax(node), extendedType: extendedType, reason: nil)
            return
        }
        // Pick the first binding's identifier name (swiftinterface always produces
        // single-binding `var`/`let`; multi-binding `var a, b: Int` is never produced).
        guard let binding = node.bindings.first,
              let pattern = binding.pattern.as(IdentifierPatternSyntax.self) else { return }
        // `ExtensionVarRegex`'s `(\w+)` capture fails on backtick-escaped names
        // entirely — emit nothing. SwiftSyntax's `.text` strips the backticks, so
        // the source representation via `trimmedDescription` is used to detect
        // the escape.
        let sourceText = pattern.identifier.dottedDescription
        if sourceText.hasPrefix("`") {
            recordDroppedDecl(DeclSyntax(node), extendedType: extendedType,
                              reason: "its name is escaped with backticks, which the foreign-extension path does not read")
            return
        }
        let propertyName = pattern.identifier.text
        guard RegexShape.isWordIdentifier(propertyName) else {
            recordDroppedDecl(DeclSyntax(node), extendedType: extendedType,
                              reason: "its name is not a word identifier the foreign-extension path reads")
            return
        }

        let anchorLine = accessModifierLine(node.modifiers)
        let rawSignature = buildVarRawSignature(node, binding: binding, anchorLine: anchorLine)
        let isMainActor = hasMainActorAttribute(node.attributes)
        let isStatic = hasModifier(node.modifiers, "static")
        let isDeprecated = hasDeprecatedAttribute(node.attributes)
        let hasSetter = detectSetter(binding.accessorBlock)
        // Effectful getters (`{ get throws }` / `{ get async }`) can't be surfaced
        // as synthetic free-function getter wrappers — the wrapper would have to
        // honor the effect, which the synthetic-MethodDecl getter pipeline doesn't
        // model. Drop them at the source, where the full accessor AST is available:
        // the swiftinterface printer emits accessors multi-line (the `throws`/`async`
        // keyword on its own line), and `buildVarRawSignature` keeps only the first
        // accessor line, so a downstream raw-signature scan can't see the effect.
        if detectEffectfulGetter(binding.accessorBlock) {
            recordDroppedDecl(DeclSyntax(node), extendedType: extendedType,
                              reason: "its getter is async or throws, which the foreign-extension path does not bind")
            return
        }

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

    /// `ExtensionFuncRegex` shape: `(?:public|open)\s+(?:static\s+)?(?:(?:mutating|consuming|borrowing)\s+)?func`.
    /// STRICT order — only `static` then one of {mutating, consuming, borrowing} allowed between
    /// access and `func`. The ownership modifiers appear on `~Copyable` extension methods.
    private func matchesExtensionFuncShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard RegexShape.advanceToAccess(&iter, ["public", "open"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "static" || mod.name.text == "class", mod.detail == nil {
            current = iter.next()
        }
        if let mod = current, ["mutating", "consuming", "borrowing"].contains(mod.name.text), mod.detail == nil {
            current = iter.next()
        }
        return current == nil
    }

    /// `ExtensionVarRegex` shape: `(?:public|open)\s+(?:static\s+)?var`.
    /// STRICT order — only `static` allowed between access and `var`. `let` is rejected
    /// (the pattern uses literal `var`, not `(?:var|let)`).
    private func matchesExtensionVarShape(_ modifiers: DeclModifierListSyntax,
                                            bindingKeyword: TokenSyntax) -> Bool {
        // `ExtensionVarRegex` requires `var`, not `let` — match it.
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

    // MARK: - Same-line attribute filter (RawSignature)

    /// Source line of the first `public`/`open` modifier, or -1. RawSignature
    /// is the trimmed line containing this modifier — attributes on a different
    /// source line are excluded (they were tracked separately as booleans via
    /// `pendingMainActor`/`pendingDeprecated`).
    private func accessModifierLine(_ modifiers: DeclModifierListSyntax) -> Int {
        for m in modifiers where (m.name.text == "public" || m.name.text == "open") && m.detail == nil {
            return converter.location(for: m.positionAfterSkippingLeadingTrivia).line
        }
        return -1
    }

    /// Builds RawSignature for a var. When the accessor block spans multiple source
    /// lines, only the var's first line is captured — which may include `{` alone OR
    /// `{` followed by accessors that share the same source line (e.g. `{ get` then
    /// `set` on the next line). Accessor body lines past the first are processed
    /// separately for setter detection, NOT joined into RawSignature. When the
    /// accessor block fits on one line (`{ get set }`), the entire trimmed line
    /// including the body is captured.
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
        var rawSignature = collapseWhitespace(detached.dottedDescription)
        if let suffix = multilineAccessorFirstLine {
            rawSignature += " " + suffix
        }
        return rawSignature
    }

    /// Filters `attributes` to only those whose source line equals `anchorLine`.
    /// `#if`/`#endif`-wrapped attributes are kept conservatively — they're vanishingly
    /// rare in `.swiftinterface` output and their line-assignment is undefined.
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
            let typeName = attribute.attributeName.dottedDescription
            if typeName == "MainActor" || typeName == "_Concurrency.MainActor" {
                return true
            }
        }
        return false
    }

    /// Whitespace-tolerant match for `@available(*, deprecated, ...)`:
    /// pattern `\(\s*\*\s*,\s*deprecated` on the attribute's argument list.
    private func hasDeprecatedAttribute(_ attributes: AttributeListSyntax) -> Bool {
        for element in attributes {
            guard case .attribute(let attribute) = element else { continue }
            guard attribute.attributeName.dottedDescription == "available" else { continue }
            // Source-text scan on the attribute's full text.
            // `available(*, deprecated, ...)` — match.
            let text = attribute.dottedDescription
            // Look for `*` then `deprecated` separated by whitespace+comma: `\(\s*\*\s*,\s*deprecated`.
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

    // MARK: - Effectful-getter detection

    /// True when any accessor carries an effect specifier (`async` and/or
    /// `throws`) — an effectful `{ get throws }` / `{ get async }` getter.
    /// Inspects the structured accessor AST (not the truncated raw signature),
    /// so it is robust to the multi-line accessor blocks the swiftinterface
    /// printer emits, where `buildVarRawSignature` keeps only the first line.
    private func detectEffectfulGetter(_ accessorBlock: AccessorBlockSyntax?) -> Bool {
        guard let accessorBlock = accessorBlock else { return false }
        switch accessorBlock.accessors {
        case .accessors(let list):
            for accessor in list {
                // Scope to the `get` accessor (mirroring detectSetter's `kw == "set"`):
                // effect specifiers are get-only in Swift today, but checking the
                // specifier keeps this matched to its name and robust if effectful
                // setters ever land — a read-write property is dropped by the setter
                // gate regardless.
                guard accessor.accessorSpecifier.text == "get" else { continue }
                if let effects = accessor.effectSpecifiers,
                   effects.asyncSpecifier != nil || effects.throwsClause != nil {
                    return true
                }
            }
            return false
        case .getter:
            // `var x: Int { return 0 }` single-expression form — no accessor
            // syntax, so no effect specifier is expressible.
            return false
        }
    }

    // MARK: - Self return detection

    /// Mirrors `DetectSelfReturn`: `-> Self` (or `-> X Self` where the trailing
    /// trimmed text ends with `-> Self`) after the last paren of the signature.
    /// SwiftSyntax skips the manual paren-walk by using the structured returnClause.
    private func detectSelfReturn(returnClause: ReturnClauseSyntax?) -> Bool {
        guard let returnClause = returnClause else { return false }
        let text = returnClause.type.dottedDescription
        return text == "Self" || text.hasSuffix(" Self")
    }

    // MARK: - Where-clause parsing

    /// Mirrors `ParseWhereConstraints`: split top-level requirements by comma, trim each.
    private func parseWhereConstraints(_ whereClause: GenericWhereClauseSyntax?) -> [String] {
        guard let whereClause = whereClause else { return [] }
        var constraints: [String] = []
        for requirement in whereClause.requirements {
            // `requirement.requirement.dottedDescription` returns the source text of
            // the requirement without the trailing comma.
            let text = requirement.requirement.dottedDescription
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

    /// Collapses runs of whitespace (spaces, tabs, newlines) into a single space.
    /// Unlike `String.trimmingCharacters` this is whitespace-run-aware: every
    /// whitespace run becomes EXACTLY one space, and we then trim leading/trailing.
    private func collapseWhitespace(_ s: String) -> String {
        var result = ""
        var inWhitespace = false
        for scalar in s.unicodeScalars {
            // Treat only space, tab, CR, LF as collapsible (`.swiftinterface` uses
            // ASCII whitespace; other Unicode whitespace is left intact).
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

/// Names of the protocols a `Self : Q` where-clause constrains `Self` to, with any leading module
/// component stripped so they compare against the module's own protocol-name set.
///
/// Only a constraint on `Self` itself qualifies: `Self.RawValue : Q` constrains an associated type
/// and `Self == C` is a same-type constraint, and neither makes the extension's members witnesses
/// for a requirement of anything. Mirrors the attribution the .NET-side defaults index performs on
/// the same constraint text.
func selfConstraintProtocolNames(_ whereConstraints: [String]) -> [String] {
    var names: [String] = []
    for constraint in whereConstraints {
        guard let colon = constraint.firstIndex(of: ":") else { continue }
        guard constraint[constraint.startIndex..<colon].trimmingCharacters(in: .whitespaces) == "Self" else { continue }

        // A composition (`Self : First & Second`) constrains Self to every member.
        let rhs = constraint[constraint.index(after: colon)...]
        for part in rhs.split(separator: "&") {
            let name = part.trimmingCharacters(in: .whitespaces)
            guard !name.isEmpty else { continue }
            if let firstDot = name.firstIndex(of: ".") {
                names.append(String(name[name.index(after: firstDot)...]))
            } else {
                names.append(name)
            }
        }
    }
    return names
}
