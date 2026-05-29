# Apple framework gap-fix campaign

A four-session campaign closing the actionable gaps surfaced by the 2026-05-27 audit (`src/docs/apple-framework-binding-gaps.md`, §6 is the underlying fixability gameplan). Sessions are grouped by **generator subsystem** so context and expertise carry across the fixes inside each one.

The dead frameworks (AppIntents, ActivityKit) are out of scope: both are RC‑STRUCTURAL (C#-authored types never seen by `swiftc`), both are on the `swift-dotnet-packages` do-not-ship list, and closing them requires a separate C#→Swift source-gen subsystem (a different product, not a binding-generator change). The keypath-branch work that productionized AppIntents v1 still pays off because the **infrastructure it built — the CSM engine, the KeyPath subsystem, the CA1416 propagation fix — is what Sessions 2 and 3 use to unblock CryptoKit, MusicKit, and the RealityKit family.**

## Operating model

- **Claude writes all code.** Codex and Grok are mid-flight design partners and end-of-session paired reviewers (`.claude/skills/coding-rules`). Codex is the stronger tool with the smaller usage limit — spend it on the one or two genuinely hard design calls per session. Grok has a much larger limit — use it freely for categorical sweeps and second opinions. Spawn them in parallel for important questions; don't outsource the thinking — synthesize both sides with your own independent investigation (`feedback_codex_design_partner.md`).
- **Codex iteration cap**: r1 review mandatory, r2 only when r1 surfaced a High you then fixed, r3 essentially never (`feedback_orchestration_token_cost.md`).
- **Every fix ships its tests at the right layer** per `CLAUDE.md`: unit tests for emitter/parser/marshaler logic, runtime tests for marshalling and P/Invoke, BindingTests for end-to-end ABI validation. "It's covered by an existing test" is only true if you can point at the assertion.
- **Device gate where noted.** Sessions tagged **device** must run `nuke binding-tests --device` (physical iOS, NativeAOT) in addition to the default sim run. Mono and NativeAOT have different bugs.
- **No session cascade.** If scope grows mid-session, audit the category empirically (Explore agent) *before* splitting — same shape in 2+ files = enumerate the whole category in one pass, don't spawn N, Nb, Nc follow-ups (`feedback_no_session_cascade.md`, `feedback_codex_loop_categorical_audit.md`).
- **Never autonomously defer real bugs to roadmap** (`feedback_no_autonomous_defer.md`). If a session can't land everything, ask before downscoping.

## Sessions at a glance

| # | Theme | Frameworks unblocked | Gate |
|---|---|---|---|
| [01](01-marshalling-correctness.md) | Value-type & stdlib-generic marshalling correctness | RealityKit, RealityFoundation, WorkoutKit | sim + **device** |
| [02](02-csm-cryptokit.md) | Generic monomorphization via the CSM engine | CryptoKit | sim + **device** |
| [03](03-proxy-callback.md) | Existential-proxy & callback bridging | RealityFoundation, RealityKit, RoomPlan | sim + **device** |
| [04](04-targeted-shims.md) | Targeted member shims & SwiftUI bridge | ProximityReader, MusicKit, FamilyControls | sim (+ **device** for ProximityReader / picker) |

Each session is independently shippable; there are no hard cross-session dependencies.

## Recommended order: 01 → 02 → 03 → 04

- **01 first** — RC‑SIMD is *silent corruption* of the most basic 3D op, so it's the highest real-world impact in the plan; ClosedRange and RC‑AOT amortize the same `SwiftArray` template work and the device-deploy cost.
- **02 second** — cleanest high-value-per-effort win, warms up the CSM engine for any later use.
- **03 third** — larger RealityKit reference-surface push; contains the campaign's one "L" item (`EveryEntityProtocol` new Swift class).
- **04 last** — cleanup sweep; **lead with the ProximityReader regen spike** to resolve the one open unknown that §6 couldn't pin without running the generator.

## Cross-cutting references

- **Gap analysis (the *why*):** `src/docs/apple-framework-binding-gaps.md` — §1 per-framework status, §2 root-cause clusters with evidence, §6 fixability gameplan with file:line landings (mirrored into each session doc below).
- **Project conventions:** `CLAUDE.md` — build/test targets, validation-gate table, BindingTests-as-real-gate rule, generator CLI usage.
- **Generator architecture:** `src/Swift.Bindings/src/` — Parser → TypeDatabase → Marshaler → Emitter.
- **Runtime:** `src/Swift.Runtime/src/Swift/` — `SwiftArray.cs` is the template Sessions 01 and 02 both lean on.
- **Roadmap:** `src/docs/roadmap.md` — higher-level themes; this campaign closes specific clusters, not roadmap entries 1:1.
- **Coding/review discipline:** `.claude/skills/coding-rules` — Codex + Grok parallel invocation, hang-proof flags, re-review loop.

## Explicitly out of scope (do NOT pull these in)

These are catalogued so a fresh session knows they're *intentionally* skipped, not forgotten:

- **AppIntents `perform()` / authoring, ActivityKit Live Activities** (RC‑STRUCTURAL) — both on the `swift-dotnet-packages` do-not-ship list; closing them needs a C#→Swift source-gen + macro-expansion subsystem (different product). The respective READMEs in `swift-dotnet-packages` carry the full structural rationale.
- **WeatherKit** statistics/summaries and `weather(for:including:)` — 6-way method-own-generic `async` tuple return; exceeds CSM cartesian cap. Full-bundle `WeatherAsync` stays the workaround.
- **TipKit result-builder DSL** (RC‑AEIC) — `@_alwaysEmitIntoClient` is shimmable as raw entrypoints, but the authoring experience is not restorable from C#.
- **`() -> [T]` result-builder closures + general SwiftUI composition** — `@ViewBuilder`/result-builder wall.
- **RC‑SB0003 reverse witness dispatch** — case-by-case; many are by-design Swift limits; forward (C#-implements) path works and is the supported mechanism.
- **RC‑CLOSURE `@autoclosure`** — no clear shipping-framework consumer; revisit only if one needs it.
- **RC‑PAT app-defined-conformer cases** (e.g. ProximityReader `requestDocument`) — CSM only works for Apple-finite conformer sets; app-defined → source-gen territory. Verify the conformer source before scoping any future work on these.
- **RC‑WILLSET** (RealityKit detached setter trap) — framework `willSet` precondition; no ABI route bypasses a Swift property observer. Session 03 lands a best-effort preflight guard + doc note; nothing more is generator-fixable.

## Free riders bundled with sessions

Two `swift-dotnet-packages` guide-accuracy corrections — the published guides slightly overstate breakage. These ride along with the relevant session at zero code cost:

- **CryptoKit guide** (Session 02) — HPKE `ExportSecret` (`CryptoKit.cs:19447`) and `Decapsulate` (`:22762`) *do* have working concrete `byte[]`/`Data` overloads. Only Seal/Open are broken.
- **TipKit guide** (Session 04) — `ITip.ShouldDisplay` (`TipKit.cs:9801`) and `Options` (`:9766`) **do** dispatch via real witness-table thunks. Only `Status`/`Invalidate` (and SwiftUI-typed members) throw.

Hold the `swift-dotnet-packages` commits until the new SDK is published per `feedback_no_commit_packages.md`.

## Deferred to a follow-up session (actionable, root cause identified)

Unlike the *out-of-scope* list above (intentional architectural skips), these three are real, fixable generator gaps the four-session campaign uncovered but did not land. Each has a concrete root cause and a candidate fix — they were carved out because each is its own generator workstream, not a hint/data tweak. A follow-up session can take them in any order; none blocks the others.

### 1. HPKE construction surface — nested-conformer (3+ part `ModuleQualifiedName`) CSM emission

**What's blocked.** All 10 `HPKE.Sender`/`HPKE.Recipient` initializers (`HPKE.Sender.init` ×5 at `CryptoKit.swiftinterface:632-637`, `HPKE.Recipient.init` ×5 at `:644-649`) and, transitively, `HPKE.Sender.ExportSecret` (its receiver is unconstructible from C#). HPKE's *methods* (`seal`/`open`/`exportSecret`) and the KEM `Decapsulate` family already work — only the keyed *construction* surface is dead.

**Root cause.** Every conformer of the key-constraining protocols (`HPKEDiffieHellmanPublicKey`/`PrivateKey`, `HPKEKEMPublicKey`/`PrivateKey`) — `Curve25519.KeyAgreement.PublicKey`, `XWingMLKEM768X25519.PublicKey`, the P256/P384/P521 KeyAgreement keys, etc. — has a **3+ component `ModuleQualifiedName`**. The `ClassifyConformerStructurally` NestedType gate at `src/Swift.Bindings/src/Emitter/.../ConcreteProtocolSpecializationEmitter.cs:1858-1860` (a bare `ModuleQualifiedName.Split('.').Length > 2` check) rejects all of them. The rejection is deliberate but undocumented on that arm (the "pin-and-pass" rationale belongs to the sibling `BlittableStructProjection` arm at `:1903-1906`). A hint-coverage extension alone does **not** help — this is structural CSM emission, not a missing conformer hint.

**Candidate fix / follow-up.** Lift the NestedType rejection so the CSM engine can emit `From{Conformer}` factories for nested-type conformers — a dedicated `src/docs/Future/csm-nested-conformer.md` track. Detail and the full conformer enumeration live in `02-csm-cryptokit.md` ("Task 3 — HPKE depth-check" and the "Outcome detail"). Ships with: BindingTest covering an HPKE `Sender` round-trip once construction is reachable; a CSM unit test asserting a 3-part-`ModuleQualifiedName` conformer emits a factory.

### 2. MusicItemProxy elided for marker-protocol-only conformance (`MusicItem : Sendable`)

**What's blocked.** The MusicKit `init(term:types:)` shim (Session 04, Task 2) compiles clean in isolation, but the **end-to-end binding-package build** fails: downstream `RunClassConstructor(typeof(MusicItemProxy).TypeHandle)` ancestor-ordering calls reference `MusicItemProxy`, which is **never emitted**. Pre-existing `SwiftBindings.Sdk` generator regression, orthogonal to the shim itself.

**Root cause.** The marker-protocol skip incorrectly elides the proxy class for `MusicItem : Sendable` — it treats the conformance as marker-only and drops the proxy, even though `MusicItem` carries a **real `id: MusicItemID` requirement**. The skip needs to fire only when the protocol is *purely* a marker (Sendable/Escapable/Copyable with zero member requirements), not when a marker conformance coexists with real requirements.

**Candidate fix / follow-up.** Tighten the marker-protocol elision so a protocol with any non-marker member requirement still emits its proxy class. Detail in `04-targeted-shims.md` (Task 2 "Validation" note). Ships with: a BindingTest reproducing a `protocol P: Sendable { var id: SomeID { get } }` conformer that must still emit its proxy and resolve `RunClassConstructor` ancestor ordering; unit test on the elision predicate.

### 3. `EveryEntityProtocol` carrier is unreachable on real RealityFoundation input

**What's blocked.** Session 03 added an `EveryEntityProtocol : Entity` existential-proxy carrier to route class-rooted protocols whose superclass requirement is `RealityFoundation.Entity` (e.g. `HasAnchoring`, the 8 Entity-rooted protocols in RealityFoundation). The carrier is **never emitted on real input** — verified: real generated `RealityFoundation.cs` contains **0** `EveryEntityProtocol` occurrences, and `IHasAnchoring` emits as a plain `public interface` (neither skipped nor routed). The Session 03 fix is effectively dead code against the shipping framework.

**Root cause.** The routing gate `HasClassSuperclassRequirement` (`EveryProtocolEmitter.cs`) reads `ProtocolDecl.InheritedProtocols`, but the parser populates `InheritedProtocols` **only from `node.Conformances`** (`SwiftABIParser.cs` `CreateProtocolDecl`). Real Entity-rooted protocols encode the superclass **solely in the generic signature** (`<Self : RealityKit.Entity>`) — `conformances` lists only `Escapable`/`Copyable` and `superclassNames` is `None` (confirmed against a `swift-api-digester` dump of RealityFoundation). So the genericSig class constraint never reaches `InheritedProtocols`, `HasClassSuperclassRequirement` returns false, and the protocol falls through to a plain interface. A probe (`protocol EntityRootedTag: Entity`) reproduced this: it emitted a regular `IEntityRootedTag` interface with no carrier.

**Why no BindingTest shipped.** The fix cannot be exercised end-to-end until the parser surfaces the constraint — a hermetic BindingTest fixture would also emit a plain interface, so it would assert nothing about the carrier. Shipping a green test here would be a fake gate (`feedback_tdd_for_regression_fixes`, "No shortcuts").

**Candidate fix / follow-up.** Extend `CreateProtocolDecl` to extract a `genericSig` class superclass constraint (`<Self : SomeClass>`) into `InheritedProtocols` (or a dedicated superclass field the routing gate reads). Then `HasClassSuperclassRequirement`/`IsEntityRootedProtocol` fire on real input, the carrier emits, and the Entity-rooted round-trip BindingTest spec in `03-proxy-callback.md` ("Failure B") becomes feasible and ships as the durable gate. Note: `03-proxy-callback.md`'s Failure B section claims the protocols "were being skipped via `HasClassSuperclassRequirement`" — that is **contradicted** by this finding (they were never routed in the first place); correct that doc when the follow-up lands.
