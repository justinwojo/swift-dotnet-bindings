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

## Deferred to a follow-up session → tracked in [`06-remaining-work.md`](06-remaining-work.md)

The actionable generator gaps the four-session campaign uncovered but did not land are now tracked — with full root cause, fix direction, and a done-criterion each — in [`06-remaining-work.md`](06-remaining-work.md) (the finish line). Status of the three originally carved out here:

1. **HPKE construction surface** — nested-conformer (3+ part `ModuleQualifiedName`) CSM emission, blocked by the `ClassifyConformerStructurally` NestedType gate (`ConcreteProtocolSpecializationEmitter.cs:1858-1860`). **Open** → [06 T2.2](06-remaining-work.md#t22--cryptokit-generic-remainders). Full conformer enumeration in `02-csm-cryptokit.md` ("Task 3 — HPKE depth-check"); candidate `src/docs/Future/csm-nested-conformer.md` track.
2. **MusicItemProxy elided for marker-protocol-only conformance** (`MusicItem : Sendable`) — **RESOLVED**: `MusicItemProxy` now emits and MusicKit builds and runs (40/0/0 on sim, incl. the `Create` shims). See [`05-residual-gaps.md`](05-residual-gaps.md) ("What landed").
3. **`EveryEntityProtocol` carrier unreachable on real RealityFoundation input** — the routing gate `HasClassSuperclassRequirement` (`EveryProtocolEmitter.cs`) never sees the `<Self : Entity>` genericSig constraint because `SwiftABIParser.cs` `CreateProtocolDecl` populates `InheritedProtocols` only from `node.Conformances`. Carrier *emission* is now generator-unit-test-covered, but real-input routing + the device round-trip remain **open** → [06 T2.4](06-remaining-work.md#t24--entity-gesture-device-round-trip-failure-b--parser-genericsig). (Correct `03-proxy-callback.md`'s Failure B claim that these were "being skipped via `HasClassSuperclassRequirement`" — they were never routed — when the follow-up lands.)
