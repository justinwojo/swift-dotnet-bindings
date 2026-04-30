# M2 — Semantic Cliffs Discovered During SwiftSyntax Migration

This is a running ledger of *intentional* parity quirks where the SwiftSyntax host
mirrors a regex limitation byte-for-byte. They are NOT bugs to "fix later" —
flipping the default in M2 S3 means downstream code consumes whatever the host
emits, so any divergence here would be a real behavioral change.

The full ledger is the source of truth for whether the v2 producer can ship as
a drop-in for the regex producer.

Everything in this file is a place where SwiftSyntax could in principle be
*more* correct than the regex, but we deliberately suppress that improvement to
preserve byte-equal parity. When the regex parser is retired in M2 S4 we re-open
each cliff and decide whether to tighten the SwiftSyntax behavior.

---

## Cliff #1 — `nonisolated(unsafe)` is dropped from `NonisolatedMembers`

**Where**: `ActorIsolationWalker.swift` — `extractNonisolatedKeyword(...)`.

**Regex behavior**: `SwiftInterfaceAccessParser.GetNonisolatedMembers` matches the
attribute via `\bnonisolated\s+\w` — i.e. `nonisolated` followed by whitespace
then another word character. `nonisolated(unsafe)` does NOT match because `(` is
not `\w`.

**SwiftSyntax could see it**: `DeclModifierSyntax` exposes the `(unsafe)` detail
via `node.detail`. But the regex misses it, so we mirror — the walker drops the
attribute when `detail != nil`, even though `nonisolated(unsafe)` is a meaningful
isolation marker in Swift 6.

**Resolution**: When the regex parser is retired in M2 S4, surface
`nonisolated(unsafe)` as a separate fact (or fold it into `NonisolatedMembers` —
but treat that as a behavioral change with downstream review).

---

## Cliff #2 — `visionOSApplicationExtension` not a known availability platform

**Where**: `AvailabilityWalker.swift` — `isKnownPlatform(_:)`.

**Regex behavior**: `SwiftInterfaceAccessParser.IsKnownPlatform` lists 14
platforms. It includes `iOSApplicationExtension`, `macOSApplicationExtension`,
`tvOSApplicationExtension`, `watchOSApplicationExtension` — but NOT
`visionOSApplicationExtension`. (The regex was written before visionOS app
extensions were a thing.)

**SwiftSyntax could include it**: there's no syntactic ambiguity. We just have to
match the regex's set exactly.

**Resolution**: Add to the SwiftSyntax walker's whitelist after the regex parser
is retired. Until then, any swiftinterface that uses
`@available(visionOSApplicationExtension N, *)` will produce identical (broken)
output from both producers — that's the parity contract.

---

## Cliff #3 — Bare protocol-requirement availability annotations are missed

**Where**: `AvailabilityWalker.swift` — `extractMemberPrintedName(...)`.

**Regex behavior**: `SwiftInterfaceAccessParser.ExtractMemberPrintedName` treats
the leading access modifier as required (`Regex(@"^\s*(?:public|open|internal)\s+(func|var|init|...)`)`. Inside a protocol body, requirements are
written without an explicit `public` modifier, so the regex skips them.

**SwiftSyntax could see them**: walking `ProtocolDeclSyntax` member-by-member is
trivial. We mirror the omission instead.

**Resolution**: After the regex retires, drop the access-modifier guard inside
protocol scope. (Other scopes still need it — internal members in extensions
shouldn't pollute.)

---

## Cliff #4 — Multi-line availability position points at the LAST line

**Where**: `AvailabilityWalker.swift` — position emission inside
`emitForDeclaration(...)`.

**Regex behavior**: positions come from line-by-line scanning. When an
`@available(...)` declaration spans multiple lines (continuation), the regex
emits the position of the last line of the attribute, not the opening `@`.

**SwiftSyntax could pick the opening `@` token's location**: we have the full
`AttributeSyntax` node. We instead navigate to the trailing trivia of the
attribute and emit the line of the closing paren token, mirroring the regex.

**Resolution**: After regex retires, switch to opening `@` location. Diagnostic
quality improves slightly.

---

## Cliff #5 — Type-level `@MainActor` does NOT propagate to members in `ActorIsolatedMembers`

**Where**: `ActorIsolationWalker.swift` — `visit(_ node: FunctionDeclSyntax)` etc.

**Regex behavior**: `SwiftInterfaceAccessParser.GetActorIsolatedMembers` only
emits members that have a *direct* `@MainActor` / `@CustomActor` /
`actor`-keyword isolation marker. Members declared inside a `@MainActor public
class Foo` body — without their own attribute — are NOT in
`ActorIsolatedMembers`. (That information lives in `MainActorTypes` instead, and
downstream consumers join those two facts.)

**SwiftSyntax could propagate**: easily, by inspecting the enclosing scope. We
mirror the regex's "individually-annotated only" behavior.

**Resolution**: Decide downstream: if joins-at-consumer is the contract, leave
alone. Otherwise propagate after retirement.

---

## Cliff #6 — `customActorIsolatorMap` only learns names from a body-local `actor X` decl

**Where**: `ActorIsolationWalker.swift` — `customActorIsolatorMap` population.

**Regex behavior**: `SwiftInterfaceAccessParser.GetCustomActorIsolatorMap`
matches `@FooActor` references against a known short-name set
(`customActorTypes`, derived from `actor X { }` decls). An imported actor used
as `@FooActor` does NOT enter `customActorIsolatorMap` because there's no body
decl. The regex applies one heuristic for imported actors via the synthesized
`Pipeline` ↔ `PipelineActor` relationship, but only inside the same file.

**SwiftSyntax could resolve imports**: not within a single-file walk, no — the
parser deliberately doesn't load `import`ed modules. So this isn't a "we could
but choose not to" cliff so much as "regex's same limitation, surfaced
differently". Documenting for completeness.

**Resolution**: No change planned. Cross-module isolation is out of scope for
the swiftinterface side-channel.

---

## Cliff #7 — Typed-throws key uses LAST-DOT-COMPONENT for extensions

**Where**: `ThrowsWalker.swift` — `visit(_ node: ExtensionDeclSyntax)`.

**Regex behavior**: `SwiftInterfaceAccessParser.ProcessFuncLineForTypedThrows`
keys typed-throws with `typeStack.Peek().Name`, where `Peek().Name` for an
extension is set to the *last* dot-component of the extended type (line
3151-3152). This is **distinct** from `ActorIsolatedMembers`, which uses the
first-stripped path. The regex is internally inconsistent here, and the .NET
generator side queries `_typedThrowsErrors` with `parentDecl.Name` (simple
name only), which matches.

**SwiftSyntax mirrors**: the walker's extension scope explicitly takes
`String(qualified[qualified.index(after: lastDot)...])` for typed-throws keying.
A separate `ActorIsolationWalker` scope helper takes the first-stripped form.

**Resolution**: When the regex retires, decide whether to harmonize the keying
scheme. Probably "no" — the consumer queries by simple name and would need
matching changes.

---

## Cliff #8 — Free-function isolation only emits `public/open func`

**Where**: `ActorIsolationWalker.swift` — `visit(_ node: FunctionDeclSyntax)`
when scope stack is empty.

**Regex behavior**: line 732-748 of `SwiftInterfaceAccessParser` requires
`(public|open)` BEFORE `func` for module-scope free functions to enter
`actorIsolatedMembers`. Internal-without-modifier free functions can't appear
in a swiftinterface anyway, but `package`-level ones can (Swift 5.9+) and they
are silently dropped.

**SwiftSyntax**: we check `node.modifiers` for `public`/`open` and skip
otherwise. Mirrors the regex.

**Resolution**: Decide on `package` once supported by upstream test corpus.

---

## Cliff #9 — `customActorIsolatorMap` is FIRST-MATCH-WINS

**Where**: `ActorIsolationWalker.swift` — `customActorIsolatorMap` population.

**Regex behavior**: line 545 of `SwiftInterfaceAccessParser` only inserts a
key if it's not already present (`if (!_customActorIsolatorMap.ContainsKey(...)`).
For a swiftinterface that has `@PipelineActor func a()` AND `@PipelineActor
func b()`, the map has one entry pointing at the first. SwiftSyntax mirrors.

**Resolution**: This is correct behavior. No change planned.

---

## Cliff #10 — Untyped `throws` excluded from `TypedThrowsErrors`

**Where**: `ThrowsWalker.swift` — `extractTypedThrows(...)`.

**Regex behavior**: only the `throws(<Type>)` form contributes. Plain `throws`
(no parens) and non-throwing functions never key. SwiftSyntax mirrors via the
`throwsClause.type` non-nil check.

**Resolution**: Correct. No change planned.

---

## Cliff #11 — Order-strict modifier shape vs unanchored regex

**Where**: `AvailabilityWalker.swift` — `matchesPublicFuncShape` /
`matchesPublicInitShape` / `matchesPublicVarShape` / `matchesPublicSubscriptShape`
/ `matchesPublicTypeShape`.

**Regex behavior**: `PublicFuncRegex` (and the four sibling regexes) is unanchored
— `Match` searches anywhere on the line. Pathological forms like `final public
final func foo` would match starting at the inner `public final func foo`
substring. Most real swiftinterfaces never produce such inputs because compiler
output is canonical, but the regex is permissive about leading garbage in
principle.

**SwiftSyntax**: my walker walks `node.modifiers` in source order and verifies
each modifier's slot. Out-of-order leading modifiers fail the gate. This is
SLIGHTLY stricter than the regex.

**Resolution**: not worth fixing. Compiler-emitted swiftinterfaces always use
canonical modifier order (access first, then final, then static/class, then
mutating). Any non-canonical input is either hand-written (out of scope) or a
compiler bug. Documented for completeness.

---

## Cliff #12 — `RegexShape.opensOnSameLine` is keyword-to-`{`, regex matches the full pattern-to-`{`

**Where**: `RegexShape.swift` — `opensOnSameLine(keyword:leftBrace:converter:)`,
used by every type-decl walker (Availability / EnumFacts / SubscriptLabels /
PublicTypeNames / MemberCollection / SignatureFacts / Throws / MainActor /
ActorIsolation).

**Regex behavior**: `TypeDeclRegex` / `PublicTypeDeclRegex` / `ExtensionDeclRegex`
all match a single line. The `openBraces > 0` gate in
`SwiftInterfaceContextTracker.cs` requires the access modifier + optional `final`
+ type keyword + name + `{` to *all* appear on the same source line. Splitting
the access modifier from the type keyword (`public\nclass Foo {`) means the
regex cannot match `TypeDeclRegex` on either line, so neither line pushes a scope.

**SwiftSyntax could see it**: the walker's `opensOnSameLine` only checks line
equality between the type keyword and `{`. For `public\nclass Foo {`, `class`
and `{` share a line, so the SwiftSyntax walker pushes the scope while the regex
producer does not.

**Why low-impact**: canonical `.swiftinterface` output never splits the access
modifier from the `class`/`struct`/`enum`/`actor`/`protocol` keyword onto a
separate line — the Swift compiler always emits the full declaration head on
one line. The 122-library validation corpus, BindingTests fixtures, and unit
parity tests all pass green; the divergence is unobservable in practice.
Documented in Codex session `019dddde-98e4-7c10-a20b-f591c4047aef` (rounds 6/7)
as a persistent residual that does not block the M2 S3 default flip.

**Resolution**: M2 S4 retires the regex producer; the parity question becomes
moot once SwiftSyntax is the only producer. No action needed in S3.

---

## Notes for M2 S3 (default flip)

When the SwiftSyntax producer becomes the default in M2 S3, none of the above
cliffs change behavior — both producers see the same swiftinterface and emit
the same JSON shape. Flipping is a no-op as long as the parity tests stay
green.

When the regex retires in M2 S4, walk this list and decide per-cliff whether
to upgrade SwiftSyntax to the more-correct semantics. Each upgrade is a
behavioral change; cluster them into one or two commits with downstream test
churn rather than scattering.
