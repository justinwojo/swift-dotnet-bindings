# Session 15 — swiftinterface end-state (+ deferred Findings 10, 46-remainder)

**Goal:** finish the SwiftSyntax migration so `SwiftInterfaceAccessParser` and the regex
producer can be deleted; ride the type-identity-home work (F10) and USR-keyed availability
(F46-remainder) on the same parser/resolver pass.
**Findings:** 3 (finish swiftinterface migration), 26 (bug-faithfulness charter), 10
(single type-identity home — deferred-in), 46-remainder (USR-keyed availability — deferred-in).
**Flags:** standard.

---

## ⚠️ Review amendments — Grok + Codex design review (2026-06-16) — READ BEFORE IMPLEMENTING

Both reviewers ran a full pre-implementation pass. Verdicts: Grok **READY WITH CHANGES**, Codex **NOT READY** (Phase E anchor source unavailable; Phase B misses parser-test consumers). The Finding-3 migration direction holds; these amendments **cut two phases out of S15 execution** and broaden Phase B. They **supersede** the body's `A → B → C → D → E` sequencing. Resume tokens — Grok `019ed3be-f4e6-72c2-ad2f-8d4c92f10ffe`, Codex `019ed3be-bc7c-73a2-be78-6ea55dba7d67`.

**A — Phase E / F46 anchor has NO data source — DEFER F46 out of S15 (Codex High / blocker; lead call).** `.swiftinterface` carries no USR/mangled name — the source itself says so (`MemberSignatureNormalizer.cs:78`, `AvailabilityWalker.swift:867`) — and the host CLI only takes `--input <path>` and passes source into `AvailabilityWalker.parse` (`main.swift:16`, `SwiftSyntaxInterfaceFactsProducer.cs:127`). Phase E's `anchorUsr`/`anchorMangledName` **cannot be emitted** from the SwiftSyntax host without an unstated ABI/digester side channel. → **DECISION: defer the F46-remainder anchor groundwork entirely.** F46 needs an ABI side-channel design first; do **not** emit empty/placeholder anchors. (Same conservative posture as the F26 deferral.)

**B — Phase B deletion breaks ~5 test files, not 1 (both, High).** The body names only `SwiftInterfaceAccessParserTests.cs` (`:250`), but direct consumers also include `SourceProvenanceTests.cs:68` (`GetMainActorTypes`), `SwiftInterfaceTypedThrowsTests.cs:25` (`GetTypedThrowsErrors`), `ProtocolExtensionStructConformerTests.cs:448` (`ProcessProtocolExtensionMemberForTesting`); `SwiftInterfaceContextTracker.cs:156,289` is production-regex-only and circular. → **Broaden Phase B's inventory** to every direct `SwiftInterfaceAccessParser`/tracker test consumer — each migrated to SwiftSyntax facts or deleted **before** the parser is removed, or the build breaks.

**C — Remove Phase C from S15 execution; pull the parity golden into Phase A/B (Codex Medium; lead call).** The risk section already recommends A/B-first with C as owner-reviewed separate work (`:399`), but the change inventory still lists Phase C steps (`:265`) and the sequencing says `A→B→C→D→E` (`:363`). → **Execution order is now `A → B → D`** (C deferred/owner-gated and explicitly non-executable; E/F46 deferred per A). The **full-fact-set parity golden** that proves it safe to delete the regex producer **must land in Phase A/B** — it was implicitly orphaned by the C/E deferrals.

**D — F10 needs a resolution-identity parity bake, not just per-stage `validate` (Codex Medium).** Resolver misses degrade to `AnyType` instead of failing compilation (`TypeDatabaseExtensions.cs:96,310`), and existing `ResolverParityTests` (`:149`) are representative samples, not corpus-wide. → Before collapsing the `SwiftTypeName` cascade, **record old-vs-new resolved identity/provenance over the validation corpus** as the safety net; compile-clean is not sufficient proof.

**E — Stale cites (Codex Low).** `constLiteralParameters` / `closureParameterAttributes` **already exist** in `InterfaceFactsJson.cs:155` — only `spiOnlyConformances` is missing (`:185`); fix the body's "all three new payload fields." `RegexInterfaceFactsProducer.cs` is **275** LOC (body says 276).

**Confirmed, no change:** the `kSchemaVersion`/`ExpectedSchemaVersion` handshake is correctly identified and must move together; deferring F26 is right.

---

## ⚠️ Phase A execution findings — corpus parity bake (2026-06-18) — Phase B is BLOCKED for owner decision

Phase A landed and is fully gated (see below). Building the full-fact-set parity golden the
amendments demanded (`InterfaceFactsCorpusParityBake.cs`, opt-in via `RUN_PARITY_BAKE=1`) then
ran both producers over **every** production `.xcframework` `.swiftinterface` under `.libraries/`
and surfaced a blocker the synthetic 189-case parity suite could not: **the two producers are NOT
byte-equal on real-world input.** ~2904 fact-level divergences across 793/838 files, in ~10
**pre-existing** fact kinds (the 4 facts Phase A added — `SpiOnlyConformances`,
`ConstLiteralParameters`, `ClosureParameterAttributes`, `ObjCRuntimeNames` — are clean). Confirmed
by Codex (`Session: 019ed991-99f4-71b3-b67a-bbf0d6eb68b4`) and Grok
(`sessionId: 019ed991-be3a-7100-88c3-832bf0472c78`) consults plus first-party investigation.

**Operational truth (changes the risk framing):** on Darwin after `nuke compile`, the default
`auto` aggregator already puts SwiftSyntax **first** and it declares coverage for all 31 facts
(first-wins, `InterfaceFactsAggregator.cs:15-18`; `BindingsGeneratorCommand.cs:1920`;
`CliOptions.cs` default `auto`). So regex is **not** a live fallback today — SwiftSyntax output is
already what `nuke validate` and normal generation consume. The divergences describe **current**
binding behavior, not a hypothetical post-cutover state. Deleting regex does not flip macOS output;
it removes the parity oracle, the `--interface-facts-producer=regex` rollback, and the
non-Darwin / missing-binary fallback (`BindingsGeneratorCommand.cs:1899-1917`).

**Per-fact binding-surface risk (both reviewers + verified downstream):**
- **HIGH — `PublicMemberNames`** (518 files): SwiftSyntax ⊂ regex; `MemberCollectionWalker` has no
  bare-protocol-requirement path that regex's `inProtocol` branch has
  (`SwiftInterfaceAccessParser.cs:3018-3076`). Drives negative-space `IsModuleInternal`
  (`SwiftABIParser.cs:835-851`) → protocol requirements can be **falsely marked internal and
  silently dropped** while still compiling.
- **HIGH — actor-isolation** (`ActorIsolatedMembers`/`MainActorIsolatedMembers`, 179 files each):
  drives `IsAsync` routing (`SwiftABIParser.cs:2590-2619`) → `Task<T>` vs sync API shape.
- **MEDIUM — `AvailabilityAnnotations`** (104): feeds `[SupportedOSPlatform]`/`[ObsoletedOSPlatform]`.
- **MEDIUM — `DefaultParameterValues`** (456): feeds `ApplyMemberDefaultValues` overload projection.
- **LOW — `AvailabilityAnnotationPositions`** (793) and other position dicts: diagnostics/provenance
  only; do not gate emission.

**Three primary root causes:** (1) regex init-regexes lack `\??` so SwiftSyntax emits failable inits
regex drops (here SwiftSyntax is **more** correct — `SignatureFactsWalker.swift:38-46`); (2)
`MemberCollectionWalker` lacks the protocol bare-requirement path (here SwiftSyntax is **wrong** —
drops members); (3) `AvailabilityWalker.swift:218-224` records positions unconditionally vs regex's
`annotations.Count>0` gate (`SwiftInterfaceAccessParser.cs:4305-4310`).

**Why Phase B is blocked (not autonomously deferrable, owner decision required):** these divergences
sit exactly on the **Finding 26 fault line**. Reconciling them means deciding **per fact** whether
regex or SwiftSyntax is correct — and the answer differs by fact (SwiftSyntax is wrong on
`PublicMemberNames`, more correct on failable-init `ParameterNames`). That per-fact correctness call
**is** the F26 inversion, which this doc explicitly defers and owner-gates. Deleting the regex oracle
now would either lock in the HIGH-risk SwiftSyntax bugs with no oracle to detect them, or silently
require the deferred F26 decision. A single "obvious" fix does not unblock it — the producers stay
non-byte-equal (so the bake stays red, the amendments' Phase-B gate) until the whole category is
reconciled. **`nuke validate` is necessary but NOT sufficient** as a fact-parity gate: it proves
compile-clean *under SwiftSyntax* (already the live producer), not regex-equivalence. The corpus bake
(or per-family semantic tests) is the right Phase B gate and both reviewers endorse keeping it.

**Recommended owner-scoped follow-up (a dedicated session, not improvised mid-discovery):** resolve
the ~10-kind divergence category as one coherent F26 per-fact reconciliation pass — fix SwiftSyntax
where it is wrong (`PublicMemberNames` protocol requirements; actor-isolation), accept it where it is
more correct (failable-init facts) and adjust the regex oracle / bake expectations to match, each
change gated by re-baking the corpus AND `nuke validate` (the `PublicMemberNames` fix is a
cross-cutting binding-surface change). Only once the bake is green can the regex producer be deleted
(Phase B) safely. Phase D (F10 resolver) is independent of this and can proceed separately.

---

## Current-state verification

The codebase has moved substantially since the June review. Verify each cite below against
the live tree — several are already landed or relocated. Build the generator before any gate
(stale `bin/Debug` masks edits — `constraints.md` "Stale generator binary").

### Finding 3 — finish swiftinterface migration

- **"27 of 30 fact kinds covered" — CONFIRMED, sharpened.** `InterfaceFactKind` now enumerates
  **30** kinds (`Parser/Producers/InterfaceFactKind.cs:21-64`), not the review's 27-of-30 wording
  read against a 30-member enum. The SwiftSyntax host's `coveredFacts` array
  (`tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/main.swift:110-146`) lists exactly
  **27** entries; it omits `SpiOnlyConformances`, `ConstLiteralParameters`,
  `ClosureParameterAttributes`. The `SwiftSyntaxInterfaceFactsProducer.Produce` conversion
  (`Parser/Producers/SwiftSyntaxInterfaceFactsProducer.cs:205-298`) likewise has no branch for the
  three. These are the **exact three** the gameplan names. The doc-comments claiming "100% (24/24)"
  / "Covers 24/24" (`SwiftSyntaxInterfaceFactsProducer.cs:20`, `main.swift:11,51`, `Output.swift:51`,
  `InterfaceFactsProducerParityTests.cs:21`) are **stale** — both the 24 count and the "100%" claim.
  Fix those strings as part of the migration.
- **Silent regex fallback — CONFIRMED, relocated.** Review cited `BindingsGeneratorCommand.cs:1704-1731`;
  the live `BuildAutoAggregator` is **`BindingsGeneratorCommand.cs:1867-1896`**. On macOS with a
  missing/unlocatable binary it logs and returns a **regex-only** aggregator
  (`:1879-1888`) — the silent-degrade channel the finding targets. `auto` is the default
  (`CliOptions.cs:364`, `getDefaultValue: () => "auto"`). `swift-syntax` mode already hard-fails on
  missing binary (`BuildSwiftSyntaxAggregator`, `:1898-1921`); `regex` mode is single-producer
  (`:1852-1855`).
- **Library-entry default regex-only — CONFIRMED, relocated.** Review cited `Program.cs:256-264`; the
  live default-aggregator construction is **`Program.cs:269-270`** — when no `factsAggregator` is
  passed, `Program.GenerateBindings` builds an `InterfaceFactsAggregator` over a lone
  `RegexInterfaceFactsProducer`. CLI always passes one from `BuildInterfaceFactsAggregator`, but any
  other entry point (tests, future direct callers) silently runs regex-only.
- **LOC — CONFIRMED (drifted down slightly).** `Parser/SwiftInterfaceAccessParser.cs` is **5,391 LOC**
  (review said 5,418); `tests/UnitTests/ParserTests/SwiftInterfaceAccessParserTests.cs` is **4,292 LOC**
  (matches). `RegexInterfaceFactsProducer.cs` is 276 LOC.
- **NOT addressed by recent sessions:** the three fact kinds, the silent fallback, the regex-only
  library default, and the dual-parser existence are all live. This finding is genuinely open.

### Finding 26 — SwiftSyntax bug-faithfulness charter

- **CONFIRMED open / charter-level.** The host program is still explicitly a parity emulator:
  `main.swift:12-13` ("byte-equal parity against the regex producer"); per-walker "mirrors" comments
  remain throughout `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/*.swift` (e.g. the
  shape-gate quirks pinned by the parity corpus in `InterfaceFactsProducerParityTests.cs`, lines
  77-88, 398-405, 462-481, 505-512, etc.). The corpus deliberately asserts SwiftSyntax *reproduces*
  regex blind spots (indirect-enum scope-not-pushed, operator/backtick name drops, grouped-case
  first-only). This is the dead-program monument the finding names. Treat F26 as the post-migration
  step: once the regex side is deleted, the "MUST mirror the regex bug" justification evaporates and
  the gates must be re-pointed at Swift semantics (see Target design).

### Finding 10 — single type-identity home (deferred-in)

**Largely landed across Sessions 6/9/10/11/14. The cited file no longer exists at the cited path:
`TypeDatabase.cs` is now `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs`; `TypeResolver.cs`
is `src/Swift.Bindings/src/TypeDatabase/Resolver/TypeResolver.cs`.** Per-claim:

- **"Two resolution universes / double supplement consult / INVARIANT comment" — MOSTLY RESOLVED.**
  The NamedTypeSpec path is now a single ordered strategy chain of 14 strategies
  (`Resolver/TypeResolver.cs:62-83`, `Default`), and the SwiftTypeName cascade's supplement arm was
  factored out: `TryGetTypeRecord` calls `TryResolveAppleSupplementArm` then
  `TryGetTypeRecordWithoutSupplement` (`TypeDatabase.cs:610-705`), with explicit "Finding 10" remarks
  at `:632-637` documenting the double-consult removal. The INVARIANT comment survives at
  `TypeDatabase.cs:622-630` but now describes a single consult, not a redundant pair.
- **"`IsTypeProcessed` disagrees with `TryGetTypeRecord`" — RESOLVED both sides.** NamedTypeSpec:
  `IsTypeProcessed(NamedTypeSpec)` projects from the resolver (`Resolver/TypeResolver.cs:36-44`
  remark). SwiftTypeName: `IsTypeProcessed(SwiftTypeName)` is now literally
  `return TryGetTypeRecord(swiftTypeName, out _)` (`TypeDatabase.cs:777-786`); the old narrow 3-arm
  body was renamed to `IsTypeRegistered` (`:789-816`) for the parser's duplicate gate.
- **STILL OPEN (this session's F10 slice):**
  - **The two universes still co-exist.** The SwiftTypeName 6-arm cascade
    (`TypeDatabase.TryGetTypeRecordWithoutSupplement`, `:664-705`, arms 2-6) is a parallel
    resolution path to the NamedTypeSpec strategy chain. They are no longer *contradictory* (the
    supplement and IsTypeProcessed divergences are gone) but they are still **two code paths**. The
    finding's end-state ("the `SwiftTypeName` overloads become adapters into the same strategy
    chain") is **not done**.
  - **Ref-alias rewrite is still unscoped.** `GetRefAliasVariant` (`TypeDatabase.cs:960-969`) appends/
    strips `Ref` for **every** module's names — no CF-family gate — even though the only motivating
    alias is the `CoreFoundation → CoreGraphics` module remap (`TypeDatabase.cs:53-57`). The
    gameplan's "scope the Ref-alias rewrite to CF-family modules" is open.
  - **ModuleProcessor's private mini-resolver persists.** `ModuleProcessor.TryGetTypeRecord`
    (`Parser/ModuleProcessor.cs:71-93`) is a fourth resolution entry, and `ClassifyFieldType`
    (`:535`) still spells `CoreFoundation.CGFloat`/`CoreGraphics.CGFloat` as literals (`:313`).
  - **Prefix-heuristic guard chain still duplicated.** `!IsKnownAppleValueType && HasObjCClassPrefix`
    recurs at `Marshaler/Projection/TypeProjectionFactory.cs:205-207, 238, 604-606, 632` — four
    sites, held in sync by comment. The "fold into a declarative type-facts store that records its
    guesses" half is open.
  - **Call-site count.** `grep` for `.TryGetTypeRecord(|.IsTypeProcessed(|.IsTypeRegistered(` over
    `src/Swift.Bindings/src` returns **363** hits today (review said 347) — the migration risk the
    finding flags is real and slightly larger now.

### Finding 46-remainder — USR-keyed availability (deferred-in)

- **CONFIRMED open.** Availability is still joined by a byte-equal signature key:
  `SwiftABIParser.ApplyAvailability` → `ApplyMemberAvailability` composes
  `MemberSignatureNormalizer.ComposeKey(bareKey, BuildSignature(...))` (`Parser/SwiftABIParser.cs:387-421,
  430-454`) and looks the result up in `_facts.AvailabilityAnnotations`. The normalizer
  (`Model/TypeSpecParsing/MemberSignatureNormalizer.cs`) exists "solely to make the two grammars
  byte-identical."
- **The anchor the overhaul wants is already in the ABI model.** ABI nodes carry `usr`
  (`SwiftABIParser.cs:121`, `public string? usr`) and `MangledName` (`:104`). The producer side
  (swiftinterface walkers) does **not** emit a usr/mangled key today — availability keys are the
  normalized signature string. So F46 is "have the SwiftSyntax availability walker emit a
  usr/mangled-name anchor and join on it," which is only tractable *after* the producer is
  SwiftSyntax-only (you control the producer's key shape). This is why it rides this session.
- **Scope call:** full F46 (delete `MemberSignatureNormalizer`, join on usr) is large and touches the
  overload-disambiguation contract that `InterfaceFactsProducerParityTests` + the SwiftABIParser
  runtime tests pin. Treat F46-remainder as **design-spike + groundwork** this session (emit the
  anchor, prove the join is reachable), not necessarily full normalizer deletion — see owner
  decision points.

---

## Target design

**End state: one swiftinterface fact producer (SwiftSyntax), one resolution chain, one
availability anchor.**

1. **Single fact producer.**
   - `SwiftSyntaxInterfaceFactsProducer` covers all **30** fact kinds. The host
     (`tools/SwiftInterfaceParser`) gains three walkers — `ConstLiteralParametersWalker`,
     `ClosureParameterAttributesWalker`, `SpiOnlyConformancesWalker` — and `main.swift` adds the
     three `coveredFacts` entries + `Facts` payload fields. The `Output.swift` `Facts` struct and the
     .NET `InterfaceFactsJsonPayload` (`Parser/Producers/InterfaceFactsJson.cs`) gain matching wire
     fields, updated in the same commit (additive, **no `kSchemaVersion` bump** per the additive-
     evolution rule at `Output.swift:6-13` — but see step 5, F26, where the bump *does* happen).
   - `RegexInterfaceFactsProducer`, `SwiftInterfaceAccessParser`, and
     `SwiftInterfaceAccessParserTests` are **deleted**. The `regex` CLI value and `auto`'s regex
     fallback are removed; the producer becomes **required** on Darwin (fail loud on missing binary).
   - `SwiftInterfaceContextTracker` (`Parser/SwiftInterfaceContextTracker.cs`) is a **shared helper**
     used by both the regex parser and its own static methods (`CountBraces`, `ExtractPrintedName`,
     `BuildMemberKey`, `ExtractMemberPrintedName`). After deleting the regex parser, audit whether any
     surviving caller needs it (today only `SwiftInterfaceAccessParser` consumes the instance API;
     `SwiftInterfaceContextTracker` itself calls `SwiftInterfaceAccessParser.CountBraces`/
     `ExtractPrintedName` — a circular dependency that dies with the regex parser). Either inline the
     three needed statics into the tracker or delete the tracker too if no consumer survives.

2. **Producer selection API (post-migration).**
   - `--interface-facts-producer` collapses to a binary contract: SwiftSyntax (required on Darwin) or
     hard-fail. Keep the flag for one release as a no-op-with-deprecation-or-removed switch per owner
     call (see decisions). `BuildInterfaceFactsAggregator`/`BuildAutoAggregator`/
     `BuildSwiftSyntaxAggregator` collapse to one builder that locates the binary and throws if
     missing on Darwin (`OperatingSystem.IsMacOS()` guard stays for the cross-platform-CI story —
     but note the whole pipeline already hard-requires a Darwin toolchain elsewhere, so the
     non-Darwin branch is a thin shim, not a regex fallback).
   - `Program.GenerateBindings`'s `?? new InterfaceFactsAggregator(new[] { new
     RegexInterfaceFactsProducer() })` default (`Program.cs:269-270`) becomes a SwiftSyntax-required
     aggregator (or the parameter becomes non-optional and callers must supply it).

3. **Single resolution chain (F10 slice).**
   - `TypeDatabase.TryGetTypeRecordWithoutSupplement` arms 2-6 become `IResolutionStrategy`
     plug-ins on the same `TypeResolver.Default` chain, and the `SwiftTypeName` overloads become thin
     adapters that build a `NamedTypeSpec` (or a `SwiftTypeName`-keyed `ResolutionContext`) and call
     the resolver — mirroring what `TypeResolver`'s own remark (`Resolver/TypeResolver.cs:8-44`)
     already promises for the NamedTypeSpec overloads. New strategies needed: an out-of-module lookup
     strategy, a cross-module-alias strategy, and a `Swift.Error→AnyError` strategy (arm 6). The
     `DatabaseLookupStrategy` already covers arm 2; `AppleSupplementStrategy` covers arm 1.
   - **Ref-alias scoping:** `GetRefAliasVariant` gains a CF-family module gate — only produce the
     `Ref` variant when `swiftTypeName.Module` is in the CF set (CoreFoundation/CoreGraphics/the
     module-remap dict at `TypeDatabase.cs:53-57`). Pin with a unit test that a non-CF `FooRef`
     no longer round-trips to `Foo`.
   - **Prefix heuristic:** fold the four `!IsKnownAppleValueType && HasObjCClassPrefix` chains in
     `TypeProjectionFactory.cs` into one helper (e.g. `AppleFrameworkRegistry.IsLikelyObjCClassByName`
     or a `TypeProjectionFactory.GuessObjCClassFallback`) that **records the guess** into the binding
     report (the finding's "demote to a last-resort tier that records its guesses"). Keep the
     `IsOptionalObjCBridged`↔`TypeProjectionFactory` parity invariant (`constraints.md`) — the
     consolidation must update both readers together.
   - `ModuleProcessor.TryGetTypeRecord` (`:71-93`) routes through the unified resolver instead of its
     private branch; the `CGFloat` literal in `ClassifyFieldType` (`:313`) routes through
     `AppleFrameworkRegistry` value-type detection.

4. **Availability anchor (F46-remainder).**
   - The SwiftSyntax `AvailabilityWalker` emits, alongside each annotation, the member's **anchor
     identity** — the mangled name / usr where the digester provides one. The `Facts` wire shape and
     `AvailabilityAnnotation` gain an optional `anchorUsr`/`anchorMangledName`. `SwiftABIParser`'s
     join prefers the anchor key (`node.usr`/`node.MangledName`) and falls back to the normalized
     signature key only when no anchor is present. This is the groundwork that lets a later session
     delete `MemberSignatureNormalizer` once anchor coverage is proven complete.

5. **Bug-faithfulness inversion (F26).**
   - After the regex producer is gone, **bump `kSchemaVersion` (Swift) + `ExpectedSchemaVersion`
     (.NET) in lockstep** (`feedback_swift_interface_parser_schema_version` memory; `Output.swift:24`
     ↔ `InterfaceFactsJson.cs:69`), delete the shape-gate cliffs that reproduce regex blindness, and
     re-point the parity corpus from "regex ⇄ SwiftSyntax byte-equal" to **golden-output** assertions
     (SwiftSyntax vs a checked-in expected JSON) so SwiftSyntax semantics win. Move signature
     normalization to exactly one side of the JSON contract.

---

## Change inventory

Ordered. Each step is independently buildable; gates between steps in *Sequencing*.

**Phase A — close the producer coverage gap (the heart of F3).**

1. `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/ConstLiteralParametersWalker.swift`
   (new) → port `SwiftInterfaceAccessParser.GetConstLiteralParameters` + `ExtractConstLiteralFlags`
   (`Parser/SwiftInterfaceAccessParser.cs:4868-4990`). Key shape: `tracker.BuildMemberKey(printedName)`;
   detect `_const ` prefix on the param's post-colon type; emit only members with ≥1 const param.
   *Why:* the host must produce this fact for the producer to be sole source.
2. `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/ClosureParameterAttributesWalker.swift`
   (new) → port `GetClosureParameterAttributes` (`SwiftInterfaceAccessParser.cs:5006+`). Emits
   `[memberKey: [[attr]]]` (per-param normalized `@MainActor`/`@Sendable` lists). *Why:* needed so the
   synthesized `EveryProtocol` conformance reproduces the requirement's exact closure type.
3. `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/SpiOnlyConformancesWalker.swift` (new) →
   port `GetSpiOnlyConformances` (`SwiftInterfaceAccessParser.cs:1054-1106`) **over the
   `.private.swiftinterface`**. Note: the host today receives one `--input` path; the SPI fact reads
   the *private* companion. Add `--private-input <path>` to `main.swift` (the .NET producer derives it
   via `RegexInterfaceFactsProducer.DerivePrivateSwiftInterfacePath`, `:223-236` — move that derivation
   into `SwiftSyntaxInterfaceFactsProducer` and pass it). Emit `Type::Protocol` keys (Codable →
   Encodable+Decodable expansion, `:1093-1097`). *Why:* the producer must read the private interface
   the regex producer reads.
4. `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/Output.swift` → add to `struct Facts`:
   `var constLiteralParameters: [String: [Bool]]?`, `var closureParameterAttributes: [String: [[String]]]?`,
   `var spiOnlyConformances: [String]?`. *Why:* wire shape.
5. `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/main.swift` → wire the three walkers
   (`:61-75`), add the three `coveredFacts` strings (`:110-146`), pass the new `facts` fields
   (`:147-175`), accept `--private-input`. Fix the stale "24/24" comment (`:11,51`). *Why:* host
   surfaces the facts.
6. `src/Swift.Bindings/src/Parser/Producers/InterfaceFactsJson.cs` → add the three fields to
   `InterfaceFactsJsonPayload` (camelCase, matching `Output.swift`). *Why:* `UnmappedMemberHandling.Disallow`
   will reject the new wire fields otherwise.
7. `src/Swift.Bindings/src/Parser/Producers/SwiftSyntaxInterfaceFactsProducer.cs` → add the three
   `covered.Contains(...)` conversions to the `PartialSwiftInterfaceFacts` initializer (`:205-298`),
   add the three `ValidateCoverageAgainstPayload` null-checks (`:436-503`), pass the private path,
   fix the "100% (24/24)" doc-comment (`:20`). *Why:* producer materializes the new facts.

**Phase B — make SwiftSyntax required, delete the regex side (F3 cutover).**

8. `src/Swift.Bindings/src/BindingsGeneratorCommand.cs` → collapse `BuildAutoAggregator`
   (`:1867-1896`) so a locatable binary is required on Darwin (no regex fallback); remove the
   `RegexInterfaceFactsProducer` fallback returns. Collapse `BuildInterfaceFactsAggregator`
   (`:1847-1860`) — `regex` value handling removed or made a hard error. *Why:* kill the silent-
   degrade channel (F3 step 3).
9. `src/Swift.Bindings/src/Program.cs:269-270` → default aggregator becomes SwiftSyntax-required
   (or make `factsAggregator` non-optional). *Why:* the library entry default is the second
   regex-only channel.
10. `src/Swift.Bindings/src/CliOptions.cs:352-364` → update `--interface-facts-producer` description
    + default; drop the "24-fact" wording. Per owner call: keep `swift-syntax`-only, or retain a
    deprecated `regex` that errors. *Why:* flag truth.
11. **Delete** `src/Swift.Bindings/src/Parser/Producers/RegexInterfaceFactsProducer.cs`,
    `src/Swift.Bindings/src/Parser/SwiftInterfaceAccessParser.cs`,
    `src/Swift.Bindings/tests/UnitTests/ParserTests/SwiftInterfaceAccessParserTests.cs`. *Why:* the
    finding's deletion target (~9.7k LOC).
12. `src/Swift.Bindings/src/Parser/SwiftInterfaceContextTracker.cs` → break the circular dependency:
    inline the three statics it borrows (`CountBraces`, `ExtractPrintedName`) or delete the tracker if
    no surviving consumer (grep `SwiftInterfaceContextTracker` after step 11). Update doc-comments in
    `SwiftInterfaceFacts.cs:10,17,220`, `SwiftABIParser.cs:1582,2061`, `EveryProtocolEmitter.cs:1805`,
    `IInterfaceFactKind.cs`/`IInterfaceFactsProducer.cs` that reference `SwiftInterfaceAccessParser` by
    name. *Why:* no dangling references; CLAUDE.md "grep the whole codebase for ALL instances."
13. `src/Swift.Bindings/src/Parser/Producers/IInterfaceFactsProducer.cs`,
    `InterfaceFactKind.cs`, `InterfaceFactsAggregator.cs` → update doc-comments that describe the
    regex producer as a real fallback / "covers all 24." The aggregator stays (it's the merge seam);
    it just runs one producer now. *Why:* docs match a one-producer world.

**Phase C — F26 inversion (after B is green).**

14. `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/Output.swift:24` +
    `src/Swift.Bindings/src/Parser/Producers/InterfaceFactsJson.cs:69` → bump `kSchemaVersion` /
    `ExpectedSchemaVersion` to 3, in lockstep. *Why:* the cliffs change output shape/semantics.
15. The per-walker `*.swift` files → delete the "mirrors the regex bug" shape gates the parity corpus
    pins (indirect-enum scope-not-pushed, operator/backtick drops, grouped-case first-only, etc.),
    letting SwiftSyntax semantics win. Each deletion is its own diff with a golden-output update.
    *Why:* F26's monument removal.
16. `src/Swift.Bindings/tests/UnitTests/ParserTests/InterfaceFactsProducerParityTests.cs` → convert
    from cross-producer parity to **golden-output** assertions (SwiftSyntax vs checked-in expected
    JSON). *Why:* the regex oracle is gone; the gate must assert intended Swift semantics.

**Phase D — F10 resolver unification (gated each stage on `nuke validate`).**

17. `src/Swift.Bindings/src/TypeDatabase/Resolver/Strategies/` → add `OutOfModuleLookupStrategy`,
    `CrossModuleAliasStrategy`, `SwiftErrorStrategy` mirroring arms 4/5/6 of
    `TypeDatabase.TryGetTypeRecordWithoutSupplement` (`:676-702`). Register them in
    `TypeResolver.Default` (`Resolver/TypeResolver.cs:62-83`) in the existing order. *Why:* the
    SwiftTypeName arms become strategies.
18. `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs` → `TryGetTypeRecord(SwiftTypeName)` and
    `TryGetTypeRecordWithoutSupplement` become adapters that call the resolver (build a NamedTypeSpec /
    SwiftTypeName-keyed `ResolutionContext`). Keep behavior identical first; collapse the parallel
    arms second. *Why:* one resolution path.
19. `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs:960-969` → gate `GetRefAliasVariant` to
    CF-family modules. *Why:* scope the Ref rewrite (gameplan).
20. `src/Swift.Bindings/src/Marshaler/Projection/TypeProjectionFactory.cs:205-207,238,604-606,632` →
    extract the duplicated `!IsKnownAppleValueType && HasObjCClassPrefix` guard into one helper that
    records its guess into the binding report; update the `IsOptionalObjCBridged` parity reader
    (`MarshallingHelpers.cs`) in the same diff. *Why:* one heuristic home; demote + record guesses.
21. `src/Swift.Bindings/src/Parser/ModuleProcessor.cs:71-93,313` → route the private mini-resolver and
    the `CGFloat` literal through the unified resolver / `AppleFrameworkRegistry`. *Why:* fourth
    resolver retired.

**Phase E — F46 anchor groundwork.**

22. `tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/AvailabilityWalker.swift` + `Output.swift`
    `AvailabilityAnnotationJson` → emit an optional `anchorUsr`/`anchorMangledName` per annotation
    where derivable. *Why:* anchored identity.
23. `src/Swift.Bindings/src/Parser/Producers/InterfaceFactsJson.cs` (AvailabilityAnnotationJson) +
    `SwiftSyntaxInterfaceFactsProducer.ConvertAvailabilityAnnotations` (`:411-434`) +
    `Parser/SwiftABIParser.cs:387-421` → join on the anchor key first (`node.usr`/`MangledName`),
    falling back to the signature key. *Why:* prefer anchored identity; sets up normalizer deletion.

**New files:** 3 Swift walkers (steps 1-3), up to 3 C# resolution strategies (step 17), and the
golden-JSON fixtures for the converted parity test (step 16).

---

## Test plan

Repo TDD policy: fixture/test first, verify red, fix, verify green
(`feedback_tdd_for_regression_fixes`). Bug-first read each ported regex method for defects before
trusting it (`SpiOnlyConformances`'s Codable expansion and the `_const ` prefix exactness are the
likely traps).

- **Unit — three new facts cross-checked, then golden (Phase A/B/C).** While both producers still
  exist (Phase A), add `ConstLiteralParameters`/`ClosureParameterAttributes`/`SpiOnlyConformances`
  corpora to `InterfaceFactsProducerParityTests.cs` asserting regex ⇄ SwiftSyntax byte-equality —
  these three have **zero** cross-producer parity coverage today (verified: no `ConstLiteral`/
  `ClosureParameterAttr`/`SpiOnly` in that file). Swift shapes:
  - const: `public func f(_ x: _const Swift.Int) -> Swift.Int` → expect `{"Mod.f(_:)": [true]}`.
  - closure attrs: `public protocol P { func on(_ a: @escaping @MainActor @Sendable () -> Void) }`
    → expect `{"...on(_:)": [["MainActor","Sendable"]]}`.
  - SPI: a `.private.swiftinterface` with
    `@_spi(X) extension Mod.T : Swift.Codable {}` → expect `{"Mod.T::Encodable","Mod.T::Decodable"}`.
  After deletion (Phase C, step 16) these become golden-output assertions (SwiftSyntax vs expected
  JSON) since the regex oracle is gone.
- **Unit — required-producer fail-loud (Phase B).** Assert `BuildAutoAggregator`/the new builder
  **throws** on a missing binary on Darwin (no silent regex fallback). Today `BuildSwiftSyntaxAggregator`
  throws but `auto` does not — invert that for `auto`.
- **Unit — `InterfaceFactKindCoversEveryFactsField` / 1:1 alignment** (already exists per
  `InterfaceFactKind.cs:17`) must stay green after wire-field additions.
- **Unit — F10 resolver parity (Phase D).** `ResolverParityTests.cs` already exists
  (`tests/UnitTests/TypeDatabaseTests/Resolver/ResolverParityTests.cs`) — extend it so the new
  strategies reproduce arms 4/5/6 exactly; add a Ref-scoping test (non-CF `FooRef` does **not**
  resolve to `Foo`; `CoreFoundation.CGSizeRef`-style still does). Add a guard test that the four-way
  prefix-heuristic consolidation records its guess into the report.
- **Unit — F46 anchor join (Phase E).** Extend `SwiftABIParserRuntimeTests` (the existing
  `ParseModule_OverloadedMembers_AvailabilityAppliesOnlyToMatchingSignature` is the model) to assert
  the anchor key wins when present and the signature key still works when absent.
- **BindingTests (the real gate for F3 + F10 + F46).** The three migrated facts have *runtime*
  consequences: `_const` params suppress wrapper emission; closure attrs shape the `EveryProtocol`
  conformance; SPI-only conformances are dropped. Add/confirm BindingTests Swift fixtures in
  `BindingTests/Sources/SwiftBindingsTestLib/` for: a protocol requirement with a
  `@MainActor @Sendable` closure param (reverse-dispatch through `EveryProtocol`), a member with a
  `_const` param, and an `@_spi` conformance. C# assertions in the matching domain file under
  `BindingTests/RuntimeTestsApp/`.
  - **PreservedProtocols note (`feedback_new_reverse_dispatch_test_preserved_protocols`):** the
    closure-attribute fixture is a new EveryProtocol reverse-dispatch test — add its protocol to
    `PreservedProtocols` in `build/Helpers/SwiftSourceStripper.cs`, or the harness strips the
    witness-table getter → `EntryPointNotFoundException`. `--skip-regen` reuses a stale wrapper and
    won't reflect the edit, so regen for this one.

---

## Sequencing & parity gates

Migrate in the order A → B → C → D → E. Each step keeps the tree green.

1. **Phase A (add the three facts to SwiftSyntax) BEFORE deleting anything.** While both producers
   exist, the cross-producer parity corpus (above) is the cheap objective oracle — run it across the
   three new facts and, ideally, a **full-corpus parity bake**: drive every `.swiftinterface` in the
   validation libraries through *both* producers and diff every fact set (the aggregator design makes
   running both trivial — construct one aggregator with `[Regex]` and one with `[SwiftSyntax]` and
   compare `SwiftInterfaceFacts`). **Gate: zero fact-set diffs across the corpus** before Phase B.
   This converts the cutover risk into a measured diff (F3 "parity bake").
2. **Phase B (cutover) only after the bake is clean.** Delete the regex side; the parity corpus that
   referenced it must convert to golden in the same or next step. Run `nuke binding-tests
   --compile-only` (fail-closed) + `nuke binding-tests --skip-regen` to confirm the SwiftSyntax-only
   producer still yields compiling, passing bindings.
3. **Phase C (F26 inversion) is a deliberate behavior change.** Each cliff deletion is its own diff
   with a golden-output update and a BindingTests run if the cliff affected a member's emission. The
   `kSchemaVersion` bump (step 14) and the .NET `ExpectedSchemaVersion` bump must land in the **same
   commit** (memory: schema-version handshake) — a one-sided bump silently mis-maps.
4. **Phase D (F10) gates each stage on `nuke validate` as the canary** (the finding mandates this for
   363 call sites). Order within D: add strategies behaviorally-identical first (validate), then
   collapse the parallel arms (validate), then Ref-scoping (validate), then the prefix-heuristic fold
   (validate). Re-baseline `build/baselines/validation-baseline.json` only on the validate runs that
   actually move it; `git checkout HEAD --` the transient `-behaviortier` version-stamp churn
   (`feedback_validate_version_stamp_artifacts`).
5. **Phase E (F46) is additive** — the anchor is optional and the signature fallback stays, so it
   cannot regress existing availability joins. Gate on the new unit test + a BindingTests availability
   fixture.

Tripwire: rebuild the generator (`dotnet build src/Swift.Bindings/src -c Debug` or `nuke compile`)
before every gate; the staged Swift host binary must be rebuilt after each walker edit
(`nuke compile` builds `tools/SwiftInterfaceParser`) — a stale staged binary + `dotnet run --no-build`
silently mis-maps (`feedback_swift_interface_parser_schema_version`, `feedback_stale_release_binary_masks_regen`).

---

## Risks & owner-decision points

- **Biggest risk — the F26 inversion is a real behavior change, not a refactor.** Deleting the regex
  cliffs lets SwiftSyntax accept Swift the regex producer rejected (indirect enums, backticked/operator
  names, modifier-bearing members). That changes the emitted binding surface for those shapes. This is
  *desired* but must be validated per-cliff against BindingTests + a validate sweep — it is the one
  place "make it green" could hide a real regression. **Recommend** doing Phases A/B (pure migration,
  parity-gated) first and treating C as a separate, owner-reviewed sub-effort.
- **Owner decision — `--interface-facts-producer` fate.** Keep a deprecated `swift-syntax`-only flag,
  keep a `regex`-that-errors for one release, or remove the flag entirely? CLAUDE.md "no shortcuts"
  argues for clean removal; the review's "kept for one release cycle for emergency rollback"
  (`CliOptions.cs:362`) argues for a deprecation window. **Needs an owner call before Phase B.**
- **Owner decision — F46 scope this session.** Full F46 (delete `MemberSignatureNormalizer`, join
  purely on usr) touches the overload-disambiguation contract pinned by multiple tests and risks
  detaching `@available` floors if anchor coverage is incomplete. **Recommend** scoping S15 to the
  *anchor groundwork* (emit anchor, prefer-anchor-with-signature-fallback) and deferring normalizer
  deletion to a follow-up once anchor coverage is proven across the corpus. Confirm with owner.
- **Owner decision — `--private-input` for SPI.** The host gains a second input path. Confirm the
  derivation (`foo.swiftinterface → foo.private.swiftinterface`) and that a missing private interface
  is non-fatal (it is today: empty set).
- **F10 blast radius.** 363 resolution call sites; the finding itself flags "higher migration risk
  than most." Each Phase-D stage must be `nuke validate`-gated and individually revertible. Do **not**
  bundle D into one commit. If a stage shows a validate regression that isn't environmental churn,
  stop and consult — do not weaken the assertion or defer to roadmap autonomously
  (`feedback_no_autonomous_defer`).
- **Circular-dependency cleanup.** `SwiftInterfaceContextTracker` ↔ `SwiftInterfaceAccessParser` is
  mutually referential; deleting the regex parser forces a decision on the tracker. Audit consumers
  before deleting (CLAUDE.md "grep for ALL instances").

---

## Gate matrix

Per CLAUDE.md "run only what the change warrants." This session is generator + emitter + parser +
resolver, so it warrants the full ladder — but staged.

| Phase / change | `nuke test` | `nuke binding-tests` | `nuke validate` |
|---|---|---|---|
| A — add 3 facts to SwiftSyntax host | Yes (parity corpus + alignment) | `--compile-only` then `--skip-regen` (sim) | Optional — run the **parity bake** (both producers, diff) as the cutover gate |
| B — make required, delete regex side | Yes | `--compile-only` (fail-closed) + `--skip-regen` (sim); `--device` (the 3 facts affect wrapper emission / reverse-dispatch / SPI drop = calling-convention + marshalling surface) | Yes — full sweep is the cross-cutting deletion gate |
| C — F26 cliff inversion (per cliff) | Yes | `--skip-regen` (sim) per cliff; `--device` if a cliff changes a member's emission | Yes — behavior change across the corpus |
| D — F10 resolver unification (per stage) | Yes | `--skip-regen` (sim); `--device` if a stage changes struct/projection marshalling | **Yes, each stage** — the finding's mandated canary (363 call sites) |
| E — F46 anchor groundwork (additive) | Yes | `--skip-regen` (sim) with the new availability fixture | Optional — additive, signature fallback intact |

Notes: regen the closure-attribute BindingTests fixture (don't `--skip-regen`) since it's a new
reverse-dispatch test needing the witness-table getter. Run `--device` where the table flags it
(B, C-as-applicable, D-as-applicable) because Mono and NativeAOT have different bugs. Keep
BindingTests pass count and unit pass count ≥ baseline before any commit (per-commit gate); the
validation baseline only needs defending on the runs where you actually invoked `nuke validate`.
