// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks func/init declarations and surfaces four signature-derived facts:
///   * `parameterNames`         — "Key -> [internalName]" (always, any access)
///   * `defaultParameterValues` — "Key -> [default?]"     (public/open only)
///   * `autoclosureParameters`  — "Key -> [bool]"         (public/open only)
///   * `variadicMembers`        — Set<"Key">              (public/open only)
///
/// PARITY CONTRACT WITH:
///   * `SwiftInterfaceAccessParser.GetParameterNames`         (line 3005)
///   * `SwiftInterfaceAccessParser.GetDefaultParameterValues` (line 3794)
///   * `SwiftInterfaceAccessParser.GetAutoclosureParameters`  (line 3846)
///   * `SwiftInterfaceAccessParser.GetVariadicMembers`        (line 3975)
///
/// 1. **Decl kinds**: `func` and `init` (including failable `init?`). Subscript,
///    var, and other decls do not contribute to these facts.
///
/// 2. **printedName**: Swift-canonical `name(label1:label2:)`. Per param, the
///    "label" is the FIRST name (i.e. the external/argument label). Unlabeled
///    forms (`_`) keep the literal `_`. Single-name params (`name: Type`) use
///    that name as the label. Zero-param signatures are `name()`. For init the
///    name is always `init` regardless of `?`.
///
/// 3. **KEY-SHAPE DIVERGENCE — critical parity trap**:
///    Two independent dimensions vary across the four fact families.
///
///    a) Per-frame prefix (extension type name):
///      * `parameterNames` uses the LAST dot-component of `extension Foo.Bar.Baz`
///        as scope (`Baz`), matching the regex's `LastIndexOf('.')` strategy.
///      * `defaultParameterValues` / `autoclosureParameters` / `variadicMembers`
///        all use FIRST-dot-strip (`Bar.Baz`), matching the
///        `SwiftInterfaceContextTracker` strategy.
///
///    b) Number of frames included in the scope key:
///      * `parameterNames` keeps ONLY the immediate parent (top of stack) — the
///        regex emits `typeStack.Peek().Name + "." + printedName` and the consumer
///        (`SwiftABIParser.CreateMethodDecl`) looks up by `parentDecl.Name + "."`
///        (the IMMEDIATE parent's simple name). For nested
///        `enum AES { struct KeyWrap { ... } }`, the parameterNames key is
///        `KeyWrap.wrap(_:using:)`, NOT `AES.KeyWrap.wrap(_:using:)`.
///      * `defaultParameterValues` / `autoclosureParameters` / `variadicMembers`
///        use the FULL qualified chain (`BuildTypeQualifiedPath` on the consumer
///        side, `QualifiedTypePath = string.Join(".", typeStack.Reverse())` on
///        the regex side). For the same nesting, the key is
///        `AES.KeyWrap.wrap(_:using:)`.
///
///    The walker maintains both prefixes per scope frame so each fact lands on
///    its own correct key.
///
/// 4. **Free functions**: bare key (no type prefix). Both fact families emit.
///
/// 5. **Access modifier filter**:
///      * `parameterNames`: ANY access (public, open, internal, bare protocol
///        requirement) — regex's `AnyFuncRegex` is unanchored AND access-optional,
///        so it matches every `func name(` / `init(` line. No shape gate needed.
///      * Other three facts: STRICT public-shape gate mirroring
///        `PublicFuncRegex = (public|open)\s+(?:final\s+)?(?:static\s+|class\s+)?(?:mutating\s+)?func`
///        and `PublicInitRegex = (public|open)\s+(?:convenience\s+)?init`. Lines like
///        `public nonisolated func`, `public override func`, or `public required init`
///        do NOT contribute (regex parity — those regexes don't match either).
///
/// 6. **Inclusion guards** — only emit when the per-fact list has the relevant
///    signal:
///      * `parameterNames`: emit when there is at least one parameter (the regex
///        skips zero-param signatures since `internalNames.Count > 0` gate).
///      * `defaultParameterValues`: emit when at least one parameter has a default.
///      * `autoclosureParameters`: emit when at least one parameter is autoclosure.
///      * `variadicMembers`: add to set when at least one parameter is variadic.
///
/// 7. **Default value extraction**: regex captures everything after `= ` (with
///    surrounding spaces) up to the next top-level comma — equivalent to the
///    SwiftSyntax `param.defaultValue.value.trimmedDescription`.
///
/// 8. **Autoclosure detection**: regex does `typeStr.Contains("@autoclosure")`
///    on the param's type substring (after the colon). We mirror by checking the
///    parameter type node's printed text — including stripping the default value
///    portion (`paramType.trimmedDescription`).
///
/// 9. **Variadic detection**: `param.ellipsis != nil`. Equivalent to regex's
///    `typeStr.EndsWith("...")` after stripping the default-value tail.
final class SignatureFactsWalker: SyntaxVisitor {
    private(set) var parameterNames: [String: [String]] = [:]
    private(set) var defaultParameterValues: [String: [String?]] = [:]
    private(set) var autoclosureParameters: [String: [Bool]] = [:]
    private(set) var variadicMembers: [String] = []

    private struct Scope {
        /// Used for the parameterNames key (regex `LastIndexOf` strategy).
        let paramNamesKey: String
        /// Used for the defaults/autoclosure/variadic keys (regex tracker strategy).
        let trackerKey: String
    }
    private var scopeStack: [Scope] = []

    /// Parallel stack: each visited type/extension records whether it actually
    /// pushed a `Scope` on the main stack. Mirrors the regex tracker's gated push
    /// (TypeDeclRegex match + same-line `{`); types/extensions that fail the gate
    /// don't appear in the parser's `typeStack`, so their members must NOT be
    /// keyed by the type scope. Each `visitPost` pops only when its visit pushed.
    private var scopePushed: [Bool] = []

    private let converter: SourceLocationConverter

    init(converter: SourceLocationConverter) {
        self.converter = converter
        super.init(viewMode: .sourceAccurate)
    }

    static func parse(filePath: String, source: String) -> (
        parameterNames: [String: [String]],
        defaultParameterValues: [String: [String?]],
        autoclosureParameters: [String: [Bool]],
        variadicMembers: [String]
    ) {
        let tree = Parser.parse(source: source)
        let converter = SourceLocationConverter(fileName: filePath, tree: tree)
        let walker = SignatureFactsWalker(converter: converter)
        walker.walk(tree)
        return (walker.parameterNames, walker.defaultParameterValues,
                walker.autoclosureParameters, walker.variadicMembers)
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
            scopeStack.append(Scope(paramNamesKey: name, trackerKey: name))
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
    /// access only an optional `final` is allowed. `public indirect enum`, etc.
    /// fail this gate and so are not pushed onto the scope stack.
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

    // MARK: - Extensions (DIVERGENT push: lastDot vs firstDotStrip; same-line `{` gated)

    override func visit(_ node: ExtensionDeclSyntax) -> SyntaxVisitorContinueKind {
        // Extensions have no modifier filter in regex (`ExtensionDeclRegex` matches
        // any line containing `extension <type>`), but the tracker still requires
        // the same-line `{` gate, AND the type capture is `[\w.]+`. Mirror all
        // three constraints.
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

        let firstStripped: String
        if let firstDot = qualified.firstIndex(of: ".") {
            firstStripped = String(qualified[qualified.index(after: firstDot)...])
        } else {
            firstStripped = qualified
        }

        scopeStack.append(Scope(paramNamesKey: lastComponent, trackerKey: firstStripped))
        scopePushed.append(true)
        return .visitChildren
    }
    override func visitPost(_ node: ExtensionDeclSyntax) { leaveTypeScope() }

    // MARK: - Members

    override func visit(_ node: FunctionDeclSyntax) -> SyntaxVisitorContinueKind {
        // Operator functions (`public static func == (...)`) have a `name` token
        // whose `.text` is the operator symbol. The regex producer's `(\w+)` capture
        // in AnyFuncRegex / PublicFuncRegex skips these (`\w` excludes `=`, `+`, etc.).
        // Mirror by skipping non-identifier names.
        guard isIdentifierName(node.name.text) else { return .skipChildren }
        emitForFunctionLike(
            funcName: node.name.text,
            params: node.signature.parameterClause.parameters,
            modifiers: node.modifiers,
            isInit: false
        )
        return .skipChildren
    }

    override func visit(_ node: InitializerDeclSyntax) -> SyntaxVisitorContinueKind {
        emitForFunctionLike(
            funcName: "init",
            params: node.signature.parameterClause.parameters,
            modifiers: node.modifiers,
            isInit: true
        )
        return .skipChildren
    }

    // MARK: - Helpers

    private func emitForFunctionLike(
        funcName: String,
        params: FunctionParameterListSyntax,
        modifiers: DeclModifierListSyntax,
        isInit: Bool
    ) {
        let (printedName, internalNames, defaults, autoclosures, variadicHit) = analyze(funcName: funcName, params: params)
        // parameterNames: TOP-OF-STACK only (immediate parent simple name).
        // Other facts: FULL chain joined with dots.
        let paramNamesKey = makeParamNamesKey(printedName: printedName)
        let trackerKey = makeKey(printedName: printedName, useTracker: true)

        // ParameterNames — any access modifier (or bare protocol requirement).
        // Regex's `AnyFuncRegex` is unanchored AND access-optional, so it matches
        // every `func name(` / `init(` line; FunctionDeclSyntax / InitializerDeclSyntax
        // covers exactly the same set, so no shape gate is needed here.
        // Regex skips zero-param signatures because internalNames.Count > 0 gate;
        // mirror by only emitting when the param list is non-empty.
        if !internalNames.isEmpty {
            parameterNames[paramNamesKey] = internalNames
        }

        // Defaults / Autoclosure / Variadic — STRICT public-shape gate.
        // Regex `PublicFuncRegex` / `PublicInitRegex` (via SwiftInterfaceContextTracker
        // line 30, 42) accept only specific modifier shapes between `public/open`
        // and `func`/`init`. Mirror those shapes precisely so e.g.
        // `public nonisolated func` and `public required init` do NOT contribute.
        let publicShapeOk = isInit
            ? matchesPublicInitShape(modifiers)
            : matchesPublicFuncShape(modifiers)
        if !publicShapeOk { return }

        if defaults.contains(where: { $0 != nil }) {
            defaultParameterValues[trackerKey] = defaults
        }
        if autoclosures.contains(where: { $0 }) {
            autoclosureParameters[trackerKey] = autoclosures
        }
        if variadicHit {
            variadicMembers.append(trackerKey)
        }
    }

    /// Walks the parameter list once, building all five outputs in step.
    private func analyze(
        funcName: String,
        params: FunctionParameterListSyntax
    ) -> (
        printedName: String,
        internalNames: [String],
        defaults: [String?],
        autoclosures: [Bool],
        variadicHit: Bool
    ) {
        if params.isEmpty {
            return ("\(funcName)()", [], [], [], false)
        }

        var labels: [String] = []
        var internals: [String] = []
        var defaults: [String?] = []
        var autoclosures: [Bool] = []
        var variadicHit = false

        for param in params {
            // Label = first token before colon. SwiftSyntax: param.firstName always present.
            labels.append(param.firstName.text)

            // Internal name = secondName when distinct, else firstName. Mirrors the
            // regex's words[1] (when 2+ words) / words[0] (when 1 word) semantics.
            if let second = param.secondName {
                internals.append(second.text)
            } else {
                internals.append(param.firstName.text)
            }

            // Default value: raw expression, trimmed.
            if let dv = param.defaultValue {
                defaults.append(dv.value.trimmedDescription)
            } else {
                defaults.append(nil)
            }

            // Autoclosure: regex does `typeStr.Contains("@autoclosure")`. Mirror by
            // checking the type's printed text (which preserves the attribute).
            autoclosures.append(param.type.trimmedDescription.contains("@autoclosure"))

            // Variadic: structured ellipsis token.
            if param.ellipsis != nil {
                variadicHit = true
            }
        }

        let printed = "\(funcName)(\(labels.map { "\($0):" }.joined()))"
        return (printed, internals, defaults, autoclosures, variadicHit)
    }

    private func makeKey(printedName: String, useTracker: Bool) -> String {
        if scopeStack.isEmpty { return printedName }
        let prefix = scopeStack.map { useTracker ? $0.trackerKey : $0.paramNamesKey }
            .joined(separator: ".")
        return "\(prefix).\(printedName)"
    }

    /// Builds the `parameterNames` key. Mirrors the regex parser's
    /// `typeStack.Peek().Name + "." + printedName` and the ABI consumer's
    /// `parentDecl.Name + "." + printedName` lookup — only the IMMEDIATE
    /// parent type's simple name is used as the prefix. Module-level free
    /// functions get the bare printedName.
    private func makeParamNamesKey(printedName: String) -> String {
        guard let top = scopeStack.last else { return printedName }
        return "\(top.paramNamesKey).\(printedName)"
    }

    // MARK: - Modifier-shape matchers (regex parity)

    /// Iterator helper: consume modifiers until we find one whose `name.text` is in
    /// `accessTexts` AND whose `detail` is nil. Returns `true` and leaves the iterator
    /// positioned just after the access modifier; `false` if no matching access
    /// modifier exists. Mirrors the regex's unanchored search for the access keyword
    /// (modifiers BEFORE access are tolerated; modifiers AFTER must satisfy the
    /// per-shape allow-list in order).
    private func advanceToAccess(_ iter: inout DeclModifierListSyntax.Iterator, _ accessTexts: [String]) -> Bool {
        while let mod = iter.next() {
            if accessTexts.contains(mod.name.text) && mod.detail == nil {
                return true
            }
        }
        return false
    }

    /// `PublicFuncRegex` shape (strict, used by tracker.IsMemberLine):
    /// `(public|open)\s+(?:final\s+)?(?:static\s+|class\s+)?(?:mutating\s+)?func`.
    /// Disallows `nonisolated`, `nonmutating`, `override`, etc., between access
    /// and `func` — those lines do NOT contribute to defaults/autoclosure/variadic.
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

    /// `PublicInitRegex` shape (strict, used by tracker.IsMemberLine):
    /// `(public|open)\s+(?:convenience\s+)?init`.
    /// Disallows `required`/`override` between access and `init` — those lines do
    /// NOT contribute to defaults/autoclosure/variadic.
    private func matchesPublicInitShape(_ modifiers: DeclModifierListSyntax) -> Bool {
        var iter = modifiers.makeIterator()
        guard advanceToAccess(&iter, ["public", "open"]) else { return false }
        var current = iter.next()
        if let mod = current, mod.name.text == "convenience", mod.detail == nil {
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
    /// Backticks are NOT stripped: `AnyFuncRegex` captures bare `(\w+)` — no
    /// `\`?` wrapper — so `func \`class\`()` (where SwiftSyntax keeps the backticks
    /// in `name.text`) wouldn't match the regex either. Backtick is not a word
    /// character, so this gate rejects backtick-escaped function names naturally.
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
}
