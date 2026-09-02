// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftSyntax
import SwiftParser

/// Walks `var` declarations and surfaces one fact:
///   * `asyncAccessorMembers` — Set of `"Qualified.Type.Path.propertyName"` keys for
///     properties whose `get` accessor carries the `async` effect specifier
///     (`{ get async }` and `{ get async throws }` alike). A type-level (`static` /
///     `class`) property's key carries a `"static "` prefix.
///
/// WHY THIS FACT EXISTS:
///
/// Nothing else in the generator's input can prove an *accessor* is async.
/// swift-api-digester's ABI JSON marks accessor nodes with `throwing` but has no
/// async flag, and an async accessor's mangled name carries no `Ya` marker (it is a
/// plain `…vg`) — so the only other evidence is a sibling `{getter}Tu` /
/// `{getter}TjTu` symbol in the `.tbd`. That made TBD parsing a single point of
/// failure: any defect that empties or mis-selects the module's symbol set silently
/// turns every `get async` property into a synchronous one, which either fails the
/// Swift wrapper compile or — for `get async throws`, where the throwing-getter
/// wrapper gate declines and emission falls through to a direct Swift-convention
/// P/Invoke — compiles and ships an ABI mismatch that only manifests at the first
/// read. Reading the effect specifier straight out of the `.swiftinterface` gives a
/// second, independent oracle; the consumer ORs the two so either one alone suffices.
///
/// EXTRACTION CONTRACT:
///
/// 1. **Decl kinds**: `var`/`let` bindings with an accessor block. `func`, `init`
///    and `subscript` do not contribute — a `func` derives async from its own
///    mangled name, and subscript accessors never consult the async probe.
///
/// 2. **Effect source**: the structured accessor AST
///    (`AccessorDeclSyntax.effectSpecifiers?.asyncSpecifier`), scoped to the `get`
///    accessor. Effect specifiers are get-only in Swift today; scoping to `get`
///    keeps the reading matched to the fact's name. The single-expression
///    `{ return 0 }` accessor form cannot express an effect specifier at all.
///
/// 3. **KEY SHAPE — FULL qualified chain, module-stripped.** The key is every
///    enclosing type frame joined by `.`, then the property name — e.g.
///    `ImageAnalysisInteraction.Subject.image` for a property on a struct nested in
///    a class. This mirrors the consumer's `BuildTypeQualifiedPath(parentDecl)`
///    lookup (the same shape `variadicMembers` / `defaultParameterValues` use), NOT
///    the immediate-parent-only shape `parameterNames` uses. A module-level (free)
///    property gets a BARE key.
///
/// 4. **Extension frames use FIRST-dot-strip.** A swiftinterface prints extension
///    targets module-qualified (`extension VisionKit.ImageAnalysisInteraction.Subject`),
///    so dropping the first component yields the module-relative path
///    `ImageAnalysisInteraction.Subject` that `BuildTypeQualifiedPath` produces.
///
/// 4b. **Type-level properties carry a `"static "` key prefix.** Swift lets one type
///    declare a `static var value` and an instance `var value` side by side, and the
///    ABI exports two separate getters for them. An unprefixed key would name both, so
///    a `static var value { get async }` would drag its synchronous instance namesake
///    onto the async path — projecting a plain property as a `Task`-returning method
///    and dropping its setter. The prefix separates the two namespaces; a space cannot
///    occur in a qualified Swift path, so it can never collide with a real key.
///
/// 5. **No access-modifier or same-line-brace gate.** Unlike the tracker-style
///    walkers, every nominal type and extension pushes a frame. A frame that is
///    skipped would silently shorten the path of every member below it, and a
///    shortened key does not merely miss — it can COLLIDE with a same-named property
///    on a different type and mark a synchronous accessor async, which routes it to
///    an async entry point that does not exist. Over-collecting is harmless instead:
///    an internal member's key is never looked up, because the ABI parser has already
///    dropped the member.
///
/// 6. **Backticks are unescaped, not rejected.** A keyword-named declaration prints as
///    `` `switch` `` in a swiftinterface and SwiftSyntax keeps the backticks in the
///    token text, but the ABI parser strips them before it builds its lookup key — so a
///    key that kept them could never match. Every path component is unescaped instead.
///    Unescaping cannot invent a collision: two types cannot share an identifier where
///    only one of them is keyword-escaped.
///
/// 7. **Unrenderable frames still drop their members.** An extension target that is not
///    a plain dotted identifier chain even after unescaping (`extension Foo<T>`,
///    `extension (Foo)`) pushes a `nil` frame; any property below it emits no key rather
///    than one the consumer cannot reproduce. The TBD probe remains that member's oracle.
final class AsyncAccessorWalker: SyntaxVisitor {
    private(set) var asyncAccessorMembers: [String] = []

    /// One frame per enclosing nominal type / extension, innermost last. `nil` marks a
    /// frame whose name cannot be rendered as a path component (see contract point 7);
    /// members below it are dropped. Every `visit` pushes exactly one frame and every
    /// `visitPost` pops one, so the stack cannot desynchronize.
    private var scopeStack: [String?] = []

    init() {
        super.init(viewMode: .sourceAccurate)
    }

    /// Source-only: this fact carries no source positions, so unlike the
    /// position-bearing walkers it needs no `SourceLocationConverter` and therefore
    /// no file path.
    static func parse(source: String) -> [String] {
        let tree = Parser.parse(source: source)
        let walker = AsyncAccessorWalker()
        walker.walk(tree)
        return walker.asyncAccessorMembers
    }

    // MARK: - Nominal type scopes

    override func visit(_ node: ClassDeclSyntax) -> SyntaxVisitorContinueKind {
        pushTypeFrame(node.name.text)
        return .visitChildren
    }
    override func visitPost(_ node: ClassDeclSyntax) { popFrame() }

    override func visit(_ node: StructDeclSyntax) -> SyntaxVisitorContinueKind {
        pushTypeFrame(node.name.text)
        return .visitChildren
    }
    override func visitPost(_ node: StructDeclSyntax) { popFrame() }

    override func visit(_ node: EnumDeclSyntax) -> SyntaxVisitorContinueKind {
        pushTypeFrame(node.name.text)
        return .visitChildren
    }
    override func visitPost(_ node: EnumDeclSyntax) { popFrame() }

    override func visit(_ node: ActorDeclSyntax) -> SyntaxVisitorContinueKind {
        pushTypeFrame(node.name.text)
        return .visitChildren
    }
    override func visitPost(_ node: ActorDeclSyntax) { popFrame() }

    override func visit(_ node: ProtocolDeclSyntax) -> SyntaxVisitorContinueKind {
        pushTypeFrame(node.name.text)
        return .visitChildren
    }
    override func visitPost(_ node: ProtocolDeclSyntax) { popFrame() }

    // MARK: - Extension scope (first-dot-strip: drop the module component)

    override func visit(_ node: ExtensionDeclSyntax) -> SyntaxVisitorContinueKind {
        let components = node.extendedType.trimmedDescription
            .split(separator: ".", omittingEmptySubsequences: false)
            .map { Self.unescape(String($0)) }
        guard components.allSatisfy({ RegexShape.isWordIdentifier($0) }) else {
            scopeStack.append(nil)
            return .visitChildren
        }
        // First-dot-strip: drop the module component that a swiftinterface always
        // prints on an extension target. A single-component target has none to drop.
        scopeStack.append((components.count > 1 ? components.dropFirst() : components[...])
            .joined(separator: "."))
        return .visitChildren
    }
    override func visitPost(_ node: ExtensionDeclSyntax) { popFrame() }

    // MARK: - Properties

    override func visit(_ node: VariableDeclSyntax) -> SyntaxVisitorContinueKind {
        let isTypeLevel = node.modifiers.contains { modifier in
            let text = modifier.name.text
            return text == "static" || text == "class"
        }
        for binding in node.bindings {
            guard hasAsyncGetter(binding.accessorBlock) else { continue }
            guard let pattern = binding.pattern.as(IdentifierPatternSyntax.self) else { continue }
            let name = Self.unescape(pattern.identifier.text)
            guard RegexShape.isWordIdentifier(name) else { continue }
            guard let key = makeKey(memberName: name, isTypeLevel: isTypeLevel) else { continue }
            if !asyncAccessorMembers.contains(key) {
                asyncAccessorMembers.append(key)
            }
        }
        // A property's accessor block holds no further declarations of interest.
        return .skipChildren
    }

    // MARK: - Helpers

    /// Pushes a nominal-type frame, or `nil` when the name is not a `\w+` identifier
    /// even after unescaping and is therefore not reproducible on the consumer's
    /// `BuildTypeQualifiedPath` side.
    private func pushTypeFrame(_ name: String) {
        let unescaped = Self.unescape(name)
        scopeStack.append(RegexShape.isWordIdentifier(unescaped) ? unescaped : nil)
    }

    /// Drops the backticks a swiftinterface prints around a keyword-named declaration.
    /// The ABI parser's side of the key carries the bare identifier, so the escape has
    /// to come off here or the two halves can never meet.
    private static func unescape(_ name: String) -> String {
        guard name.count >= 2, name.hasPrefix("`"), name.hasSuffix("`") else { return name }
        return String(name.dropFirst().dropLast())
    }

    private func popFrame() {
        _ = scopeStack.popLast()
    }

    /// Full qualified chain + member name, or `nil` when any enclosing frame is
    /// unrenderable. A module-level property (empty stack) gets a bare key. A
    /// type-level property is prefixed so it cannot name its instance namesake.
    private func makeKey(memberName: String, isTypeLevel: Bool) -> String? {
        let prefix = isTypeLevel ? "static " : ""
        if scopeStack.isEmpty { return prefix + memberName }
        var parts: [String] = []
        for frame in scopeStack {
            guard let frame = frame else { return nil }
            parts.append(frame)
        }
        parts.append(memberName)
        return prefix + parts.joined(separator: ".")
    }

    /// True when the property's `get` accessor carries the `async` effect specifier.
    /// Mirrors `ExtensionsWalker.detectEffectfulGetter`'s structured-AST reading, but
    /// narrowed to `async` — the `throws` half is already carried by the ABI JSON's
    /// `throwing` flag on the accessor node, so a second oracle for it would add a
    /// disagreement surface without adding information.
    private func hasAsyncGetter(_ accessorBlock: AccessorBlockSyntax?) -> Bool {
        guard let accessorBlock = accessorBlock else { return false }
        switch accessorBlock.accessors {
        case .accessors(let list):
            for accessor in list where accessor.accessorSpecifier.text == "get" {
                if accessor.effectSpecifiers?.asyncSpecifier != nil {
                    return true
                }
            }
            return false
        case .getter:
            // `var x: Int { return 0 }` single-expression form — no accessor syntax,
            // so no effect specifier is expressible.
            return false
        }
    }
}
